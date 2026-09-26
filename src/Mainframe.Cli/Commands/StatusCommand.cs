using System.Text.Json;

namespace Mainframe.Cli.Commands;

public sealed class StatusCommand : KernelCommand
{
    public override string Name => "status";
    public override string Description => "Query the running kernel's identity and health.";

    protected override async Task ExecuteConnectedAsync(KernelConnection connection, CommandContext context, CancellationToken cancellationToken)
    {
        JsonElement description = await connection.Client.CallAsync("kernel.describe", null, cancellationToken);
        JsonElement health = await connection.Client.CallAsync("kernel.health", null, cancellationToken);
        if (context.Json)
        {
            context.WriteJson(new { endpoint = connection.Endpoint.Display, description, health });
            return;
        }

        context.Output.WriteLine("MAINFRAME / kernel status");
        context.Output.WriteLine($"Mainframe   {description.GetProperty("name").GetString()}");
        context.Output.WriteLine($"Identity    {description.GetProperty("mainframeId").GetString()}");
        context.Output.WriteLine($"Kernel      {description.GetProperty("kernelId").GetString()}");
        context.Output.WriteLine($"Status      {health.GetProperty("status").GetString()}");
        context.Output.WriteLine($"Endpoint    {connection.Endpoint.Display}");
        context.Output.WriteLine($"Version     {description.GetProperty("version").GetString()}");
    }
}
