using System.Text;
using System.Diagnostics;
using Mainframe.Client;
using Mainframe.Core;
using Mainframe.Host;
using Mainframe.Protocol;

namespace Mainframe.Tests;

public sealed partial class StorageIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Mainframe-StorageRpc-" + Guid.NewGuid().ToString("N"));
    private const string Password = "storage test password only";
    private KernelStore _store = null!;
    private KernelServer _server = null!;
    private KernelClient _client = null!;
    private VolumeInfo _volume = null!;
    private string Container => Path.Combine(_root, "data.mfv");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        KernelStore.Initialize(Path.Combine(_root, "state"), "storage-test");
        _store = KernelStore.Open(Path.Combine(_root, "state"));
        await Start();
        await _client.CreateVolumeAsync(Container, Password);
        _volume = await _client.MountVolumeAsync(Container, "/vol/data", Password);
    }

    private async Task Start(Action<string>? storageFault = null)
    {
        _server = new(_store, 0, storageFault: storageFault);
        _server.Start();
        _client = await Connect();
    }

    private async Task<KernelClient> Connect()
    {
        using var ca = _store.LoadCaCertificate();
        using var certificate = _store.LoadOperatorCertificate();
        return await KernelClient.ConnectAsync("127.0.0.1", _server.Port, certificate, ca);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        _store.Dispose();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task BlockedVolumeHasBoundedQueueAndDoesNotBlockAnotherVolume()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int armed = 0;
        await Start(point =>
        {
            if (point == "before-commit" && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(20)))
                {
                    throw new IOException("Test did not release the blocked commit.");
                }
            }
        });
        await _client.MountVolumeAsync(Container, "/vol/data", Password);
        string second = Path.Combine(_root, "second.mfv");
        await _client.CreateVolumeAsync(second, Password);
        await _client.MountVolumeAsync(second, "/vol/second", Password);
        Interlocked.Exchange(ref armed, 1);
        Task<KernelFileStream> opening = _client.Files.OpenAsync("/vol/data/blocked", "write", "create-new");
        Task[] queued = [];
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = Enumerable.Range(0, 64).Select(async _ =>
            {
                try
                {
                    await _client.Files.StatAsync("/vol/data/blocked");
                }
                catch (KernelRpcException ex)
                {
                    Assert.Equal("RESOURCE_UNAVAILABLE", ex.Code);
                }
            }).ToArray();
            // One create plus 63 queued operations fills this volume's admission bound.
            await (await Task.WhenAny(queued).WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("VOLUME_BUSY", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.UnmountVolumeAsync("/vol/data"))).Code);
            await _client.Files.CreateDirectoryAsync("/vol/second/independent").WaitAsync(TimeSpan.FromSeconds(5));
            await using (KernelFileStream other = await _client.Files.OpenAsync("/vol/second/independent/file", "write", "create-new").WaitAsync(TimeSpan.FromSeconds(5)))
            {
                await other.WriteAsync(new byte[] { 4, 5, 6 });
            }
            await _client.UnmountVolumeAsync("/vol/second").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(opening.IsCompleted);
        }
        finally
        {
            release.Set();
            await using KernelFileStream file = await opening;
            await Task.WhenAll(queued);
        }
    }

    [Fact]
    public async Task SdkStreamsPersistThroughLockedRestartAndEnforceSharing()
    {
        await _client.Files.CreateDirectoryAsync("/vol/data/folder");
        byte[] bytes = Enumerable.Range(0, 180000).Select(i => (byte)(i % 253)).ToArray();
        await using (KernelFileStream file = await _client.Files.OpenAsync("/vol/data/folder/input", "read-write", "create-new"))
        {
            await file.WriteAsync(bytes);
            await file.FlushAsync();
            Assert.Equal(bytes.Length, file.Length);
            Assert.Equal(bytes.Length, file.Position);
            Assert.Equal("VOLUME_BUSY", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.UnmountVolumeAsync("/vol/DATA"))).Code);
            Assert.Equal("SHARING_VIOLATION", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.OpenAsync("/vol/data/folder/input"))).Code);
            file.Seek(0, SeekOrigin.Begin);
            using var output = new MemoryStream();
            await file.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
        }

        await _client.Files.RenameAsync("/vol/data/folder/input", "/vol/data/folder/result");
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        await Start();
        Assert.Equal("locked", Assert.Single(await _client.ListVolumesAsync()).State);
        Assert.Equal("VOLUME_LOCKED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.OpenAsync("/vol/data/folder/result"))).Code);
        Assert.Equal("UNLOCK_FAILED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.MountVolumeAsync(Container, "/vol/data", "wrong secret"))).Code);
        await _client.MountVolumeAsync(Container, "/vol/data", Password);
        await using (KernelFileStream file = await _client.Files.OpenAsync("/vol/data/folder/result"))
        {
            using var output = new MemoryStream();
            await file.CopyToAsync(output);
            Assert.Equal(bytes, output.ToArray());
        }

        await _client.UnmountVolumeAsync("/vol/data");
        Assert.False(File.Exists(Container + "-wal"));
        string copied = Path.Combine(_root, "copied.mfv");
        File.Copy(Container, copied);
        await _client.MountVolumeAsync(copied, "/vol/copied", Password);
        Assert.Single((await _client.Files.ListAsync("/vol/copied/folder")).Entries);
    }

    [Fact]
    public async Task StandaloneStorageProgramsUseScopedSdkAndCatIsBinaryExact()
    {
        string prefix = $"volume:{_volume.Id}:";
        foreach (string name in new[]
        {
            "StorageDemo",
            "Ls",
            "Cat"
        })
        {
            await _client.RegisterProgramAsync(new(1, "examples/" + name.ToLowerInvariant(), "1.0.0", Path.Combine(AppContext.BaseDirectory, "Mainframe." + name + ".exe"), [], _root, ["windows"], ["x64"], ["pipes"], [], [prefix + "read", prefix + "write"], []));
        }

        var demo = await Run("storagedemo", "/vol/data");
        Assert.Equal(0, demo.Exit);
        Assert.Empty(demo.Error);
        string path = Encoding.UTF8.GetString(demo.Output).Trim();
        var cat = await Run("cat", path);
        Assert.Equal(0, cat.Exit);
        Assert.Empty(cat.Error);
        Assert.Equal(Enumerable.Range(0, 150000).Select(i => (byte)(i % 251)).ToArray(), cat.Output);
        var ls = await Run("ls", path[..path.LastIndexOf('/')]);
        Assert.Equal(0, ls.Exit);
        Assert.Contains("data.bin", Encoding.UTF8.GetString(ls.Output));
        await _client.RegisterProgramAsync(new(1, "examples/denied", "1.0.0", Path.Combine(AppContext.BaseDirectory, "Mainframe.Cat.exe"), [], _root, ["windows"], ["x64"], ["pipes"], [], [], []));
        var denied = await Run("denied", path);
        Assert.Equal(1, denied.Exit);
        Assert.Empty(denied.Output);
        Assert.NotEmpty(denied.Error);
    }

    private async Task<(byte[] Output, string Error, int Exit)> Run(string name, params string[] args)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        KernelInvocation call = await _client.StartProcessAsync(new(name, args), timeout.Token);
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        Task copy = Task.WhenAll(call.Stdout.CopyToAsync(output, timeout.Token), call.Stderr!.CopyToAsync(error, timeout.Token));
        await call.EndInputAsync();
        ProcessExited exit = await call.WaitAsync(timeout.Token);
        await copy;
        return (output.ToArray(), Encoding.UTF8.GetString(error.ToArray()), exit.ExitCode);
    }

    [Theory]
    [InlineData("before-commit", false, false)]
    [InlineData("after-commit", true, false)]
    [InlineData("before-commit", false, true)]
    [InlineData("after-commit", true, true)]
    public async Task KilledHostNeverExposesTornWrite(string point, bool committed, bool rename)
    {
        await using (var file = await _client.Files.OpenAsync("/vol/data/file", "write", "create-new"))
            await file.WriteAsync(new byte[] { 1, 2, 3 });
        if (rename)
        {
            await using (var temp = await _client.Files.OpenAsync("/vol/data/temp", "write", "create-new"))
                await temp.WriteAsync(new byte[] { 9, 8, 7 });
        }

        await _client.DisposeAsync();
        await _server.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Mainframe.Fixtures.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[]
        {
            "storage-host",
            Path.Combine(_root, "state"),
            point
        })
        {
            start.ArgumentList.Add(arg);
        }

        using Process child = Process.Start(start)!;
        try
        {
            string ready = (await child.StandardOutput.ReadLineAsync(timeout.Token))!;
            Assert.StartsWith("READY:", ready);
            using var ca = _store.LoadCaCertificate();
            using var cert = _store.LoadOperatorCertificate();
            await using KernelClient remote = await KernelClient.ConnectAsync("127.0.0.1", int.Parse(ready[6..]), cert, ca, timeout.Token);
            await remote.MountVolumeAsync(Container, "/vol/data", Password, timeout.Token);
            Task completion;
            if (rename)
            {
                completion = remote.Files.RenameAsync("/vol/data/temp", "/vol/data/file", true, timeout.Token);
            }
            else
            {
                FsOpened opened = KernelClient.Decode<FsOpened>(await remote.CallAsync("fs.open", new FsOpen("/vol/data/file", "write"), timeout.Token));
                KernelInvocation call = await remote.BeginAsync("fs.write", new FsRange(opened.Handle, "0", "3"), timeout.Token);
                await call.Exchange.Channel(1).SendAsync(new byte[] { 9, 8, 7 });
                await call.Exchange.Channel(1).EndAsync();
                completion = call.Exchange.Completion.Task;
            }

            Assert.Equal("POINT:" + point, await child.StandardOutput.ReadLineAsync(timeout.Token));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<IOException>(() => completion);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }

            await Start();
        }

        Assert.Equal("locked", Assert.Single(await _client.ListVolumesAsync()).State);
        await _client.MountVolumeAsync(Container, "/vol/data", Password);
        await using var reopened = await _client.Files.OpenAsync("/vol/data/file");
        byte[] output = new byte[3];
        await reopened.ReadExactlyAsync(output);
        Assert.Equal(committed ? new byte[] { 9, 8, 7 } : new byte[] { 1, 2, 3 }, output);
    }

    [Theory]
    [InlineData("create-committed")]
    [InlineData("create-publish")]
    [InlineData("checkpoint")]
    public async Task KilledHostPreservesCreationAndCheckpointRecovery(string point)
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Mainframe.Fixtures.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[]
        {
            "storage-host",
            Path.Combine(_root, "state"),
            point
        })
        {
            start.ArgumentList.Add(arg);
        }

        using Process child = Process.Start(start)!;
        string createdPath = Path.Combine(_root, "crash-create.mfv");
        try
        {
            string ready = (await child.StandardOutput.ReadLineAsync(timeout.Token))!;
            using var ca = _store.LoadCaCertificate();
            using var cert = _store.LoadOperatorCertificate();
            await using KernelClient remote = await KernelClient.ConnectAsync("127.0.0.1", int.Parse(ready[6..]), cert, ca, timeout.Token);
            if (point == "checkpoint")
            {
                await remote.MountVolumeAsync(Container, "/vol/data", Password);
            }

            Task operation = point == "checkpoint" ? remote.UnmountVolumeAsync("/vol/data", timeout.Token) : remote.CreateVolumeAsync(createdPath, Password, timeout.Token);
            Assert.Equal("POINT:" + point, await child.StandardOutput.ReadLineAsync(timeout.Token));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<IOException>(() => operation);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync();
            }

            await Start();
        }

        Assert.False(File.Exists(createdPath));
        if (point != "checkpoint")
        {
            Assert.Single(Directory.GetDirectories(_root, ".mfv-create-*"));
        }

        await _client.MountVolumeAsync(Container, "/vol/data", Password);
        Assert.Empty((await _client.Files.ListAsync("/vol/data")).Entries);
    }

    [Fact]
    public async Task EnumerationTokensAndReadOnlyProgramScopesAreEnforced()
    {
        for (int i = 0; i < 4; i++)
        {
            await _client.Files.CreateDirectoryAsync("/vol/data/dir" + i);
        }

        FsListing first = await _client.Files.ListAsync("/vol/data", 2);
        Assert.Equal(2, first.Entries.Length);
        Assert.NotNull(first.Continuation);
        FsListing second = await _client.Files.ListAsync("/vol/data", 2, first.Continuation);
        Assert.Equal(2, second.Entries.Length);
        Assert.Empty(first.Entries.Select(e => e.Id).Intersect(second.Entries.Select(e => e.Id)));
        await using KernelClient other = await Connect();
        Assert.Equal("INVALID_ARGUMENT", (await Assert.ThrowsAsync<KernelRpcException>(() => other.Files.ListAsync("/vol/data", 2, first.Continuation))).Code);
        await _client.RegisterProgramAsync(new(1, "examples/readonly", "1.0.0", Path.Combine(AppContext.BaseDirectory, "Mainframe.StorageDemo.exe"), [], _root, ["windows"], ["x64"], ["pipes"], [], [$"volume:{_volume.Id}:read"], []));
        var denied = await Run("readonly", "/vol/data");
        Assert.Equal(1, denied.Exit);
        Assert.Equal(4, (await _client.Files.ListAsync("/vol/data")).Entries.Length);
    }

    [Fact]
    public async Task ProgramsCannotAdministerVolumesAndRevokedHandlesAreRejected()
    {
        await _client.RegisterProgramAsync(new(1, "fixtures/adminprobe", "1.0.0", Path.Combine(AppContext.BaseDirectory, "Mainframe.Fixtures.exe"), ["storage-admin"], _root, ["windows"], ["x64"], ["pipes"], [], [$"volume:{_volume.Id}:read"], []));
        var probe = await Run("adminprobe");
        Assert.Equal(0, probe.Exit);
        Assert.Contains("ACCESS_DENIED", Encoding.UTF8.GetString(probe.Output));
        FsOpened opened = KernelClient.Decode<FsOpened>(await _client.CallAsync("fs.open", new FsOpen("/vol/data/file", "write", "create-new")));
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state", "kernel.db"), Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM grants WHERE principal='local-admin'";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.CallAsync("fs.truncate", new FsTruncate(opened.Handle, "1")))).Code);
    }

    [Fact]
    public async Task OverlongAndCancelledWritesDoNotCommit()
    {
        FsOpened opened = KernelClient.Decode<FsOpened>(await _client.CallAsync("fs.open", new FsOpen("/vol/data/file", "write", "create-new")));
        KernelInvocation overlong = await _client.BeginAsync("fs.write", new FsRange(opened.Handle, "0", "1"));
        await overlong.Exchange.Channel(1).SendAsync(new byte[] { 1, 2 });
        await overlong.Exchange.Channel(1).EndAsync();
        Assert.False((await overlong.Exchange.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        KernelInvocation cancelled = await _client.BeginAsync("fs.write", new FsRange(opened.Handle, "0", "3"));
        await cancelled.Exchange.Channel(1).SendAsync(new byte[] { 1 });
        await cancelled.CancelAsync();
        Assert.False((await cancelled.Exchange.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10))).Ok);
        Assert.Equal("0", (await _client.Files.StatAsync("/vol/data/file")).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(2048)]
    public async Task VolumeCliMasksPasswordsInRealConPty(int passwordLength)
    {
        string password = new('q', passwordLength);
        string target = Path.Combine(_root, "cli.mfv");
        var manifest = new ProgramManifest(1, "fixtures/cli", "1.0.0", Path.Combine(AppContext.BaseDirectory, "Mainframe.Fixtures.exe"), ["cli-harness", "volume", "create", target, "--state", Path.Combine(_root, "state"), "--endpoint", $"127.0.0.1:{_server.Port}"], _root, ["windows"], ["x64"], ["terminal"], [], [], []);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var process = WindowsProcess.Start(manifest, [], _root, true, 100, 30, "unused");
        process.Bootstrap.Dispose();
        using var reader = new StreamReader(process.Stdout);
        var text = new StringBuilder();
        async Task Until(string expected)
        {
            char[] buffer = new char[512];
            while (!text.ToString().Contains(expected, StringComparison.Ordinal))
            {
                int count = await reader.ReadAsync(buffer, timeout.Token);
                Assert.True(count > 0, text.ToString());
                text.Append(buffer, 0, count);
            }
        }

        await Until("Password: ");
        await process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(password + "\r"), timeout.Token);
        await process.Stdin.FlushAsync();
        await Until("Confirm password: ");
        await process.Stdin.WriteAsync(Encoding.UTF8.GetBytes(password + "\r"), timeout.Token);
        await process.Stdin.FlushAsync();
        await Until("MODES_RESTORED=True");
        Assert.Equal(0, await process.WaitAsync(timeout.Token));
        Task<string> drain = reader.ReadToEndAsync(timeout.Token);
        await process.CloseTerminalAsync();
        text.Append(await drain);
        if (passwordLength > 0)
        {
            Assert.DoesNotContain(password, text.ToString());
        }
        await _client.MountVolumeAsync(target, "/vol/cli", password);
    }

    [Fact]
    public async Task PasswordRpcPreservesLongUnicodeAndControlCharacters()
    {
        string password = new string('λ', 2048) + "\0\r\n😀'\" ";
        string target = Path.Combine(_root, "unicode.mfv");
        VolumeCreated created = await _client.CreateVolumeAsync(target, password);
        VolumeInfo mounted = await _client.MountVolumeAsync(target, "/vol/unicode", password);
        Assert.Equal(created.Id, mounted.Id);
        await _client.UnmountVolumeAsync("/vol/unicode");
    }

    [Fact]
    public async Task ShortWritesRollbackAndDisconnectedHandlesAreInvalid()
    {
        FsOpened opened = KernelClient.Decode<FsOpened>(await _client.CallAsync("fs.open", new FsOpen("/vol/data/file", "read-write", "create-new")));
        KernelInvocation call = await _client.BeginAsync("fs.write", new FsRange(opened.Handle, "0", "10"));
        await call.Exchange.Channel(1).SendAsync(new byte[] { 1, 2 });
        await call.Exchange.Channel(1).EndAsync();
        RpcResponse completion = await call.Exchange.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(completion.Ok);
        Assert.Equal("0", (await _client.Files.StatAsync("/vol/data/file")).Length);
        await using KernelClient other = await Connect();
        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(() => other.CallAsync("fs.stat", new FsStat(Handle: opened.Handle)))).Code);
        await _client.DisposeAsync();
        _client = await Connect();
        await _client.UnmountVolumeAsync("/vol/data");
    }
}
