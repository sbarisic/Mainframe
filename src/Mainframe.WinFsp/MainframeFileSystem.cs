using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Fsp;
using Mainframe.Client;
using Mainframe.Protocol;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Mainframe.WinFsp;

/// <summary>Windows presentation of the kernel API. No disk provider or reconnect/replay path.</summary>
public sealed class MainframeFileSystem : FileSystemBase, IDisposable
{
    private sealed class Node(string identity)
    {
        public string Identity { get; } = identity;
        public int References;
    }
    private sealed class Descriptor(KernelFileHandle handle, string path, string[] rights)
    {
        public readonly object Gate = new();
        public KernelFileHandle Handle { get; } = handle;
        public string Path = path;
        public string[] Rights { get; } = rights;
        public bool Cleaned;
        public bool Closed;
    }
    private sealed class Enumeration
    {
        public FsEntry[] Entries = [];
        public int Index;
        public string? Continuation;
        public bool Started;
    }
    private readonly KernelClient _client;
    private readonly string _root;
    private readonly byte[] _security;
    private readonly object _nodesGate = new();
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Descriptor, Node> _handles = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly Action<string> _log;
    public Action<string>? Trace { get; init; }
    private int _failed;
    private volatile bool _stopping;
    private int _disposed;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Stopped => _stopped.Task;
    public bool Failed => Volatile.Read(ref _failed) != 0;
    public int OpenHandleCount => _handles.Count;

    public MainframeFileSystem(KernelClient client, string root, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        _client = client;
        _root = VirtualPath.NormalizeRoot(root);
        _log = log ?? Console.Error.WriteLine;
        using var identity = WindowsIdentity.GetCurrent();
        string sid = identity.User!.Value;
        var descriptor = new RawSecurityDescriptor($"O:{sid}G:{sid}D:P(A;OICI;FA;;;{sid})(A;OICI;FA;;;SY)");
        _security = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(_security, 0);
    }

    public byte[] Security => (byte[])_security.Clone();
    public void BeginStop()
    {
        _stopping = true;
        _abort.CancelAfter(TimeSpan.FromSeconds(10));
    }
    public void Abort() => _abort.Cancel();
    public override int ExceptionHandler(Exception ex)
    {
        Trace?.Invoke($"{ex.GetType().Name}: {ex.Message} => {WindowsMapping.Status(ex):X8}");
        return WindowsMapping.Status(ex);
    }
    public override void DispatcherStopped(bool normally) => _stopped.TrySetResult();
    private T Rpc<T>(Func<CancellationToken, Task<T>> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        return action(deadline.Token).WaitAsync(deadline.Token).GetAwaiter().GetResult();
    }
    private FsEntry Stat(string path) => Rpc(t => _client.Files.StatAsync(path, t));
    private static Descriptor Desc(object descriptor) => (Descriptor)descriptor;
    private void Failure(Exception ex)
    {
        Interlocked.Exchange(ref _failed, 1);
        _log($"Filesystem cleanup failed: {ex.Message}");
    }

