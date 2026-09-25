namespace Mainframe.Cli.Commands;

public sealed class StatusCommand : ICommand
{
    public string Name => "status";
    public string Description => "Show the implemented system components.";
    public string Usage => "mf status";

    public int Execute(TextWriter output)
    {
        output.WriteLine("MAINFRAME / system status");
        output.WriteLine("CLI         available");
        output.WriteLine("Kernel      not implemented");
        output.WriteLine("Storage     not implemented");
        output.WriteLine("Windows VFS not implemented");
        return 0;
    }
}
