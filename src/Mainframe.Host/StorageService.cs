using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Mainframe.Core;
using Mainframe.Protocol;
using Mainframe.Storage;
using Microsoft.Data.Sqlite;

namespace Mainframe.Host;

internal sealed partial class StorageService : IDisposable
{
    private readonly KernelStore _store;
    private readonly Action<string>? _fault;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _maintenance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _operations = new(64, 64);
    private readonly SemaphoreSlim _passwords = new(2, 2);
    private readonly ConcurrentDictionary<string, Mount> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Handle> _handles = new();
    private readonly Dictionary<string, int> _pendingHandles = new();
    private readonly Queue<(DateTimeOffset Time, string Principal)> _failures = new();
    private readonly string _rootGeneration = Guid.NewGuid().ToString("N");
    private readonly byte[] _cursorKey = RandomNumberGenerator.GetBytes(32);
    private sealed record Mount(VolumeInfo Info, IStorageVolume? Volume)
    {
        public FsEntry? Root { get; } = Volume?.Stat(1);
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public SemaphoreSlim Operations { get; } = new(64, 64);
    }

    private sealed class Admission(SemaphoreSlim slots, SemaphoreSlim gate) : IDisposable
    {
        public SemaphoreSlim Gate { get; } = gate;
        public void Dispose() => slots.Release();
    }

