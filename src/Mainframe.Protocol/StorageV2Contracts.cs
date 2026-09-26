using System.Text.Json.Serialization;

namespace Mainframe.Protocol;

public sealed record FsOpenV2([property: JsonRequired] string Path, string Kind = "either", string Disposition = "open", string[]? Rights = null, string[]? Share = null);
public sealed record FsOpenedV2([property: JsonRequired] string Handle, [property: JsonRequired] FsEntry Entry, [property: JsonRequired] string Action);
public sealed record FsEnumerate([property: JsonRequired] string Handle, int Limit = 256, bool Restart = false, string? Marker = null, string? Continuation = null);
public sealed record FsRenameHandle([property: JsonRequired] string Handle, [property: JsonRequired] string Destination, bool Replace = false);
public sealed record FsMetadata([property: JsonRequired] string Handle, int? Attributes = null, string? Created = null, string? Modified = null, string? Accessed = null, string? Changed = null);
public sealed record FsSize([property: JsonRequired] string Handle, [property: JsonRequired] string Length, bool Allocation = false);
public sealed record FsDisposition([property: JsonRequired] string Handle, [property: JsonRequired] bool Delete);
public sealed record FsLock([property: JsonRequired] string Handle, [property: JsonRequired] string Offset, [property: JsonRequired] string Length, bool Exclusive = true);
public sealed record FsWriteV2([property: JsonRequired] string Handle, [property: JsonRequired] string Offset, [property: JsonRequired] string Length, bool Append = false, bool Constrained = false);
public sealed record FsCapabilities([property: JsonRequired] string[] Supported, [property: JsonRequired] string[] Unsupported, int MaximumTransfer = 65536, int MaximumEnumeration = 256, bool ThinAllocation = true);
public sealed record FsSpace([property: JsonRequired] string Identity, [property: JsonRequired] string TotalBytes, [property: JsonRequired] string FreeBytes, string Scope = "shared-host-capacity");
public sealed record FsDiscovery([property: JsonRequired] FsCapabilities Capabilities, [property: JsonRequired] FsEntry Entry, [property: JsonRequired] FsSpace[] BackingStorage);
