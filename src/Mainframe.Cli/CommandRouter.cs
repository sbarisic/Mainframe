using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using Mainframe.Cli.Commands;
using Mainframe.Client;

namespace Mainframe.Cli;

public sealed class CommandRouter(IEnumerable<ICommand> commands)
{
    private readonly Dictionary<string, ICommand> commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
    public async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken cancellationToken = default)
    {
        try
        {
            (List<string>? words, Dictionary<string, string?>? options, bool help) = Parse(args);
            if (words.Count == 0)
            {
                if (!help && options.Count != 0)
                    throw new UsageException("A command is required. Run 'mframe help' for available commands.");
                return Help([], output);
            }

            if (words[0] == "help")
            {
                if (options.Count != 0)
                    throw new UsageException("Usage: mframe help [command]");
                return Help(words.Skip(1).ToArray(), output);
            }

            var name = words[0];
            if (!commands.TryGetValue(name, out ICommand? command))
                throw new UsageException($"Unknown command '{name}'. Run 'mframe help' for available commands.");
            if (help)
                return Help([name], output);
            var context = new CommandContext(words.Skip(1).ToArray(), options, output);
            return await command.ExecuteAsync(context, cancellationToken);
        }
        catch (UsageException ex)
        {
            error.WriteLine($"mframe: {ex.Message}");
            return 2;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("mframe: Cancelled.");
            return 130;
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("mframe: The kernel request timed out.");
            return 1;
        }
        catch (KernelRpcException ex)
        {
            error.WriteLine($"mframe: {ex.Code}: {ex.Message} (outcome: {ex.Outcome})");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or SocketException or AuthenticationException or CryptographicException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            error.WriteLine($"mframe: {ex.Message}");
            return 1;
        }
    }

    private static (List<string> Words, Dictionary<string, string?> Options, bool Help) Parse(string[] args)
    {
        var words = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        var help = false;
        var positionalOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (words.Count >= 2 && words[0] == "exec")
                positionalOnly = true;
            if (positionalOnly)
            {
                words.Add(argument);
                continue;
            }

            switch (argument)
            {
                case "--":
                    positionalOnly = true;
                    break;
                case "--help":
                case "-h":
                    help = true;
                    break;
                case "--version":
                    words.Add("version");
                    break;
                case "--json":
                case "--terminal":
                    AddOption(argument, null);
                    break;
                case "--state":
                case "--endpoint":
                case "--name":
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i + 1]))
                        throw new UsageException($"Option '{argument}' requires a value.");
                    AddOption(argument, args[++i]);
                    break;
                default:
                    if (argument.StartsWith('-'))
                        throw new UsageException($"Unknown option '{argument}'.");
                    words.Add(argument);
                    break;
            }
        }

        return (words, options, help);
        void AddOption(string name, string? value)
        {
            if (!options.TryAdd(name, value))
                throw new UsageException($"Option '{name}' was specified more than once.");
        }
    }

    private int Help(IReadOnlyList<string> args, TextWriter output)
    {
        if (args.Count > 1)
            throw new UsageException("Usage: mframe help [command]");
        if (args.Count == 1)
        {
            if (args[0] == "help")
            {
                output.WriteLine("Usage: mframe help [command]");
                return 0;
            }

            if (!commands.TryGetValue(args[0], out ICommand? command))
                throw new UsageException($"Unknown command '{args[0]}'.");
            output.WriteLine(command.Description);
            output.WriteLine($"Usage: {command.Usage}");
            return 0;
        }

        output.WriteLine("MAINFRAME / operator interface");
        output.WriteLine();
        output.WriteLine("Usage: mframe [options] <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  help         Show help for all commands or one command.");
        foreach (ICommand? command in commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
            output.WriteLine($"  {command.Name,-12} {command.Description}");
        output.WriteLine();
        output.WriteLine("Options:");
        output.WriteLine("  --state DIR       Identity directory (default: %LOCALAPPDATA%/Mainframe).");
        output.WriteLine("  --endpoint HOST:PORT  Kernel address (default: localhost:7443; loopback only).");
        output.WriteLine("  --json            Write structured command results to stdout.");
        output.WriteLine("  --help, -h        Show help. --version prints the CLI version.");
        output.WriteLine();
        output.WriteLine("Get started on this machine:");
        output.WriteLine("  mframe cluster init --name atlas");
        output.WriteLine("  mframed serve                 (keep running in another terminal)");
        output.WriteLine("  mframe status");
        output.WriteLine("  mframe capabilities --json");
        output.WriteLine();
        output.WriteLine("Register installed programs, then use mframe exec or mframe connect. Filesystem RPCs are a later milestone.");
        return 0;
    }
}
