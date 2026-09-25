using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Mainframe.Core;

internal static class PrivateStateDirectory
{
    public static void Create(string directory)
    {
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("The state directory already exists.");

        if (OperatingSystem.IsWindows())
            CreateWindows(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void Validate(string directory)
    {
        RejectLinks(directory);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("The kernel is not initialized. Run 'mf cluster init' first.");

        if (OperatingSystem.IsWindows())
            ValidateWindows(directory);
        else if ((File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("The kernel state directory must be accessible only to its owner.");
    }

    public static void RejectLinks(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Kernel state paths must not contain symbolic links, junctions, or reparse points.");
        }
    }

    public static string ValidateFile(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Kernel state is incomplete: {name} is missing.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Kernel state files must not be symbolic links or reparse points.");
        if (OperatingSystem.IsWindows())
            ValidateWindowsFile(path);
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("Kernel state files must be accessible only to their owner.");
        return path;
    }

    public static void Write(string directory, string name, byte[] contents)
    {
        var path = Path.Combine(directory, name);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        file.Write(contents);
        file.Flush(flushToDisk: true);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindows(string directory)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Cannot identify the local Windows account.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectorySecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var identity in new[] { currentUser, system }.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindows(string directory) =>
        ValidateWindowsRules(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsFile(string path) =>
        ValidateWindowsRules(new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner));

    [SupportedOSPlatform("windows")]
    private static void ValidateWindowsRules(FileSystemSecurity security)
    {
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Cannot identify the local Windows account.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || (owner != currentUser && owner != system))
            throw new UnauthorizedAccessException("Kernel state must be owned by the current Windows account or SYSTEM.");
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        if (rules.Count == 0)
            throw new UnauthorizedAccessException("Kernel state requires an explicit private access policy.");
        foreach (FileSystemAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference != currentUser && rule.IdentityReference != system)
                throw new UnauthorizedAccessException("Kernel state grants access to another account. Restrict it to the current Windows account and SYSTEM.");
        }
    }
}
