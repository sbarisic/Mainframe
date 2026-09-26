using Mainframe.Sdk;

namespace Mainframe.Cat;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: cat /vol/<name>/<file>");
            return 2;
        }

        try
        {
            await using var kernel = await MainframeProgram.ConnectAsync();
            await using var file = await kernel.Files.OpenAsync(args[0], share: ["read"]);
            await file.CopyToAsync(Console.OpenStandardOutput());
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cat: " + ex.Message);
            return 1;
        }
    }
}
