using System.Text.Json;

namespace Mainframe.Cli.Commands;

public sealed class CapabilitiesCommand : KernelCommand
{
    public override string Name => "capabilities";
    public override string Description => "List syscall contracts advertised by the kernel.";

    protected override async Task ExecuteConnectedAsync(KernelConnection connection, CommandContext context, CancellationToken cancellationToken)
    {
        JsonElement result = await connection.Client.CallAsync("kernel.capabilities", null, cancellationToken);
        if (context.Json)
        {
            context.WriteJson(result);
            return;
        }

        context.Output.WriteLine("SYSCALL                  VERSION  DESCRIPTION");
        foreach (JsonElement capability in result.GetProperty("capabilities").EnumerateArray())
            context.Output.WriteLine($"{capability.GetProperty("method").GetString(),-24} {capability.GetProperty("version").GetInt32(),-8} {capability.GetProperty("description").GetString()}");
    }
}
