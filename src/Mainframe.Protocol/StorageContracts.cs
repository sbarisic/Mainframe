using System.Globalization;
using System.Text.Json.Serialization;

namespace Mainframe.Protocol;

public sealed record VolumeCreateRequest([property: JsonRequired] string Path, [property: JsonRequired] string Password)
{
    public override string ToString() => "VolumeCreateRequest [redacted]";
}

public sealed record VolumeMountRequest([property: JsonRequired] string Path, [property: JsonRequired] string Mount, [property: JsonRequired] string Password)
{
    public override string ToString() => "VolumeMountRequest [redacted]";
}

public sealed record VolumeInfo([property: JsonRequired] string Id, [property: JsonRequired] string Path, [property: JsonRequired] string Mount, [property: JsonRequired] string State, [property: JsonRequired] string Generation, string Provider = "sqlcipher-v1", string Durability = "full-commit");
public sealed record VolumeCreated([property: JsonRequired] string Id);
public sealed record FsStat(string? Path = null, string? Handle = null);
public sealed record FsPath([property: JsonRequired] string Path);
public sealed record FsRename([property: JsonRequired] string Source, [property: JsonRequired] string Destination, bool Replace = false);
public sealed record FsList([property: JsonRequired] string Path, int Limit = 256, string? Continuation = null);
public sealed record FsEntry([property: JsonRequired] string Id, [property: JsonRequired] string Name, [property: JsonRequired] bool Directory, [property: JsonRequired] string Length, [property: JsonRequired] string Created, [property: JsonRequired] string Modified, string? ResourceId = null, string? Accessed = null, string? Changed = null, string AllocationLength = "0", string StoredBytes = "0", int Attributes = 0, string? State = null, string? Generation = null);
public sealed record FsListing([property: JsonRequired] FsEntry[] Entries, string? Continuation);
public sealed record FsOpen([property: JsonRequired] string Path, string Access = "read", string Mode = "open-existing", string[]? Share = null);
public sealed record FsOpened([property: JsonRequired] string Handle, [property: JsonRequired] FsEntry Entry);
public sealed record FsHandle([property: JsonRequired] string Handle);
public sealed record FsRange([property: JsonRequired] string Handle, [property: JsonRequired] string Offset, [property: JsonRequired] string Length);
public sealed record FsTruncate([property: JsonRequired] string Handle, [property: JsonRequired] string Length);
public sealed record StorageStarted([property: JsonRequired] StreamDescriptor[] Channels);
public sealed record FsTransferred([property: JsonRequired] string Bytes, bool Eof = false);
public sealed record FsCommitted(bool Committed = true);
public static class FileNumbers
{
    public static long Parse(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 19 || value.Any(c => c < '0' || c > '9') || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
        {
            throw new ArgumentException("Expected a nonnegative signed 64-bit decimal string.");
        }

        return number;
    }

    public static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
}
