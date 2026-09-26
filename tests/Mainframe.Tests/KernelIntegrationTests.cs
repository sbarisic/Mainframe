using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Mainframe.Client;
using Mainframe.Core;
using Mainframe.Host;
using Mainframe.Protocol;

namespace Mainframe.Tests;

public sealed class KernelIntegrationTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private readonly string testRoot = Path.Combine(Path.GetTempPath(), "Mainframe-Integration-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(45));
    private string NewState() => Path.Combine(testRoot, Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task IdentitySurvivesRestartAndQueriesUseAuthenticatedTls()
    {
        string path = NewState();
        KernelIdentity identity = KernelStore.Initialize(path, "integration");
        using var store = KernelStore.Open(path);
        using X509Certificate2 cert = store.LoadOperatorCertificate();
        using X509Certificate2 ca = store.LoadCaCertificate();
        for (int restart = 0; restart < 2; restart++)
        {
            await using var server = new KernelServer(store, 0, output.WriteLine);
            server.Start();
            await using KernelClient client = await KernelClient.ConnectAsync("127.0.0.1", server.Port, cert, ca, TestContextToken());
            JsonElement description = await client.CallAsync("kernel.describe");
            Assert.Equal(identity.KernelId, description.GetProperty("kernelId").GetString());
            Assert.Equal(identity.MainframeId, description.GetProperty("mainframeId").GetString());
            JsonElement health = await client.CallAsync("kernel.health");
            Assert.Equal("ready", health.GetProperty("status").GetString());
            JsonElement capabilities = await client.CallAsync("kernel.capabilities");
            Assert.Equal(KernelDispatcher.Capabilities.Length, capabilities.GetProperty("capabilities").GetArrayLength());
        }
    }

    [Fact]
    public async Task UntrustedServerAndClientCertificatesFail()
    {
        using var first = KernelStore.Open(Init());
        using var other = KernelStore.Open(Init());
        await using var server = new KernelServer(first, 0);
        server.Start();
        using X509Certificate2 validClient = first.LoadOperatorCertificate();
        using X509Certificate2 wrongClient = other.LoadOperatorCertificate();
        using X509Certificate2 validCa = first.LoadCaCertificate();
        using X509Certificate2 wrongCa = other.LoadCaCertificate();
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using KernelClient ignored = await KernelClient.ConnectAsync("127.0.0.1", server.Port, validClient, wrongCa, TestContextToken());
        });
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using KernelClient ignored = await KernelClient.ConnectAsync("127.0.0.1", server.Port, wrongClient, validCa, TestContextToken());
        });
        await using KernelClient working = await KernelClient.ConnectAsync("127.0.0.1", server.Port, validClient, validCa, TestContextToken());
        Assert.Equal("ready", (await working.CallAsync("kernel.health")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnsupportedMethodDoesNotBreakConnection()
    {
        using var store = KernelStore.Open(Init());
        await using var server = new KernelServer(store, 0);
        server.Start();
        using X509Certificate2 cert = store.LoadOperatorCertificate();
        using X509Certificate2 ca = store.LoadCaCertificate();
        await using KernelClient client = await KernelClient.ConnectAsync("127.0.0.1", server.Port, cert, ca, TestContextToken());
        KernelRpcException error = await Assert.ThrowsAsync<KernelRpcException>(() => client.CallAsync("fs.open"));
        Assert.Equal("UNSUPPORTED_METHOD", error.Code);
        Assert.Equal("ready", (await client.CallAsync("kernel.health")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ReusedExchangeIdClosesConnection()
    {
        using var store = KernelStore.Open(Init());
        await using var server = new KernelServer(store, 0);
        server.Start();
        using X509Certificate2 cert = store.LoadOperatorCertificate();
        using X509Certificate2 ca = store.LoadCaCertificate();
        using var tcp = new TcpClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await tcp.ConnectAsync("127.0.0.1", server.Port, deadline.Token);
        using var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, errors) => certificate is X509Certificate2 x509 && (errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0 && CertificateTrust.Validate(x509, ca, CertificateTrust.ServerAuthentication));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", EnabledSslProtocols = SslProtocols.Tls13, ClientCertificates = new X509CertificateCollection { cert }, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, deadline.Token);
        Assert.Equal(SslProtocols.Tls13, ssl.SslProtocol);
        await FrameCodec.WriteAsync(ssl, new Frame(FrameType.Hello, 0, 0, ProtocolJson.Serialize(new HelloRequest([1], [], ["unary-rpc"], "terminal", "test", FrameCodec.MaxPayloadBytes))), deadline.Token);
        Assert.Equal(FrameType.Welcome, (await FrameCodec.ReadAsync(ssl, deadline.Token))!.Type);
        var request = new Frame(FrameType.Request, 1, 0, ProtocolJson.Serialize(new RpcRequest("kernel.health", 1, 5000, System.Text.Json.JsonSerializer.SerializeToElement(new { }))));
        await FrameCodec.WriteAsync(ssl, request, deadline.Token);
        Assert.Equal(FrameType.Response, (await FrameCodec.ReadAsync(ssl, deadline.Token))!.Type);
        await FrameCodec.WriteAsync(ssl, request, deadline.Token);
        // Either a TLS EOF or socket closure is valid; neither can be a successful response.
        try
        {
            Assert.Null(await FrameCodec.ReadAsync(ssl, deadline.Token));
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task RevokedOperatorCannotReuseAnAuthenticatedSession()
    {
        using var store = KernelStore.Open(Init());
        await using var server = new KernelServer(store, 0);
        server.Start();
        using X509Certificate2 cert = store.LoadOperatorCertificate();
        using X509Certificate2 ca = store.LoadCaCertificate();
        await using KernelClient client = await KernelClient.ConnectAsync("127.0.0.1", server.Port, cert, ca, TestContextToken());
        await client.CallAsync("kernel.health");
        store.RevokeOperator(CertificateTrust.Fingerprint(cert));
        await Assert.ThrowsAnyAsync<IOException>(() => client.CallAsync("kernel.health", cancellationToken: TestContextToken()));
    }

    [Fact]
    public async Task IdleConnectionSurvivesHeartbeatAndConcurrentClientCallsAreSerialized()
    {
        using var store = KernelStore.Open(Init());
        await using var server = new KernelServer(store, 0);
        server.Start();
        using X509Certificate2 cert = store.LoadOperatorCertificate();
        using X509Certificate2 ca = store.LoadCaCertificate();
        await using KernelClient client = await KernelClient.ConnectAsync("127.0.0.1", server.Port, cert, ca, TestContextToken());
        await Task.Delay(TimeSpan.FromSeconds(12), TestContextToken());
        JsonElement[] results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.CallAsync("kernel.health", cancellationToken: TestContextToken())));
        Assert.All(results, result => Assert.Equal("ready", result.GetProperty("status").GetString()));
    }

    private CancellationToken TestContextToken() => deadline.Token;
    private string Init()
    {
        var path = NewState();
        KernelStore.Initialize(path, "test");
        return path;
    }

    public void Dispose()
    {
        deadline.Dispose();
        string resolved = Path.GetFullPath(testRoot);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("Mainframe-Integration-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid generated test directory.");
        if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }
}
