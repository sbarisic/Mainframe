using Mainframe.Cli;
using Mainframe.Cli.Commands;

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler onCancel = (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
Console.CancelKeyPress += onCancel;
try
{
    return await new CommandRouter([
        new ClusterCommand(),
        new StatusCommand(),
        new HealthCommand(),
        new CapabilitiesCommand(),
        new VersionCommand()
    ]).RunAsync(args, Console.Out, Console.Error, cancellation.Token);
}
finally
{
    Console.CancelKeyPress -= onCancel;
}
