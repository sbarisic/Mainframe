using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using Fsp;
using Mainframe.Client;
using Mainframe.Protocol;

namespace Mainframe.WinFsp;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.WriteLine(WinFspRuntime.Attribution);
        if (args.Length == 0 || args.Any(a => a is "--help" or "-h") || args[0] == "help")
        {
            Console.WriteLine(MountOptions.Usage);
            Console.WriteLine("Foreground Windows x64 mount; Ctrl+C unmounts. Requires WinFsp " + WinFspRuntime.QualifiedVersion + ".");
            return 0;
        }
        if (args is ["--version"] or ["version"]) { Console.WriteLine("mframe-fs 0.1.0"); return 0; }
        try
        {
            MountOptions options = MountOptions.Parse(args);
            string point = options.ValidateMountPoint();
            WinFspRuntime.Initialize();
            return await RunAsync(options, point);
        }
        catch (ArgumentException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }

    // Keep Fsp types out of startup JIT until the installed binding resolver is registered.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<int> RunAsync(MountOptions options, string mountPoint)
    {
        using X509Certificate2 ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(options.State, "ca.cer"));
        using X509Certificate2 identity = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(options.State, "operator.pfx"), null, X509KeyStorageFlags.UserKeySet);
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using KernelClient client = await KernelClient.ConnectFilesystemAsync(options.Host, options.Port, identity, ca, startup.Token);
        foreach (string feature in new[] { "namespace-v1", "storage-v2", "exchange-retire-v1" })
            if (!client.Welcome.Features.Contains(feature)) throw new NotSupportedException($"Kernel lacks required feature: {feature}.");
        FsDiscovery discovery = await client.Files.DiscoverAsync(options.Root, startup.Token);
        if (!discovery.Entry.Directory) throw new ArgumentException("The exported root must be a directory.");
        await using (KernelFileHandle root = await client.Files.OpenHandleAsync(new(options.Root, "directory", Rights: ["list", "read-metadata"], Share: ["read", "write", "delete"]), startup.Token))
            await root.EnumerateAsync(1, token: startup.Token);

        using var filesystem = new MainframeFileSystem(client, options.Root);
        using var host = new FileSystemHost(filesystem);
        int preflight = host.Preflight(mountPoint);
        if (preflight < 0) throw new IOException($"WinFsp mount preflight failed: 0x{preflight:X8}.");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancelled.TrySetResult(); };
        Console.CancelKeyPress += cancel;
        try
        {
            int status = host.MountEx(mountPoint, 8, filesystem.Security, false, 0);
            if (status < 0) throw new IOException($"WinFsp mount failed: 0x{status:X8}.");
            Console.WriteLine($"Mounted {options.Root} at {mountPoint}. Press Ctrl+C to unmount.");
            Console.WriteLine("Concurrent SDK access is enabled. Windows locks and caches do not coordinate fully with SDK clients or other mounts; simultaneous edits can conflict. Close and reopen files to refresh SDK changes.");
            await Task.WhenAny(cancelled.Task, client.Completion, filesystem.Stopped);
            bool disconnected = client.Completion.IsCompleted;
            if (disconnected) Console.Error.WriteLine("Kernel connection ended. Unmounting; restart the command to reconnect. No operations are replayed.");
            filesystem.BeginStop();
            if (disconnected) filesystem.Abort();
            Task unmount = Task.Run(host.Unmount);
            try { await unmount.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException)
            {
                filesystem.Abort();
                Console.Error.WriteLine("Unmount exceeded its drain deadline; outstanding RPCs were cancelled.");
                await unmount.WaitAsync(TimeSpan.FromSeconds(10));
                return 1;
            }
            filesystem.Dispose();
            Console.WriteLine("Unmounted.");
            return disconnected || filesystem.Failed ? 1 : 0;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
