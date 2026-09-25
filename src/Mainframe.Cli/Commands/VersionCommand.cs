using System.Reflection;

namespace Mainframe.Cli.Commands;

public sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Description => "Show the CLI version.";
    public string Usage => "mf version [--json]";

    public Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.RequireNoArguments(Usage);
        context.Validate(Usage, "--json");
        cancellationToken.ThrowIfCancellationRequested();
        var version = typeof(VersionCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        if (context.Json)
            context.WriteJson(new { name = "mf", version });
        else
            context.Output.WriteLine($"mf {version}");
        return Task.FromResult(0);
    }
}
