using System.Security.Cryptography.X509Certificates;
using Mainframe.Client;

namespace Mainframe.Cli.Commands;

public abstract class KernelCommand : ICommand
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public string Usage => $"mf {Name} [--state DIR] [--endpoint HOST:PORT] [--json]";

    public async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.RequireNoArguments(Usage);
        context.Validate(Usage, "--state", "--endpoint", "--json");
        var endpoint = context.GetEndpoint();
        var caPath = Path.Combine(context.StateDirectory, "ca.cer");
        var operatorPath = Path.Combine(context.StateDirectory, "operator.pfx");
        if (!File.Exists(caPath) || !File.Exists(operatorPath))
            throw new InvalidOperationException($"No operator credentials in '{context.StateDirectory}'. Run 'mf cluster init' with this --state directory first.");

        using var ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
        // Schannel requires a user key container for client authentication. Without
        // PersistKeySet the imported container is released when the certificate is disposed.
        var keyStorage = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.UserKeySet
            : X509KeyStorageFlags.EphemeralKeySet;
        using var identity = X509CertificateLoader.LoadPkcs12FromFile(operatorPath, null, keyStorage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var client = await KernelClient.ConnectAsync(endpoint.Host, endpoint.Port, identity, ca, deadline.Token);
        await ExecuteConnectedAsync(new KernelConnection(client, endpoint), context, deadline.Token);
        return 0;
    }

    protected abstract Task ExecuteConnectedAsync(KernelConnection connection, CommandContext context, CancellationToken cancellationToken);
}

public sealed record KernelConnection(KernelClient Client, KernelEndpoint Endpoint);
