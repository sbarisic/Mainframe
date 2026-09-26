using System.Text.Json;
using Mainframe.Cli;
using Mainframe.Cli.Commands;
using Mainframe.Client;

namespace Mainframe.Tests;

public sealed class CliTests
{
    [Theory]
    [MemberData(nameof(HelpArguments))]
    public async Task HelpDoesNotRequireInitializedState(string[] arguments)
    {
        (int Code, string Output, string Error) result = await RunAsync(arguments);
        Assert.Equal(0, result.Code);
        Assert.Contains("mframe cluster init", result.Output);
        Assert.Contains("mframed serve", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [MemberData(nameof(CommandHelpArguments))]
    public async Task CommandHelpDoesNotConnectToKernel(string[] arguments)
    {
        (int Code, string Output, string Error) result = await RunAsync(arguments);
        Assert.Equal(0, result.Code);
        Assert.Contains("Usage: mframe status", result.Output);
        Assert.Empty(result.Error);
    }

    [Theory]
    [MemberData(nameof(VersionArguments))]
    public async Task VersionWorksWithoutKernel(string[] arguments)
    {
        (int Code, string Output, string Error) result = await RunAsync(arguments);
        Assert.Equal(0, result.Code);
        Assert.StartsWith("mframe ", result.Output);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task JsonOutputContainsOnlyStructuredResult()
    {
        (int Code, string Output, string Error) result = await RunAsync(["--json", "version"]);
        Assert.Equal(0, result.Code);
        using var parsed = JsonDocument.Parse(result.Output);
        Assert.Equal("mframe", parsed.RootElement.GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(parsed.RootElement.GetProperty("version").GetString()));
        Assert.Empty(result.Error);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task InvalidUsageReturnsTwoOnStderr(string[] arguments)
    {
        (int Code, string Output, string Error) result = await RunAsync(arguments);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.StartsWith("mframe: ", result.Error);
    }

    [Theory]
    [InlineData("example.com:7443")]
    [InlineData("192.168.1.10:7443")]
    [InlineData("localhost")]
    [InlineData("localhost:0")]
    [InlineData("localhost:70000")]
    [InlineData("localhost:7443/files")]
    [InlineData("user@localhost:7443")]
    [InlineData("localhost:7443?option=true")]
    public async Task InvalidAndNonLoopbackEndpointsFailBeforeLoadingState(string endpoint)
    {
        (int Code, string Output, string Error) result = await RunAsync(["--endpoint", endpoint, "status"]);
        Assert.Equal(2, result.Code);
        Assert.Empty(result.Output);
        Assert.StartsWith("mframe: ", result.Error);
    }

    [Theory]
    [InlineData("localhost:7443")]
    [InlineData("127.0.0.1:7443")]
    [InlineData("[::1]:7443")]
    public async Task MissingCredentialsFailWithoutCreatingState(string endpoint)
    {
        var state = Path.Combine(Path.GetTempPath(), $"mainframe-cli-missing-{Guid.NewGuid():N}");
        (int Code, string Output, string Error) result = await RunAsync(["--state", state, "status", "--endpoint", endpoint]);
        Assert.Equal(1, result.Code);
        Assert.Contains("No operator credentials", result.Error);
        Assert.Empty(result.Output);
        Assert.False(Directory.Exists(state));
    }

    [Fact]
    public async Task CommonOptionsWorkBeforeAndAfterCommand()
    {
        var router = new CommandRouter([new CaptureCommand()]);
        using var before = new StringWriter();
        using var after = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, await router.RunAsync(["--state", "a path", "--endpoint", "localhost:7555", "--json", "capture"], before, error));
        Assert.Equal(0, await router.RunAsync(["capture", "--state", "a path", "--endpoint", "localhost:7555", "--json"], after, error));
        Assert.Equal(before.ToString(), after.ToString());
        Assert.Contains("localhost:7555", before.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task CancellationReturns130()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        (int Code, string Output, string Error) result = await RunAsync(["version"], cancellation.Token);
        Assert.Equal(130, result.Code);
        Assert.Empty(result.Output);
        Assert.Contains("Cancelled", result.Error);
    }

    [Fact]
    public async Task RpcErrorRetainsStableCodeAndOutcome()
    {
        var router = new CommandRouter([new ErrorCommand()]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await router.RunAsync(["fail"], output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("PERMISSION_DENIED", error.ToString());
        Assert.Contains("not_started", error.ToString());
    }

    public static IEnumerable<object[]> HelpArguments => Cases([], ["help"], ["--help"], ["-h"]);

    [Fact]
    public async Task DeadlineFailureReturnsControlledError()
    {
        var router = new CommandRouter([new TimeoutCommand()]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await router.RunAsync(["timeout"], output, error));
        Assert.Empty(output.ToString());
        Assert.Contains("deadline", error.ToString());
    }

    private sealed class TimeoutCommand : ICommand
    {
        public string Name => "timeout";
        public string Description => "Test deadlines.";
        public string Usage => "mframe timeout";

        public Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) => throw new TimeoutException("The kernel request deadline elapsed.");
    }

    public static IEnumerable<object[]> CommandHelpArguments => Cases(["help", "status"], ["status", "--help"], ["--help", "status"]);
    public static IEnumerable<object[]> VersionArguments => Cases(["version"], ["--version"]);
    public static IEnumerable<object[]> InvalidArguments => Cases(["unknown"], ["status", "--unknown"], ["status", "--state"], ["status", "--state", "--json"], ["status", "--json", "--json"], ["status", "--state", "a", "--state", "b"], ["status", "extra"], ["status", "--name", "unused"], ["cluster", "join"], ["help", "status", "extra"], ["--json"]);

    private static IEnumerable<object[]> Cases(params string[][] arguments) => arguments.Select(argument => new object[] { argument });
    private static async Task<(int Code, string Output, string Error)> RunAsync(string[] arguments, CancellationToken token = default)
    {
        var router = new CommandRouter([new ClusterCommand(), new StatusCommand(), new HealthCommand(), new CapabilitiesCommand(), new VersionCommand()]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await router.RunAsync(arguments, output, error, token);
        return (code, output.ToString(), error.ToString());
    }

    private sealed class CaptureCommand : ICommand
    {
        public string Name => "capture";
        public string Description => "Test argument parsing.";
        public string Usage => "mframe capture";

        public Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
        {
            context.RequireNoArguments(Usage);
            context.Validate(Usage, "--state", "--endpoint", "--json");
            context.WriteJson(new { state = context.StateDirectory, endpoint = context.GetEndpoint().Display, context.Json });
            return Task.FromResult(0);
        }
    }

    private sealed class ErrorCommand : ICommand
    {
        public string Name => "fail";
        public string Description => "Test error reporting.";
        public string Usage => "mframe fail";

        public Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken) => throw new KernelRpcException("PERMISSION_DENIED", "Denied.", "not_started");
    }
}
