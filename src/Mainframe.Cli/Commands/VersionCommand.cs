using System.Reflection;

namespace Mainframe.Cli.Commands;

public sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Description => "Show the CLI version.";
    public string Usage => "mframe version [--json]";

    public Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.RequireNoArguments(Usage);
        context.Validate(Usage, "--json");
        cancellationToken.ThrowIfCancellationRequested();
        var version = typeof(VersionCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        if (context.Json)
            context.WriteJson(new { name = "mframe", version });
        else
            context.Output.WriteLine($"mframe {version}");
        return Task.FromResult(0);
    }
}
