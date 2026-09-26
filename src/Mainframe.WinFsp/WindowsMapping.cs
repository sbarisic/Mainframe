using System.Globalization;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using Fsp;
using Mainframe.Client;
using Mainframe.Protocol;
using FileInfo = Fsp.Interop.FileInfo;

namespace Mainframe.WinFsp;

public static class WindowsMapping
{
    public static int Status(Exception exception) => exception switch
    {
        KernelRpcException e => e.Code switch
        {
            "NOT_FOUND" => FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND,
            "PATH_NOT_FOUND" => FileSystemBase.STATUS_OBJECT_PATH_NOT_FOUND,
            "ALREADY_EXISTS" => FileSystemBase.STATUS_OBJECT_NAME_COLLISION,
            "ACCESS_DENIED" or "UNAUTHORIZED" or "AUTHENTICATION_FAILED" => FileSystemBase.STATUS_ACCESS_DENIED,
            "SHARING_VIOLATION" => FileSystemBase.STATUS_SHARING_VIOLATION,
            "LOCK_CONFLICT" => FileSystemBase.STATUS_FILE_LOCK_CONFLICT,
            "LOCK_NOT_HELD" => FileSystemBase.STATUS_RANGE_NOT_LOCKED,
            "VOLUME_LOCKED" => FileSystemBase.STATUS_DEVICE_NOT_READY,
            "INVALID_HANDLE" => FileSystemBase.STATUS_INVALID_HANDLE,
            "DELETE_PENDING" => FileSystemBase.STATUS_DELETE_PENDING,
            "DIRECTORY_NOT_EMPTY" => FileSystemBase.STATUS_DIRECTORY_NOT_EMPTY,
            "NOT_DIRECTORY" => FileSystemBase.STATUS_NOT_A_DIRECTORY,
            "IS_DIRECTORY" => FileSystemBase.STATUS_FILE_IS_A_DIRECTORY,
            "INVALID_PATH" => FileSystemBase.STATUS_OBJECT_NAME_INVALID,
            "INVALID_ARGUMENT" => FileSystemBase.STATUS_INVALID_PARAMETER,
            "NOT_SUPPORTED" or "UNSUPPORTED_METHOD" => FileSystemBase.STATUS_NOT_SUPPORTED,
            "RESOURCE_UNAVAILABLE" or "VOLUME_BUSY" => FileSystemBase.STATUS_INSUFFICIENT_RESOURCES,
            "DISK_FULL" or "STORAGE_FULL" => FileSystemBase.STATUS_DISK_FULL,
            "DEADLINE_EXCEEDED" or "CANCELLED" => FileSystemBase.STATUS_IO_TIMEOUT,
            _ => FileSystemBase.STATUS_IO_DEVICE_ERROR
        },
        PathTooLongException => FileSystemBase.STATUS_NAME_TOO_LONG,
        ArgumentException or OverflowException => FileSystemBase.STATUS_OBJECT_NAME_INVALID,
        UnauthorizedAccessException or AuthenticationException => FileSystemBase.STATUS_ACCESS_DENIED,
        OperationCanceledException or TimeoutException => FileSystemBase.STATUS_IO_TIMEOUT,
        ObjectDisposedException => FileSystemBase.STATUS_DEVICE_NOT_READY,
        NotSupportedException => FileSystemBase.STATUS_NOT_SUPPORTED,
        IOException => FileSystemBase.STATUS_IO_DEVICE_ERROR,
        _ => FileSystemBase.STATUS_UNEXPECTED_IO_ERROR
    };

    public static string[] Rights(uint access, bool directory, bool create = false)
    {
        var rights = new List<string> { "read-metadata" };
        // Cached partial writes can cause paging reads on a Windows write-only handle.
        // Windows still enforces the application's original granted access.
        if ((access & 1) != 0 || !directory && (access & 6) != 0) rights.Add(directory ? "list" : "read-data");
        if (create || !directory && (access & 2) != 0) rights.Add("write-data");
        if (!directory && (access & 4) != 0) rights.Add("append");
        if (create || (access & 0x100) != 0) rights.Add("write-metadata");
        if ((access & 0x10000) != 0) rights.Add("delete");
        return rights.ToArray();
    }

    public static ulong Identity(string id) => BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    public static FileInfo Info(FsEntry entry) => new()
    {
        FileAttributes = (uint)entry.Attributes | (entry.Directory ? 0x10u : entry.Attributes == 0 ? 0x80u : 0),
        FileSize = (ulong)FileNumbers.Parse(entry.Length),
        AllocationSize = (ulong)FileNumbers.Parse(entry.AllocationLength),
        CreationTime = Time(entry.Created), LastWriteTime = Time(entry.Modified),
        LastAccessTime = Time(entry.Accessed ?? entry.Modified), ChangeTime = Time(entry.Changed ?? entry.Modified),
        IndexNumber = Identity(entry.ResourceId ?? entry.Id)
    };
    private static ulong Time(string time) => checked((ulong)DateTimeOffset.Parse(time, CultureInfo.InvariantCulture).UtcDateTime.ToFileTimeUtc());
    public static string? Timestamp(ulong time) => time is 0 or ulong.MaxValue ? null : new DateTimeOffset(DateTime.FromFileTimeUtc(checked((long)time))).ToString("O");
    public static int Attributes(uint attributes)
    {
        if ((attributes & ~(0x127u | 0x10u | 0x80u)) != 0) throw new NotSupportedException("Unsupported file attributes.");
        return (int)(attributes & 0x127);
    }
}
