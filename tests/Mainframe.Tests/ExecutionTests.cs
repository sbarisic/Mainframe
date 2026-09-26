using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Mainframe.Client;
using Mainframe.Core;
using Mainframe.Host;
using Mainframe.Protocol;
using Microsoft.Data.Sqlite;

namespace Mainframe.Tests;

public sealed class ExecutionTests(Xunit.Abstractions.ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Mainframe-Execution-" + Guid.NewGuid().ToString("N"));
    private KernelStore store = null!;
    private KernelServer server = null!;
    private KernelClient client = null!;
    private readonly AdjustableClock clock = new();
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
    private static string FixturePath
    {
        get
        {
            return Path.Combine(AppContext.BaseDirectory, "Mainframe.Fixtures.exe");
        }
    }

    private ProgramManifest Manifest(string mode, string identity = "fixtures/test", string version = "1.0.0") => new(1, identity, version, FixturePath, [mode], root, ["windows"], ["x64"], ["pipes", "terminal"], [], ["kernel.describe"], ["workspace"]);
    private ProgramManifest HelloManifest() => Manifest("unused") with
    {
        Executable = Path.Combine(AppContext.BaseDirectory, "Mainframe.Hello.exe"),
        FixedArguments = []
    };
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(root);
        KernelStore.Initialize(Path.Combine(root, "state"), "execution-test");
        store = KernelStore.Open(Path.Combine(root, "state"));
        server = new(store, 0, output.WriteLine, clock);
        server.Start();
        using X509Certificate2 ca = store.LoadCaCertificate();
        using X509Certificate2 cert = store.LoadOperatorCertificate();
        client = await KernelClient.ConnectAsync("127.0.0.1", server.Port, cert, ca, deadline.Token);
    }

    public async Task DisposeAsync()
    {
        await client.DisposeAsync();
        await server.DisposeAsync();
        store.Dispose();
        deadline.Dispose();
        string resolved = Path.GetFullPath(root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Mainframe-Execution-"))
            throw new InvalidOperationException();
        Directory.Delete(resolved, true);
    }

    private async Task<(string Output, string Error, int Code)> Collect(KernelInvocation call, byte[]? input = null)
    {
        using var output = new MemoryStream();
        using var error = new MemoryStream();
        var outputs = Task.WhenAll(call.Stdout.CopyToAsync(output, deadline.Token), call.Stderr?.CopyToAsync(error, deadline.Token) ?? Task.CompletedTask);
        if (input is not null)
            await call.WriteInputAsync(input, deadline.Token);
        await call.EndInputAsync();
        ProcessExited result = await call.WaitAsync(deadline.Token);
        await outputs;
        return (Encoding.UTF8.GetString(output.ToArray()), Encoding.UTF8.GetString(error.ToArray()), result.ExitCode);
    }

    [Fact]
    public async Task OrdinaryProgramPreservesArgumentsAndExitCode()
    {
        await client.RegisterProgramAsync(Manifest("args"));
        string[] args = ["", "hello world", "a\"b", "C:\\path with spaces\\", "--name", "--help", "λ"];
        (string Output, string Error, int Code) result = await Collect(await client.StartProcessAsync(new("test", args), deadline.Token));
        Assert.Equal(args, JsonSerializer.Deserialize<string[]>(result.Output.Trim()));
        Assert.Equal(19, result.Code);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task ByteStreamsAndEofAreIndependentAndLossless()
    {
        await client.RegisterProgramAsync(Manifest("copy"));
        byte[] bytes = Enumerable.Range(0, 262144).Select(i => (byte)(i % 127)).ToArray();
        (string Output, string Error, int Code) result = await Collect(await client.StartProcessAsync(new("test", []), deadline.Token), bytes);
        Assert.Equal(Encoding.UTF8.GetString(bytes), result.Output);
        Assert.Equal("stderr-exact", result.Error);
        Assert.Equal(23, result.Code);
    }

    [Fact]
    public async Task SdkUsesScopedBootstrapAndNamedWorkingDirectory()
    {
        await client.RegisterProgramAsync(HelloManifest());
        await client.AddHostRootAsync(new("workspace", root));
        ShellState shell = await client.OpenShellAsync();
        KernelInvocation cd = await client.ExecuteShellAsync(new(shell.SessionId, "cd /host/workspace"));
        Assert.Equal("/host/workspace", KernelClient.Decode<ShellState>(cd.Response.Result!.Value).WorkingDirectory);
        (string Output, string Error, int Code) result = await Collect(await client.StartProcessAsync(new("test", ["Alice"], SessionId: shell.SessionId), deadline.Token));
        Assert.Contains("Hello Alice from execution-test", result.Output);
        Assert.Contains(root, result.Output);
        Assert.Equal(0, result.Code);
    }

    [Fact]
    public async Task SdkRejectsUndeclaredPermission()
    {
        await client.RegisterProgramAsync(Manifest("denied"));
        (string Output, string Error, int Code) result = await Collect(await client.StartProcessAsync(new("test", []), deadline.Token));
        Assert.Contains("ACCESS_DENIED", result.Output);
        Assert.Equal(0, result.Code);
    }

    [Fact]
    public async Task AmbiguousNamesRequireQualificationAndRemovedRootsFail()
    {
        ProgramRegistration first = await client.RegisterProgramAsync(Manifest("args"));
        await client.RegisterProgramAsync(Manifest("args", version: "2.0.0"));
        KernelRpcException error = await Assert.ThrowsAsync<KernelRpcException>(() => client.StartProcessAsync(new("test", []), deadline.Token));
        Assert.Equal("AMBIGUOUS_PROGRAM", error.Code);
        Assert.Equal(19, (await Collect(await client.StartProcessAsync(new(first.QualifiedName, []), deadline.Token))).Code);
        await client.AddHostRootAsync(new("workspace", root));
        ShellState shell = await client.OpenShellAsync();
        await client.ExecuteShellAsync(new(shell.SessionId, "cd /host/workspace"));
        await client.RemoveHostRootAsync("workspace");
        await Assert.ThrowsAsync<KernelRpcException>(() => client.StartProcessAsync(new(first.QualifiedName, [], SessionId: shell.SessionId), deadline.Token));
    }

    [Fact]
    public async Task EnvironmentIsNotInheritedWholesale()
    {
        const string name = "MAINFRAME_TEST_SECRET";
        Environment.SetEnvironmentVariable(name, "do-not-inherit");
        try
        {
            await client.RegisterProgramAsync(Manifest("env"));
            (string Output, string Error, int Code) result = await Collect(await client.StartProcessAsync(new("test", [name]), deadline.Token));
            Assert.Equal("ABSENT", result.Output.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public async Task CancellationTerminatesChildTree()
    {
        await client.RegisterProgramAsync(Manifest("tree"));
        KernelInvocation call = await client.StartProcessAsync(new("test", []), deadline.Token);
        using var reader = new StreamReader(call.Stdout);
        int childId = int.Parse((await reader.ReadLineAsync(deadline.Token))!);
        await call.CancelAsync();
        Assert.True((await call.WaitAsync(deadline.Token)).Cancelled);
        try
        {
            using var child = Process.GetProcessById(childId);
            Assert.True(child.HasExited);
        }
        catch (ArgumentException)
        {
        }
    }

    [Fact]
    public async Task SlowOutputConsumerDoesNotBlockUnaryCalls()
    {
        await client.RegisterProgramAsync(Manifest("burst"));
        KernelInvocation call = await client.StartProcessAsync(new("test", []), deadline.Token);
        await Task.Delay(250);
        Assert.Equal("ready", (await client.CallAsync("kernel.health", cancellationToken: deadline.Token)).GetProperty("status").GetString());
        using var output = new MemoryStream();
        Task read = call.Stdout.CopyToAsync(output, deadline.Token);
        await call.WaitAsync(deadline.Token);
        await read;
        Assert.Equal(8 * 1024 * 1024, output.Length);
    }

    [Fact]
    public async Task RealConPtyInputResizeInterruptAndExit()
    {
        await client.RegisterProgramAsync(Manifest("terminal"));
        KernelInvocation call = await client.StartProcessAsync(new("test", [], "terminal", Columns: 80, Rows: 25), deadline.Token);
        using var reader = new StreamReader(call.Stdout);
        var text = new StringBuilder();
        async Task Until(string expected)
        {
            var buffer = new char[256];
            while (!text.ToString().Contains(expected))
            {
                int n = await reader.ReadAsync(buffer, deadline.Token);
                Assert.True(n > 0, text.ToString());
                text.Append(buffer, 0, n);
            }
        }

        await Until("READY");
        await call.WriteInputAsync("hello\r"u8.ToArray(), deadline.Token);
        await Until("ECHO=hello");
        await client.ResizeAsync(call.Process.ProcessId, 100, 40, deadline.Token);
        await call.WriteInputAsync("size\r"u8.ToArray(), deadline.Token);
        await Until("SIZE=100x40");
        await client.InterruptAsync(call.Process.ProcessId, deadline.Token);
        await Until("INTERRUPTED");
        await call.WriteInputAsync("exit\r"u8.ToArray(), deadline.Token);
        Task<string> drain = reader.ReadToEndAsync(deadline.Token);
        Assert.Equal(31, (await call.WaitAsync(deadline.Token)).ExitCode);
        await drain;
    }

    [Theory]
    [InlineData("bootstrap-replay", "REPLAY_REJECTED")]
    [InlineData("bootstrap-expired", "EXPIRED_REJECTED")]
    public async Task BootstrapIsSingleUseAndExpires(string mode, string expected)
    {
        await client.RegisterProgramAsync(Manifest(mode));
        (string Output, string Error, int Code) result = await Collect(await client.StartProcessAsync(new("test", []), deadline.Token));
        Assert.Equal(0, result.Code);
        Assert.Contains(expected, result.Output);
    }

    [Fact]
    public async Task RealConPtyWindowsEof()
    {
        await client.RegisterProgramAsync(Manifest("terminal"));
        KernelInvocation call = await client.StartProcessAsync(new("test", [], "terminal"), deadline.Token);
        using var reader = new StreamReader(call.Stdout);
        var text = new StringBuilder();
        var chars = new char[512];
        while (!text.ToString().Contains("READY"))
        {
            int n = await reader.ReadAsync(chars, deadline.Token);
            Assert.True(n > 0);
            text.Append(chars, 0, n);
        }

        await call.WriteInputAsync(new byte[] { 26, 13 }, deadline.Token);
        Task<string> drain = reader.ReadToEndAsync(deadline.Token);
        Assert.Equal(32, (await call.WaitAsync(deadline.Token)).ExitCode);
        Assert.Contains("EOF", await drain);
    }

    [Fact]
    public async Task ActualCliShellNavigatesAndRestoresConsoleModes()
    {
        await client.RegisterProgramAsync(HelloManifest());
        await client.AddHostRootAsync(new("workspace", root));
        ProgramManifest manifest = Manifest("cli-harness") with
        {
            FixedArguments = ["cli-harness", "connect", "--state", Path.Combine(root, "state"), "--endpoint", $"127.0.0.1:{server.Port}"]
        };
        using var process = WindowsProcess.Start(manifest, [], root, true, 100, 30, "unused");
        process.Bootstrap.Dispose();
        using var reader = new StreamReader(process.Stdout);
        var text = new StringBuilder();
        async Task Until(string expected)
        {
            var chars = new char[512];
            while (!text.ToString().Contains(expected))
            {
                int n;
                try
                {
                    n = await reader.ReadAsync(chars, deadline.Token);
                }
                catch
                {
                    output.WriteLine(text.ToString());
                    throw;
                }

                Assert.True(n > 0, text.ToString());
                text.Append(chars, 0, n);
            }
        }

        await Until("mframe:/>");
        await process.Stdin.WriteAsync("cd /host/workspace\r"u8.ToArray(), deadline.Token);
        await process.Stdin.FlushAsync();
        await Until("mframe:/host/workspace>");
        await process.Stdin.WriteAsync("test Alice\r"u8.ToArray(), deadline.Token);
        await process.Stdin.FlushAsync();
        await Until("Hello Alice from execution-test");
        // Wait for the post-execution prompt, then exit the local shell.
        if (text.ToString().LastIndexOf("mframe:/host/workspace>", StringComparison.Ordinal) < text.ToString().LastIndexOf("Hello Alice", StringComparison.Ordinal))
        {
            text.Clear();
            await Until("mframe:/host/workspace>");
        }

        await process.Stdin.WriteAsync("exit\r"u8.ToArray(), deadline.Token);
        await process.Stdin.FlushAsync();
        await Until("MODES_RESTORED=True");
        Assert.Equal(0, await process.WaitAsync(deadline.Token));
        Task<string> drain = reader.ReadToEndAsync(deadline.Token);
        await process.CloseTerminalAsync();
        await drain;
    }

    [Fact]
    public async Task DisconnectCleansUpForegroundDescendants()
    {
        await client.RegisterProgramAsync(Manifest("tree"));
        KernelInvocation call = await client.StartProcessAsync(new("test", []), deadline.Token);
        using var reader = new StreamReader(call.Stdout);
        int pid = int.Parse((await reader.ReadLineAsync(deadline.Token))!);
        await client.DisposeAsync();
        bool exited = false;
        for (int i = 0; i < 60; i++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                exited = process.HasExited;
            }
            catch (ArgumentException)
            {
                exited = true;
            }

            if (exited)
                break;
            await Task.Delay(50, deadline.Token);
        }

        Assert.True(exited);
    }

    [Fact]
    public async Task ActualCliExecPreservesChildFlagsAndExitCode()
    {
        await client.RegisterProgramAsync(Manifest("args"));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "mframe.exe"))
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        string[] argv = ["--help", "--name", "", "Alice Smith", "C:\\trailing\\"];
        foreach (var word in new[]
        {
            "exec",
            "--state",
            Path.Combine(root, "state"),
            "--endpoint",
            $"127.0.0.1:{server.Port}",
            "test"
        }.Concat(argv))
            start.ArgumentList.Add(word);
        using Process process = Process.Start(start)!;
        process.StandardInput.Close();
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(deadline.Token);
        await process.WaitForExitAsync(deadline.Token);
        Assert.Equal(19, process.ExitCode);
        Assert.Equal(argv, JsonSerializer.Deserialize<string[]>(await stdout));
        Assert.Empty(await stderr);
    }

    [Fact]
    public async Task RegistrationRevisionIsFrozenForLiveExecution()
    {
        ProgramRegistration first = await client.RegisterProgramAsync(Manifest("copy"));
        KernelInvocation call = await client.StartProcessAsync(new("test", []), deadline.Token);
        ProgramRegistration replacement = await client.RegisterProgramAsync(Manifest("args"));
        Assert.NotEqual(first.Revision, replacement.Revision);
        (string Output, string Error, int Code) result = await Collect(call, "existing revision"u8.ToArray());
        Assert.Equal(23, result.Code);
        Assert.Equal("existing revision", result.Output);
        Assert.Equal(19, (await Collect(await client.StartProcessAsync(new("test", []), deadline.Token))).Code);
    }

    [Fact]
    public async Task RemovingGrantTerminatesLiveExecution()
    {
        await client.RegisterProgramAsync(Manifest("sleep"));
        KernelInvocation call = await client.StartProcessAsync(new("test", []), deadline.Token);
        using (var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "state", "kernel.db")};Pooling=False"))
        {
            database.Open();
            using SqliteCommand command = database.CreateCommand();
            command.CommandText = "DELETE FROM grants WHERE principal='local-admin'";
            command.ExecuteNonQuery();
        }

        Assert.True((await call.WaitAsync(deadline.Token)).Cancelled);
    }

    [Fact]
    public async Task ExpiredAuthorizationLeaseCannotBeRenewed()
    {
        await client.RegisterProgramAsync(Manifest("sleep"));
        KernelInvocation call = await client.StartProcessAsync(new("test", []), deadline.Token);
        clock.Advance(TimeSpan.FromSeconds(61));
        Assert.True((await call.WaitAsync(deadline.Token)).Cancelled);
    }

    private sealed class AdjustableClock : TimeProvider
    {
        private long offsetTicks;
        public void Advance(TimeSpan duration) => Interlocked.Add(ref offsetTicks, duration.Ticks);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref offsetTicks));
    }
}
