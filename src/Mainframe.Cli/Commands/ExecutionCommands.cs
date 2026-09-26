using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Mainframe.Client;
using Mainframe.Protocol;

namespace Mainframe.Cli.Commands;

internal static class OperatorClient
{
    public static async Task<KernelClient> ConnectAsync(CommandContext context, CancellationToken token)
    {
        KernelEndpoint endpoint = context.GetEndpoint();
        using X509Certificate2 ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(context.StateDirectory, "ca.cer"));
        using X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(context.StateDirectory, "operator.pfx"), null, OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet);
        return await KernelClient.ConnectAsync(endpoint.Host, endpoint.Port, certificate, ca, token);
    }
}

public sealed class ProgramCommand : ICommand
{
    public string Name => "program";
    public string Description => "Register, list, or remove installed programs.";
    public string Usage => "mframe program register <manifest.json> | list | remove <qualified-name> [--state DIR] [--endpoint HOST:PORT] [--json]";

    public async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Validate(Usage, "--state", "--endpoint", "--json");
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0 || !(args[0] == "list" && args.Count == 1 || args[0] is "register" or "remove" && args.Count == 2))
            throw new UsageException(Usage);
        ProgramManifest? manifest = null;
        if (args[0] == "register")
        {
            if (new FileInfo(args[1]).Length > FrameCodec.MaxPayloadBytes / 2)
                throw new UsageException("Manifest exceeds the size limit.");
            manifest = ProtocolJson.Deserialize<ProgramManifest>(await File.ReadAllBytesAsync(args[1], cancellationToken));
        }

        await using KernelClient client = await OperatorClient.ConnectAsync(context, cancellationToken);
        switch (args[0])
        {
            case "register":
                ProgramRegistration registration = await client.RegisterProgramAsync(manifest!, cancellationToken);
                if (context.Json)
                    context.WriteJson(registration);
                else
                    context.Output.WriteLine(registration.QualifiedName + (registration.Incompatibility is null ? "" : " (" + registration.Incompatibility + ")"));
                break;
            case "list":
                ProgramRegistration[] programs = await client.ListProgramsAsync(cancellationToken);
                if (context.Json)
                    context.WriteJson(programs);
                else
                    foreach (ProgramRegistration program in programs)
                        context.Output.WriteLine(program.QualifiedName + (program.Incompatibility is null ? "" : " (" + program.Incompatibility + ")"));
                break;
            case "remove":
                await client.RemoveProgramAsync(args[1], cancellationToken);
                if (context.Json)
                    context.WriteJson(new { removed = args[1] });
                else
                    context.Output.WriteLine("Registration removed.");
                break;
        }

        return 0;
    }
}

public sealed class HostRootCommand : ICommand
{
    public string Name => "host-root";
    public string Description => "Manage approved shell working-directory roots.";
    public string Usage => "mframe host-root add <name> <absolute-directory> | list | remove <name> [--state DIR] [--endpoint HOST:PORT] [--json]";

    public async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Validate(Usage, "--state", "--endpoint", "--json");
        IReadOnlyList<string> args = context.Arguments;
        if (args.Count == 0 || !(args[0] == "list" && args.Count == 1 || args[0] == "add" && args.Count == 3 || args[0] == "remove" && args.Count == 2))
            throw new UsageException(Usage);
        await using KernelClient client = await OperatorClient.ConnectAsync(context, cancellationToken);
        if (args[0] == "add")
        {
            JsonElement result = await client.AddHostRootAsync(new(args[1], args[2]), cancellationToken);
            if (context.Json)
                context.WriteJson(result);
            else
                context.Output.WriteLine($"Approved /host/{args[1]}.");
        }
        else if (args[0] == "remove")
        {
            await client.RemoveHostRootAsync(args[1], cancellationToken);
            if (context.Json)
                context.WriteJson(new { removed = args[1] });
            else
                context.Output.WriteLine("Host root removed.");
        }
        else
        {
            HostRoot[] roots = await client.ListHostRootsAsync(cancellationToken);
            if (context.Json)
                context.WriteJson(roots);
            else
                foreach (HostRoot root in roots)
                    context.Output.WriteLine($"/host/{root.Name} -> {root.Path}");
        }

        return 0;
    }
}

public sealed class ExecCommand : ICommand
{
    public string Name => "exec";
    public string Description => "Run a registered program and relay its streams and exit code.";
    public string Usage => "mframe exec [--state DIR] [--endpoint HOST:PORT] [--terminal] <program> [arguments...]";

