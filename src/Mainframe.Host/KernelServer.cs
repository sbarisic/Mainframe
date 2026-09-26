using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Mainframe.Core;
using Mainframe.Protocol;

namespace Mainframe.Host;
/// <summary>Loopback-only authenticated unary kernel host. It does not enable public or peer access.</summary>
public sealed class KernelServer : IAsyncDisposable
{
    private readonly KernelStore store;
    private readonly TcpListener listener;
    private readonly X509Certificate2 serverCertificate;
    private readonly X509Certificate2 caCertificate;
    private readonly Action<string> log;
    private readonly CancellationTokenSource stopping = new();
    private readonly ConcurrentDictionary<long, Task> connections = new();
    private readonly ConcurrentDictionary<long, TcpClient> clients = new();
    private readonly SemaphoreSlim connectionSlots = new(32, 32);
    private readonly SemaphoreSlim handshakeSlots = new(8, 8);
    private Task? acceptLoop;
    private long nextConnection;
    private KernelDispatcher? dispatcher;
    private bool disposed;
    private readonly FileStream ownership;
    private readonly ExecutionService execution;
    private readonly ConcurrentDictionary<long, WireConnection> wires = new();
    public KernelServer(KernelStore store, int port = 7443, Action<string>? log = null, TimeProvider? timeProvider = null)
    {
        if (port is < 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        this.store = store;
        this.log = log ?? (_ =>
        {
        });
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        ownership = store.AcquireHostOwnership();
        X509Certificate2? loadedServer = null, loadedCa = null;
        try
        {
            store.RecoverExecutions();
            execution = new ExecutionService(store, () => Port, this.log, timeProvider ?? TimeProvider.System);
            loadedServer = store.LoadServerCertificate();
            loadedCa = store.LoadCaCertificate();
            if (!CertificateTrust.Validate(loadedServer, loadedCa, CertificateTrust.ServerAuthentication))
                throw new AuthenticationException("Kernel certificate is invalid or expired.");
            serverCertificate = loadedServer;
            caCertificate = loadedCa;
        }
        catch
        {
            loadedServer?.Dispose();
            loadedCa?.Dispose();
            ownership.Dispose();
            throw;
        }
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int ActiveConnections => clients.Count;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (acceptLoop is not null)
            throw new InvalidOperationException("Kernel already started.");
        listener.Start(32);
        dispatcher = new KernelDispatcher(store.Identity, DateTimeOffset.UtcNow, () => ActiveConnections);
        acceptLoop = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                TcpClient client = await listener.AcceptTcpClientAsync(stopping.Token);
                if (!connectionSlots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                client.NoDelay = true;
                long id = Interlocked.Increment(ref nextConnection);
                clients[id] = client;
                Task task = HandleAsync(client, stopping.Token);
                connections[id] = task;
                _ = RemoveWhenCompleteAsync(id, task);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (SocketException) when (stopping.IsCancellationRequested || disposed)
        {
        }
        catch (ObjectDisposedException) when (disposed)
        {
        }
    }

    private async Task RemoveWhenCompleteAsync(long id, Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            log($"Connection handler failed ({ex.GetType().Name}).");
        }
        finally
        {
            connections.TryRemove(id, out _);
            clients.TryRemove(id, out _);
            connectionSlots.Release();
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken stop)
    {
        using (client)
        using (var ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) =>
        {
            if (cert is null)
                return true; // Restricted bootstrap only; operational dispatch remains unauthenticated.
            using var presented = new X509Certificate2(cert);
            return CertificateTrust.Validate(presented, caCertificate, CertificateTrust.ClientAuthentication) && store.IsOperatorAuthorized(presented);
        }))
        {
            try
            {
                if (!handshakeSlots.Wait(0))
                    return;
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCertificate, ClientCertificateRequired = true, EnabledSslProtocols = SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck, AllowRenegotiation = false }, handshake.Token);
                }
                finally
                {
                    handshakeSlots.Release();
                }

