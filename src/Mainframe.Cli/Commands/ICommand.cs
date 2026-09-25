namespace Mainframe.Cli.Commands;

/// <summary>A CLI operation with parsed options and cancellable execution.</summary>
public interface ICommand
{
    string Name { get; }
    string Description { get; }
    string Usage { get; }
    Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken);
}
