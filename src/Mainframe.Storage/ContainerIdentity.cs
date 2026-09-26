using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mainframe.Storage;

internal static class ContainerIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    public static string Canonical(FileStream file)
    {
        if (!GetFileInformationByHandle(file.SafeFileHandle, out FileInformation info))
        {
            throw new IOException("Cannot inspect container identity.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        if (info.Links != 1 || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
        {
            throw EncryptedVolume.Error("INVALID_PATH", "Container hard links and reparse points are unsupported.");
        }

        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(file.SafeFileHandle, path, (uint)path.Capacity, 0);
        if (length == 0 || length >= path.Capacity)
        {
            throw new IOException("Cannot resolve container path.");
        }

        string result = path.ToString();
        if (result.StartsWith(@"\\?\"))
        {
            result = result[4..];
        }

        return EncryptedVolume.ValidateHostPath(result);
    }
}
