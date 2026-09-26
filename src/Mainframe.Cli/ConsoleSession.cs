using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Mainframe.Cli;

public sealed class ConsoleSession : IDisposable
{
    private readonly IntPtr input, output;
    private readonly uint inputMode, outputMode;
    private readonly Encoding inputEncoding, outputEncoding;
    private bool disposed;
    public ConsoleSession()
    {
        RequireConsole();
        input = GetStdHandle(-10);
        output = GetStdHandle(-11);
        if (!GetConsoleMode(input, out inputMode) || !GetConsoleMode(output, out outputMode))
            throw new Win32Exception();
        inputEncoding = Console.InputEncoding;
        outputEncoding = Console.OutputEncoding;
        try
        {
            Console.InputEncoding = new UTF8Encoding(false);
            Console.OutputEncoding = new UTF8Encoding(false);
            if (!SetConsoleMode(input, inputMode & ~7u) || !SetConsoleMode(output, outputMode | 4u))
                throw new Win32Exception();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static void RequireConsole()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
            throw new UsageException("Terminal mode requires a Windows console. Use mframe exec without --terminal for redirected streams.");
    }

    public static (int Columns, int Rows) Dimensions() => (Math.Max(1, Console.WindowWidth), Math.Max(1, Console.WindowHeight));
    public static string EncodeKey(ConsoleKeyInfo key)
    {
        string text = key.Key switch
        {
            ConsoleKey.UpArrow => "\u001b[A",
            ConsoleKey.DownArrow => "\u001b[B",
            ConsoleKey.RightArrow => "\u001b[C",
            ConsoleKey.LeftArrow => "\u001b[D",
            ConsoleKey.Home => "\u001b[H",
            ConsoleKey.End => "\u001b[F",
            ConsoleKey.Delete => "\u001b[3~",
            ConsoleKey.Insert => "\u001b[2~",
            ConsoleKey.PageUp => "\u001b[5~",
            ConsoleKey.PageDown => "\u001b[6~",
            ConsoleKey.Enter => "\r",
            ConsoleKey.Backspace => "\b",
            ConsoleKey.F1 => "\u001bOP",
            ConsoleKey.F2 => "\u001bOQ",
            ConsoleKey.F3 => "\u001bOR",
            ConsoleKey.F4 => "\u001bOS",
            ConsoleKey.F5 => "\u001b[15~",
            ConsoleKey.F6 => "\u001b[17~",
            ConsoleKey.F7 => "\u001b[18~",
            ConsoleKey.F8 => "\u001b[19~",
            ConsoleKey.F9 => "\u001b[20~",
            ConsoleKey.F10 => "\u001b[21~",
            ConsoleKey.F11 => "\u001b[23~",
            ConsoleKey.F12 => "\u001b[24~",
            _ => key.KeyChar == '\0' ? "" : key.KeyChar.ToString()
        };
        return key.Modifiers.HasFlag(ConsoleModifiers.Alt) ? "\u001b" + text : text;
    }

    public static async Task<string?> ReadLineAsync(CancellationToken token)
    {
        using var modes = new ConsoleSession();
        var line = new StringBuilder();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                await Task.Delay(10, token);
                continue;
            }

            ConsoleKeyInfo key = Console.ReadKey(true);
            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key is ConsoleKey.D or ConsoleKey.Z && line.Length == 0)
            {
                Console.WriteLine();
                return null;
            }

            if (key.Modifiers.HasFlag(ConsoleModifiers.Control) && key.Key == ConsoleKey.C)
            {
                line.Clear();
                Console.WriteLine("^C");
                return "";
            }

            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return line.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (line.Length > 0)
                {
                    line.Length--;
                    Console.Write("\b \b");
                }

                continue;
            }

            if (!char.IsControl(key.KeyChar) && line.Length < 32767)
            {
                line.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        Console.InputEncoding = inputEncoding;
        Console.OutputEncoding = outputEncoding;
        SetConsoleMode(input, inputMode);
        SetConsoleMode(output, outputMode);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);
}
