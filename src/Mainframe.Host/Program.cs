using Mainframe.Core;

namespace Mainframe.Host;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            if (args.Length > 1)
            {
                Console.Error.WriteLine("mframed: unexpected arguments.");
                return 2;
            }

            Console.WriteLine("Usage: mframed serve [--state <directory>] [--port <1..65535>]");
            Console.WriteLine("Initialize first with: mframe cluster init [--state <directory>] [--name <name>]");
            Console.WriteLine("This milestone listens on 127.0.0.1 only. Stop with Ctrl+C.");
            return 0;
        }

        if (args[0] != "serve")
        {
            Console.Error.WriteLine("mframed: expected 'serve'. Run 'mframed help'.");
            return 2;
        }

        if (args.Length == 2 && args[1] is "--help" or "-h")
        {
            Console.WriteLine("Usage: mframed serve [--state <directory>] [--port <1..65535>]");
            return 0;
        }

        string state = KernelPaths.DefaultDirectory;
        int port = 7443;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            string option = args[i];
            if (!seen.Add(option) || ++i >= args.Length)
            {
                Console.Error.WriteLine("mframed: duplicate option or missing value.");
                return 2;
            }

            if (option == "--state")
                state = args[i];
            else if (option == "--port" && int.TryParse(args[i], out int value) && value is > 0 and <= 65535)
                port = value;
            else
            {
                Console.Error.WriteLine("mframed: invalid option or value. Run 'mframed help'.");
                return 2;
            }
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            shutdown.Cancel();
        };
        try
        {
            using var store = KernelStore.Open(state);
            await using var server = new KernelServer(store, port, message => Console.Error.WriteLine($"mframed: {message}"));
            server.Start();
            Console.WriteLine($"MAINFRAME / {store.Identity.Name}");
            Console.WriteLine($"Kernel {store.Identity.KernelId}");
            Console.WriteLine($"Listening on 127.0.0.1:{server.Port} (TLS 1.3, authenticated local operators)");
            try
            {
                await Task.Delay(Timeout.Infinite, shutdown.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
            }

            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or System.Security.Authentication.AuthenticationException or System.Security.Cryptography.CryptographicException or System.Net.Sockets.SocketException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"mframed: {ex.Message}");
            return 1;
        }
    }
}
