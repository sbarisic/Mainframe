using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Mainframe.Core;
using Mainframe.Protocol;

namespace Mainframe.Host;

internal sealed class ExecutionService(KernelStore store, Func<int> port, Action<string> log, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, Execution> executions = new();
    private readonly ConcurrentDictionary<string, Execution> bootstraps = new();
    private readonly SemaphoreSlim processSlots = new(64, 64);
    public (Func<bool> Valid, Func<string, bool> Allows)? Authenticate(string token)
    {
        if (token.Length != 64 || !bootstraps.TryRemove(Hash(token), out Execution? execution) || clock.GetUtcNow() >= execution.BootstrapExpiry || !execution.Authorized())
        {
            return null;
        }

        return (execution.Authorized, method => execution.Permissions.Contains(method) && execution.Authorized() && execution.HasGrant(method));
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    public async Task HandleAsync(WireExchange exchange, RpcRequest request, OperatorSession session, KernelDispatcher dispatcher)
    {
        bool streaming = false;
        try
        {
            if (request.Version != 1)
            {
                throw new KernelOperationException("UNSUPPORTED_METHOD", "Unsupported method version.");
            }

            if (request.TimeoutMs is > 30000)
            {
                throw new ArgumentException("Launch deadline must be at most 30 seconds.");
            }

            if (!session.Allows(request.Method))
            {
                throw new KernelOperationException("ACCESS_DENIED", "This identity is not granted that operation.");
            }

            object result;
            switch (request.Method)
            {
                case "program.register":
                    result = store.RegisterProgram(Args<RegisterProgramRequest>(request).Manifest);
                    break;
                case "program.list":
                    result = store.ListPrograms();
                    break;
                case "program.remove":
                    store.RemoveProgram(Args<SelectorRequest>(request).Name);
                    result = new EmptyArguments();
                    break;
                case "host-root.add":
                    result = store.AddHostRoot(Args<HostRoot>(request));
                    break;
                case "host-root.list":
                    result = store.ListHostRoots();
                    break;
                case "host-root.remove":
                    store.RemoveHostRoot(Args<SelectorRequest>(request).Name);
                    result = new EmptyArguments();
                    break;
                case "shell.open":
                    lock (session.Shells)
                    {
                        if (session.Shells.Count >= 16)
                        {
                            throw new KernelOperationException("RESOURCE_EXHAUSTED", "Shell session limit reached.");
                        }

                        var shell = new ShellState(Guid.NewGuid().ToString("N"), "/");
                        session.Shells[shell.SessionId] = shell;
                        result = shell;
                    }

                    break;
                case "shell.command":
                    ShellCommandRequest command = Args<ShellCommandRequest>(request);
                    ShellState state = session.GetShell(command.SessionId);
                    var words = ParseCommand(command.Line);
                    if (words.Length == 0)
                    {
                        result = state;
                        break;
                    }

                    switch (words[0])
                    {
                        case "help":
                            result = state with
                            {
                                Text = "help | programs | pwd | cd /host/<name> | exit | <registered-program> [arguments]"
                            };
                            break;
                        case "programs":
                            result = state with
                            {
                                Text = string.Join('\n', store.ListPrograms().Select(p => p.QualifiedName + (p.Incompatibility is null ? "" : " (" + p.Incompatibility + ")")))
                            };
                            break;
                        case "pwd":
                            result = state with
                            {
                                Text = state.WorkingDirectory
                            };
                            break;
                        case "exit":
                            session.Shells.TryRemove(state.SessionId, out _);
                            result = state with
                            {
                                Closed = true
                            };
                            break;
                        case "cd":
                            if (words.Length != 2)
                            {
                                throw new ArgumentException("Usage: cd <directory>");
                            }

                            (string Logical, string? Host, string? Root) location = store.ResolveDirectory(state.WorkingDirectory, words[1], session.Principal);
                            state = state with
                            {
                                WorkingDirectory = location.Logical
                            };
                            session.Shells[state.SessionId] = state;
                            result = state;
                            break;
                        default:
                            streaming = true;
                            await RunAsync(exchange, new(words[0], words.Skip(1).ToArray(), "terminal", state.SessionId, command.Columns, command.Rows), session, request.TimeoutMs ?? 10000);
                            return;
                    }

                    break;
                case "process.start":
                    streaming = true;
                    await RunAsync(exchange, Args<ProcessStartRequest>(request), session, request.TimeoutMs ?? 10000);
                    return;
                case "process.resize":
                case "process.interrupt":
                    ProcessControlRequest control = Args<ProcessControlRequest>(request);
                    if (!executions.TryGetValue(control.ProcessId, out Execution? live) || live.Owner != session.Id)
                    {
                        throw new KernelOperationException("NOT_FOUND", "Execution is unavailable to this session.");
                    }

                    if (request.Method == "process.resize")
                    {
                        live.Process.Resize(control.Columns, control.Rows);
                    }
                    else
                    {
                        if (control.Signal is not (null or "interrupt"))
                        {
                            throw new ArgumentException("Unsupported signal.");
                        }

                        await live.Process.InterruptAsync();
                    }

                    result = new EmptyArguments();
                    break;
                default:
                    await exchange.ReplyAsync(dispatcher.Dispatch(request));
                    return;
            }

            await exchange.ReplyAsync(Success(result));
        }
        catch (Exception ex) when (!streaming && ex is KernelOperationException or ArgumentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            await exchange.ReplyAsync(KernelDispatcher.Error(ex is KernelOperationException op ? op.Code : "INVALID_ARGUMENT", ex.Message));
        }
    }

    private async Task RunAsync(WireExchange exchange, ProcessStartRequest request, OperatorSession session, int timeoutMs)
    {
        bool replied = false;
        string id = Guid.NewGuid().ToString("N");
        Execution? live = null;
        bool recorded = false;
        string? bootstrapKey = null;
        bool shellLocked = false, slotReserved = false;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(exchange.Cancelled);
        try
        {
            var startTime = Stopwatch.GetTimestamp();
            if (request.IoMode is not ("pipes" or "terminal") || request.Argv is null || request.Argv.Length > 256 || request.Argv.Any(a => a is null || a.Contains('\0')))
            {
                throw new ArgumentException("Invalid process arguments or I/O mode.");
            }

            if (!processSlots.Wait(0))
            {
                throw new KernelOperationException("RESOURCE_EXHAUSTED", "Local process limit reached.");
            }

            slotReserved = true;
            ProgramRegistration registration = store.ResolveProgram(request.Program, session.Principal, request.IoMode);
            string directory = registration.Manifest.WorkingDirectory;
            if (request.SessionId is not null)
            {
                if (!session.BusyShells.TryAdd(request.SessionId, true))
                {
                    throw new KernelOperationException("RESOURCE_UNAVAILABLE", "Shell already has a foreground execution.");
                }

                shellLocked = true;
                ShellState shell = session.GetShell(request.SessionId);
                (string Logical, string? Host, string? Root) location = store.ResolveDirectory("/", shell.WorkingDirectory, session.Principal);
                if (location.Host is not null)
                {
                    if (!registration.Manifest.HostRoots.Contains(location.Root!))
                    {
                        throw new KernelOperationException("ACCESS_DENIED", "Program manifest does not permit this host root.");
                    }

                    directory = location.Host;
                }
            }

            KernelStore.ValidateDirectory(directory);
            var permissions = registration.Manifest.Permissions.Concat(registration.Manifest.HostRoots.Select(r => "host-root:" + r)).Append("program:" + registration.Manifest.Identity).Where(p => store.HasGrant(session.Principal, p)).ToArray();
            store.RecordExecution(id, session.Principal, registration.Revision, permissions);
            recorded = true;
            lifetime.Token.ThrowIfCancellationRequested();
            using var process = WindowsProcess.Start(registration.Manifest, request.Argv, directory, request.IoMode == "terminal", request.Columns, request.Rows, $"127.0.0.1:{port()}");
            live = new Execution(process, session.Id, permissions, session.Valid, clock, permission => store.HasGrant(session.Principal, permission));
            executions[id] = live;
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            bootstrapKey = Hash(token);
            bootstraps[bootstrapKey] = live;
            using X509Certificate2 ca = store.LoadCaCertificate();
            var credential = new BootstrapCredential(token, "127.0.0.1", port(), Convert.ToBase64String(ca.RawData));
            await process.Bootstrap.WriteAsync(ProtocolJson.Serialize(credential), lifetime.Token);
            process.Bootstrap.Dispose();
            if (Stopwatch.GetElapsedTime(startTime).TotalMilliseconds >= timeoutMs)
            {
                throw new KernelOperationException("DEADLINE_EXCEEDED", "Launch deadline elapsed.");
            }

            lifetime.Token.ThrowIfCancellationRequested();
            store.MarkExecutionRunning(id);
            StreamDescriptor[] channels = request.IoMode == "terminal" ? [new(1, "stdin", "requester"), new(2, "terminal", "responder")] : [new(1, "stdin", "requester"), new(2, "stdout", "responder"), new(3, "stderr", "responder")];
            await exchange.ReplyAsync(Success(new ProcessStarted(id, channels), true));
            replied = true;
            Task stdout = PumpOutputAsync(process.Stdout, exchange.Channel(2), lifetime.Token);
            Task stderr = process.Stderr is null ? Task.CompletedTask : PumpOutputAsync(process.Stderr, exchange.Channel(3), lifetime.Token);
            Task stdin = PumpInputAsync(exchange.Channel(1).Input, process.Stdin, lifetime.Token);
            Task renewal = RenewAsync(live, session, registration, lifetime);
            int exitCode;
            bool cancelled = false;
            try
            {
                exitCode = await process.WaitAsync(lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
                await process.StopAsync();
                exitCode = await process.WaitAsync();
            }

            process.KillDescendants();
            live.Alive = false;
            Task closeTerminal = process.CloseTerminalAsync();
            // Output EOF must be observed before COMPLETE. A slow client applies backpressure here.
            try
            {
                await Task.WhenAll(stdout, stderr, closeTerminal);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                if (!cancelled)
                {
                    throw;
                }
            }

            await exchange.Channel(2).EndAsync();
            if (process.Stderr is not null)
            {
                await exchange.Channel(3).EndAsync();
            }

            store.FinishExecution(id, exitCode, cancelled ? "cancelled" : "completed");
            recorded = false;
            await exchange.CompleteAsync(Success(new ProcessExited(exitCode, cancelled)));
            lifetime.Cancel();
            process.Stdin.Dispose();
            try
            {
                await Task.WhenAll(stdin, renewal);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
        catch (Exception ex) when (ex is KernelOperationException or ArgumentException or IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            log($"Execution ended unsuccessfully ({ex.GetType().Name}, {ex.HResult}).");
            if (live is not null)
            {
                live.Alive = false;
                try
                {
                    await live.Process.StopAsync();
                }
                catch (Exception stopError) when (stopError is InvalidOperationException or ObjectDisposedException)
                {
                }
            }

            if (recorded)
            {
                store.FinishExecution(id, null, replied ? "outcome_unknown" : "failed");
            }

            if (!replied && !exchange.ConnectionClosed)
            {
                await exchange.ReplyAsync(new(false, false, null, new(ex is OperationCanceledException ? "CANCELLED" : ex is KernelOperationException op ? op.Code : "LAUNCH_FAILED", ex.Message, live is null ? "not_started" : "unknown")));
            }
            else if (replied && !exchange.ConnectionClosed)
            {
                await exchange.Channel(2).EndAsync();
                if (request.IoMode == "pipes")
                {
                    await exchange.Channel(3).EndAsync();
                }

                await exchange.CompleteAsync(new(false, false, null, new("EXECUTION_FAILED", "Execution could not complete.", "unknown")));
            }
        }
        finally
        {
            lifetime.Cancel();
            if (live is not null)
            {
                live.Alive = false;
            }

            if (bootstrapKey is not null)
            {
                bootstraps.TryRemove(bootstrapKey, out _);
            }

            executions.TryRemove(id, out _);
            if (shellLocked)
            {
                session.BusyShells.TryRemove(request.SessionId!, out _);
            }

            if (slotReserved)
            {
                processSlots.Release();
            }
        }
    }

    private async Task RenewAsync(Execution live, OperatorSession session, ProgramRegistration registration, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(1000, lifetime.Token);
                if (clock.GetUtcNow() >= live.LeaseExpiry || !session.Valid() || !store.HasGrant(session.Principal, "program:" + registration.Manifest.Identity) || live.Permissions.Any(p => !store.HasGrant(session.Principal, p)))
                {
                    live.Alive = false;
                    lifetime.Cancel();
                    return;
                }

                live.LeaseExpiry = clock.GetUtcNow().AddSeconds(60);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            TimeSpan remaining = live.LeaseExpiry - clock.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(remaining, lifetime.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }

            live.Alive = false;
            lifetime.Cancel();
        }
    }

    private static async Task PumpOutputAsync(Stream source, WireChannel target, CancellationToken token)
    {
        byte[] buffer = new byte[65536];
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.SendAsync(buffer.AsMemory(0, count), token);
            }
        }
        catch (OperationCanceledException)
        {
            // ClosePseudoConsole can wait for its output pipe. Continue consuming native
            // output during cancellation so terminating a noisy program cannot deadlock.
            try
            {
                await source.CopyToAsync(Stream.Null);
            }
            catch (IOException)
            {
            }
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) == 109)
        {
        } // Windows broken pipe is EOF.

        await target.EndAsync();
    }

    private static async Task PumpInputAsync(Stream source, Stream target, CancellationToken token)
    {
        try
        {
            await source.CopyToAsync(target, 65536, token);
            await target.FlushAsync(token);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
        finally
        {
            target.Dispose();
        }
    }

    internal static RpcResponse Success(object result, bool streaming = false) => new(true, streaming, ProtocolJson.ToElement(result), null);
    internal static T Args<T>(RpcRequest request) => ProtocolJson.Deserialize<T>(ProtocolJson.Serialize(request.Arguments));
    public static string[] ParseCommand(string line)
    {
        if (line.Length > 32767)
        {
            throw new ArgumentException("Command is too long.");
        }

        var words = new List<string>();
        var word = new StringBuilder();
        char quote = '\0';
        bool present = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\'' or '\\')
            {
                word.Append(line[++i]);
                present = true;
                continue;
            }

            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }

                present = true;
                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                present = true;
                continue;
            }

            if (c is '|' or '<' or '>' or '$' or '`' or '&' or ';')
            {
                throw new ArgumentException("Shell operators and expansion are not supported.");
            }

            if (char.IsWhiteSpace(c))
            {
                if (present)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    present = false;
                }
            }
            else
            {
                word.Append(c);
                present = true;
            }
        }

        if (quote != '\0')
        {
            throw new ArgumentException("Unclosed quote.");
        }

        if (present)
        {
            words.Add(word.ToString());
        }

        return words.ToArray();
    }

    private sealed class Execution(WindowsProcess process, string owner, string[] permissions, Func<bool> operatorValid, TimeProvider clock, Func<string, bool> hasGrant)
    {
        public WindowsProcess Process { get; } = process;
        public string Owner { get; } = owner;
        public string[] Permissions { get; } = permissions;
        public Func<string, bool> HasGrant { get; } = hasGrant;
        public DateTimeOffset BootstrapExpiry { get; } = clock.GetUtcNow().AddSeconds(30);

        private long expiryTicks = clock.GetUtcNow().AddSeconds(60).UtcTicks;
        public DateTimeOffset LeaseExpiry
        {
            get => new(Interlocked.Read(ref expiryTicks), TimeSpan.Zero); set => Interlocked.Exchange(ref expiryTicks, value.UtcTicks);
        }

        public volatile bool Alive = true;
        public bool Authorized() => Alive && clock.GetUtcNow() < LeaseExpiry && operatorValid();
    }
}

internal sealed class OperatorSession(string principal, Func<bool> valid, Func<string, bool> allows, bool isOperator = true)
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Principal { get; } = principal;
    public bool IsOperator { get; } = isOperator;
    public Func<bool> Valid { get; } = valid;
    public Func<string, bool> Allows { get; } = allows;
    public ConcurrentDictionary<string, ShellState> Shells { get; } = new();
    public ConcurrentDictionary<string, bool> BusyShells { get; } = new();

    public ShellState GetShell(string id) => Shells.TryGetValue(id, out ShellState? value) ? value : throw new KernelOperationException("NOT_FOUND", "Shell is unavailable to this connection.");
}
