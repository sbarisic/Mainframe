using Mainframe.Core;

namespace Mainframe.Cli.Commands;

public sealed class ClusterCommand : ICommand
{
    public string Name => "cluster";
    public string Description => "Initialize a local mainframe identity and operator credentials.";
    public string Usage => "mframe cluster init [--state DIR] [--name NAME] [--json] | mframe cluster renew [--state DIR]";

    public Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        if (context.Arguments.Count == 1 && context.Arguments[0] == "renew")
        {
            context.Validate(Usage, "--state");
            using var store = KernelStore.Open(context.StateDirectory);
            context.Output.WriteLine(store.RenewCertificates() ? "Eligible certificates renewed. Identity preserved." : "No certificates are in their final 24 hours.");
            return Task.FromResult(0);
        }

        if (context.Arguments.Count != 1 || context.Arguments[0] != "init")
            throw new UsageException($"Usage: {Usage}");
        context.Validate(Usage, "--state", "--name", "--json");
        cancellationToken.ThrowIfCancellationRequested();
        var name = context.GetOption("--name") ?? Environment.MachineName;
        KernelIdentity identity = KernelStore.Initialize(context.StateDirectory, name);
        if (context.Json)
            context.WriteJson(new { identity.MainframeId, identity.KernelId, identity.Name, identity.CreatedAt, stateDirectory = context.StateDirectory });
        else
        {
            context.Output.WriteLine($"Initialized mainframe '{identity.Name}'.");
            context.Output.WriteLine($"Mainframe   {identity.MainframeId}");
            context.Output.WriteLine($"Kernel      {identity.KernelId}");
            context.Output.WriteLine($"State       {context.StateDirectory}");
            context.Output.WriteLine("Start the kernel with: mframed serve --state <state directory>");
        }

        return Task.FromResult(0);
    }
}
