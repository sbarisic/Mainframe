using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mainframe.Cli;
using Mainframe.Cli.Commands;
using Mainframe.Client;
using Mainframe.Protocol;
using Mainframe.Sdk;
using Mainframe.Core;
using Mainframe.Host;

namespace Mainframe.Fixtures;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string mode = args.FirstOrDefault() ?? "";
        switch (mode)
        {
            case "storage-host":
                using (KernelStore store = KernelStore.Open(args[1]))
                await using (var host = new KernelServer(store, 0, storageFault: point =>
                {
                    if (point == args[2])
                    {
                        Console.WriteLine("POINT:" + point);
                        Console.Out.Flush();
                        Thread.Sleep(Timeout.Infinite);
                    }
                }))
                {
                    host.Start();
                    Console.WriteLine("READY:" + host.Port);
                    Console.Out.Flush();
                    await Task.Delay(Timeout.Infinite);
                }

                return 0;
            case "storage-admin":
                using (var pipe = new AnonymousPipeClientStream(PipeDirection.In, Environment.GetEnvironmentVariable("MF_BOOTSTRAP_HANDLE")!))
                using (var data = new MemoryStream())
                {
                    await pipe.CopyToAsync(data);
                    BootstrapCredential credential = ProtocolJson.Deserialize<BootstrapCredential>(data.ToArray());
                    await using KernelClient kernel = await KernelClient.ConnectProgramAsync(credential);
                    try
                    {
                        await kernel.ListVolumesAsync();
                        return 99;
                    }
                    catch (KernelRpcException ex)
                    {
                        Console.WriteLine(ex.Code);
                        return 0;
                    }
                }

            case "working-directory":
                Console.WriteLine(Environment.CurrentDirectory);
                return 0;
            case "denied":
                await using (MainframeProgram kernel = await MainframeProgram.ConnectAsync())
                {
                    try
                    {
                        await kernel.HealthAsync();
                        return 90;
                    }
                    catch (Mainframe.Client.KernelRpcException ex)
                    {
                        Console.WriteLine(ex.Code);
                        return 0;
                    }
                }

            case "args":
                Console.WriteLine(JsonSerializer.Serialize(args.Skip(1).ToArray()));
                return 19;
            case "env":
                Console.WriteLine(Environment.GetEnvironmentVariable(args[1]) ?? "ABSENT");
                return 0;
            case "copy":
                await Console.OpenStandardInput().CopyToAsync(Console.OpenStandardOutput());
                Console.Error.Write("stderr-exact");
                return 23;
            case "burst":
                var block = Enumerable.Range(0, 65536).Select(i => (byte)i).ToArray();
                for (int i = 0; i < 128; i++)
                {
                    await Console.OpenStandardOutput().WriteAsync(block);
                }

                return 0;
            case "tree":
                using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "sleep" }, UseShellExecute = false }))
                {
                    Console.WriteLine(child!.Id);
                    Console.Out.Flush();
                    await Task.Delay(Timeout.Infinite);
                }

                return 0;
            case "sleep":
                await Task.Delay(Timeout.Infinite);
                return 0;
            case "terminal":
                int interrupts = 0;
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    Interlocked.Increment(ref interrupts);
                    Console.WriteLine("INTERRUPTED");
                };
                Console.WriteLine("READY");
                string? line;
                while (true)
                {
                    int before = Volatile.Read(ref interrupts);
                    line = Console.ReadLine();
                    if (line is null)
                    {
                        await Task.Delay(100);
                        if (Volatile.Read(ref interrupts) != before)
                        {
                            continue;
                        }

                        break;
                    }

                    if (line == "exit")
                    {
                        return 31;
                    }

                    if (line == "size")
                    {
                        Console.WriteLine($"SIZE={Console.WindowWidth}x{Console.WindowHeight}");
                    }
                    else
                    {
                        Console.WriteLine("ECHO=" + line);
                    }
                }

                Console.WriteLine("EOF");
                return 32;
            case "restore":
                Encoding oldIn = Console.InputEncoding;
                Encoding oldOut = Console.OutputEncoding;
                using (var modes = new ConsoleSession())
                {
                }

                Console.WriteLine(oldIn.Equals(Console.InputEncoding) && oldOut.Equals(Console.OutputEncoding) ? "RESTORED" : "BAD");
                return 0;
            case "bootstrap-replay":
                using (var pipe = new AnonymousPipeClientStream(PipeDirection.In, Environment.GetEnvironmentVariable("MF_BOOTSTRAP_HANDLE")!))
                using (var data = new MemoryStream())
                {
                    await pipe.CopyToAsync(data);
                    BootstrapCredential credential = ProtocolJson.Deserialize<BootstrapCredential>(data.ToArray());
                    await using KernelClient first = await KernelClient.ConnectProgramAsync(credential);
                    try
                    {
                        await using KernelClient second = await KernelClient.ConnectProgramAsync(credential);
                        return 95;
                    }
                    catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
                    {
                        Console.WriteLine("REPLAY_REJECTED");
                        return 0;
                    }
                }

            case "bootstrap-expired":
                await Task.Delay(TimeSpan.FromSeconds(31));
                try
                {
                    await using MainframeProgram connected = await MainframeProgram.ConnectAsync();
                    return 96;
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
                {
                    Console.WriteLine("EXPIRED_REJECTED");
                    return 0;
                }

            case "cli-harness":
                ConsoleModes.GetConsoleMode(ConsoleModes.GetStdHandle(-10), out uint inputMode);
                ConsoleModes.GetConsoleMode(ConsoleModes.GetStdHandle(-11), out uint outputMode);
                int code = await new CommandRouter([new ExecCommand(), new ConnectCommand(), new VolumeCommand()]).RunAsync(args.Skip(1).ToArray(), Console.Out, Console.Error);
                ConsoleModes.GetConsoleMode(ConsoleModes.GetStdHandle(-10), out uint afterInput);
                ConsoleModes.GetConsoleMode(ConsoleModes.GetStdHandle(-11), out uint afterOutput);
                Console.WriteLine($"CLI_EXIT={code};MODES_RESTORED={inputMode == afterInput && outputMode == afterOutput}");
                return code;
            default:
                return 2;
        }
    }
}

internal static class ConsoleModes
{
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
}
