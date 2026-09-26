using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mainframe.Protocol;
/// <summary>One reader and one fair writer shared by operator and program connections.</summary>
public sealed class WireConnection : IAsyncDisposable
{
    internal readonly CancellationTokenSource Lifetime = new();
    internal readonly SemaphoreSlim InboundSlots = new(96, 96); // 6 MiB of reserved receive windows.
    internal readonly SemaphoreSlim StorageSlots = new(4, 4); // Each operation reserves 256 KiB, including provider scratch.
    private readonly SemaphoreSlim outboundSlots = new(96, 96);
    internal readonly ConcurrentDictionary<WireChannel, byte> BufferedChannels = new();
    private readonly bool retirement;
    private readonly Dictionary<ulong, HashSet<PendingWrite>> pendingByExchange = new();
    public int RetainedExchangeCount => exchanges.Count + completedExchanges.Count;
    private readonly Stream stream;
    private readonly bool connector;
    private readonly int maxPayload;
    private readonly Func<bool>? authorized;
    private readonly ConcurrentDictionary<ulong, WireExchange> exchanges = new();
    private readonly ConcurrentDictionary<ulong, CompletedExchange> completedExchanges = new();
    private readonly ConcurrentDictionary<ulong, Task> handlers = new();
    private readonly object sendGate = new();
    private readonly Queue<PendingWrite> controls = new();
    private readonly Dictionary<(ulong, uint), Queue<PendingWrite>> data = new();
    private readonly Queue<(ulong, uint)> ready = new();
    private readonly SemaphoreSlim writable = new(0);
    private readonly SemaphoreSlim requestGate = new(1);
    private int controlBytes, dataBytes, active, requestBytes;
    private ulong nextId, lastRemote;
    private Task? reader, writer, heartbeat;
    private long lastTraffic = Stopwatch.GetTimestamp();
    private byte[]? ping;
    private bool draining;
    private int disposed;
    public Func<WireExchange, RpcRequest, Task>? RequestHandler
    {
        get; set;
    }
    public Task Completion => reader ?? Task.CompletedTask;
    public CancellationToken Closed => Lifetime.Token;

    public int BufferedPayloadBytes
    {
        get
        {
            lock (sendGate)
            {
                return (96 - InboundSlots.CurrentCount) * 65536 + dataBytes + controlBytes + Volatile.Read(ref requestBytes) + (4 - StorageSlots.CurrentCount) * 262144;
            }
        }
    }

    internal int MaximumPayload => maxPayload;

    public WireConnection(Stream stream, bool connector, int maxPayload = FrameCodec.MaxPayloadBytes, Func<bool>? authorized = null, bool retirement = false)
    {
        this.retirement = retirement;
        this.stream = stream;
        this.connector = connector;
        this.maxPayload = maxPayload;
        this.authorized = authorized;
        nextId = connector ? 1UL : 2UL;
    }

    public void Start()
    {
        writer = WriteLoopAsync();
        reader = ReadLoopAsync();
        heartbeat = HeartbeatAsync();
    }

    public async Task<WireExchange> RequestAsync(RpcRequest request, CancellationToken token = default)
    {
        await requestGate.WaitAsync(token);
        try
        {
            if (draining || Lifetime.IsCancellationRequested)
            {
                throw new IOException("Connection is closed or draining.");
            }

            ulong id = nextId;
            nextId = checked(nextId + 2);
            WireExchange exchange = AddExchange(id, true);
            await SendAsync(new(FrameType.Request, id, 0, ProtocolJson.Serialize(request)), token);
            return exchange;
        }
        finally
        {
            requestGate.Release();
        }
    }

    private WireExchange AddExchange(ulong id, bool requester)
    {
        if (exchanges.Count + completedExchanges.Count >= 4096)
        {
            throw new ProtocolException("Exchange bookkeeping limit reached.");
        }

        if (Interlocked.Increment(ref active) > 128)
        {
            Interlocked.Decrement(ref active);
            throw new ProtocolException("Exchange bookkeeping limit reached.");
        }

        var exchange = new WireExchange(this, id, requester);
        if (!exchanges.TryAdd(id, exchange))
        {
            throw new ProtocolException("Exchange ID collision.");
        }

        return exchange;
    }

