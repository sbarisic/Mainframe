namespace Mainframe.Core;

public sealed record KernelIdentity(string MainframeId, string KernelId, string Name, DateTimeOffset CreatedAt);
public static class KernelPaths
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mainframe");
}
