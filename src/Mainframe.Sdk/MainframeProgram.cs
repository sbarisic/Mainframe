using System.IO.Pipes;
using System.Text.Json;
using Mainframe.Client;
using Mainframe.Protocol;

namespace Mainframe.Sdk;

public sealed class MainframeProgram : IAsyncDisposable
{
    private readonly KernelClient client;
    private MainframeProgram(KernelClient client) => this.client = client;
    public static async Task<MainframeProgram> ConnectAsync(CancellationToken token = default)
    {
        string handle = Environment.GetEnvironmentVariable("MF_BOOTSTRAP_HANDLE") ?? throw new InvalidOperationException("No Mainframe bootstrap pipe was inherited.");
        Environment.SetEnvironmentVariable("MF_BOOTSTRAP_HANDLE", null);
        using var pipe = new AnonymousPipeClientStream(PipeDirection.In, handle);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(10000);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[1024];
        int count;
        while ((count = await pipe.ReadAsync(bytes, timeout.Token)) != 0)
        {
            if (buffer.Length + count > 16384)
                throw new InvalidDataException("Bootstrap exceeds limit.");
            buffer.Write(bytes, 0, count);
        }

        BootstrapCredential credential = ProtocolJson.Deserialize<BootstrapCredential>(buffer.ToArray());
        return new(await KernelClient.ConnectProgramAsync(credential, timeout.Token));
    }

    public Task<JsonElement> DescribeAsync(CancellationToken token = default) => client.CallAsync("kernel.describe", cancellationToken: token);
    public Task<JsonElement> HealthAsync(CancellationToken token = default) => client.CallAsync("kernel.health", cancellationToken: token);
    public Task<JsonElement> CapabilitiesAsync(CancellationToken token = default) => client.CallAsync("kernel.capabilities", cancellationToken: token);
    public ValueTask DisposeAsync() => client.DisposeAsync();
}
