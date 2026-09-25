using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mainframe.Protocol;

namespace Mainframe.Client;

/// <summary>
/// Authenticated unary RPC client. Calls are serialized and never replayed. A cancelled
/// or timed-out call closes its connection because the remote outcome may be unknown.
/// </summary>
public sealed class KernelClient : IAsyncDisposable
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(10);
    private readonly TcpClient _tcp;
    private readonly SslStream _stream;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly Task _readTask;
    private readonly Task _heartbeatTask;
    private TaskCompletionSource<Frame>? _pending;
    private ulong _pendingId;
    private ulong _nextExchangeId = 1;
    private byte[]? _heartbeatToken;
    private Exception? _failure;
    private bool _goingAway;
    private int _disposed;
    private long _lastReceived = Stopwatch.GetTimestamp();
    private long _lastActivity = Stopwatch.GetTimestamp();

    public WelcomeResponse Welcome { get; }

    private KernelClient(TcpClient tcp, SslStream stream, WelcomeResponse welcome)
    {
        _tcp = tcp;
        _stream = stream;
        Welcome = welcome;
        _readTask = ReadLoopAsync();
        _heartbeatTask = HeartbeatLoopAsync();
    }

    public static async Task<KernelClient> ConnectAsync(
        string host, int port, X509Certificate2 clientCertificate, X509Certificate2 caCertificate,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ArgumentNullException.ThrowIfNull(caCertificate);
        if (!clientCertificate.HasPrivateKey)
            throw new ArgumentException("The operator certificate requires a private key.", nameof(clientCertificate));

        var tcp = new TcpClient { NoDelay = true };
        SslStream? stream = null;
        try
        {
            using (var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectDeadline.CancelAfter(StageTimeout);
                await tcp.ConnectAsync(host, port, connectDeadline.Token).ConfigureAwait(false);
            }
            stream = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false,
                (_, certificate, _, errors) => ValidateServerCertificate(certificate, errors, caCertificate));
            using (var tlsDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                tlsDeadline.CancelAfter(StageTimeout);
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.Tls13,
                    ClientCertificates = new X509CertificateCollection { clientCertificate },
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    EncryptionPolicy = EncryptionPolicy.RequireEncryption
                }, tlsDeadline.Token).ConfigureAwait(false);
            }
            using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            helloDeadline.CancelAfter(StageTimeout);
            var hello = new HelloRequest([ProtocolVersions.Current], [], [ProtocolVersions.UnaryRpcFeature],
                "terminal", "mf", FrameCodec.MaxPayloadBytes);
            await FrameCodec.WriteAsync(stream, new Frame(FrameType.Hello, 0, 0, ProtocolJson.Serialize(hello)), helloDeadline.Token).ConfigureAwait(false);
            Frame frame = await FrameCodec.ReadAsync(stream, helloDeadline.Token).ConfigureAwait(false)
                ?? throw new ProtocolException("The kernel closed the connection before WELCOME.");
            if (frame.Type == FrameType.GoAway)
                throw new ProtocolException($"The kernel rejected the connection: {ProtocolJson.Deserialize<GoAwayMessage>(frame.Payload).Reason}");
            if (frame.Type != FrameType.Welcome)
                throw new ProtocolException("Expected WELCOME after HELLO.");
            WelcomeResponse welcome = ProtocolJson.Deserialize<WelcomeResponse>(frame.Payload);
            if (welcome.Version != ProtocolVersions.Current ||
                !welcome.Features.Contains(ProtocolVersions.UnaryRpcFeature, StringComparer.Ordinal) ||
                welcome.Features.Any(feature => feature != ProtocolVersions.UnaryRpcFeature))
                throw new ProtocolException("The kernel selected unsupported protocol features.");
            if (welcome.Authentication != "authenticated")
                throw new AuthenticationException("The kernel did not authenticate the operator certificate.");
            if (frame.Payload.Length > welcome.MaxFrameBytes)
                throw new ProtocolException("WELCOME exceeds its negotiated payload limit.");
            return new KernelClient(tcp, stream, welcome);
        }
        catch
        {
            if (stream is not null)
                await stream.DisposeAsync().ConfigureAwait(false);
            tcp.Dispose();
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, object? arguments = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (method.Length > 128)
            throw new ArgumentException("Method names must not exceed 128 characters.", nameof(method));
        JsonElement args = ProtocolJson.ToElement(arguments ?? new JsonObject());
        if (args.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("RPC arguments must be a JSON object.", nameof(arguments));
        byte[] payload = ProtocolJson.Serialize(new RpcRequest(method, 1, 10_000, args));
        if (payload.Length > Welcome.MaxFrameBytes)
            throw new ProtocolException("Request exceeds the negotiated frame limit.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        deadline.CancelAfter(StageTimeout);
        bool acquired = false;
        bool began = false;
        try
        {
            await _callLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            acquired = true;
            TaskCompletionSource<Frame> pending;
            ulong exchangeId;
            lock (_stateLock)
            {
                ThrowIfUnavailable();
                if (_nextExchangeId > ulong.MaxValue - 2)
                    throw new ProtocolException("Exchange IDs are exhausted; create a new connection.");
                exchangeId = _nextExchangeId;
                _nextExchangeId += 2;
                pending = new TaskCompletionSource<Frame>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = pending;
                _pendingId = exchangeId;
                began = true;
            }
            await SendAsync(new Frame(FrameType.Request, exchangeId, 0, payload), deadline.Token).ConfigureAwait(false);
            Frame frame = await pending.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            RpcResponse response = ProtocolJson.Deserialize<RpcResponse>(frame.Payload);
            if (response.Streaming)
                throw new ProtocolException("The unary client cannot accept a streaming response.");
            if (!response.Ok)
                throw new KernelRpcException(response.Error!.Code, response.Error.Message, response.Error.Outcome);
            return response.Result!.Value.Clone();
        }
        catch (KernelRpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            bool timedOut = ex is OperationCanceledException && !cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested;
            if (began)
                Fail(ex);
            if (timedOut)
                throw new TimeoutException("The kernel request deadline elapsed; its remote outcome may be unknown.", ex);
            throw;
        }
        finally
        {
            if (began)
            {
                bool closeAfterDrain;
                lock (_stateLock)
                {
                    _pending = null;
                    _pendingId = 0;
                    closeAfterDrain = _goingAway;
                }
                if (closeAfterDrain)
                    Fail(new IOException("The kernel is shutting down."));
            }
            if (acquired)
                _callLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                Frame frame = await FrameCodec.ReadAsync(_stream, Welcome.MaxFrameBytes, _lifetime.Token).ConfigureAwait(false)
                    ?? throw new IOException("The kernel closed the connection.");
                Interlocked.Exchange(ref _lastReceived, Stopwatch.GetTimestamp());
                Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());
                switch (frame.Type)
                {
                    case FrameType.Response:
                        lock (_stateLock)
                        {
                            if (_pending is null || frame.ExchangeId != _pendingId || !_pending.TrySetResult(frame))
                                throw new ProtocolException("Response does not match the active exchange.");
                        }
                        break;
                    case FrameType.Ping:
                        await SendAsync(new Frame(FrameType.Pong, 0, 0, frame.Payload), _lifetime.Token).ConfigureAwait(false);
                        break;
                    case FrameType.Pong:
                        lock (_stateLock)
                        {
                            if (_heartbeatToken is null || !CryptographicOperations.FixedTimeEquals(_heartbeatToken, frame.Payload))
                                throw new ProtocolException("PONG does not match an outstanding PING.");
                            _heartbeatToken = null;
                        }
                        break;
                    case FrameType.GoAway:
                        GoAwayMessage shutdown = ProtocolJson.Deserialize<GoAwayMessage>(frame.Payload);
                        bool hasPending;
                        lock (_stateLock)
                        {
                            if (_goingAway)
                                throw new ProtocolException("Duplicate GOAWAY.");
                            _goingAway = true;
                            hasPending = _pending is not null;
                        }
                        if (!hasPending)
                            throw new IOException($"The kernel is shutting down: {shutdown.Reason}");
                        _ = StopAfterDrainAsync(shutdown.DrainTimeoutMs ?? 10_000);
                        break;
                    default:
                        throw new ProtocolException("Unexpected frame on an authenticated unary connection.");
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceived)) >= TimeSpan.FromSeconds(30))
                    throw new IOException("The kernel is unresponsive.");
                byte[]? token = null;
                lock (_stateLock)
                {
                    if (!_goingAway && _heartbeatToken is null && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastActivity)) >= TimeSpan.FromSeconds(10))
                        token = _heartbeatToken = RandomNumberGenerator.GetBytes(8);
                }
                if (token is not null)
                    await SendAsync(new Frame(FrameType.Ping, 0, 0, token), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private async Task StopAfterDrainAsync(int milliseconds)
    {
        try
        {
            await Task.Delay(milliseconds, _lifetime.Token).ConfigureAwait(false);
            Fail(new IOException("The kernel shutdown drain deadline elapsed."));
        }
        catch (OperationCanceledException) { }
    }

    private async Task SendAsync(Frame frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FrameCodec.WriteAsync(_stream, frame, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastActivity, Stopwatch.GetTimestamp());
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static bool ValidateServerCertificate(X509Certificate? certificate, SslPolicyErrors errors, X509Certificate2 root)
    {
        if (certificate is null || (errors & (SslPolicyErrors.RemoteCertificateNotAvailable | SslPolicyErrors.RemoteCertificateNameMismatch)) != 0)
            return false;
        using var server = new X509Certificate2(certificate);
        X509BasicConstraintsExtension[] constraints = server.Extensions.OfType<X509BasicConstraintsExtension>().ToArray();
        X509EnhancedKeyUsageExtension[] usages = server.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (constraints.Length != 1 || constraints[0].CertificateAuthority || usages.Length != 1 ||
            !usages[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == "1.3.6.1.5.5.7.3.1"))
            return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(server) && chain.ChainElements.Count > 1 &&
            chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(root.RawData);
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_failure is not null)
            throw new IOException("The kernel connection is closed.", _failure);
        if (_goingAway)
            throw new IOException("The kernel is shutting down and accepts no new requests.");
    }

    private void Fail(Exception failure)
    {
        lock (_stateLock)
        {
            if (_failure is not null)
                return;
            _failure = failure;
            _pending?.TrySetException(failure);
        }
        _lifetime.Cancel();
        _tcp.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Fail(new ObjectDisposedException(nameof(KernelClient)));
        await Task.WhenAll(_readTask, _heartbeatTask).ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        // The semaphore and cancellation objects may still be observed by a concurrently
        // cancelled caller. Their managed resources are reclaimed with this client.
    }
}
