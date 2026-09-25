using Mainframe.Cli;
using Mainframe.Cli.Commands;

return new CommandRouter([
    new StatusCommand(),
    new VersionCommand()
]).Run(args, Console.Out, Console.Error);
