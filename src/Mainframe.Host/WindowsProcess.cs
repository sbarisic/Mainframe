using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Mainframe.Protocol;
using Microsoft.Win32.SafeHandles;

namespace Mainframe.Host;
/// <summary>Owns native process, job, pseudoconsole, and only the explicitly inherited pipes.</summary>
public sealed class WindowsProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly Process process;
    private IntPtr pseudoConsole;
    private int disposed;
    public Stream Stdin
    {
        get;
    }
    public Stream Stdout
    {
        get;
    }
    public Stream? Stderr
    {
        get;
    }
    public Stream Bootstrap
    {
        get;
    }
    public int Id => process.Id;
    public bool Terminal => pseudoConsole != IntPtr.Zero;

    private WindowsProcess(Process process, SafeFileHandle job, IntPtr pseudoConsole, Stream stdin, Stream stdout, Stream? stderr, Stream bootstrap)
    {
        this.process = process;
        this.job = job;
        this.pseudoConsole = pseudoConsole;
        Stdin = stdin;
        Stdout = stdout;
        Stderr = stderr;
        Bootstrap = bootstrap;
    }

    public static WindowsProcess Start(ProgramManifest manifest, string[] arguments, string directory, bool terminal, int columns, int rows, string endpoint)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows process adapter required.");
        if (columns is < 1 or > 32767 || rows is < 1 or > 32767)
            throw new ArgumentException("Invalid terminal dimensions.");
        var owned = new List<IDisposable>();
        IntPtr hpcon = IntPtr.Zero, attributes = IntPtr.Zero, environment = IntPtr.Zero;
        var allocations = new List<IntPtr>();
        try
        {
            SafeFileHandle job = Native.CreateJobObjectW(IntPtr.Zero, null);
            owned.Add(job);
            if (job.IsInvalid)
                throw new Win32Exception();
            var limits = new ExtendedLimits();
            limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
            if (!Native.SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
                throw new Win32Exception();
            (SafeFileHandle Read, SafeFileHandle Write) input = Pipe();
            (SafeFileHandle Read, SafeFileHandle Write) output = Pipe();
            (SafeFileHandle Read, SafeFileHandle Write) bootstrap = Pipe();
            owned.AddRange([input.Read, input.Write, output.Read, output.Write, bootstrap.Read, bootstrap.Write]);
            (SafeFileHandle Read, SafeFileHandle Write)? error = terminal ? null : Pipe();
            if (error is { } err)
                owned.AddRange([err.Read, err.Write]);
            foreach (SafeFileHandle hostHandle in new[]
            {
                input.Write,
                output.Read,
                bootstrap.Write,
                error?.Read
            }.OfType<SafeFileHandle>())
                if (!Native.SetHandleInformation(hostHandle, 1, 0))
                    throw new Win32Exception();
            if (terminal)
            {
                int hr = Native.CreatePseudoConsole(new Coord((short)columns, (short)rows), input.Read, output.Write, 0, out hpcon);
                Marshal.ThrowExceptionForHR(hr);
            }

            int attributeCount = terminal ? 3 : 2;
            IntPtr size = IntPtr.Zero;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!Native.InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size))
                throw new Win32Exception();
            IntPtr AttributeHandles(params IntPtr[] handles)
            {
                var pointer = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
                allocations.Add(pointer);
                Marshal.Copy(handles, 0, pointer, handles.Length);
                return pointer;
            }

            void Attribute(nint key, IntPtr pointer, int bytes)
            {
                if (!Native.UpdateProcThreadAttribute(attributes, 0, key, pointer, (nuint)bytes, IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception();
            }

            Attribute(0x2000D, AttributeHandles(job.DangerousGetHandle()), IntPtr.Size); // Job membership at creation, no orphan gap.
            IntPtr[] inherited = terminal ? [bootstrap.Read.DangerousGetHandle()] : [input.Read.DangerousGetHandle(), output.Write.DangerousGetHandle(), error!.Value.Write.DangerousGetHandle(), bootstrap.Read.DangerousGetHandle()];
            Attribute(0x20002, AttributeHandles(inherited), IntPtr.Size * inherited.Length);
            if (terminal)
                Attribute(0x20016, hpcon, IntPtr.Size);
            var startup = new StartupInfoEx();
            startup.Info.Size = Marshal.SizeOf<StartupInfoEx>();
            startup.Attributes = attributes;
            // Null standard handles ask ConPTY to create console handles, instead of inheriting
            // a redirected parent console. This also works with an explicit bootstrap handle list.
            startup.Info.Flags = 0x100;
            if (!terminal)
            {
                startup.Info.Flags = 0x100;
                startup.Info.Input = input.Read.DangerousGetHandle();
                startup.Info.Output = output.Write.DangerousGetHandle();
                startup.Info.Error = error!.Value.Write.DangerousGetHandle();
            }

            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in manifest.EnvironmentAllowlist.Concat(new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" }))
                if (Environment.GetEnvironmentVariable(name) is { } value)
                    env[name] = value;
            env["MF_ENDPOINT"] = endpoint;
            env["MF_BOOTSTRAP_HANDLE"] = bootstrap.Read.DangerousGetHandle().ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', env.Select(kv => kv.Key + "=" + kv.Value)) + "\0\0");
            string executable = manifest.Interpreter ?? manifest.Executable;
            IEnumerable<string> argv = (manifest.Interpreter is null ? Array.Empty<string>() : new[]
            {
                manifest.Executable
            }

            ).Concat(manifest.FixedArguments).Concat(arguments);
            var commandLine = new StringBuilder(string.Join(' ', new[] { executable }.Concat(argv).Select(QuoteArgument)));
            if (commandLine.Length >= 32767)
                throw new ArgumentException("Windows command line is too long.");
            if (!Native.CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, 0x80000 | 0x400 | 0x4 | (terminal ? 0u : 0x08000000u), environment, directory, ref startup, out ProcessInfo info))
                throw new Win32Exception();
            using var processHandle = new SafeFileHandle(info.Process, true);
            using var threadHandle = new SafeFileHandle(info.Thread, true);
            var process = Process.GetProcessById(info.ProcessId);
            owned.Add(process);
            _ = process.SafeHandle; // Hold a query/wait handle before resume, including very short-lived children.
            // Dispose child ends in the parent; retaining them would hide EOF.
            input.Read.Dispose();
            output.Write.Dispose();
            error?.Write.Dispose();
            bootstrap.Read.Dispose();
            var stdin = new FileStream(input.Write, FileAccess.Write);
            owned.Add(stdin);
            var stdout = new FileStream(output.Read, FileAccess.Read);
            owned.Add(stdout);
            FileStream? stderr = error is { } e ? new FileStream(e.Read, FileAccess.Read) : null;
            if (stderr is not null)
                owned.Add(stderr);
            var boot = new FileStream(bootstrap.Write, FileAccess.Write);
            owned.Add(boot);
            if (Native.ResumeThread(threadHandle) == uint.MaxValue)
                throw new Win32Exception();
            var result = new WindowsProcess(process, job, hpcon, stdin, stdout, stderr, boot);
            owned.Clear();
            hpcon = IntPtr.Zero;
            return result;
        }
        finally
        {
            foreach (IDisposable? item in owned.AsEnumerable().Reverse())
                item.Dispose();
            if (hpcon != IntPtr.Zero)
                Native.ClosePseudoConsole(hpcon);
            if (attributes != IntPtr.Zero)
            {
                Native.DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            foreach (var pointer in allocations)
                Marshal.FreeHGlobal(pointer);
            if (environment != IntPtr.Zero)
                Marshal.FreeHGlobal(environment);
        }
    }

    public static string QuoteArgument(string argument)
    {
        if (argument.Contains('\0'))
            throw new ArgumentException("NUL is not a valid argument.");
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            slashes = 0;
            result.Append(c);
        }

        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) Pipe()
    {
        var security = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            Inherit = 1
        };
        if (!Native.CreatePipe(out SafeFileHandle? read, out SafeFileHandle? write, ref security, 65536))
            throw new Win32Exception();
        return (read, write);
    }

    public async Task<int> WaitAsync(CancellationToken token = default)
    {
        await process.WaitForExitAsync(token);
        return process.ExitCode;
    }

    public void Resize(int columns, int rows)
    {
        if (!Terminal)
            throw new InvalidOperationException("Resize requires terminal mode.");
        if (columns is < 1 or > 32767 || rows is < 1 or > 32767)
            throw new ArgumentException("Invalid terminal dimensions.");
        Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(pseudoConsole, new((short)columns, (short)rows)));
    }

    public async Task InterruptAsync()
    {
        if (!Terminal)
            throw new InvalidOperationException("Pipe mode has no console interrupt; cancel the execution instead.");
        await Stdin.WriteAsync(new byte[] { 3 });
        await Stdin.FlushAsync();
    }

    public async Task StopAsync()
    {
        try
        {
            if (Terminal)
                await InterruptAsync();
            else
                Stdin.Dispose();
        }
        catch (IOException)
        {
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
        }

        Native.TerminateJobObject(job, 130);
    }

    public void KillDescendants() => Native.TerminateJobObject(job, 130);
    public Task CloseTerminalAsync()
    {
        var handle = Interlocked.Exchange(ref pseudoConsole, IntPtr.Zero);
        return handle == IntPtr.Zero ? Task.CompletedTask : Task.Run(() => Native.ClosePseudoConsole(handle));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        job.Dispose();
        Stdin.Dispose();
        Bootstrap.Dispose();
        if (pseudoConsole != IntPtr.Zero)
        {
            // Closing the read endpoint makes ConPTY's final flush fail promptly when a
            // launch is abandoned before the normal output pump has been installed.
            Stdout.Dispose();
            Native.ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;
        }

        Stdout.Dispose();
        Stderr?.Dispose();
        process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Coord(short X, short Y);
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short Show, ReservedSize;
        public IntPtr ReservedPointer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Info;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr Minimum, Maximum;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong A, B, C, D, E, F;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObjectW(IntPtr security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int information, ref ExtendedLimits limits, int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
        [DllImport("kernel32.dll")]
        internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr handle);
        [DllImport("kernel32.dll")]
        internal static extern int ResizePseudoConsole(IntPtr handle, Coord size);
        [DllImport("kernel32.dll")]
        internal static extern void ClosePseudoConsole(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, nint attribute, IntPtr value, nuint size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")]
        internal static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool CreateProcessW(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern uint ResumeThread(SafeFileHandle thread);
    }
}
