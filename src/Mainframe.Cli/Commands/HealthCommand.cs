namespace Mainframe.Cli.Commands;

public sealed class HealthCommand : KernelCommand
{
    public override string Name => "health";
    public override string Description => "Query the running kernel's readiness.";

    protected override async Task ExecuteConnectedAsync(KernelConnection connection, CommandContext context, CancellationToken cancellationToken)
    {
        var health = await connection.Client.CallAsync("kernel.health", null, cancellationToken);
        if (context.Json)
            context.WriteJson(health);
        else
            context.Output.WriteLine($"Kernel {health.GetProperty("status").GetString()} at {connection.Endpoint.Display}");
    }
}
