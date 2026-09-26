using Mainframe.Sdk;

namespace Mainframe.StorageDemo;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: storage-demo /vol/<name>");
            return 2;
        }

        try
        {
            await using var kernel = await MainframeProgram.ConnectAsync();
            string folder = args[0].TrimEnd('/') + "/demo-" + Guid.NewGuid().ToString("N");
            await kernel.Files.CreateDirectoryAsync(folder);
            await kernel.Files.CreateDirectoryAsync(folder + "/nested");
            string temporary = folder + "/nested/data.tmp";
            string target = folder + "/nested/data.bin";
            byte[] data = Enumerable.Range(0, 150000).Select(i => (byte)(i % 251)).ToArray();
            await using (var file = await kernel.Files.OpenAsync(temporary, "write", "create-new"))
            {
                await file.WriteAsync(data);
                await file.FlushAsync();
            }

            await kernel.Files.RenameAsync(temporary, target);
            await using (var file = await kernel.Files.OpenAsync(target))
            {
                using var output = new MemoryStream();
                await file.CopyToAsync(output);
                if (!data.AsSpan().SequenceEqual(output.ToArray()))
                {
                    throw new IOException("Content verification failed.");
                }
            }

            Console.WriteLine(target);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("storage-demo: " + ex.Message);
            return 1;
        }
    }
}
