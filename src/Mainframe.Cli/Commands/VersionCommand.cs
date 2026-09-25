using System.Reflection;

namespace Mainframe.Cli.Commands;

public sealed class VersionCommand : ICommand
{
    public string Name => "version";
    public string Description => "Show the CLI version.";
    public string Usage => "mf version";

    public int Execute(TextWriter output)
    {
        var version = typeof(VersionCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        output.WriteLine($"mf {version}");
        return 0;
    }
}
