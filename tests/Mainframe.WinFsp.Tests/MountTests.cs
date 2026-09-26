using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using Fsp;
using Mainframe.Client;
using Mainframe.Core;
using Mainframe.Host;
using Mainframe.Protocol;

namespace Mainframe.WinFsp.Tests;

public sealed class MountTests : IAsyncLifetime
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;
    public MountTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;
    private readonly string _temporary = Path.Combine(AppContext.BaseDirectory, "Mainframe-WinFsp-" + Guid.NewGuid().ToString("N"));
    private KernelStore _store = null!;
    private KernelServer _server = null!;
    private KernelClient _admin = null!;
    private KernelClient _client = null!;
    private MainframeFileSystem _filesystem = null!;
    private FileSystemHost _host = null!;
    private string _point = "";
    private string Data => Path.Combine(_point, "vol", "data");
    private string Container => Path.Combine(_temporary, "data.mfv");
    private readonly List<string> _errors = [];
    private bool _failFlush;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_temporary);
        KernelStore.Initialize(Path.Combine(_temporary, "state"), "winfsp-test");
        _store = KernelStore.Open(Path.Combine(_temporary, "state"));
        _server = new(_store, 0, storageFault: point => { if (_failFlush && point == "flush") throw new IOException("Injected flush failure"); });
        _server.Start();
        using var ca = _store.LoadCaCertificate();
        using var identity = _store.LoadOperatorCertificate();
        _admin = await KernelClient.ConnectAsync("127.0.0.1", _server.Port, identity, ca);
        _client = await KernelClient.ConnectFilesystemAsync("127.0.0.1", _server.Port, identity, ca);
        await _admin.CreateVolumeAsync(Container, "test");
        await _admin.MountVolumeAsync(Container, "/vol/data", "test");
        Mount(Path.Combine(_temporary, "mount"), "/");
    }

    private void Mount(string point, string root)
    {
        _point = point;
        _filesystem = new(_client, root, message => { lock (_errors) _errors.Add(message); }) { Trace = _output.WriteLine };
        _host = new(_filesystem);
        Assert.Equal(0, _host.Preflight(point));
        Assert.Equal(0, _host.MountEx(point, 8, _filesystem.Security, false, 0));
    }

    private void Unmount()
    {
        _filesystem.BeginStop();
        _host.Unmount();
        _filesystem.Dispose();
        Assert.Equal(0, _filesystem.OpenHandleCount);
    }

    public async Task DisposeAsync()
    {
        Unmount();
        await _client.DisposeAsync();
        await _admin.DisposeAsync();
        await _server.DisposeAsync();
        _store.Dispose();
        // WinFsp owns only its mount point; the fixture owns this exact temporary directory.
        Directory.Delete(_temporary, true);
    }

    [Fact]
    public async Task ExplorerStyleCopyRenameMetadataDeleteAndSdkReopen()
    {
        Assert.Contains(Path.Combine(_point, "vol"), Directory.GetDirectories(_point));
        Assert.Contains(Data, Directory.GetDirectories(Path.Combine(_point, "vol")));
        string nested = Path.Combine(Data, "Žuti folder");
        Directory.CreateDirectory(nested);
        Assert.True((await _admin.Files.StatAsync("/vol/data/Žuti folder")).Directory);
        Assert.True(Directory.Exists(nested));
        string file = Path.Combine(nested, "hello.txt");
        File.WriteAllText(file, "hello");
        Assert.Equal("hello", File.ReadAllText(file));
        File.AppendAllText(file, " world");
        Assert.Equal("hello world", File.ReadAllText(file));
        File.Copy(file, file + ".copy");
        File.Move(file + ".copy", file + ".moved");
        File.SetAttributes(file, FileAttributes.Hidden);
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.Hidden));
        DateTime stamp = new(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(file));
        await using (var sdk = await _admin.Files.OpenAsync("/vol/data/Žuti folder/hello.txt", "read-write", "open-existing", ["read", "write", "delete"]))
        {
            sdk.SetLength(0);
            await sdk.WriteAsync("from sdk"u8.ToArray());
            await sdk.FlushAsync();
        }
        Assert.Equal("from sdk", File.ReadAllText(file));
        Directory.Delete(nested, true);
        Assert.False(Directory.Exists(nested));
        Assert.Empty(_errors);
    }

    [Fact]
    public void EditorAtomicReplacementAndTruncation()
    {
        string file = Path.Combine(Data, "document.txt"), temporary = file + ".tmp";
        File.WriteAllText(file, "old contents");
        File.WriteAllText(temporary, "new contents");
        File.Move(temporary, file, true);
        Assert.Equal("new contents", File.ReadAllText(file));
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.SetLength(3);
            stream.Flush(true);
        }
        Assert.Equal("new", File.ReadAllText(file));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task LargeTransfersEnumerationAndLongLivedConnection()
    {
        string file = Path.Combine(Data, "large.bin");
        byte[] bytes = new byte[4 * 1024 * 1024 + 17];
        RandomNumberGenerator.Fill(bytes);
        File.WriteAllBytes(file, bytes);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(file)));
        // Populate via SDK to isolate the Windows paged-enumeration path from creation overhead.
        for (int i = 0; i < 270; i++)
        {
            await using var handle = await _admin.Files.OpenAsync($"/vol/data/f{i:000}", "write", "create-new");
        }
        string[] names = Directory.GetFiles(Data).Select(Path.GetFileName).ToArray()!;
        Assert.Equal(271, names.Length);
        Assert.Equal(271, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        string probe = Path.Combine(Data, "retirement-probe");
        File.WriteAllBytes(probe, []);
        using (var handle = File.OpenHandle(probe, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var elapsed = Stopwatch.StartNew();
            for (int i = 0; i < 10060; i++)
            {
                Assert.True(elapsed.Elapsed < TimeSpan.FromMinutes(2), "Filesystem stress run exceeded its deadline.");
                Assert.Equal(0, RandomAccess.GetLength(handle));
            }
        }
        Assert.False(_client.Completion.IsCompleted);
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(file)));
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task LockedVolumesStayVisibleAndCanBeUnlocked()
    {
        await _admin.UnmountVolumeAsync("/vol/data");
        // Restart the host to reload the configured mount in its locked state.
        Unmount();
        await _client.DisposeAsync();
        await _admin.DisposeAsync();
        await _server.DisposeAsync();
        _server = new(_store, 0); _server.Start();
        using var ca = _store.LoadCaCertificate();
        using var identity = _store.LoadOperatorCertificate();
        _admin = await KernelClient.ConnectAsync("127.0.0.1", _server.Port, identity, ca);
        await _admin.MountVolumeAsync(Container, "/vol/data", "test");
        await _admin.DisposeAsync();
        await _server.DisposeAsync();
        _server = new(_store, 0); _server.Start();
        _admin = await KernelClient.ConnectAsync("127.0.0.1", _server.Port, identity, ca);
        _client = await KernelClient.ConnectFilesystemAsync("127.0.0.1", _server.Port, identity, ca);
        Mount(Path.Combine(_temporary, "locked-mount"), "/");
        Assert.Contains(Data, Directory.GetDirectories(Path.Combine(_point, "vol")));
        Assert.Throws<IOException>(() => Directory.GetFiles(Data));
        await _admin.MountVolumeAsync(Container, "/vol/data", "test");
        File.WriteAllText(Path.Combine(Data, "unlocked"), "ok");
    }

    [Fact]
    public void DirectoryMountOfVolumeCanCreateDirectory()
    {
        Unmount();
        Mount(Path.Combine(_temporary, "subtree"), "/vol/data");
        string directory = Path.Combine(_point, "nested");
        Directory.CreateDirectory(directory);
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void DriveLetterSubtreeAndWindowsSharing()
    {
        Unmount();
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char letter = Enumerable.Range('D', 'Z' - 'D' + 1).Select(i => (char)i).Reverse().First(c => !used.Contains(c));
        Mount(letter + ":", "/vol/data");
        Directory.CreateDirectory(letter + @":\folder");
        Assert.True(Directory.Exists(letter + @":\folder"));
        string file = letter + @":\exclusive";
        using (var exclusive = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<IOException>(() => File.OpenRead(file));
        Assert.True(File.Exists(file));
        Assert.False(Directory.Exists(letter + @":\vol"));
    }

    [Fact]
    public async Task SdkLocksAreEnforcedWhenReadsReachBackend()
    {
        string file = Path.Combine(Data, "locked");
        File.WriteAllBytes(file, [1, 2, 3]);
        await using var sdk = await _admin.Files.OpenHandleAsync(new("/vol/data/locked", Rights: ["read-data", "write-data", "read-metadata"], Share: ["read", "write", "delete"]));
        await sdk.LockAsync(0, 3);
        // FILE_FLAG_NO_BUFFERING forces this Windows read through the backend lock check.
        using var handle = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, (FileOptions)0x20000000);
        Assert.Throws<IOException>(() => RandomAccess.Read(handle, new byte[4096], 0));
        await sdk.UnlockAsync(0, 3);
    }

    [Fact]
    public async Task WindowsLocksAndSharingApplyAcrossProcesses()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string file = Path.Combine(Data, "process-lock");
        File.WriteAllBytes(file, new byte[4096]);
        using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            locked.Lock(0, 4096);
            string literal = "'" + file.Replace("'", "''") + "'";
            int exit = await RunPowerShell($"try {{ $s = [IO.File]::Open({literal}, 'Open', 'ReadWrite', 'ReadWrite'); $s.Lock(0,4096); $s.Dispose(); exit 7 }} catch {{ exit 0 }}");
            Assert.Equal(0, exit);
            locked.Unlock(0, 4096);
        }
        using (var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(0, await RunPowerShell($"try {{ $s = [IO.File]::OpenRead('{file.Replace("'", "''")}'); $s.Dispose(); exit 7 }} catch {{ exit 0 }}"));
    }

    private static async Task<int> RunPowerShell(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using Process process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        return process.ExitCode;
    }

    [Fact]
    public void DeleteDispositionCanBeCancelledAndCloseReleasesHandles()
    {
        string file = Path.Combine(Data, "cancel-delete");
        File.WriteAllText(file, "keep");
        using var handle = CreateFile(file, 0x10000 | 0x80, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero);
        Assert.False(handle.IsInvalid);
        byte disposition = 1;
        Assert.True(SetFileInformationByHandle(handle, 4, ref disposition, 1));
        disposition = 0;
        Assert.True(SetFileInformationByHandle(handle, 4, ref disposition, 1));
        handle.Dispose();
        Assert.Equal("keep", File.ReadAllText(file));
    }

    [Fact]
    public void FlushFailuresReachWindowsCaller()
    {
        using var stream = new FileStream(Path.Combine(Data, "flush-failure"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.WriteByte(1);
        stream.Flush(true);
        _failFlush = true;
        try { Assert.Throws<IOException>(() => stream.Flush(true)); }
        finally { _failFlush = false; }
    }

    [Fact]
    public async Task ConnectionCompletionSignalsIdleHostLoss()
    {
        await _server.DisposeAsync();
        await _client.Completion.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.ThrowsAny<IOException>(() => File.ReadAllText(Path.Combine(Data, "disconnected")));
    }

    [Fact]
    public void OpenHandleRemainsReadableAfterCleanupUntilFinalClose()
    {
        File.WriteAllText(Path.Combine(Data, "retained"), "abc");
        Assert.Equal(0, _filesystem.Open(@"\vol\data\retained", 0, 0x81, out var node, out var descriptor, out _, out _));
        _filesystem.Cleanup(node, descriptor, @"\vol\data\retained", 0);
        IntPtr buffer = Marshal.AllocHGlobal(3);
        try
        {
            Assert.Equal(0, _filesystem.Read(node, descriptor, buffer, 0, 3, out uint read));
            Assert.Equal(3u, read);
        }
        finally { Marshal.FreeHGlobal(buffer); _filesystem.Close(node, descriptor); }
        Assert.Empty(_errors);
    }

    [Fact]
    public async Task ForegroundCommandCtrlCDrainsAndReleasesAnOpenFile()
    {
        string point = Path.Combine(_temporary, "cli-mount");
        using var adapter = new AdapterProcess(point, Path.Combine(_temporary, "state"), _server.Port);
        await adapter.ReadyAsync(point);
        using var stream = new FileStream(Path.Combine(point, "vol", "data", "open-at-stop"), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        stream.Write("persisted before stop"u8);
        stream.Flush(true);
        await adapter.InterruptAsync();
        Assert.Equal(0, adapter.ExitCode);
        Assert.False(Directory.Exists(point));
        Assert.Equal("persisted before stop", File.ReadAllText(Path.Combine(Data, "open-at-stop")));
    }

    [Fact]
    public async Task ForegroundCommandUnmountsAfterHostLoss()
    {
        string point = Path.Combine(_temporary, "cli-disconnect");
        using var adapter = new AdapterProcess(point, Path.Combine(_temporary, "state"), _server.Port);
        await adapter.ReadyAsync(point);
        await _server.DisposeAsync();
        await adapter.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(1, adapter.ExitCode);
        Assert.False(Directory.Exists(point));
    }

    [Fact]
    public async Task KilledAdapterReleasesBackendHandles()
    {
        string point = Path.Combine(_temporary, "cli-killed");
        using var adapter = new AdapterProcess(point, Path.Combine(_temporary, "state"), _server.Port);
        await adapter.ReadyAsync(point);
        using var stream = new FileStream(Path.Combine(point, "vol", "data", "kill"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        stream.WriteByte(42); stream.Flush(true);
        adapter.Process.Kill();
        await adapter.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        stream.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            try { await _admin.UnmountVolumeAsync("/vol/data", timeout.Token); break; }
            catch (KernelRpcException ex) when (ex.Code == "VOLUME_BUSY") { await Task.Delay(100, timeout.Token); }
        }
        await _admin.MountVolumeAsync(Container, "/vol/data", "test");
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(Data, "kill")));
    }

    [Fact]
    public void RevokedGrantsInvalidateBackendHandles()
    {
        File.WriteAllText(Path.Combine(Data, "revoked"), "private");
        Assert.Equal(0, _filesystem.Open(@"\vol\data\revoked", 0, 0x81, out var node, out var descriptor, out _, out _));
        using var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_temporary, "state", "kernel.db"), Pooling = false }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "DELETE FROM grants WHERE principal='local-admin'";
        command.ExecuteNonQuery();
        try { Assert.Equal(FileSystemBase.STATUS_INVALID_HANDLE, _filesystem.GetFileInfo(node, descriptor, out _)); }
        finally
        {
            command.CommandText = "INSERT INTO grants VALUES('local-admin','*')";
            command.ExecuteNonQuery();
            _filesystem.Close(node, descriptor);
        }
    }

    [Fact]
    public async Task RevokedCertificateEndsFilesystemSession()
    {
        using var certificate = _store.LoadOperatorCertificate();
        _store.RevokeOperator(CertificateTrust.Fingerprint(certificate));
        Assert.NotEqual(0, _filesystem.GetVolumeInfo(out _));
        await _client.Completion.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task ForegroundCommandRejectsUntrustedCredentialsBeforeMount()
    {
        string other = Path.Combine(_temporary, "other-state");
        KernelStore.Initialize(other, "untrusted");
        string point = Path.Combine(_temporary, "untrusted-mount");
        using var adapter = new AdapterProcess(point, other, _server.Port);
        await adapter.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(1, adapter.ExitCode);
        Assert.False(Directory.Exists(point));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass, ref byte information, uint size);
}
