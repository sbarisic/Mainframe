using Mainframe.Sdk;
using Mainframe.Protocol;

namespace Mainframe.Ls;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            await using var kernel = await MainframeProgram.ConnectAsync();
            string path = args.FirstOrDefault() ?? "/";
            string? cursor = null;
            do
            {
                FsListing listing = await kernel.Files.ListAsync(path, continuation: cursor);
                foreach (FsEntry entry in listing.Entries)
                {
                    Console.WriteLine(entry.Name + (entry.Directory ? "/" : "") + (entry.State == "locked" ? " [locked]" : ""));
                }

                cursor = listing.Continuation;
            }
            while (cursor is not null);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ls: " + ex.Message);
            return 1;
        }
    }
}