    internal void Terminal(ulong id, bool requester, Dictionary<uint, WireChannel> channels)
    {
        CompletedExchange completed;
        lock (sendGate)
        {
            Task fence = pendingByExchange.TryGetValue(id, out var pending) ? Task.WhenAll(pending.Select(p => p.Done.Task)) : Task.CompletedTask;
            completed = new(requester, channels) { Fence = fence };
            completedExchanges[id] = completed;
            exchanges.TryRemove(id, out WireExchange? exchange);
            completed.RetireReceived = exchange?.RetireReceived == true;
            Interlocked.Decrement(ref active);
        }
        if (retirement && (requester || completed.RetireReceived)) _ = RetireAsync(id, completed);
    }

    private async Task RetireAsync(ulong id, CompletedExchange completed)
    {
        if (Interlocked.Exchange(ref completed.RetirementStarted, 1) != 0) return;
        try
        {
            await completed.Fence.WaitAsync(Closed);
            if (completed.Requester)
            {
                completed.RetireSent = true;
                await SendAsync(new(FrameType.Retire, id, 0, []));
            }
            else
            {
                await SendAsync(new(FrameType.RetireAck, id, 0, []));
                completedExchanges.TryRemove(id, out _);
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void ReceiveRetirement(Frame frame)
    {
        if (!retirement) throw new ProtocolException("Retirement was not negotiated.");
        lock (sendGate)
        {
            if (completedExchanges.TryGetValue(frame.ExchangeId, out CompletedExchange? completed))
            {
                if (frame.Type == FrameType.Retire)
                {
                    if (completed.Requester || completed.RetireReceived) throw new ProtocolException("Unexpected RETIRE.");
                    completed.RetireReceived = true;
                    _ = RetireAsync(frame.ExchangeId, completed);
                }
                else
                {
                    if (!completed.Requester || !completed.RetireSent) throw new ProtocolException("Unexpected RETIRE_ACK.");
                    completedExchanges.TryRemove(frame.ExchangeId, out _);
                }
            }
            else if (frame.Type == FrameType.Retire && exchanges.TryGetValue(frame.ExchangeId, out WireExchange? exchange))
            {
                exchange.AcceptRetire();
            }
            else throw new ProtocolException("Retirement for unknown exchange.");
        }
    }

    private sealed record CompletedExchange(bool Requester, Dictionary<uint, WireChannel> Channels)
    {
        public Task Fence { get; init; } = Task.CompletedTask;
        public int RetirementStarted;
        public volatile bool RetireReceived;
        public volatile bool RetireSent;
        public void Receive(Frame frame)
        {
            if (RetireReceived) throw new ProtocolException("Frame after RETIRE.");
            if (frame.Type == FrameType.Cancel && !Requester)
            {
                if (ProtocolJson.Deserialize<JsonElement>(frame.Payload).ValueKind != JsonValueKind.Object)
                {
                    throw new ProtocolException("Invalid cancellation.");
                }

                return;
            }

            if (frame.Type is not (FrameType.Data or FrameType.EndStream or FrameType.WindowUpdate) || !Channels.TryGetValue(frame.ChannelId, out WireChannel? channel))
            {
                throw new ProtocolException("Illegal frame for completed exchange.");
            }

            channel.Receive(frame, true);
        }
    }

    public async Task DrainAsync()
    {
        if (Lifetime.IsCancellationRequested)
        {
            return;
        }

        draining = true;
        try
        {
            await SendAsync(new(FrameType.GoAway, 0, 0, ProtocolJson.Serialize(new GoAwayMessage("Host shutting down.", 10000))));
            var deadline = Stopwatch.StartNew();
            while (Volatile.Read(ref active) > 0 && deadline.Elapsed < TimeSpan.FromSeconds(10) && !Closed.IsCancellationRequested)
            {
                await Task.Delay(25, Closed);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }

    internal Task SendAsync(Frame frame, CancellationToken token = default, Func<bool>? beforeEnqueue = null)
    {
        token.ThrowIfCancellationRequested();
        if (Lifetime.IsCancellationRequested)
        {
            throw new IOException("Connection closed.");
        }

        if (frame.Payload.Length > maxPayload)
        {
            throw new ProtocolException("Frame exceeds negotiated payload limit.");
        }

        var pending = new PendingWrite(frame);
        lock (sendGate)
        {
            if (retirement && frame.ExchangeId != 0 && frame.Type is not (FrameType.Retire or FrameType.RetireAck))
            {
                // A terminal exchange cannot produce more traffic. Existing queued writes
                // are fenced before RETIRE; racing channel producers are discarded here.
                if (completedExchanges.ContainsKey(frame.ExchangeId) || !exchanges.ContainsKey(frame.ExchangeId)) return Task.CompletedTask;
            }
            if (beforeEnqueue is not null && !beforeEnqueue()) return Task.CompletedTask;
            if (frame.ExchangeId != 0)
            {
                if (!pendingByExchange.TryGetValue(frame.ExchangeId, out var pendingSet)) pendingByExchange[frame.ExchangeId] = pendingSet = new();
                pendingSet.Add(pending);
            }
            if (frame.Type == FrameType.Data)
            {
                if (dataBytes + frame.Payload.Length > 6 * 1024 * 1024)
                {
                    throw new ProtocolException("Outbound buffer limit reached.");
                }

                dataBytes += frame.Payload.Length;
                (ulong ExchangeId, uint ChannelId) key = (frame.ExchangeId, frame.ChannelId);
                if (!data.TryGetValue(key, out Queue<PendingWrite>? queue))
                {
                    queue = new();
                    data[key] = queue;
                    ready.Enqueue(key);
                }

                queue.Enqueue(pending);
            }
            else
            {
                if (controls.Count >= 1024 || controlBytes + frame.Payload.Length > 1024 * 1024)
                {
                    throw new ProtocolException("Control buffer limit reached.");
                }

                controlBytes += frame.Payload.Length;
                controls.Enqueue(pending);
            }
        }

        writable.Release();
        return pending.Done.Task.WaitAsync(token);
    }

    internal async Task SendDataAsync(ulong exchange, uint channel, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, Closed);
        await outboundSlots.WaitAsync(linked.Token);
        try
        {
            // Reserve before copying. Once queued, preserve stream ordering until the
            // writer completes or the connection fails, even if the caller cancels.
            await SendAsync(new(FrameType.Data, exchange, channel, bytes.ToArray()));
        }
        finally
        {
            outboundSlots.Release();
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (true)
            {
                await writable.WaitAsync(Closed);
                PendingWrite pending;
                lock (sendGate)
                {
                    if (controls.Count > 0)
                    {
                        pending = controls.Dequeue();
                    }
                    else
                    {
                        (ulong, uint) key = ready.Dequeue();
                        Queue<PendingWrite> queue = data[key];
                        pending = queue.Dequeue();
                        if (queue.Count == 0)
                        {
                            data.Remove(key);
                        }
                        else
                        {
                            ready.Enqueue(key);
                        }
                    }
                }

                try
                {
                    if (pending.Frame.Type == FrameType.Data && completedExchanges.ContainsKey(pending.Frame.ExchangeId))
                    {
                        pending.Done.TrySetResult();
                        continue;
                    }

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Closed);
                    timeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await FrameCodec.WriteAsync(stream, pending.Frame, timeout.Token);
                    pending.Done.TrySetResult();
                }
                catch (Exception ex)
                {
                    pending.Done.TrySetException(ex);
                    throw;
                }
                finally
                {
                    lock (sendGate)
                    {
                        if (pendingByExchange.TryGetValue(pending.Frame.ExchangeId, out var pendingSet))
                        {
                            pendingSet.Remove(pending);
                            if (pendingSet.Count == 0) pendingByExchange.Remove(pending.Frame.ExchangeId);
                        }
                        if (pending.Frame.Type == FrameType.Data)
                        {
                            dataBytes -= pending.Frame.Payload.Length;
                        }
                        else
                        {
                            controlBytes -= pending.Frame.Payload.Length;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!Closed.IsCancellationRequested)
            {
                Frame frame = await FrameCodec.ReadAsync(stream, maxPayload, Closed) ?? throw new IOException("Peer disconnected.");
                if (authorized is not null && !authorized())
                {
                    throw new IOException("Identity revoked or expired.");
                }

                Volatile.Write(ref lastTraffic, Stopwatch.GetTimestamp());
                switch (frame.Type)
                {
                    case FrameType.Ping:
                        await SendAsync(new(FrameType.Pong, 0, 0, frame.Payload));
                        break;
                    case FrameType.Pong:
                        if (ping is null || !frame.Payload.AsSpan().SequenceEqual(ping))
                        {
                            throw new ProtocolException("Unexpected PONG.");
                        }

                        ping = null;
                        break;
                    case FrameType.GoAway:
                        GoAwayMessage goaway = ProtocolJson.Deserialize<GoAwayMessage>(frame.Payload);
                        draining = true;
                        _ = CloseAfterDrainAsync(goaway.DrainTimeoutMs ?? 10000);
                        break;
                    case FrameType.Retire:
                    case FrameType.RetireAck:
                        ReceiveRetirement(frame);
                        break;
                    case FrameType.Request:
                        if (draining || frame.ExchangeId <= lastRemote || frame.ExchangeId % 2 == (connector ? 1UL : 0UL))
                        {
                            throw new ProtocolException("Invalid request ID or draining connection.");
                        }

                        if (exchanges.Count + completedExchanges.Count >= 4096)
                        {
                            await SendAsync(new(FrameType.GoAway, 0, 0, ProtocolJson.Serialize(new GoAwayMessage("Exchange bookkeeping limit reached.", 0))));
                            throw new ProtocolException("Exchange bookkeeping limit reached.");
                        }

                        lastRemote = frame.ExchangeId;
                        if (Interlocked.Add(ref requestBytes, frame.Payload.Length) > 1024 * 1024)
                        {
                            throw new ProtocolException("Pending request buffer limit reached.");
                        }

                        RpcRequest request = ProtocolJson.Deserialize<RpcRequest>(frame.Payload);
                        WireExchange exchange = AddExchange(frame.ExchangeId, false);
                        var handler = Task.Run(() => DispatchAsync(exchange, request, frame.Payload.Length));
                        handlers[exchange.Id] = handler;
                        _ = RemoveHandlerAsync(exchange.Id, handler);
                        break;
                    default:
                        if (exchanges.TryGetValue(frame.ExchangeId, out WireExchange? existing))
                        {
                            existing.Receive(frame);
                        }
                        else if (completedExchanges.TryGetValue(frame.ExchangeId, out CompletedExchange? completed))
                        {
                            completed.Receive(frame);
                        }
                        else
                        {
                            throw new ProtocolException("Unknown exchange.");
                        }

                        break;
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private async Task DispatchAsync(WireExchange exchange, RpcRequest request, int bytes)
    {
        try
        {
            if (RequestHandler is null)
            {
                throw new ProtocolException("Peer requests are unavailable.");
            }

            await RequestHandler(exchange, request);
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            Interlocked.Add(ref requestBytes, -bytes);
        }
    }

    private async Task RemoveHandlerAsync(ulong id, Task handler)
    {
        await handler;
        handlers.TryRemove(id, out _);
    }

    private async Task CloseAfterDrainAsync(int milliseconds)
    {
        try
        {
            await Task.Delay(milliseconds, Closed);
            Fail(new IOException("Connection drained."));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!Closed.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), Closed);
                if (authorized is not null && !authorized())
                {
                    throw new IOException("Identity revoked or expired.");
                }

                TimeSpan idle = Stopwatch.GetElapsedTime(Volatile.Read(ref lastTraffic));
                if (idle >= TimeSpan.FromSeconds(30))
                {
                    throw new IOException("Peer is unresponsive.");
                }

                if (idle >= TimeSpan.FromSeconds(10) && ping is null)
                {
                    ping = RandomNumberGenerator.GetBytes(8);
                    await SendAsync(new(FrameType.Ping, 0, 0, ping));
                }
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    internal void Fail(Exception error)
    {
        if (!Lifetime.IsCancellationRequested)
        {
            Lifetime.Cancel();
        }

        IOException failure = error as IOException ?? new IOException("Connection failed.", error);
        foreach (WireExchange exchange in exchanges.Values)
        {
            exchange.Fail(failure);
        }

        lock (sendGate)
        {
            foreach (PendingWrite? item in controls.Concat(data.Values.SelectMany(q => q)))
            {
                item.Done.TrySetException(failure);
            }

            controls.Clear();
            data.Clear();
            ready.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        Fail(new IOException("Connection disposed."));
        await stream.DisposeAsync();
        await Task.WhenAll(new[] { reader, writer, heartbeat }.OfType<Task>());
        await Task.WhenAll(handlers.Values);
        foreach (WireChannel channel in BufferedChannels.Keys) channel.Terminate(false);
        foreach (CompletedExchange record in completedExchanges.Values)
        {
            foreach (WireChannel channel in record.Channels.Values)
            {
                channel.Terminate(false);
            }
        }

        completedExchanges.Clear();
    }

    private sealed record PendingWrite(Frame Frame)
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class WireExchange
{
    private readonly WireConnection connection;
    private readonly Dictionary<uint, WireChannel> channels = new();
    private readonly object gate = new();
    private bool responded, terminal;
    private readonly CancellationTokenSource cancelled;
    private readonly CancellationTokenRegistration connectionCancellation;
    public ulong Id
    {
        get;
    }
    public bool Requester
    {
        get;
    }
    public CancellationToken Cancelled => cancelled.Token;
    public bool ConnectionClosed => connection.Closed.IsCancellationRequested;
    public TaskCompletionSource<RpcResponse> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<RpcResponse> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal WireExchange(WireConnection connection, ulong id, bool requester)
    {
        this.connection = connection;
        Id = id;
        Requester = requester;
        cancelled = new CancellationTokenSource();
        connectionCancellation = connection.Closed.Register(() => cancelled.Cancel());
    }

    public async Task<IDisposable> ReserveStorageBufferAsync(CancellationToken token)
    {
        await connection.StorageSlots.WaitAsync(token);
        return new StorageReservation(connection.StorageSlots);
    }

    private sealed class StorageReservation(SemaphoreSlim slots) : IDisposable
    {
        public void Dispose() => slots.Release();
    }

    public WireChannel Channel(uint id)
    {
        lock (gate)
        {
            return channels.TryGetValue(id, out WireChannel? channel) ? channel : throw new ProtocolException("Unknown channel.");
        }
    }

    private void Declare(RpcResponse response)
    {
        if (!response.Streaming)
        {
            return;
        }

        StorageStarted started = ProtocolJson.Deserialize<StorageStarted>(ProtocolJson.Serialize(response.Result!.Value));
        if (started.Channels is null || started.Channels.Length is < 1 or > 8)
        {
            throw new ProtocolException("Invalid channel count.");
        }

        foreach (StreamDescriptor descriptor in started.Channels)
        {
            if (descriptor.Id == 0 || descriptor.Sender is not ("requester" or "responder") || channels.ContainsKey(descriptor.Id))
            {
                throw new ProtocolException("Invalid channel declaration.");
            }

            channels.Add(descriptor.Id, new WireChannel(connection, this, descriptor.Id, (descriptor.Sender == "requester") == Requester));
        }
    }

    public async Task ReplyAsync(RpcResponse response)
    {
        lock (gate)
        {
            if (Requester || responded)
            {
                throw new ProtocolException("Duplicate response.");
            }

            responded = true;
            Declare(response);
        }

        if (!response.Streaming) retirementExpected = true;
        await connection.SendAsync(new(FrameType.Response, Id, 0, ProtocolJson.Serialize(response)));
        if (!response.Streaming)
        {
            End();
        }
        else
        {
            StartReceivers();
        }
    }

    private void StartReceivers()
    {
        foreach (WireChannel? channel in channels.Values.Where(c => !c.Sending))
        {
            channel.Start();
        }
    }

    public async Task CompleteAsync(RpcResponse result)
    {
        lock (gate)
        {
            if (Requester || !responded || terminal || channels.Values.Any(c => c.Sending && !c.Ended))
            {
                throw new ProtocolException("Output must end before COMPLETE.");
            }
        }

        retirementExpected = true;
        await connection.SendAsync(new(FrameType.Complete, Id, 0, ProtocolJson.Serialize(result)));
        Completion.TrySetResult(result);
        End();
    }

    internal volatile bool RetireReceived;
    private volatile bool retirementExpected;
    internal void AcceptRetire()
    {
        if (Requester || !retirementExpected || RetireReceived) throw new ProtocolException("Premature or duplicate RETIRE.");
        RetireReceived = true;
    }

    public Task CancelAsync() => connection.SendAsync(new(FrameType.Cancel, Id, 0, "{}"u8.ToArray()));
    internal void Receive(Frame frame)
    {
        if (RetireReceived) throw new ProtocolException("Frame after RETIRE.");
        lock (gate)
        {
            switch (frame.Type)
            {
                case FrameType.Response:
                    if (!Requester || responded || terminal)
                    {
                        throw new ProtocolException("Unexpected RESPONSE.");
                    }

                    RpcResponse response = ProtocolJson.Deserialize<RpcResponse>(frame.Payload);
                    responded = true;
                    Declare(response);
                    if (!response.Streaming)
                    {
                        Completion.TrySetResult(response);
                        End();
                    }
                    else
                    {
                        StartReceivers();
                    }

                    Response.TrySetResult(response);
                    break;
                case FrameType.Complete:
                    if (!Requester || !responded || terminal || channels.Count == 0 || channels.Values.Any(c => !c.Sending && !c.Ended))
                    {
                        throw new ProtocolException("Unexpected COMPLETE or missing output EOF.");
                    }

                    RpcResponse completed = ProtocolJson.Deserialize<RpcResponse>(frame.Payload);
                    if (completed.Streaming)
                    {
                        throw new ProtocolException("COMPLETE cannot start another stream.");
                    }

                    End(preserveOutput: true);
                    Completion.TrySetResult(completed);
                    break;
                case FrameType.Cancel:
                    if (ProtocolJson.Deserialize<JsonElement>(frame.Payload).ValueKind != JsonValueKind.Object)
                    {
                        throw new ProtocolException("Invalid cancellation.");
                    }

                    if (Requester)
                    {
                        throw new ProtocolException("Only requesters cancel exchanges.");
                    }

                    cancelled.Cancel();
                    break;
                case FrameType.Data:
                case FrameType.EndStream:
                case FrameType.WindowUpdate:
                    Channel(frame.ChannelId).Receive(frame, terminal);
                    break;
                default:
                    throw new ProtocolException("Unexpected exchange frame.");
            }
        }
    }

    private void End(bool preserveOutput = false)
    {
        lock (gate)
        {
            if (terminal)
            {
                return;
            }

            terminal = true;
            connectionCancellation.Unregister();
            connection.Terminal(Id, Requester, channels);
            foreach (WireChannel channel in channels.Values)
            {
                channel.Terminate(preserveOutput && !channel.Sending);
            }
        }
    }

    internal void Fail(Exception error)
    {
        Response.TrySetException(error);
        Completion.TrySetException(error);
        cancelled.Cancel();
        End();
    }
}

/// <summary>A credit-limited stream. Receive storage is reserved before credit is sent.</summary>
public sealed class WireChannel
{
    private readonly WireConnection connection;
    private readonly ulong exchangeId;
    private readonly uint id;
    private readonly object gate = new();
    private readonly SemaphoreSlim sendLock = new(1);
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationTokenRegistration connectionCancellation;
    private TaskCompletionSource changed = Signal();
    private byte[]? ring;
    private int readPosition, count, receiveCredit, sendCredit;
    private bool reserved, terminal;
    public bool Sending
    {
        get;
    }
    public bool Ended
    {
        get; private set;
    }
    public Stream Input
    {
        get;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal WireChannel(WireConnection connection, WireExchange exchange, uint id, bool sending)
    {
        this.connection = connection;
        exchangeId = exchange.Id;
        this.id = id;
        Sending = sending;
        lifetime = new CancellationTokenSource();
        connectionCancellation = connection.Closed.Register(() => lifetime.Cancel());
        Input = new InputStream(this);
    }

    internal void Start() => _ = GrantInitialAsync();
    private async Task GrantInitialAsync()
    {
        try
        {
            await connection.InboundSlots.WaitAsync(lifetime.Token);
            lock (gate)
            {
                if (terminal)
                {
                    connection.InboundSlots.Release();
                    return;
                }

                reserved = true;
                connection.BufferedChannels[this] = 0;
                ring = new byte[65536];
                receiveCredit = 0;
            }

            await GrantAsync(65536, lifetime.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            connection.Fail(ex);
        }
    }

    private Task GrantAsync(int bytes, CancellationToken token) => connection.SendAsync(
        new(FrameType.WindowUpdate, exchangeId, id, ProtocolJson.Serialize(new WindowCredit(bytes))), token, () =>
        {
            lock (gate)
            {
                if (terminal || Ended) return false;
                receiveCredit = checked(receiveCredit + bytes);
                return true;
            }
        });

    internal void Receive(Frame frame, bool completed)
    {
        lock (gate)
        {
            switch (frame.Type)
            {
                case FrameType.WindowUpdate:
                    int credit = ProtocolJson.Deserialize<WindowCredit>(frame.Payload).Credit;
                    if (!Sending || credit <= 0 || credit > 262144 - sendCredit)
                    {
                        throw new ProtocolException("Invalid stream credit.");
                    }

                    sendCredit += credit;
                    Pulse();
                    break;
                case FrameType.Data:
                    if (Sending || Ended || frame.Payload.Length is <= 0 or > 65536 || frame.Payload.Length > receiveCredit)
                    {
                        throw new ProtocolException("DATA exceeds credit or stream state.");
                    }

                    receiveCredit -= frame.Payload.Length;
                    if (completed || terminal)
                    {
                        return; // Only already-authorized bytes may arrive here.
                    }

                    if (ring is null || count + frame.Payload.Length > ring.Length)
                    {
                        throw new ProtocolException("Receive window exhausted.");
                    }

                    int position = (readPosition + count) % ring.Length;
                    int first = Math.Min(frame.Payload.Length, ring.Length - position);
                    frame.Payload.AsSpan(0, first).CopyTo(ring.AsSpan(position));
                    frame.Payload.AsSpan(first).CopyTo(ring);
                    count += frame.Payload.Length;
                    Pulse();
                    break;
                case FrameType.EndStream:
                    if (Sending || Ended)
                    {
                        throw new ProtocolException("Duplicate or wrong-direction EOF.");
                    }

                    Ended = true;
                    Pulse();
                    if (count == 0)
                    {
                        Release();
                    }

                    break;
            }
        }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await sendLock.WaitAsync(linked.Token);
        try
        {
            while (!bytes.IsEmpty)
            {
                Task? wait = null;
                int length = 0;
                lock (gate)
                {
                    if (!Sending || Ended || terminal)
                    {
                        throw new IOException("Stream is closed.");
                    }

                    length = Math.Min(Math.Min(Math.Min(65536, connection.MaximumPayload), sendCredit), bytes.Length);
                    if (length == 0)
                    {
                        wait = changed.Task;
                    }
                    else
                    {
                        sendCredit -= length;
                    }
                }

                if (wait is not null)
                {
                    await wait.WaitAsync(linked.Token);
                    continue;
                }

                await connection.SendDataAsync(exchangeId, id, bytes[..length], linked.Token);
                bytes = bytes[length..];
            }
        }
        finally
        {
            sendLock.Release();
        }
    }

    public async Task EndAsync()
    {
        await sendLock.WaitAsync(connection.Closed);
        try
        {
            lock (gate)
            {
                if (Ended || terminal)
                {
                    return;
                }

                if (!Sending)
                {
                    throw new InvalidOperationException();
                }

                Ended = true;
            }

            await connection.SendAsync(new(FrameType.EndStream, exchangeId, id, []));
        }
        finally
        {
            sendLock.Release();
        }
    }

    private async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken token)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        while (true)
        {
            int length = 0;
            Task? wait = null;
            bool grant = false;
            lock (gate)
            {
                if (count > 0)
                {
                    length = Math.Min(count, destination.Length);
                    int first = Math.Min(length, ring!.Length - readPosition);
                    ring.AsMemory(readPosition, first).CopyTo(destination);
                    ring.AsMemory(0, length - first).CopyTo(destination[first..]);
                    readPosition = (readPosition + length) % ring.Length;
                    count -= length;
                    grant = !Ended && !terminal;
                    if (count == 0 && (Ended || terminal))
                    {
                        Release();
                    }
                }
                else if (Ended || terminal)
                {
                    return 0;
                }
                else
                {
                    wait = changed.Task;
                }
            }

            if (wait is not null)
            {
                await wait.WaitAsync(token);
                continue;
            }

            if (grant)
            {
                await GrantAsync(length, token);
            }

            return length;
        }
    }

    internal void Terminate(bool preserve)
    {
        lock (gate)
        {
            terminal = true;
            lifetime.Cancel();
            connectionCancellation.Unregister();
            if (!preserve || count == 0)
            {
                count = 0;
                Release();
            }

            Pulse();
        }
    }

    private void Release()
    {
        ring = null;
        connection.BufferedChannels.TryRemove(this, out _);
        if (reserved)
        {
            reserved = false;
            connection.InboundSlots.Release();
        }
    }

    private void Pulse()
    {
        TaskCompletionSource previous = changed;
        changed = Signal();
        previous.TrySetResult();
    }

    private sealed class InputStream(WireChannel channel) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException(); set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => channel.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
