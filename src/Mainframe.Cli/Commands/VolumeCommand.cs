using System.Text;
using Mainframe.Client;
using Mainframe.Protocol;

namespace Mainframe.Cli.Commands;

public sealed class VolumeCommand : ICommand
{
    public string Name => "volume";
    public string Description => "Create, unlock, list, or unmount local encrypted containers.";
    public string Usage => "mframe volume create <absolute-path> | mount <absolute-path> /vol/<name> | list | unmount /vol/<name> [--state DIR] [--endpoint HOST:PORT] [--json]";

    public async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        context.Validate(Usage, "--state", "--endpoint", "--json");
        var args = context.Arguments;
        if (args.Count == 0 || !(args[0] == "list" && args.Count == 1 || args[0] is "create" or "unmount" && args.Count == 2 || args[0] == "mount" && args.Count == 3))
        {
            throw new UsageException(Usage);
        }

        if (args[0] is "create" or "mount")
        {
            ConsoleSession.RequireConsole();
        }

        await using KernelClient client = await OperatorClient.ConnectAsync(context, cancellationToken);
        switch (args[0])
        {
            case "list":
                VolumeInfo[] volumes = await client.ListVolumesAsync(cancellationToken);
                if (context.Json)
                {
                    context.WriteJson(volumes);
                }
                else
                {
                    foreach (VolumeInfo volume in volumes)
                    {
                        context.Output.WriteLine($"{volume.Mount} [{volume.State}] {volume.Id} -> {volume.Path}");
                    }
                }

                break;
            case "unmount":
                await client.UnmountVolumeAsync(args[1], cancellationToken);
                context.Output.WriteLine("Volume unmounted; container retained.");
                break;
            case "create":
                Console.Error.WriteLine("There is no password recovery. Store your password safely. Creation leaves the volume unmounted.");
                string password = ReadPassword("Password: ", cancellationToken);
                if (ReadPassword("Confirm password: ", cancellationToken) != password)
                {
                    throw new UsageException("Passwords do not match.");
                }

                VolumeCreated created = await client.CreateVolumeAsync(args[1], password, cancellationToken);
                if (context.Json)
                {
                    context.WriteJson(created);
                }
                else
                {
                    context.Output.WriteLine($"Created encrypted volume {created.Id}.");
                }

                break;
            case "mount":
                VolumeInfo mounted = await client.MountVolumeAsync(args[1], args[2], ReadPassword("Password: ", cancellationToken), cancellationToken);
                if (context.Json)
                {
                    context.WriteJson(mounted);
                }
                else
                {
                    context.Output.WriteLine($"Mounted {mounted.Id} at {mounted.Mount}.");
                }

                break;
        }

        return 0;
    }

    private static string ReadPassword(string prompt, CancellationToken token)
    {
        Console.Error.Write(prompt);
        char[] chars = new char[128];
        int count = 0;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!Console.KeyAvailable)
                {
                    Thread.Sleep(20);
                    continue;
                }

                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.Error.WriteLine();
                    return new string(chars, 0, count);
                }

                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                {
                    throw new OperationCanceledException(token);
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (count > 0)
                    {
                        chars[--count] = '\0';
                        Console.Error.Write("\b \b");
                    }

                    continue;
                }

                if (key.KeyChar != '\0' || (key.Key == ConsoleKey.Spacebar && key.Modifiers.HasFlag(ConsoleModifiers.Control)))
                {
                    if (count == chars.Length)
                    {
                        char[] expanded = new char[checked(chars.Length * 2)];
                        chars.CopyTo(expanded, 0);
                        Array.Clear(chars);
                        chars = expanded;
                    }
                    chars[count++] = key.KeyChar;
                    Console.Error.Write('*');
                }
            }
        }
        finally
        {
            Array.Clear(chars);
        }
    }
}
