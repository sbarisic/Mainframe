using System.Text;
using System.Text.RegularExpressions;
using Mainframe.Core;

namespace Mainframe.Storage;

public static class VolumeNames
{
    public static string Component(string name)
    {
        string normalized = name.Normalize(NormalizationForm.FormC);
        if (normalized.Length is 0 or > 255 || normalized is "." or ".." || normalized.EndsWith(' ') || normalized.EndsWith('.') || normalized.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) || Regex.IsMatch(normalized.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            throw new KernelOperationException("INVALID_PATH", "Invalid volume filename.");
        }

        return normalized;
    }

    public static string Normalize(string path)
    {
        if (path is null || !path.StartsWith('/') || path.Length > 4096)
        {
            throw new KernelOperationException("INVALID_PATH", "Expected an absolute virtual path of at most 4096 characters.");
        }

        var parts = new List<string>();
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new KernelOperationException("INVALID_PATH", "Cannot traverse above /.");
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(Component(part));
            }
        }

        if (parts.Count > 0 && parts[0].Equals("vol", StringComparison.OrdinalIgnoreCase))
        {
            parts[0] = "vol";
        }

        string result = "/" + string.Join('/', parts);
        if (result.Length > 4096)
        {
            throw new KernelOperationException("INVALID_PATH", "Normalized path is too long.");
        }

        return result;
    }
}