                using X509Certificate2? remote = ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate);
                using var application = CancellationTokenSource.CreateLinkedTokenSource(stop);
                application.CancelAfter(TimeSpan.FromSeconds(10));
                Frame? first = await FrameCodec.ReadAsync(ssl, application.Token);
                if (first is null || first.Type != FrameType.Hello)
                    throw new ProtocolException("HELLO required.");
                HelloRequest hello = ProtocolJson.Deserialize<HelloRequest>(first.Payload);
                if (!hello.Versions.Contains(1) || hello.Role is not ("terminal" or "program") || hello.RequiredFeatures.Any(f => !ExecutionFeatures.All.Contains(f)) || !hello.RequiredFeatures.Concat(hello.OptionalFeatures).Contains("unary-rpc") || hello.MaxFrameBytes < 1024)
                {
                    await FrameCodec.WriteAsync(ssl, new(FrameType.GoAway, 0, 0, ProtocolJson.Serialize(new GoAwayMessage("Unsupported version, role, or feature."))), application.Token);
                    return;
                }

                bool operatorRole = hello.Role == "terminal" && remote is not null && store.IsOperatorAuthorized(remote);
                if (hello.Role == "terminal" && !operatorRole)
                    throw new AuthenticationException("Operator certificate required.");
                var features = ExecutionFeatures.All.Intersect(hello.RequiredFeatures.Concat(hello.OptionalFeatures)).ToArray();
                await FrameCodec.WriteAsync(ssl, new(FrameType.Welcome, 0, 0, ProtocolJson.Serialize(new WelcomeResponse(1, features, store.Identity.KernelId, store.Identity.MainframeId, hello.MaxFrameBytes, operatorRole ? "authenticated" : "required"))), application.Token);
                Func<bool> valid;
                Func<string, bool> allows;
                string principal;
                if (operatorRole)
                {
                    principal = store.GetOperatorIdentity(remote!)!;
                    valid = () => store.IsOperatorAuthorized(remote!);
                    allows = method => store.HasGrant(principal, method);
                }
                else
                {
                    Frame? authFrame = await FrameCodec.ReadAsync(ssl, application.Token);
                    if (authFrame?.Type != FrameType.Auth)
                        throw new AuthenticationException("Bootstrap AUTH required.");
                    BootstrapAuth auth = ProtocolJson.Deserialize<BootstrapAuth>(authFrame.Payload);
                    (Func<bool> Valid, Func<string, bool> Allows) authorization = execution.Authenticate(auth.Token) ?? throw new AuthenticationException("Invalid bootstrap.");
                    valid = authorization.Valid;
                    allows = authorization.Allows;
                    principal = "execution";
                    await FrameCodec.WriteAsync(ssl, new(FrameType.AuthResult, 0, 0, ProtocolJson.Serialize(new AuthenticationResult(true))), application.Token);
                }

                var session = new OperatorSession(principal, valid, allows);
                await using var wire = new WireConnection(ssl, false, hello.MaxFrameBytes, valid);
                wire.RequestHandler = (exchange, request) =>
                {
                    if (!request.Method.StartsWith("kernel.", StringComparison.Ordinal) && !features.Contains("execution-v1"))
                        return exchange.ReplyAsync(KernelDispatcher.Error("UNSUPPORTED_METHOD", "Execution feature was not negotiated."));
                    if (request.Method.StartsWith("shell.", StringComparison.Ordinal) && !features.Contains("shell-v1") || request.Method is "process.start" or "shell.command" && !features.Contains("streaming-v1"))
                        return exchange.ReplyAsync(KernelDispatcher.Error("UNSUPPORTED_METHOD", "Required shell/streaming features were not negotiated."));
                    return execution.HandleAsync(exchange, request, session, dispatcher!);
                };
                long wireId = Interlocked.Increment(ref nextConnection);
                wires[wireId] = wire;
                try
                {
                    wire.Start();
                    await wire.Completion;
                }
                finally
                {
                    wires.TryRemove(wireId, out _);
                }
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or SocketException or System.Text.Json.JsonException or CryptographicException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
            {
                if (!stop.IsCancellationRequested)
                    log($"Connection ended ({ex.GetType().Name}).");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
            return;
        disposed = true;
        listener.Stop();
        await Task.WhenAll(wires.Values.Select(w => w.DrainAsync()));
        await stopping.CancelAsync();
        if (acceptLoop is not null)
            await acceptLoop;
        foreach (TcpClient client in clients.Values)
            client.Dispose();
        try
        {
            await Task.WhenAll(connections.Values);
        }
        finally
        {
            ownership.Dispose();
            serverCertificate.Dispose();
            caCertificate.Dispose();
            stopping.Dispose();
        }
        // Slot semaphores remain valid for final completion continuations.
    }
}