    public override int Init(object hostObject)
    {
        var host = (FileSystemHost)hostObject;
        host.FileSystemName = "Mainframe";
        host.SectorSize = 512;
        host.SectorsPerAllocationUnit = 8;
        host.MaxComponentLength = 255;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.ReparsePoints = false;
        host.NamedStreams = false;
        host.ExtendedAttributes = false;
        host.FileInfoTimeout = 0;
        host.DirInfoTimeout = 0;
        host.PassQueryDirectoryFileName = true;
        host.VolumeInfoTimeout = 0;
        host.SecurityTimeout = 0;
        host.FlushAndPurgeOnCleanup = true;
        host.PostCleanupWhenModifiedOnly = false;
        host.PostDispositionWhenNecessaryOnly = false;
        host.VolumeSerialNumber = (uint)WindowsMapping.Identity(_root);
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo info)
    {
        info = default;
        try
        {
            FsDiscovery discovery = Rpc(t => _client.Files.DiscoverAsync(_root, t));
            foreach (FsSpace space in discovery.BackingStorage.DistinctBy(s => s.Identity, StringComparer.OrdinalIgnoreCase))
            {
                info.TotalSize = checked(info.TotalSize + (ulong)FileNumbers.Parse(space.TotalBytes));
                info.FreeSize = checked(info.FreeSize + (ulong)FileNumbers.Parse(space.FreeBytes));
            }
            info.SetVolumeLabel("Mainframe");
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int GetSecurityByName(string name, out uint attributes, ref byte[] security)
    {
        Trace?.Invoke($"GetSecurityByName {name}");
        attributes = 0;
        try
        {
            attributes = WindowsMapping.Info(Stat(VirtualPath.Map(_root, name))).FileAttributes;
            if (security is not null) security = Security;
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int GetSecurity(object node, object descriptor, ref byte[] security)
    {
        try
        {
            Descriptor d = Desc(descriptor);
            lock (d.Gate) _ = Rpc(t => d.Handle.StatAsync(t));
            security = Security;
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }
    public override int SetSecurity(object node, object descriptor, AccessControlSections sections, byte[] security) => STATUS_NOT_SUPPORTED;

    private int OpenCore(string name, uint options, uint access, bool create, uint attributes, ulong allocation,
        out object nodeObject, out object descriptorObject, out FileInfo info, out string normalized)
    {
        nodeObject = descriptorObject = null!;
        normalized = null!;
        info = default;
        KernelFileHandle? handle = null;
        try
        {
            if (_stopping) return STATUS_DEVICE_NOT_READY;
            string path = VirtualPath.Map(_root, name);
            Trace?.Invoke($"{(create ? "Create" : "Open")} {path} options={options:X8} access={access:X8}");
            bool directory = create ? (options & FILE_DIRECTORY_FILE) != 0 : Stat(path).Directory;
            if ((options & FILE_NON_DIRECTORY_FILE) != 0 && directory) return STATUS_FILE_IS_A_DIRECTORY;
            if ((options & FILE_DIRECTORY_FILE) != 0 && !directory) return STATUS_NOT_A_DIRECTORY;
            int storedAttributes = WindowsMapping.Attributes(attributes);
            string[] rights = WindowsMapping.Rights(access, directory, create);
            handle = Rpc(t => _client.Files.OpenHandleAsync(new(path, directory ? "directory" : "file", create ? "create" : "open", rights, ["read", "write", "delete"]), t));
            if (create)
            {
                if (allocation != 0 && !directory) Rpc(t => handle.SetSizeAsync(checked((long)allocation), true, t));
                if (storedAttributes != 0) Rpc(t => handle.SetMetadataAsync(new(handle.Id, Attributes: storedAttributes), t));
            }
            var descriptor = new Descriptor(handle, path, rights);
            FsEntry entry = Rpc(t => handle.StatAsync(t));
            string identity = entry.ResourceId ?? entry.Id;
            Node node;
            lock (_nodesGate)
            {
                if (!_nodes.TryGetValue(identity, out node!)) _nodes.Add(identity, node = new(identity));
                node.References++;
            }
            _handles.TryAdd(descriptor, node);
            nodeObject = node;
            descriptorObject = descriptor;
            info = WindowsMapping.Info(entry);
            normalized = name == "\\" || string.IsNullOrEmpty(entry.Name) ? name : name[..(name.LastIndexOf('\\') + 1)] + entry.Name;
            handle = null;
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
        finally
        {
            if (handle is not null)
                try { Rpc(async t => { await handle.CloseAsync(t); return true; }); }
                catch (Exception ex) { Failure(ex); }
        }
    }

    public override int Open(string name, uint options, uint access, out object node, out object descriptor, out FileInfo info, out string normalized) =>
        OpenCore(name, options, access, false, 0, 0, out node, out descriptor, out info, out normalized);
    public override int Create(string name, uint options, uint access, uint attributes, byte[] security, ulong allocation, out object node, out object descriptor, out FileInfo info, out string normalized) =>
        OpenCore(name, options, access, true, attributes, allocation, out node, out descriptor, out info, out normalized);

    private int Update(object descriptor, Action<Descriptor> operation, out FileInfo info)
    {
        info = default;
        try
        {
            Descriptor d = Desc(descriptor);
            lock (d.Gate)
            {
                if (d.Closed) return STATUS_INVALID_HANDLE;
                operation(d);
                info = WindowsMapping.Info(Rpc(t => d.Handle.StatAsync(t)));
            }
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int GetFileInfo(object node, object descriptor, out FileInfo info) => Update(descriptor, _ => { }, out info);
    public override int Overwrite(object node, object descriptor, uint attributes, bool replace, ulong allocation, out FileInfo info) => Update(descriptor, d =>
    {
        int value = WindowsMapping.Attributes(attributes);
        if (!replace) value |= Rpc(t => d.Handle.StatAsync(t)).Attributes;
        Rpc(t => d.Handle.SetSizeAsync(0, false, t));
        Rpc(t => d.Handle.SetSizeAsync(checked((long)allocation), true, t));
        Rpc(t => d.Handle.SetMetadataAsync(new(d.Handle.Id, Attributes: value), t));
    }, out info);

    public override int SetBasicInfo(object node, object descriptor, uint attributes, ulong created, ulong accessed, ulong modified, ulong changed, out FileInfo info) => Update(descriptor, d =>
        Rpc(t => d.Handle.SetMetadataAsync(new(d.Handle.Id, attributes == uint.MaxValue ? null : WindowsMapping.Attributes(attributes),
            WindowsMapping.Timestamp(created), WindowsMapping.Timestamp(modified), WindowsMapping.Timestamp(accessed), WindowsMapping.Timestamp(changed)), t)), out info);
    public override int SetFileSize(object node, object descriptor, ulong size, bool allocation, out FileInfo info) => Update(descriptor, d =>
        Rpc(t => d.Handle.SetSizeAsync(checked((long)size), allocation, t)), out info);

    public override int Read(object node, object descriptor, IntPtr buffer, ulong offset, uint length, out uint transferred)
    {
        transferred = 0;
        try
        {
            Descriptor d = Desc(descriptor);
            lock (d.Gate)
            {
                while (transferred < length)
                {
                    int count = (int)Math.Min(65536u, length - transferred);
                    long position = checked((long)(offset + transferred));
                    byte[] bytes = Rpc(t => d.Handle.ReadAsync(position, count, t));
                    Marshal.Copy(bytes, 0, buffer + checked((int)transferred), bytes.Length);
                    transferred += (uint)bytes.Length;
                    if (bytes.Length < count) break;
                }
            }
            return transferred == 0 && length != 0 ? STATUS_END_OF_FILE : STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int Write(object node, object descriptor, IntPtr buffer, ulong offset, uint length, bool append, bool constrained, out uint transferred, out FileInfo info)
    {
        transferred = 0;
        info = default;
        try
        {
            Descriptor d = Desc(descriptor);
            lock (d.Gate)
            {
                while (transferred < length)
                {
                    int count = (int)Math.Min(65536u, length - transferred);
                    byte[] bytes = new byte[count];
                    Marshal.Copy(buffer + checked((int)transferred), bytes, 0, count);
                    long position = append ? 0 : checked((long)(offset + transferred));
                    int written = Rpc(t => d.Handle.WriteAsync(position, bytes, append, constrained, t));
                    transferred += (uint)written;
                    if (written != count)
                    {
                        if (!constrained) return STATUS_IO_DEVICE_ERROR;
                        break;
                    }
                }
                info = WindowsMapping.Info(Rpc(t => d.Handle.StatAsync(t)));
            }
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int Flush(object node, object descriptor, out FileInfo info)
    {
        if (descriptor is not null)
            return Update(descriptor, d => { if (!VirtualPath.Protected(d.Path) && d.Rights.Any(r => r is "write-data" or "append" or "write-metadata")) Rpc(t => d.Handle.FlushAsync(t)); }, out info);
        info = default;
        try
        {
            // Read-only handles cannot have dirty data. Flush every writable volume represented by this session.
            foreach (Descriptor d in _handles.Keys)
                lock (d.Gate)
                    if (!d.Closed && !VirtualPath.Protected(d.Path) && d.Rights.Any(r => r is "write-data" or "append" or "write-metadata")) Rpc(t => d.Handle.FlushAsync(t));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int SetDelete(object node, object descriptor, string name, bool delete)
    {
        Trace?.Invoke($"SetDelete {name} {delete}");
        try
        {
            Descriptor d = Desc(descriptor);
            lock (d.Gate) Rpc(t => d.Handle.SetDeleteAsync(delete, t));
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int Rename(object node, object descriptor, string name, string destination, bool replace)
    {
        try
        {
            string target = VirtualPath.Map(_root, destination);
            Descriptor d = Desc(descriptor);
            lock (d.Gate)
            {
                string old = d.Path;
                Rpc(t => d.Handle.RenameAsync(target, replace, t));
                d.Path = target;
                // Paths are used only for namespace bookkeeping; all data access uses stable handles.
                foreach (Descriptor other in _handles.Keys)
                    if (other.Path.StartsWith(old + "/", StringComparison.OrdinalIgnoreCase)) other.Path = target + other.Path[old.Length..];
            }
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int ReadDirectory(object node, object descriptor, string pattern, string marker, IntPtr buffer, uint length, out uint transferred)
    {
        transferred = 0;
        try
        {
            lock (Desc(descriptor).Gate) return SeekableReadDirectory(node, descriptor, pattern, marker, buffer, length, out transferred);
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override int GetDirInfoByName(object node, object descriptor, string name, out string normalized, out FileInfo info)
    {
        normalized = null!;
        info = default;
        try
        {
            if (name.Contains('\\') || name.Contains('/') || name is "." or "..") return STATUS_OBJECT_NAME_INVALID;
            Descriptor d = Desc(descriptor);
            lock (d.Gate)
            {
                // Validate the directory handle (including current grants) before a path lookup.
                _ = Rpc(t => d.Handle.StatAsync(t));
                string path = VirtualPath.Map(d.Path, "\\" + name);
                FsEntry entry = Stat(path);
                normalized = entry.Name;
                info = WindowsMapping.Info(entry);
            }
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return ExceptionHandler(ex); }
    }

    public override bool ReadDirectoryEntry(object node, object descriptor, string pattern, string marker, ref object context, out string name, out FileInfo info)
    {
        name = null!;
        info = default;
        var cursor = context as Enumeration ?? new Enumeration();
        context = cursor;
        Descriptor d = Desc(descriptor);
        if (cursor.Index == cursor.Entries.Length)
        {
            if (cursor.Started && cursor.Continuation is null) return false;
            FsListing page = Rpc(t => d.Handle.EnumerateAsync(256, restart: !cursor.Started && marker is null,
                marker: cursor.Started || marker is "." or ".." ? null : marker, continuation: cursor.Continuation, token: t));
            cursor.Entries = page.Entries;
            cursor.Index = 0;
            cursor.Continuation = page.Continuation;
            cursor.Started = true;
            if (cursor.Entries.Length == 0) return false;
        }
        FsEntry entry = cursor.Entries[cursor.Index++];
        name = entry.Name;
        info = WindowsMapping.Info(entry);
        return true;
    }

    public override void Cleanup(object node, object descriptor, string name, uint flags)
    {
        Descriptor d = Desc(descriptor);
        Trace?.Invoke($"Cleanup {d.Path} flags={flags:X8}");
        lock (d.Gate)
        {
            if (d.Cleaned || d.Closed) return;
            try
            {
                if ((flags & CleanupDelete) != 0) Rpc(t => d.Handle.SetDeleteAsync(true, t));
                Rpc(t => d.Handle.CleanupAsync(t));
                d.Cleaned = true;
            }
            catch (Exception ex) { Failure(ex); }
        }
    }

    public override void Close(object nodeObject, object descriptor)
    {
        Descriptor d = Desc(descriptor);
        lock (d.Gate)
        {
            if (d.Closed) return;
            try
            {
                if (!d.Cleaned)
                    try { Rpc(t => d.Handle.CleanupAsync(t)); }
                    catch (Exception ex) { Failure(ex); }
                Rpc(async t => { await d.Handle.CloseAsync(t); return true; });
            }
            catch (Exception ex) { Failure(ex); }
            finally
            {
                d.Closed = true;
                if (_handles.TryRemove(d, out Node? node))
                    lock (_nodesGate) if (--node.References == 0) _nodes.Remove(node.Identity);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var pair in _handles) Close(pair.Value, pair.Key);
        _abort.Dispose();
    }
}