    public async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Validate(Usage, "--state", "--endpoint", "--terminal");
        if (context.Arguments.Count == 0)
            throw new UsageException(Usage);
        if (context.Terminal)
            ConsoleSession.RequireConsole();
        await using KernelClient client = await OperatorClient.ConnectAsync(context, cancellationToken);
        (int, int) size = context.Terminal ? ConsoleSession.Dimensions() : (80, 25);
        KernelInvocation invocation = await client.StartProcessAsync(new(context.Arguments[0], context.Arguments.Skip(1).ToArray(), context.Terminal ? "terminal" : "pipes", Columns: size.Item1, Rows: size.Item2), cancellationToken);
        return await ExecutionRelay.RunAsync(client, invocation, context.Terminal, cancellationToken);
    }
}

public sealed class ConnectCommand : ICommand
{
    public string Name => "connect";
    public string Description => "Open an interactive mainframe shell.";
    public string Usage => "mframe connect [--state DIR] [--endpoint HOST:PORT]";

    public async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Validate(Usage, "--state", "--endpoint");
        context.RequireNoArguments(Usage);
        ConsoleSession.RequireConsole();
        await using KernelClient client = await OperatorClient.ConnectAsync(context, cancellationToken);
        ShellState shell = await client.OpenShellAsync(cancellationToken);
        while (!shell.Closed && !cancellationToken.IsCancellationRequested)
        {
            context.Output.Write($"mframe:{shell.WorkingDirectory}> ");
            string? line = await ConsoleSession.ReadLineAsync(cancellationToken);
            if (line is null)
                line = "exit";
            try
            {
                (int Columns, int Rows) size = ConsoleSession.Dimensions();
                KernelInvocation invocation = await client.ExecuteShellAsync(new(shell.SessionId, line, size.Columns, size.Rows), cancellationToken);
                if (invocation.Response.Streaming)
                    await ExecutionRelay.RunAsync(client, invocation, true, cancellationToken);
                else
                {
                    shell = KernelClient.Decode<ShellState>(invocation.Response.Result!.Value);
                    if (shell.Text is not null)
                        context.Output.WriteLine(shell.Text);
                }
            }
            catch (KernelRpcException ex)
            {
                context.Output.WriteLine($"{ex.Code}: {ex.Message}");
            }
        }

        return 0;
    }
}

internal static class ExecutionRelay
{
    public static async Task<int> RunAsync(KernelClient client, KernelInvocation call, bool terminal, CancellationToken token)
    {
        using ConsoleSession? console = terminal ? new ConsoleSession() : null;
        using var inputLifetime = new CancellationTokenSource();
        using CancellationTokenRegistration cancellationRegistration = token.Register(() => _ = CancelQuietly(call));
        Task output = call.Stdout.CopyToAsync(Console.OpenStandardOutput());
        Task error = call.Stderr?.CopyToAsync(Console.OpenStandardError()) ?? Task.CompletedTask;
        Task input = terminal ? TerminalInputAsync(client, call, inputLifetime.Token) : PipeInputAsync(call, inputLifetime.Token);
        try
        {
            ProcessExited result = await call.WaitAsync();
            await Task.WhenAll(output, error);
            return result.ExitCode;
        }
        finally
        {
            inputLifetime.Cancel();
            if (terminal)
                try
                {
                    await input;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException)
                {
                }
            // Redirected OS stdin can be non-cancellable; the process exits after this one invocation.
            else
                _ = input.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    private static async Task CancelQuietly(KernelInvocation call)
    {
        try
        {
            await call.CancelAsync();
        }
        catch (IOException)
        {
        }
    }

    private static async Task PipeInputAsync(KernelInvocation call, CancellationToken token)
    {
        try
        {
            Stream input = Console.OpenStandardInput();
            byte[] bytes = new byte[65536];
            int count;
            while ((count = await input.ReadAsync(bytes, token)) > 0)
                await call.WriteInputAsync(bytes.AsMemory(0, count), token);
            await call.EndInputAsync();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }

    private static async Task TerminalInputAsync(KernelClient client, KernelInvocation call, CancellationToken token)
    {
        (int Columns, int Rows) size = ConsoleSession.Dimensions();
        while (!token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    await client.InterruptAsync(call.Process.ProcessId, token);
                else
                {
                    string text = ConsoleSession.EncodeKey(key);
                    if (text.Length > 0)
                        await call.WriteInputAsync(Encoding.UTF8.GetBytes(text), token);
                }
            }

            (int Columns, int Rows) next = ConsoleSession.Dimensions();
            if (next != size)
            {
                size = next;
                await client.ResizeAsync(call.Process.ProcessId, size.Columns, size.Rows, token);
            }

            await Task.Delay(10, token);
        }
    }
}
