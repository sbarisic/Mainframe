using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Win32;

namespace Mainframe.WinFsp;

public static class WinFspRuntime
{
    public const string Attribution = "WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos\nhttps://github.com/winfsp/winfsp";
    public const string QualifiedVersion = "2.1.25156";
    private static string? s_directory;

    public static void Initialize()
    {
        if (s_directory is not null) return;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("mframe-fs requires Windows x64.");
        using RegistryKey registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
        using RegistryKey? key = registry.OpenSubKey(@"SOFTWARE\WinFsp");
        string directory = key?.GetValue("InstallDir") as string ?? throw new InvalidOperationException("WinFsp is not installed. Install WinFsp 2.1.25156 with its .NET binding.");
        string binding = Path.Combine(directory, "bin", "winfsp-msil.dll");
        string native = Path.Combine(directory, "bin", "winfsp-x64.dll");
        foreach (string path in new[] { binding, native })
        {
            if (!File.Exists(path)) throw new InvalidOperationException($"Missing WinFsp component: {path}");
            var version = FileVersionInfo.GetVersionInfo(path);
            if (version.FileMajorPart != 2 || version.FileMinorPart != 1 || version.FileBuildPart != 25156)
                throw new InvalidOperationException($"WinFsp {version.FileVersion} is not qualified; expected {QualifiedVersion}.");
        }
        s_directory = directory;
        AssemblyLoadContext.Default.Resolving += Resolve;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name) => name.Name == "winfsp-msil"
        ? context.LoadFromAssemblyPath(Path.Combine(s_directory!, "bin", "winfsp-msil.dll")) : null;
}
