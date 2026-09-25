namespace Mainframe.Cli.Commands;

/// <summary>A CLI operation. Future commands can call a shared kernel client here.</summary>
public interface ICommand
{
    string Name { get; }
    string Description { get; }
    string Usage { get; }
    int Execute(TextWriter output);
}