    private sealed class HandleReservation(StorageService owner, string session) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._handles)
            {
                if (--owner._pendingHandles[session] == 0)
                {
                    owner._pendingHandles.Remove(session);
                }
            }
        }
    }

    private IDisposable ReserveHandle(string session)
    {
        lock (_handles)
        {
            int pending = _pendingHandles.GetValueOrDefault(session);
            if (_handles.Count + _pendingHandles.Values.Sum() >= 1024 ||
                _handles.Values.Count(h => h.Session == session) + pending >= 256)
            {
                throw Error("RESOURCE_UNAVAILABLE", "File handle limit reached.");
            }
            _pendingHandles[session] = pending + 1;
            return new HandleReservation(this, session);
        }
    }
    private sealed record Handle(string Session, string Mount, long Entry, string Access, string[] Share, Func<bool> Valid)
    {
        public bool V2
        {
            get; init;
        }
        public string[] Rights { get; init; } = [];
        public volatile bool Revoked;
        public bool Cleaned
        {
            get; set;
        }
        public string? SyntheticPath
        {
            get; init;
        }
        public string Generation { get; init; } = "";
        public List<RangeLock> Locks { get; } = new();
    }
    private sealed record RangeLock(long Offset, long Length, bool Exclusive);
    public StorageService(KernelStore store, Action<string>? fault = null, Action<string>? log = null)
    {
        _store = store;
        _fault = fault;
        _log = log ?? (_ =>
        {
        });
        foreach (VolumeInfo info in store.ListVolumeMounts())
        {
            _mounts[info.Mount] = new(info, null);
        }

        _maintenance = Task.Run(MaintainAsync);
    }

    private static KernelOperationException Error(string code, string message) => EncryptedVolume.Error(code, message);
    private static void Require(bool allowed)
    {
        if (!allowed)
        {
            throw Error("ACCESS_DENIED", "Operation is not authorized.");
        }
    }

    private static bool Allowed(OperatorSession session, Mount mount, string access) => session.Valid() && session.Allows($"volume:{mount.Info.Id}:{access}");
    private static void Admin(OperatorSession session) => Require(session.IsOperator && session.Valid() && session.Allows("volume.manage"));
    private static void CheckAccess(OperatorSession session, Mount mount, bool read, bool write)
    {
        Require(session.Valid() && (!read || Allowed(session, mount, "read")) && (!write || Allowed(session, mount, "write")));
    }

    private (Mount Mount, string Relative) Resolve(string path, OperatorSession session, bool allowLocked = false, string? permission = null)
    {
        string normalized = VolumeNames.Normalize(path);
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != "vol" || !_mounts.TryGetValue("/vol/" + parts[1], out Mount? mount))
        {
            throw Error(parts.Length > (parts.Length > 0 && parts[0] == "vol" ? 2 : 1) ? "PATH_NOT_FOUND" : "NOT_FOUND", "No namespace entry exists at this path.");
        }

        Require(permission is null ? Allowed(session, mount, "read") || Allowed(session, mount, "write") : Allowed(session, mount, permission));
        if (mount.Volume is null && !allowLocked)
        {
            throw Error("VOLUME_LOCKED", "Volume requires manual unlock.");
        }

        return (mount, string.Join('/', parts.Skip(2)));
    }

    private (Handle Handle, Mount Mount) GetHandle(string id, OperatorSession session, bool read = false, bool write = false)
    {
        if (!_handles.TryGetValue(id, out Handle? handle) || handle.Session != session.Id)
            throw Error("INVALID_HANDLE", "Handle is invalid for this session.");
        if (handle.Revoked || !handle.Valid())
        {
            handle.Revoked = true;
            throw Error("INVALID_HANDLE", "Handle authorization has ended.");
        }
        Mount mount;
        if (handle.SyntheticPath is not null) mount = _namespace;
        else if (!_mounts.TryGetValue(handle.Mount, out mount!) || mount.Volume is null || handle.Generation != mount.Info.Generation)
            throw Error("INVALID_HANDLE", "Handle generation has ended.");
        if (handle.V2)
        {
            if (read) Right(handle, "read-data");
            if (write && !handle.Rights.Contains("write-data") && !handle.Rights.Contains("append")) throw Error("ACCESS_DENIED", "Data write access is required.");
        }
        Require((!read || handle.Access is "read" or "read-write") && (!write || handle.Access is "write" or "read-write"));
        if (handle.SyntheticPath is null) CheckAccess(session, mount, read, write);
        return (handle, mount);
    }

    private void Sharing(string mount, long entry, string access, string[] share)
    {
        foreach (Handle old in _handles.Values.Where(h => h.Mount == mount && h.Entry == entry && !h.Cleaned))
        {
            if ((access is "read" or "read-write") && !old.Share.Contains("read") || (access is "write" or "read-write") && !old.Share.Contains("write") || (old.Access is "read" or "read-write") && !share.Contains("read") || (old.Access is "write" or "read-write") && !share.Contains("write") || old.Rights.Contains("delete") && !share.Contains("delete"))
            {
                throw Error("SHARING_VIOLATION", "File sharing modes conflict.");
            }
        }
    }

    private void DeleteSharing(string mount, long entry)
    {
        if (_handles.Values.Any(h => h.Mount == mount && h.Entry == entry && !h.Cleaned && !h.Share.Contains("delete")))
        {
            throw Error("SHARING_VIOLATION", "An open handle denies deletion or rename.");
        }
    }

    private void Invalidate(string mount, long entry)
    {
        foreach (string key in _handles.Where(p => p.Value.Mount == mount && p.Value.Entry == entry && !p.Value.V2).Select(p => p.Key).ToArray())
        {
            CloseHandle(key);
        }
    }

    private void Reap(Mount? mount = null)
    {
        foreach (string key in _handles.Where(p => (p.Value.Revoked || !p.Value.Valid()) && (mount is null ? p.Value.SyntheticPath is not null : p.Value.Mount == mount.Info.Mount)).Select(p => p.Key).ToArray())
        {
            CloseHandle(key);
        }
    }

    public async Task ReleaseSessionAsync(string session)
    {
        await _gate.WaitAsync();
        try
        {
            foreach (string key in _handles.Where(p => p.Value.Session == session).Select(p => p.Key).ToArray())
            {
                Handle handle = _handles[key];
                Mount? mount = _mounts.GetValueOrDefault(handle.Mount);
                if (mount is not null) await mount.Gate.WaitAsync();
                try { CloseHandle(key); }
                finally { mount?.Gate.Release(); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Admission> AdmitAsync(RpcRequest request, OperatorSession session, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            string? path = null;
            string? handle = null;
            if (request.Version == 2)
            {
                switch (request.Method)
                {
                    case "fs.open":
                        path = ExecutionService.Args<FsOpenV2>(request).Path;
                        break;
                    case "fs.discover":
                    case "fs.flush-volume":
                        path = ExecutionService.Args<FsPath>(request).Path;
                        break;
                    case "fs.stat":
                        FsStat stat = ExecutionService.Args<FsStat>(request);
                        if ((stat.Path is null) == (stat.Handle is null)) throw Error("INVALID_ARGUMENT", "Supply exactly one path or handle.");
                        path = stat.Path;
                        handle = stat.Handle;
                        break;
                    case "fs.read":
                    case "fs.write":
                    case "fs.enumerate":
                    case "fs.rename":
                    case "fs.metadata":
                    case "fs.size":
                    case "fs.disposition":
                    case "fs.cleanup":
                    case "fs.close":
                    case "fs.flush":
                    case "fs.lock":
                    case "fs.unlock":
                        handle = ExecutionService.Args<FsHandle>(request).Handle;
                        break;
                    default:
                        throw Error("UNSUPPORTED_METHOD", "Unknown version-2 filesystem method.");
                }
            }
            else switch (request.Method)
                {
                    case "fs.read":
                    case "fs.write":
                        handle = ExecutionService.Args<FsRange>(request).Handle;
                        break;
                    case "fs.close":
                    case "fs.flush":
                        handle = ExecutionService.Args<FsHandle>(request).Handle;
                        break;
                    case "fs.truncate":
                        handle = ExecutionService.Args<FsTruncate>(request).Handle;
                        break;
                    case "fs.stat":
                        FsStat stat = ExecutionService.Args<FsStat>(request);
                        if ((stat.Path is null) == (stat.Handle is null))
                        {
                            throw Error("INVALID_ARGUMENT", "Supply exactly one path or handle.");
                        }
                        path = stat.Path;
                        handle = stat.Handle;
                        break;
                    case "fs.list":
                        path = ExecutionService.Args<FsList>(request).Path;
                        break;
                    case "fs.open":
                        path = ExecutionService.Args<FsOpen>(request).Path;
                        break;
                    case "fs.mkdir":
                    case "fs.delete":
                        path = ExecutionService.Args<FsPath>(request).Path;
                        break;
                    case "fs.rename":
                        path = ExecutionService.Args<FsRename>(request).Source;
                        break;
                }

            if (handle is not null && request.Version == 1 && GetHandle(handle, session).Handle.V2) throw Error("INVALID_HANDLE", "Version-2 handles require version-2 calls.");
            Mount? mount = handle is not null ? GetHandle(handle, session).Mount : null;
            if (mount == _namespace) mount = null;
            if (path is not null && !IsSynthetic(path))
            {
                mount = Resolve(path, session, allowLocked: true).Mount;
            }

            SemaphoreSlim slots = mount?.Operations ?? _operations;
            if (!slots.Wait(0))
            {
                throw Error("RESOURCE_UNAVAILABLE", "Storage operation queue is full.");
            }
            return new Admission(slots, mount?.Gate ?? _gate);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task HandleAsync(WireExchange exchange, RpcRequest request, OperatorSession session)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(exchange.Cancelled);
        deadline.CancelAfter(Math.Clamp(request.TimeoutMs ?? 10000, 1, 30000));
        CancellationToken token = deadline.Token;
        bool streamed = false;
        Admission? admission = null;
        try
        {
            if (request.TimeoutMs is < 1 or > 30000)
            {
                throw Error("INVALID_ARGUMENT", "Storage deadline must be 1 to 30000 milliseconds.");
            }

            if (request.Version is not (1 or 2) || request.Version == 2 && !request.Method.StartsWith("fs.", StringComparison.Ordinal))
            {
                throw Error("UNSUPPORTED_METHOD", "Unsupported storage method version.");
            }

            Require(session.Valid());
            if (request.Version == 1 && request.Method is "fs.mkdir" or "fs.delete" && ProtectedNamespaceEntry(ExecutionService.Args<FsPath>(request).Path))
                throw Error("ACCESS_DENIED", "Namespace and mount entries are immutable.");
            if (request.Method == "fs.open")
            {
                bool creates = request.Version == 1 ? ExecutionService.Args<FsOpen>(request).Mode != "open-existing" : ExecutionService.Args<FsOpenV2>(request).Disposition != "open";
                string path = request.Version == 1 ? ExecutionService.Args<FsOpen>(request).Path : ExecutionService.Args<FsOpenV2>(request).Path;
                if (creates && ProtectedNamespaceEntry(path)) throw Error("ACCESS_DENIED", "Namespace and mount entries are immutable.");
            }
            if (request.Version == 1 && request.Method == "fs.rename")
            {
                FsRename rename = ExecutionService.Args<FsRename>(request);
                if (ProtectedNamespaceEntry(rename.Source) || ProtectedNamespaceEntry(rename.Destination)) throw Error("ACCESS_DENIED", "Namespace and mount entries are immutable.");
            }
            admission = await AdmitAsync(request, session, token);

            if (request.Method is "volume.create" or "volume.mount")
            {
                object result = await PasswordOperation(request, session, token);
                await exchange.ReplyAsync(ExecutionService.Success(result));
                return;
            }

            if (request.Method is "fs.read" or "fs.write")
            {
                FsRange args = ExecutionService.Args<FsRange>(request);
                FsWriteV2? options = request.Version == 2 && request.Method == "fs.write" ? ExecutionService.Args<FsWriteV2>(request) : null;
                long offset = FileNumbers.Parse(args.Offset), length = FileNumbers.Parse(args.Length);
                if (length > 65536 || offset > long.MaxValue - length)
                {
                    throw Error("INVALID_ARGUMENT", "Transfer exceeds the bounded range.");
                }

                using IDisposable capacity = await exchange.ReserveStorageBufferAsync(token);
                bool write = request.Method == "fs.write";
                await admission.Gate.WaitAsync(token);
                try
                {
                    Reap();
                    GetHandle(args.Handle, session, !write, write);
                }
                finally
                {
                    admission.Gate.Release();
                }

                byte[] data = new byte[(int)length];
                if (length != 0)
                {
                    await exchange.ReplyAsync(ExecutionService.Success(new StorageStarted([new(1, "data", write ? "requester" : "responder")]), true));
                    streamed = true;
                    if (write)
                    {
                        await exchange.Channel(1).Input.ReadExactlyAsync(data, token);
                        if (await exchange.Channel(1).Input.ReadAsync(new byte[1], token) != 0)
                        {
                            throw Error("INVALID_ARGUMENT", "Write exceeded its declared length.");
                        }
                    }
                }

                bool eof = false;
                await admission.Gate.WaitAsync(token);
                try
                {
                    (Handle handle, Mount mount) = GetHandle(args.Handle, session, !write, write);
                    if (handle.SyntheticPath is not null) throw Error("IS_DIRECTORY", "Cannot transfer directory data.");
                    if (handle.V2 && write && options?.Append != true) Right(handle, "write-data");
                    if (write)
                    {
                        Func<bool> valid = () => !token.IsCancellationRequested && !handle.Revoked && handle.Valid() && Allowed(session, mount, "write");
                        mount.Volume!.Atomic(() =>
                        {
                            if (options?.Append == true) offset = FileNumbers.Parse(mount.Volume.Stat(handle.Entry).Length);
                            if (options?.Constrained == true)
                            {
                                long available = Math.Max(0, FileNumbers.Parse(mount.Volume.Stat(handle.Entry).Length) - offset);
                                if (data.Length > available) data = data[..(int)available];
                            }
                            if (offset > long.MaxValue - data.Length) throw Error("INVALID_ARGUMENT", "Transfer overflows file range.");
                            CheckRange(handle, mount, offset, data.Length, true);
                            mount.Volume.Write(handle.Entry, offset, data, valid);
                            return 0;
                        }, valid);
                    }
                    else
                    {
                        CheckRange(handle, mount, offset, length, false);
                        data = mount.Volume!.Read(handle.Entry, offset, (int)length);
                        eof = offset >= FileNumbers.Parse(mount.Volume.Stat(handle.Entry).Length) - data.Length;
                    }
                }
                finally
                {
                    admission.Gate.Release();
                }

                if (streamed && !write)
                {
                    if (data.Length != 0)
                    {
                        await exchange.Channel(1).SendAsync(data, token);
                    }

                    await exchange.Channel(1).EndAsync();
                }

                RpcResponse completion = ExecutionService.Success(new FsTransferred(FileNumbers.Format(data.Length), eof));
                if (streamed)
                {
                    await exchange.CompleteAsync(completion);
                }
                else
                {
                    await exchange.ReplyAsync(completion);
                }

                return;
            }

            await admission.Gate.WaitAsync(token);
            object response;
            try
            {
                Reap();
                response = request.Version == 2 ? UnaryV2(request, session, token) : Unary(request, session, token);
            }
            finally
            {
                admission.Gate.Release();
            }

            await exchange.ReplyAsync(ExecutionService.Success(response));
        }
        catch (Exception ex) when (ex is KernelOperationException or SqliteException or IOException or ArgumentException or OperationCanceledException or UnauthorizedAccessException)
        {
            string code = ex switch
            {
                KernelOperationException known => known.Code,
                OperationCanceledException => "CANCELLED",
                SqliteException sql when sql.SqliteErrorCode == 13 => "DISK_FULL",
                IOException io when (io.HResult & 0xffff) is 32 or 33 => "VOLUME_BUSY",
                SqliteException sql when sql.SqliteErrorCode is 11 or 26 => "CORRUPT_VOLUME",
                ProtocolException => "INVALID_ARGUMENT",
                ArgumentException => "INVALID_ARGUMENT",
                UnauthorizedAccessException => "ACCESS_DENIED",
                _ => "IO_ERROR"
            };
            RpcResponse error = new RpcResponse(false, false, null, new RpcError(code, ex is KernelOperationException ? ex.Message : "Storage operation failed; no mutation will be replayed.", ex is KernelOperationException ? "not_started" : "unknown"));
            if (!exchange.ConnectionClosed)
            {
                if (streamed)
                {
                    if (request.Method == "fs.read")
                    {
                        await exchange.Channel(1).EndAsync();
                    }

                    await exchange.CompleteAsync(error);
                }
                else
                {
                    await exchange.ReplyAsync(error);
                }
            }
        }
        finally
        {
            admission?.Dispose();
        }
    }

    private async Task<object> PasswordOperation(RpcRequest request, OperatorSession session, CancellationToken token)
    {
        Admin(session);
        if (!_passwords.Wait(0))
        {
            throw Error("RESOURCE_UNAVAILABLE", "Unlock capacity is busy.");
        }

        try
        {
            lock (_failures)
            {
                while (_failures.TryPeek(out var old) && old.Time < DateTimeOffset.UtcNow.AddMinutes(-1))
                {
                    _failures.Dequeue();
                }

                if (_failures.Count >= 20 || _failures.Count(f => f.Principal == session.Principal) >= 5)
                {
                    throw Error("RESOURCE_UNAVAILABLE", "Unlock attempts are throttled.");
                }
            }

            try
            {
                if (request.Method == "volume.create")
                {
                    VolumeCreateRequest args = ExecutionService.Args<VolumeCreateRequest>(request);
                    return new VolumeCreated(await Task.Run(() => EncryptedVolume.Create(args.Path, args.Password, point =>
                    {
                        token.ThrowIfCancellationRequested();
                        Admin(session);
                        _fault?.Invoke(point);
                    }), token));
                }

                VolumeMountRequest mountArgs = ExecutionService.Args<VolumeMountRequest>(request);
                string path = VolumeNames.Normalize(mountArgs.Mount);
                if (path.Split('/').Length != 3 || !path.StartsWith("/vol/", StringComparison.Ordinal))
                {
                    throw Error("INVALID_PATH", "Mount directly under /vol with one name.");
                }

                await _gate.WaitAsync(token);
                try
                {
                    if (_mounts.TryGetValue(path, out Mount? prior) && prior.Volume is not null)
                    {
                        throw Error("ALREADY_EXISTS", "Mount is already unlocked.");
                    }

                    if (prior is not null) path = prior.Info.Mount;
                    if (prior is null && _mounts.Count >= 32)
                    {
                        throw Error("RESOURCE_UNAVAILABLE", "Mount limit reached.");
                    }

                    EncryptedVolume volume = await Task.Run(() => EncryptedVolume.Mount(mountArgs.Path, mountArgs.Password, token, _fault), token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        Admin(session);
                        if (prior is not null && prior.Info.Id != volume.Id || _mounts.Values.Any(m => m.Info.Id == volume.Id && !string.Equals(m.Info.Mount, path, StringComparison.OrdinalIgnoreCase)))
                        {
                            throw Error("ALREADY_EXISTS", "Volume identity is already configured or does not match.");
                        }

                        volume.Fault = _fault;
                        var info = new VolumeInfo(volume.Id, volume.Path, path, "mounted", volume.Generation);
                        _store.SaveVolumeMount(info);
                        _mounts[path] = new(info, volume);
                        _log($"Volume {info.Id} mounted.");
                        return info;
                    }
                    catch
                    {
                        volume.Dispose();
                        throw;
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch
            {
                _log("Volume unlock/create rejected.");
                lock (_failures)
                {
                    if (_failures.Count < 20)
                    {
                        _failures.Enqueue((DateTimeOffset.UtcNow, session.Principal));
                    }
                }

                throw;
            }
        }
        finally
        {
            _passwords.Release();
        }
    }

    private object Unary(RpcRequest request, OperatorSession session, CancellationToken token)
    {
        if (request.Method.StartsWith("volume.", StringComparison.Ordinal))
        {
            Admin(session);
            if (request.Method == "volume.list")
            {
                return _mounts.Values.Select(m => m.Info).ToArray();
            }

            if (request.Method == "volume.unmount")
            {
                string path = VolumeNames.Normalize(ExecutionService.Args<FsPath>(request).Path);
                if (!_mounts.TryGetValue(path, out Mount? mounted))
                {
                    throw Error("NOT_FOUND", "Mount is not configured.");
                }

                path = mounted.Info.Mount;
                if (_handles.Values.Any(h => string.Equals(h.Mount, path, StringComparison.OrdinalIgnoreCase)) || mounted.Operations.CurrentCount != 64)
                {
                    throw Error("VOLUME_BUSY", "Close handles and finish outstanding storage operations first.");
                }

                mounted.Volume?.Flush();
                mounted.Volume?.Checkpoint();
                mounted.Volume?.Dispose();
                // Leave configuration locked if metadata publication fails after close.
                _mounts[path] = new(mounted.Info with { State = "locked", Generation = "" }, null);
                _store.RemoveVolumeMount(path);
                _mounts.TryRemove(path, out _);
                return new FsCommitted();
            }

            throw Error("UNSUPPORTED_METHOD", "Unknown volume method.");
        }

        if (request.Method == "fs.stat")
        {
            FsStat args = ExecutionService.Args<FsStat>(request);
            if ((args.Path is null) == (args.Handle is null))
            {
                throw Error("INVALID_ARGUMENT", "Supply exactly one path or handle.");
            }

            if (args.Handle is not null)
            {
                var h = GetHandle(args.Handle, session);
                if (h.Handle.V2) Right(h.Handle, "read-metadata");
                CheckAccess(session, h.Mount, h.Handle.Access != "write", h.Handle.Access == "write");
                return h.Mount.Volume!.Stat(h.Handle.Entry);
            }

            if (IsSynthetic(args.Path!)) return SyntheticStat(VolumeNames.Normalize(args.Path!));

            var resolved = Resolve(args.Path!, session, allowLocked: true);
            CheckAccess(session, resolved.Mount, true, false);
            if (resolved.Relative.Length == 0) return MountStat(resolved.Mount);
            if (resolved.Mount.Volume is null) throw Error("VOLUME_LOCKED", "Volume requires manual unlock.");
            return resolved.Mount.Volume!.Stat(resolved.Mount.Volume.Resolve(resolved.Relative));
        }

        if (request.Method is "fs.close" or "fs.flush" or "fs.truncate")
        {
            string id = request.Method == "fs.truncate" ? ExecutionService.Args<FsTruncate>(request).Handle : ExecutionService.Args<FsHandle>(request).Handle;
            var h = GetHandle(id, session, write: request.Method == "fs.truncate");
            if (request.Method == "fs.close")
            {
                CloseHandle(id);
            }
            else if (request.Method == "fs.flush")
            {
                CheckAccess(session, h.Mount, false, true);
                h.Mount.Volume!.Flush();
            }
            else
            {
                if (h.Handle.V2) Right(h.Handle, "write-data");
                CheckResize(h.Handle, h.Mount, FileNumbers.Parse(ExecutionService.Args<FsTruncate>(request).Length));
                h.Mount.Volume!.Truncate(h.Handle.Entry, FileNumbers.Parse(ExecutionService.Args<FsTruncate>(request).Length), () => !token.IsCancellationRequested && Allowed(session, h.Mount, "write"));
            }

            return new FsCommitted();
        }

        if (request.Method == "fs.list")
        {
            FsList args = ExecutionService.Args<FsList>(request);
            if (args.Limit is < 1 or > 256)
            {
                throw Error("INVALID_ARGUMENT", "Enumeration batch must contain 1 to 256 entries.");
            }

            if (VolumeNames.Normalize(args.Path) == "/")
            {
                return new FsListing(args.Continuation is null ? [SyntheticStat("/vol")] : throw Error("INVALID_ARGUMENT", "Root has no continuation."), null);
            }
            if (VolumeNames.Normalize(args.Path) == "/vol")
            {
                Require(session.Valid());
                var root = new Mount(new("vol", "", "/vol", "mounted", _rootGeneration), null);
                long offset = DecodeCursor(args.Continuation, session.Id, root, 0);
                FsEntry[] all = _mounts.Values.Where(m => Allowed(session, m, "read")).OrderBy(m => m.Info.Mount, StringComparer.OrdinalIgnoreCase).Select(MountStat).ToArray();
                if (offset > all.Length)
                {
                    return new FsListing([], null);
                }

                FsEntry[] batch = all.Skip((int)offset).Take(args.Limit).ToArray();
                return new FsListing(batch, offset + batch.Length < all.Length ? EncodeCursor(session.Id, root, 0, offset + batch.Length) : null);
            }

            var r = Resolve(args.Path, session, permission: "read");
            CheckAccess(session, r.Mount, true, false);
            long parent = r.Mount.Volume!.Resolve(r.Relative);
            long after = DecodeCursor(args.Continuation, session.Id, r.Mount, parent);
            FsEntry[] entries = r.Mount.Volume.List(parent, after, args.Limit);
            return new FsListing(entries, entries.Length == args.Limit ? EncodeCursor(session.Id, r.Mount, parent, FileNumbers.Parse(entries[^1].Id)) : null);
        }

        if (request.Method == "fs.open")
        {
            return OpenFile(ExecutionService.Args<FsOpen>(request), session, token);
        }

        if (request.Method is "fs.mkdir" or "fs.delete")
        {
            if (IsSynthetic(ExecutionService.Args<FsPath>(request).Path)) throw Error("ACCESS_DENIED", "Namespace directories are immutable.");
            var r = Resolve(ExecutionService.Args<FsPath>(request).Path, session, permission: "write");
            CheckAccess(session, r.Mount, false, true);
            Func<bool> valid = () => !token.IsCancellationRequested && Allowed(session, r.Mount, "write");
            if (request.Method == "fs.mkdir")
            {
                r.Mount.Volume!.CreateEntry(r.Relative, true, valid);
            }
            else
            {
                long id = r.Mount.Volume!.Resolve(r.Relative);
                DeleteSharing(r.Mount.Info.Mount, id);
                r.Mount.Volume.Delete(id, valid);
                Invalidate(r.Mount.Info.Mount, id);
                r.Mount.Volume.Maintain();
            }

            return new FsCommitted();
        }

        if (request.Method == "fs.rename")
        {
            FsRename args = ExecutionService.Args<FsRename>(request);
            if (IsSynthetic(args.Source) || IsSynthetic(args.Destination)) throw Error("ACCESS_DENIED", "Namespace directories are immutable.");
            var source = Resolve(args.Source, session, permission: "write");
            var target = Resolve(args.Destination, session, permission: "write");
            if (source.Mount != target.Mount)
            {
                throw Error("NOT_SUPPORTED", "Cross-volume rename is unsupported.");
            }

            CheckAccess(session, source.Mount, false, true);
            long id = source.Mount.Volume!.Resolve(source.Relative);
            DeleteSharing(source.Mount.Info.Mount, id);
            long? replaced = null;
            source.Mount.Volume.Rename(id, target.Relative, args.Replace, existing =>
            {
                DeleteSharing(source.Mount.Info.Mount, existing);
                replaced = existing;
            }, () => !token.IsCancellationRequested && Allowed(session, source.Mount, "write"), 4096 - source.Mount.Info.Mount.Length - 1);
            if (replaced is long removed)
            {
                Invalidate(source.Mount.Info.Mount, removed);
            }

            source.Mount.Volume.Maintain();
            return new FsCommitted();
        }

        throw Error("UNSUPPORTED_METHOD", "Unknown filesystem method.");
    }

    private FsOpened OpenFile(FsOpen args, OperatorSession session, CancellationToken token)
    {
        if (args.Access is not ("read" or "write" or "read-write") || args.Mode is not ("open-existing" or "create-new" or "open-or-create" or "truncate-existing"))
        {
            throw Error("INVALID_ARGUMENT", "Invalid access or open mode.");
        }

        string[] share = args.Share ?? [];
        if (share.Length > 3 || share.Any(s => s is not ("read" or "write" or "delete")) || share.Distinct().Count() != share.Length)
        {
            throw Error("INVALID_ARGUMENT", "Invalid sharing flags.");
        }

        bool read = args.Access != "write", write = args.Access != "read";
        if (args.Mode != "open-existing" && !write)
        {
            throw Error("INVALID_ARGUMENT", "Creation/truncation requires write access.");
        }

        if (IsSynthetic(args.Path)) throw Error("IS_DIRECTORY", "Open v1 supports files only.");
        var r = Resolve(args.Path, session, allowLocked: true);
        CheckAccess(session, r.Mount, read, write);
        if (r.Mount.Volume is null) throw Error("VOLUME_LOCKED", "Volume requires manual unlock.");
        using IDisposable reservation = ReserveHandle(session.Id);

        long entry;
        try
        {
            entry = r.Mount.Volume!.Resolve(r.Relative);
            if (args.Mode == "create-new")
            {
                throw Error("ALREADY_EXISTS", "File already exists.");
            }
        }
        catch (KernelOperationException ex) when (ex.Code == "NOT_FOUND" && args.Mode is "create-new" or "open-or-create")
        {
            entry = r.Mount.Volume!.CreateEntry(r.Relative, false, () => !token.IsCancellationRequested && Allowed(session, r.Mount, "write"));
        }

        if (r.Mount.Volume!.Stat(entry).Directory)
        {
            throw Error("IS_DIRECTORY", "Open supports files only.");
        }

        r.Mount.Volume.RejectPending(entry);
        Sharing(r.Mount.Info.Mount, entry, args.Access, share);
        if (args.Mode == "truncate-existing")
        {
            r.Mount.Volume.Truncate(entry, 0, () => !token.IsCancellationRequested && Allowed(session, r.Mount, "write"));
        }

        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        FsEntry metadata = r.Mount.Volume.Stat(entry);
        r.Mount.Volume.Pin(entry);
        lock (_handles)
        {
            _handles[id] = new(session.Id, r.Mount.Info.Mount, entry, args.Access, share, () => session.Valid() && (!read || Allowed(session, r.Mount, "read")) && (!write || Allowed(session, r.Mount, "write"))) { Generation = r.Mount.Info.Generation };
        }
        return new FsOpened(id, metadata);
    }

    private string EncodeCursor(string session, Mount mount, long parent, long after)
    {
        string payload = $"{session}|{mount.Info.Generation}|{parent}|{after}";
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        return Convert.ToBase64String(bytes) + "." + Convert.ToHexString(HMACSHA256.HashData(_cursorKey, bytes));
    }

    private long DecodeCursor(string? cursor, string session, Mount mount, long parent)
    {
        if (cursor is null)
        {
            return 0;
        }

        try
        {
            if (cursor.Length > 512)
            {
                throw new FormatException();
            }

            string[] parts = cursor.Split('.');
            byte[] bytes = Convert.FromBase64String(parts[0]);
            if (parts.Length != 2 || !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), HMACSHA256.HashData(_cursorKey, bytes)))
            {
                throw new FormatException();
            }

            string prefix = $"{session}|{mount.Info.Generation}|{parent}|";
            string text = Encoding.UTF8.GetString(bytes);
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            return FileNumbers.Parse(text[prefix.Length..]);
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException or ArgumentException)
        {
            throw Error("INVALID_ARGUMENT", "Invalid enumeration continuation.");
        }
    }

    private async Task MaintainAsync()
    {
        int next = 0;
        try
        {
            while (true)
            {
                await Task.Delay(1000, _stopping.Token);
                if (!await _gate.WaitAsync(0, _stopping.Token))
                {
                    continue;
                }

                try
                {
                    Reap();
                    Mount[] mounted = _mounts.Values.Where(m => m.Volume is not null).ToArray();
                    if (mounted.Length > 0)
                    {
                        Mount mount = mounted[next++ % mounted.Length];
                        if (mount.Gate.Wait(0))
                        {
                            try
                            {
                                Reap(mount);
                                mount.Volume!.Maintain();
                            }
                            finally
                            {
                                mount.Gate.Release();
                            }
                        }
                    }

                    if (next == int.MaxValue)
                    {
                        next = 0;
                    }
                }
                catch (Exception ex) when (ex is SqliteException or IOException)
                {
                    _log("Volume maintenance could not complete; recovery data retained.");
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _maintenance.GetAwaiter().GetResult();
        foreach (Mount mount in _mounts.Values)
        {
            mount.Volume?.Dispose();
        }

        _handles.Clear();
        _mounts.Clear();
        CryptographicOperations.ZeroMemory(_cursorKey);
    }
}
