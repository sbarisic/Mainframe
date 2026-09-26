using System.Net;

namespace Mainframe.WinFsp;

public sealed record MountOptions(string MountPoint, string Root, string State, string Host, int Port)
{
    public const string Usage = "mframe-fs mount <M:|new-directory> [--root /] [--state DIR] [--endpoint HOST:PORT]";

    public static MountOptions Parse(string[] args)
    {
        if (args.Length < 2 || args[0] != "mount") throw new ArgumentException(Usage);
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 2; i < args.Length; i += 2)
        {
            if (args[i] is not ("--root" or "--state" or "--endpoint") || i + 1 == args.Length || !options.TryAdd(args[i], args[i + 1]))
                throw new ArgumentException($"Invalid or repeated option '{args[i]}'. {Usage}");
        }
        string root = VirtualPath.NormalizeRoot(options.GetValueOrDefault("--root", "/"));
        string state = Path.GetFullPath(options.GetValueOrDefault("--state", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Mainframe")));
        string endpoint = options.GetValueOrDefault("--endpoint", "localhost:7443");
        if (!Uri.TryCreate("tcp://" + endpoint, UriKind.Absolute, out var uri) || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (!uri.DnsSafeHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) && (!IPAddress.TryParse(uri.DnsSafeHost, out var address) || !IPAddress.IsLoopback(address))))
            throw new ArgumentException("Endpoint must be a loopback host and port, for example localhost:7443.");
        return new(args[1], root, state, uri.DnsSafeHost, uri.Port);
    }

    public string ValidateMountPoint()
    {
        if (MountPoint.Length == 2 && char.IsAsciiLetter(MountPoint[0]) && MountPoint[1] == ':')
        {
            string drive = MountPoint.ToUpperInvariant();
            if (DriveInfo.GetDrives().Any(d => d.Name.StartsWith(drive, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Drive {drive} is already occupied.");
            return drive;
        }
        if (!Path.IsPathFullyQualified(MountPoint) || MountPoint.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("Use an unused drive letter or an absolute local directory path.");
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(MountPoint));
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (path.Equals(temporary, StringComparison.OrdinalIgnoreCase) || path.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("WinFsp directory mounts beneath %TEMP% are not supported: directory creation fails on the qualified system, including with WinFsp's sample filesystem. Use a drive letter or a directory outside %TEMP%.");
        if (File.Exists(path) || Directory.Exists(path)) throw new ArgumentException("The directory mount point must not already exist.");
        DirectoryInfo? parent = Directory.GetParent(path);
        if (parent is null || !parent.Exists) throw new ArgumentException("The mount point's parent directory must exist.");
        for (DirectoryInfo? p = parent; p is not null; p = p.Parent)
            if ((p.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Mount point parents must not be reparse points.");
        return path;
    }
}
