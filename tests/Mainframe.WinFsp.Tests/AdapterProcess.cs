using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Mainframe.WinFsp.Tests;

/// <summary>A private hidden console permits testing Ctrl+C without signalling the test runner.</summary>
internal sealed class AdapterProcess : IDisposable
{
    public Process Process { get; }
    private readonly IntPtr _processHandle;
    public int ExitCode
    {
        get
        {
            if (!GetExitCodeProcess(_processHandle, out uint code)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return unchecked((int)code);
        }
    }
    public AdapterProcess(string point, string state, int port)
    {
        string executable = Path.Combine(AppContext.BaseDirectory, "mframe-fs.exe");
        string command = $"\"{executable}\" mount \"{point}\" --state \"{state}\" --endpoint 127.0.0.1:{port}";
        var startup = new Startup { Size = Marshal.SizeOf<Startup>(), Flags = 1, ShowWindow = 0 };
        if (!CreateProcess(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, false, 0x10 | 0x200, IntPtr.Zero, null, ref startup, out ProcessInformation info))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        _processHandle = info.Process;
        Process = Process.GetProcessById((int)info.ProcessId);
        CloseHandle(info.Thread);
    }

    public async Task ReadyAsync(string point)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!Directory.Exists(point))
        {
            if (Process.HasExited) throw new IOException($"Adapter exited during mount: {Process.ExitCode}");
            await Task.Delay(100, timeout.Token);
        }
    }

    public async Task InterruptAsync()
    {
        string script = "Add-Type -TypeDefinition @'\nusing System; using System.Runtime.InteropServices; public static class Signal { [DllImport(\"kernel32.dll\")] public static extern bool FreeConsole(); [DllImport(\"kernel32.dll\")] public static extern bool AttachConsole(uint id); [DllImport(\"kernel32.dll\")] public static extern bool SetConsoleCtrlHandler(IntPtr p, bool add); [DllImport(\"kernel32.dll\")] public static extern bool GenerateConsoleCtrlEvent(uint type, uint group); }\n'@\n" +
            $"[Signal]::FreeConsole() | Out-Null; if (![Signal]::AttachConsole({Process.Id})) {{ exit 2 }}; [Signal]::SetConsoleCtrlHandler([IntPtr]::Zero,$true) | Out-Null; if (![Signal]::GenerateConsoleCtrlEvent(0,0)) {{ exit 3 }}; Start-Sleep -Milliseconds 300; [Signal]::FreeConsole() | Out-Null";
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using Process sender = Process.Start(start)!;
        await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0, sender.ExitCode);
        await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25));
    }

    public void Dispose()
    {
        if (!Process.HasExited) { Process.Kill(true); Process.WaitForExit(10000); }
        Process.Dispose();
        CloseHandle(_processHandle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Startup
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string? directory, ref Startup startup, out ProcessInformation information);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);
}
