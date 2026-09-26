using System.Text.Json;
using Mainframe.Sdk;

namespace Mainframe.Hello;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        await using MainframeProgram kernel = await MainframeProgram.ConnectAsync();
        JsonElement description = await kernel.DescribeAsync();
        string name = args.FirstOrDefault() ?? "world";

        Console.WriteLine($"Hello {name} from {description.GetProperty("name").GetString()}");

        for (int i = 0; i < 5; i++)
        {
            Console.WriteLine($"Hello #{i}");
            await Task.Delay(500);
        }

        Console.WriteLine("Done!");
    }
}
