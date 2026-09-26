using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Mainframe.Protocol;

namespace Mainframe.Storage;

public static class BackingStorage
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, uint capacity);

    public static FsSpace Inspect(string container)
    {
        string root = Path.GetPathRoot(container) ?? throw new IOException("Backing storage root is unavailable.");
        var identity = new StringBuilder(1024);
        if (!GetVolumeNameForVolumeMountPointW(root, identity, (uint)identity.Capacity))
            throw new IOException("Cannot identify backing storage.", new Win32Exception(Marshal.GetLastWin32Error()));
        var drive = new DriveInfo(root);
        return new(identity.ToString(), FileNumbers.Format(drive.TotalSize), FileNumbers.Format(drive.AvailableFreeSpace));
    }
}
