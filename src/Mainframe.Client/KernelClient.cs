using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Mainframe.Protocol;

namespace Mainframe.Client;

public sealed class KernelClient : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly WireConnection wire;
    public WelcomeResponse Welcome
    {
        get;
    }

    private KernelClient(TcpClient tcp, WireConnection wire, WelcomeResponse welcome)
    {
        this.tcp = tcp;
        this.wire = wire;
        Welcome = welcome;
        wire.Start();
    }

    public static Task<KernelClient> ConnectAsync(string host, int port, X509Certificate2 clientCertificate, X509Certificate2 caCertificate, CancellationToken cancellationToken = default) => ConnectCoreAsync(host, port, clientCertificate, caCertificate, null, cancellationToken);
    public static async Task<KernelClient> ConnectProgramAsync(BootstrapCredential credential, CancellationToken cancellationToken = default)
    {
        using X509Certificate2 ca = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(credential.CaCertificate));
        return await ConnectCoreAsync(credential.Host, credential.Port, null, ca, credential.Token, cancellationToken);
    }

    private static async Task<KernelClient> ConnectCoreAsync(string host, int port, X509Certificate2? certificate, X509Certificate2 ca, string? bootstrap, CancellationToken token)
    {
        var tcp = new TcpClient
        {
            NoDelay = true
        };
        SslStream? ssl = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(10000);
                await tcp.ConnectAsync(host, port, timeout.Token);
            }

            ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, errors) => ValidateServerCertificate(cert, errors, ca));
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(10000);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host, EnabledSslProtocols = SslProtocols.Tls13, ClientCertificates = certificate is null ? null : new X509CertificateCollection { certificate }, CertificateRevocationCheckMode = X509RevocationMode.NoCheck }, timeout.Token);
            }

            using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            helloTimeout.CancelAfter(10000);
            var hello = new HelloRequest([1], ExecutionFeatures.All.Where(f => f != "unary-rpc").ToArray(), ["unary-rpc"], bootstrap is null ? "terminal" : "program", "mframe", FrameCodec.MaxPayloadBytes);
            await FrameCodec.WriteAsync(ssl, new(FrameType.Hello, 0, 0, ProtocolJson.Serialize(hello)), helloTimeout.Token);
            Frame? frame = await FrameCodec.ReadAsync(ssl, helloTimeout.Token);
            if (frame?.Type != FrameType.Welcome)
                throw new ProtocolException("Kernel did not accept HELLO.");
            WelcomeResponse welcome = ProtocolJson.Deserialize<WelcomeResponse>(frame.Payload);
            if (welcome.Version != 1 || !welcome.Features.Contains("unary-rpc") || welcome.Features.Any(f => !ExecutionFeatures.All.Contains(f)))
                throw new ProtocolException("Unsupported negotiated protocol.");
            if (bootstrap is not null)
            {
                if (welcome.Authentication != "required")
                    throw new AuthenticationException("Expected bootstrap authentication.");
                await FrameCodec.WriteAsync(ssl, new(FrameType.Auth, 0, 0, ProtocolJson.Serialize(new BootstrapAuth(bootstrap))), helloTimeout.Token);
                Frame? auth = await FrameCodec.ReadAsync(ssl, helloTimeout.Token);
                if (auth?.Type != FrameType.AuthResult || !ProtocolJson.Deserialize<AuthenticationResult>(auth.Payload).Ok)
                    throw new AuthenticationException("Bootstrap rejected.");
            }
            else if (welcome.Authentication != "authenticated")
                throw new AuthenticationException("Operator authentication failed.");
            return new(tcp, new WireConnection(ssl, true, welcome.MaxFrameBytes), welcome);
        }
        catch
        {
            if (ssl is not null)
                await ssl.DisposeAsync();
            tcp.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, object? arguments = null, CancellationToken cancellationToken = default)
    {
        KernelInvocation call = await BeginAsync(method, arguments, cancellationToken);
        if (call.Response.Streaming)
        {
            await call.Exchange.CancelAsync();
            throw new InvalidOperationException("Use the streaming execution API.");
        }

        return call.Response.Result!.Value;
    }

    public async Task<KernelInvocation> BeginAsync(string method, object? arguments = null, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(10000);
        WireExchange exchange = await wire.RequestAsync(new(method, 1, 10000, ProtocolJson.ToElement(arguments ?? new EmptyArguments())), timeout.Token);
        try
        {
            RpcResponse response = await exchange.Response.Task.WaitAsync(timeout.Token);
            Check(response);
            return new(exchange, response);
        }
        catch (OperationCanceledException)
        {
            try
            {
                await exchange.CancelAsync();
            }
            catch (IOException)
            {
            }

            throw;
        }
    }

    public async Task<ProgramRegistration> RegisterProgramAsync(ProgramManifest manifest, CancellationToken token = default) => Decode<ProgramRegistration>(await CallAsync("program.register", new RegisterProgramRequest(manifest), token));
    public async Task<ProgramRegistration[]> ListProgramsAsync(CancellationToken token = default) => Decode<ProgramRegistration[]>(await CallAsync("program.list", cancellationToken: token));
    public Task<JsonElement> RemoveProgramAsync(string name, CancellationToken token = default) => CallAsync("program.remove", new SelectorRequest(name), token);
    public Task<JsonElement> AddHostRootAsync(HostRoot root, CancellationToken token = default) => CallAsync("host-root.add", root, token);
    public async Task<HostRoot[]> ListHostRootsAsync(CancellationToken token = default) => Decode<HostRoot[]>(await CallAsync("host-root.list", cancellationToken: token));
    public Task<JsonElement> RemoveHostRootAsync(string name, CancellationToken token = default) => CallAsync("host-root.remove", new SelectorRequest(name), token);
    public async Task<ShellState> OpenShellAsync(CancellationToken token = default) => Decode<ShellState>(await CallAsync("shell.open", cancellationToken: token));
    public Task<KernelInvocation> ExecuteShellAsync(ShellCommandRequest request, CancellationToken token = default) => BeginAsync("shell.command", request, token);
    public Task<KernelInvocation> StartProcessAsync(ProcessStartRequest request, CancellationToken token = default) => BeginAsync("process.start", request, token);
    public Task<JsonElement> ResizeAsync(string processId, int columns, int rows, CancellationToken token = default) => CallAsync("process.resize", new ProcessControlRequest(processId, Columns: columns, Rows: rows), token);
    public Task<JsonElement> InterruptAsync(string processId, CancellationToken token = default) => CallAsync("process.interrupt", new ProcessControlRequest(processId), token);
    public static T Decode<T>(JsonElement value) => ProtocolJson.Deserialize<T>(ProtocolJson.Serialize(value));
    internal static void Check(RpcResponse response)
    {
        if (!response.Ok)
            throw new KernelRpcException(response.Error!.Code, response.Error.Message, response.Error.Outcome);
    }

    private static bool ValidateServerCertificate(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 root)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;
        using var server = new X509Certificate2(certificate);
        X509BasicConstraintsExtension[] constraints = server.Extensions.OfType<X509BasicConstraintsExtension>().ToArray();
        X509EnhancedKeyUsageExtension[] usages = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (constraints.Length != 1 || constraints[0].CertificateAuthority || usages.Length != 1 || !usages[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"))
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(server) && chain.ChainElements.Count > 1 && chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData);
    }

    public async ValueTask DisposeAsync()
    {
        await wire.DisposeAsync();
        tcp.Dispose();
    }
}

public sealed class KernelInvocation(WireExchange exchange, RpcResponse response)
{
    public WireExchange Exchange { get; } = exchange;
    public RpcResponse Response { get; } = response;
    public ProcessStarted Process => KernelClient.Decode<ProcessStarted>(Response.Result!.Value);
    public Stream Stdout => Exchange.Channel(2).Input;
    public Stream? Stderr => Process.Channels.Any(c => c.Id == 3) ? Exchange.Channel(3).Input : null;

    public Task WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => Exchange.Channel(1).SendAsync(bytes, token);
    public Task EndInputAsync() => Exchange.Channel(1).EndAsync();
    public Task CancelAsync() => Exchange.CancelAsync();
    public async Task<ProcessExited> WaitAsync(CancellationToken token = default)
    {
        RpcResponse result = await Exchange.Completion.Task.WaitAsync(token);
        KernelClient.Check(result);
        return KernelClient.Decode<ProcessExited>(result.Result!.Value);
    }
}
