using Mainframe.Cli.Commands;

namespace Mainframe.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            var router = new CommandRouter([new ClusterCommand(), new StatusCommand(), new HealthCommand(), new CapabilitiesCommand(), new ProgramCommand(), new VolumeCommand(), new HostRootCommand(), new ExecCommand(), new ConnectCommand(), new VersionCommand()]);
            return await router.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }
}
