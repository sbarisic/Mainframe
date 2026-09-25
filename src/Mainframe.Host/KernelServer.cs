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

    public KernelServer(KernelStore store, int port = 7443, Action<string>? log = null)
    {
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        this.store = store;
        this.log = log ?? (_ => { });
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.ExclusiveAddressUse = true;
        serverCertificate = store.LoadServerCertificate();
        caCertificate = store.LoadCaCertificate();
        if (!CertificateTrust.Validate(serverCertificate, caCertificate, CertificateTrust.ServerAuthentication))
            throw new AuthenticationException("Kernel certificate is invalid or expired.");
    }

    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public int ActiveConnections => clients.Count;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (acceptLoop is not null) throw new InvalidOperationException("Kernel already started.");
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
                var client = await listener.AcceptTcpClientAsync(stopping.Token);
                if (!connectionSlots.Wait(0)) { client.Dispose(); continue; }
                client.NoDelay = true;
                long id = Interlocked.Increment(ref nextConnection);
                clients[id] = client;
                var task = HandleAsync(client, stopping.Token);
                connections[id] = task;
                _ = RemoveWhenCompleteAsync(id, task);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (SocketException) when (stopping.IsCancellationRequested) { }
    }

    private async Task RemoveWhenCompleteAsync(long id, Task task)
    {
        try { await task; }
        catch (Exception ex) { log($"Connection handler failed ({ex.GetType().Name})."); }
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
            if (cert is null) return false;
            using var presented = new X509Certificate2(cert);
            return CertificateTrust.Validate(presented, caCertificate, CertificateTrust.ClientAuthentication)
                && store.IsOperatorAuthorized(presented);
        }))
        {
            try
            {
                if (!handshakeSlots.Wait(0)) return;
                try
                {
                    using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop);
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = serverCertificate,
                        ClientCertificateRequired = true,
                        EnabledSslProtocols = SslProtocols.Tls13,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                        AllowRenegotiation = false
                    }, handshake.Token);
                }
                finally { handshakeSlots.Release(); }

                using var remote = ssl.RemoteCertificate is null ? null : new X509Certificate2(ssl.RemoteCertificate);
                if (remote is null || !store.IsOperatorAuthorized(remote)) return;
                int maxPayload;
                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    handshake.CancelAfter(TimeSpan.FromSeconds(10));
                    var first = await FrameCodec.ReadAsync(ssl, handshake.Token);
                    if (first is null || first.Type != FrameType.Hello)
                        throw new ProtocolException("HELLO required.");
                    var hello = ProtocolJson.Deserialize<HelloRequest>(first.Payload);
                    if (!hello.Versions.Contains(1) || hello.Role != "terminal" ||
                        hello.RequiredFeatures.Any(f => f != "unary-rpc") ||
                        !hello.RequiredFeatures.Concat(hello.OptionalFeatures).Contains("unary-rpc", StringComparer.Ordinal) ||
                        hello.MaxFrameBytes is < 1024 or > FrameCodec.MaxPayloadBytes)
                    {
                        await SendAsync(ssl, new Frame(FrameType.GoAway, 0, 0,
                            ProtocolJson.Serialize(new GoAwayMessage("Unsupported version, role, feature, or frame limit."))), handshake.Token);
                        return;
                    }
                    maxPayload = hello.MaxFrameBytes;
                    await SendAsync(ssl, new Frame(FrameType.Welcome, 0, 0,
                        ProtocolJson.Serialize(new WelcomeResponse(1, ["unary-rpc"], store.Identity.KernelId,
                            store.Identity.MainframeId, maxPayload, "authenticated"))), handshake.Token);
                }
                await ServeUnaryAsync(ssl, remote, maxPayload, stop);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException
                or SocketException or System.Text.Json.JsonException or CryptographicException
                or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
            {
                // Never echo peer-supplied payloads, credentials, or certificate contents.
                if (!stop.IsCancellationRequested)
                    log(ex is AuthenticationException
                        ? $"TLS authentication failed: {ex.Message}"
                        : $"Connection ended ({ex.GetType().Name}).");
            }
        }
    }

    private async Task ServeUnaryAsync(SslStream ssl, X509Certificate2 remote, int maxPayload, CancellationToken stop)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var timer = Stopwatch.StartNew();
        Task<Frame?>? pending = null;
        byte[]? ping = null;
        ulong lastRequest = 0;
        int exchanges = 0;
        var acceptedExchanges = new HashSet<ulong>();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                pending ??= FrameCodec.ReadAsync(ssl, maxPayload, lifetime.Token).AsTask();
                using var tickCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                var tick = Task.Delay(TimeSpan.FromSeconds(10), tickCancellation.Token);
                var completed = await Task.WhenAny(pending, tick);
                await tickCancellation.CancelAsync();
                if (completed != pending)
                {
                    stop.ThrowIfCancellationRequested();
                    if (!store.IsOperatorAuthorized(remote) || timer.Elapsed >= TimeSpan.FromSeconds(30)) return;
                    if (ping is null)
                    {
                        ping = RandomNumberGenerator.GetBytes(8);
                        await SendAsync(ssl, new Frame(FrameType.Ping, 0, 0, ping), stop);
                    }
                    continue;
                }

                var frame = await pending;
                long receivedAt = Stopwatch.GetTimestamp();
                pending = null;
                if (frame is null) return;
                if (!store.IsOperatorAuthorized(remote))
                {
                    await SendAsync(ssl, new Frame(FrameType.GoAway, 0, 0,
                        ProtocolJson.Serialize(new GoAwayMessage("Identity revoked or expired."))), stop);
                    return;
                }
                timer.Restart();
                switch (frame.Type)
                {
                    case FrameType.Ping:
                        await SendAsync(ssl, new Frame(FrameType.Pong, 0, 0, frame.Payload), stop);
                        break;
                    case FrameType.Pong:
                        if (ping is null || !frame.Payload.AsSpan().SequenceEqual(ping))
                            throw new ProtocolException("Unexpected PONG.");
                        ping = null;
                        break;
                    case FrameType.GoAway:
                        _ = ProtocolJson.Deserialize<GoAwayMessage>(frame.Payload);
                        return;
                    case FrameType.Cancel:
                        if (ProtocolJson.Deserialize<System.Text.Json.JsonElement>(frame.Payload).ValueKind != System.Text.Json.JsonValueKind.Object)
                            throw new ProtocolException("Cancellation must be a JSON object.");
                        // Unary handlers are immediate and side-effect-free; their response is already final.
                        if (!acceptedExchanges.Contains(frame.ExchangeId))
                            throw new ProtocolException("Unknown exchange.");
                        break;
                    case FrameType.Request:
                        if (frame.ExchangeId % 2 == 0 || frame.ExchangeId <= lastRequest)
                            throw new ProtocolException("Request IDs must be increasing odd integers.");
                        lastRequest = frame.ExchangeId;
                        acceptedExchanges.Add(frame.ExchangeId);
                        var request = ProtocolJson.Deserialize<RpcRequest>(frame.Payload);
                        var response = Stopwatch.GetElapsedTime(receivedAt).TotalMilliseconds >= (request.TimeoutMs ?? 10000)
                            ? KernelDispatcher.Error("DEADLINE_EXCEEDED", "The request deadline elapsed before dispatch.")
                            : dispatcher!.Dispatch(request);
                        byte[] payload = ProtocolJson.Serialize(response);
                        if (payload.Length > maxPayload)
                            payload = ProtocolJson.Serialize(KernelDispatcher.Error("RESOURCE_EXHAUSTED", "Result exceeds negotiated frame limit."));
                        await SendAsync(ssl, new Frame(FrameType.Response, frame.ExchangeId, 0, payload), stop);
                        if (++exchanges >= 4096)
                        {
                            await SendAsync(ssl, new Frame(FrameType.GoAway, 0, 0,
                                ProtocolJson.Serialize(new GoAwayMessage("Unary session exchange limit reached."))), stop);
                            return;
                        }
                        break;
                    default:
                        throw new ProtocolException("Frame is not supported in a unary session.");
                }
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            if (pending is not null)
                try { await pending; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
        }
    }

    private static async Task SendAsync(Stream stream, Frame frame, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await FrameCodec.WriteAsync(stream, frame, deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        await stopping.CancelAsync();
        listener.Stop();
        if (acceptLoop is not null) await acceptLoop;
        foreach (var client in clients.Values) client.Dispose();
        try { await Task.WhenAll(connections.Values); }
        finally
        {
            serverCertificate.Dispose();
            caCertificate.Dispose();
            stopping.Dispose();
        }
        // Slot semaphores remain valid for final completion continuations.
    }
}
