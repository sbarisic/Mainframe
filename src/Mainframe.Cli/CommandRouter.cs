using Mainframe.Cli.Commands;

namespace Mainframe.Cli;

public sealed class CommandRouter(IEnumerable<ICommand> commands)
{
    private readonly Dictionary<string, ICommand> commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);

    public int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
            return Help(args.Skip(1).ToArray(), output, error);

        var name = args[0] == "--version" ? "version" : args[0];
        if (!commands.TryGetValue(name, out var command))
            return Fail($"Unknown command '{args[0]}'. Run 'mf help' for available commands.", error);

        if (args.Length == 2 && args[1] is "--help" or "-h")
            return Help([name], output, error);

        if (args.Length != 1)
            return Fail($"Unexpected arguments. Usage: {command.Usage}", error);

        try
        {
            return command.Execute(output);
        }
        catch (IOException ex)
        {
            error.WriteLine($"mf: {ex.Message}");
            return 1;
        }
    }

    private int Help(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length > 1)
            return Fail("Usage: mf help [command]", error);

        if (args.Length == 1)
        {
            if (args[0] == "help")
            {
                output.WriteLine("Usage: mf help [command]");
                return 0;
            }

            if (!commands.TryGetValue(args[0], out var command))
                return Fail($"Unknown command '{args[0]}'.", error);

            output.WriteLine(command.Description);
            output.WriteLine($"Usage: {command.Usage}");
            return 0;
        }

        output.WriteLine("MAINFRAME / operator interface");
        output.WriteLine();
        output.WriteLine("Usage: mf <command> [options]");
        output.WriteLine();
        output.WriteLine("Commands:");
        output.WriteLine("  help         Show help for all commands or one command.");
        foreach (var command in commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
            output.WriteLine($"  {command.Name,-12} {command.Description}");
        output.WriteLine();
        output.WriteLine("Options: --help, -h, --version");
        return 0;
    }

    private static int Fail(string message, TextWriter error)
    {
        error.WriteLine($"mf: {message}");
        return 2;
    }
}
