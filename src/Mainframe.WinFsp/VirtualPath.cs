using System.Text;
using System.Text.RegularExpressions;

namespace Mainframe.WinFsp;

public static class VirtualPath
{
    public static string NormalizeRoot(string root)
    {
        if (!root.StartsWith('/') || root.Contains('\\')) throw new ArgumentException("Root must be an absolute virtual path.");
        return Normalize(root);
    }

    public static string Map(string root, string windowsPath)
    {
        if (!windowsPath.StartsWith('\\') || windowsPath.StartsWith(@"\\", StringComparison.Ordinal)) throw new ArgumentException("Expected a rooted filesystem path.");
        string relative = Normalize(windowsPath.Replace('\\', '/'));
        string path = root == "/" ? relative : root + (relative == "/" ? "" : relative);
        if (path.Length > 4096) throw new PathTooLongException();
        return path;
    }

    private static string Normalize(string path)
    {
        if (path.Length > 4096) throw new PathTooLongException();
        var parts = new List<string>();
        foreach (string raw in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw == ".") continue;
            if (raw == "..")
            {
                if (parts.Count == 0) throw new ArgumentException("Cannot traverse above the exported root.");
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            string part = raw.Normalize(NormalizationForm.FormC);
            if (part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => char.IsControl(c) || "<>:\"\\|?*".Contains(c)) ||
                Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new ArgumentException("Invalid virtual filename.");
            parts.Add(part);
        }
        return "/" + string.Join('/', parts);
    }

    public static bool Protected(string path) => path == "/" || path.Equals("/vol", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/vol/", StringComparison.OrdinalIgnoreCase) && path.Count(c => c == '/') == 2;
}
