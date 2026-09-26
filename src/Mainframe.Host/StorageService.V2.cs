using System.Security.Cryptography;
using System.Text;
using Mainframe.Core;
using Mainframe.Protocol;
using Mainframe.Storage;

namespace Mainframe.Host;

internal sealed partial class StorageService
{
    private static readonly string s_namespaceTime = DateTimeOffset.UnixEpoch.ToString("O");
    private readonly Mount _namespace = new(new VolumeInfo("namespace", "", "/", "mounted", "namespace-v1"), null);
    private static readonly string[] s_rights = ["read-data", "write-data", "append", "list", "read-metadata", "write-metadata", "delete"];
    private static bool ProtectedNamespaceEntry(string path)
    {
        string normalized = VolumeNames.Normalize(path);
        return normalized is "/" or "/vol" || normalized.StartsWith("/vol/", StringComparison.Ordinal) && normalized.Count(c => c == '/') == 2;
    }
    private static bool IsSynthetic(string path) => VolumeNames.Normalize(path) is "/" or "/vol";
    private FsEntry SyntheticStat(string path) => new("namespace:" + path, path == "/" ? "" : "vol", true, "0", s_namespaceTime, s_namespaceTime, "namespace:" + _store.Identity.MainframeId + ":" + path, s_namespaceTime, s_namespaceTime, Generation: _rootGeneration);
    private FsEntry MountStat(Mount mount) => mount.Volume is null
        ? new(mount.Info.Id, mount.Info.Mount[5..], true, "0", s_namespaceTime, s_namespaceTime, mount.Info.Id + ":1", s_namespaceTime, s_namespaceTime, State: "locked", Generation: mount.Info.Generation)
        : mount.Root! with
        {
            Name = mount.Info.Mount[5..],
            State = "mounted"
        };

    private void CloseHandle(string id)
    {
        if (!_handles.TryGetValue(id, out Handle? handle)) return;
        CleanupHandle(handle);
        if (_handles.TryRemove(id, out _) && handle.SyntheticPath is null && _mounts.TryGetValue(handle.Mount, out Mount? mount))
            mount.Volume?.Unpin(handle.Entry);
    }

    private void CleanupHandle(Handle handle)
    {
        handle.Cleaned = true;
        lock (_handles) handle.Locks.Clear();
        if (handle.SyntheticPath is not null) return;
        if (_mounts.TryGetValue(handle.Mount, out Mount? mount) && mount.Volume is not null &&
            !_handles.Values.Any(h => h.Mount == handle.Mount && h.Entry == handle.Entry && !h.Cleaned))
        {
            if (mount.Volume.IsDeletePending(handle.Entry))
            {
                mount.Volume.FinalizeDelete(handle.Entry);
                Invalidate(handle.Mount, handle.Entry);
            }
        }
    }

    private static bool Overlaps(long a, long count, long b, long length) => count > 0 && length > 0 && a < b + length && b < a + count;
    private void CheckRange(Handle handle, Mount mount, long offset, long length, bool write)
    {
        lock (_handles)
        {
            foreach (Handle other in _handles.Values.Where(h => h.Mount == handle.Mount && h.Entry == handle.Entry))
                foreach (RangeLock range in other.Locks)
                {
                    if ((write && !range.Exclusive || !ReferenceEquals(handle, other) && (write || range.Exclusive)) && Overlaps(offset, length, range.Offset, range.Length))
                        throw Error("LOCK_CONFLICT", "Operation conflicts with a byte-range lock.");
                }
        }
    }

    private void CheckResize(Handle handle, Mount mount, long length)
    {
        long current = FileNumbers.Parse(mount.Volume!.Stat(handle.Entry).Length);
        CheckRange(handle, mount, Math.Min(current, length), Math.Abs(current - length), true);
    }

    private static void Right(Handle handle, string right) => Require(handle.V2 && handle.Rights.Contains(right));
    private static bool Reads(string[] rights) => rights.Any(r => r is "read-data" or "list" or "read-metadata");
    private static bool Writes(string[] rights) => rights.Any(r => r is "write-data" or "append" or "write-metadata" or "delete");

    private object UnaryV2(RpcRequest request, OperatorSession session, CancellationToken token)
    {
        if (request.Method == "fs.open") return OpenV2(ExecutionService.Args<FsOpenV2>(request), session, token);
        if (request.Method == "fs.stat")
        {
            FsStat args = ExecutionService.Args<FsStat>(request);
            if ((args.Handle is null) == (args.Path is null)) throw Error("INVALID_ARGUMENT", "Supply exactly one path or handle.");
            if (args.Handle is null) return Unary(request with { Version = 1 }, session, token);
            var h = GetHandle(args.Handle, session);
            Right(h.Handle, "read-metadata");
            return h.Handle.SyntheticPath is string path ? SyntheticStat(path) : h.Mount.Volume is null ? MountStat(h.Mount) : h.Mount.Volume.Stat(h.Handle.Entry);
        }
        if (request.Method is "fs.discover" or "fs.flush-volume")
        {
            string path = VolumeNames.Normalize(ExecutionService.Args<FsPath>(request).Path);
            Mount[] mounts = IsSynthetic(path) ? _mounts.Values.Where(m => Allowed(session, m, "read")).ToArray() : [Resolve(path, session, true).Mount];
            if (request.Method == "fs.flush-volume")
            {
                if (IsSynthetic(path)) throw Error("NOT_SUPPORTED", "Flush selects one volume.");
                CheckAccess(session, mounts[0], false, true);
                if (mounts[0].Volume is null) throw Error("VOLUME_LOCKED", "Volume requires manual unlock.");
                mounts[0].Volume!.Flush();
                return new FsCommitted();
            }
            foreach (Mount mount in mounts) CheckAccess(session, mount, true, false);
            FsEntry entry = IsSynthetic(path) ? SyntheticStat(path) : (FsEntry)Unary(new RpcRequest("fs.stat", 1, 10000, ProtocolJson.ToElement(new FsStat(Path: path))), session, token);
            FsSpace[] space = mounts.Select(m => BackingStorage.Inspect(m.Info.Path)).DistinctBy(s => s.Identity, StringComparer.OrdinalIgnoreCase).ToArray();
            return new FsDiscovery(new(["namespace-v1", "storage-v2", "directory-handles", "retained-objects", "byte-range-locks", "atomic-append", "constrained-write", "explicit-timestamps"], ["persistent-acls", "alternate-streams", "reparse-points", "hard-links", "compression", "windows-sparse-controls", "cross-volume-rename", "recursive-delete"]), entry, space);
        }

        if (request.Method is not ("fs.close" or "fs.cleanup" or "fs.enumerate" or "fs.flush" or "fs.metadata" or "fs.size" or "fs.disposition" or "fs.rename" or "fs.lock" or "fs.unlock"))
            throw Error("UNSUPPORTED_METHOD", "Unknown version-2 filesystem method.");
        if (!request.Arguments.TryGetProperty("handle", out var handleValue) || handleValue.ValueKind != System.Text.Json.JsonValueKind.String)
            throw Error("INVALID_ARGUMENT", "A handle is required.");
        string id = handleValue.GetString()!;
        (Handle handle, Mount owner) = GetHandle(id, session);
        if (!handle.V2) throw Error("INVALID_HANDLE", "This operation requires a version-2 handle.");
        Func<bool> valid = () => !token.IsCancellationRequested && !handle.Revoked && handle.Valid();
        if (request.Method == "fs.close") { CloseHandle(id); return new FsCommitted(); }
        if (request.Method == "fs.cleanup") { CleanupHandle(handle); return new FsCommitted(); }
        if (request.Method == "fs.enumerate")
        {
            Right(handle, "list");
            FsEnumerate args = ExecutionService.Args<FsEnumerate>(request);
            if (args.Limit is < 1 or > 256 || args.Restart && args.Continuation is not null || args.Marker is not null && args.Continuation is not null)
                throw Error("INVALID_ARGUMENT", "Invalid enumeration request.");
            string? marker = args.Continuation is null ? args.Marker : DecodeNameCursor(args.Continuation, id, handle.Generation);
            if (marker is not null) marker = VolumeNames.Component(marker);
            FsEntry[] entries;
            if (handle.SyntheticPath is string synthetic)
            {
                IEnumerable<FsEntry> candidates = synthetic == "/" ? [SyntheticStat("/vol")] : _mounts.Values.Where(m => Allowed(session, m, "read")).Select(MountStat);
                entries = candidates.Where(e => marker is null || StringComparer.OrdinalIgnoreCase.Compare(e.Name, marker) > 0).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).Take(args.Limit).ToArray();
            }
            else
            {
                if (owner.Volume is null) throw Error("VOLUME_LOCKED", "Volume requires manual unlock.");
                entries = owner.Volume.Enumerate(handle.Entry, marker, args.Limit);
            }
            return new FsListing(entries, entries.Length == args.Limit ? EncodeNameCursor(id, handle.Generation, entries[^1].Name) : null);
        }
        if (handle.SyntheticPath is not null || owner.Volume is null) throw Error("ACCESS_DENIED", "Synthetic directories cannot be mutated.");
        IStorageVolume volume = owner.Volume;
        switch (request.Method)
        {
            case "fs.flush":
                CheckAccess(session, owner, false, true);
                volume.Flush();
                break;
            case "fs.metadata":
                Right(handle, "write-metadata");
                volume.SetMetadata(handle.Entry, ExecutionService.Args<FsMetadata>(request), valid);
                break;
            case "fs.size":
                Right(handle, "write-data");
                FsSize size = ExecutionService.Args<FsSize>(request);
                long length = FileNumbers.Parse(size.Length);
                if (!size.Allocation || length < FileNumbers.Parse(volume.Stat(handle.Entry).Length)) CheckResize(handle, owner, length);
                if (size.Allocation) volume.SetAllocation(handle.Entry, length, valid);
                else volume.Truncate(handle.Entry, length, valid);
                break;
            case "fs.disposition":
                Right(handle, "delete");
                if (handle.Cleaned) throw Error("INVALID_HANDLE", "Deletion disposition must precede cleanup.");
                DeleteSharingExcept(handle);
                volume.SetDelete(handle.Entry, ExecutionService.Args<FsDisposition>(request).Delete, valid);
                break;
            case "fs.rename":
                Right(handle, "delete");
                if (!volume.IsLinked(handle.Entry)) throw Error("NOT_FOUND", "Entry has no name.");
                FsRenameHandle rename = ExecutionService.Args<FsRenameHandle>(request);
                if (ProtectedNamespaceEntry(rename.Destination)) throw Error("ACCESS_DENIED", "Mount entries are immutable.");
                var target = Resolve(rename.Destination, session);
                if (target.Mount != owner) throw Error("NOT_SUPPORTED", "Cross-volume rename is unsupported.");
                DeleteSharingExcept(handle);
                long? replaced = null;
                volume.Rename(handle.Entry, target.Relative, rename.Replace, existing => { DeleteSharing(handle.Mount, existing); replaced = existing; }, valid, 4096 - owner.Info.Mount.Length - 1);
                if (replaced is long old) Invalidate(handle.Mount, old);
                break;
            case "fs.lock":
            case "fs.unlock":
                FsLock args = ExecutionService.Args<FsLock>(request);
                if (!handle.Rights.Any(r => r is "read-data" or "write-data" or "append")) throw Error("ACCESS_DENIED", "Data access is required for locks.");
                if (handle.Cleaned || volume.Stat(handle.Entry).Directory) throw Error("INVALID_HANDLE", "Cannot lock this handle.");
                long offset = FileNumbers.Parse(args.Offset), count = FileNumbers.Parse(args.Length);
                if (count == 0 || offset > long.MaxValue - count) throw Error("INVALID_ARGUMENT", "Invalid lock range.");
                var range = new RangeLock(offset, count, args.Exclusive);
                lock (_handles)
                {
                    if (request.Method == "fs.unlock")
                    {
                        if (!handle.Locks.Remove(range)) throw Error("LOCK_NOT_HELD", "Exact lock is not owned by this handle.");
                    }
                    else
                    {
                        if (handle.Locks.Count >= 256 || _handles.Values.Sum(h => h.Locks.Count) >= 4096) throw Error("RESOURCE_UNAVAILABLE", "Lock quota reached.");
                        foreach (Handle other in _handles.Values.Where(h => h.Mount == handle.Mount && h.Entry == handle.Entry))
                            foreach (RangeLock held in other.Locks)
                                if ((args.Exclusive || held.Exclusive) && Overlaps(offset, count, held.Offset, held.Length)) throw Error("LOCK_CONFLICT", "Lock range conflicts.");
                        handle.Locks.Add(range);
                    }
                }
                break;
            default:
                throw Error("UNSUPPORTED_METHOD", "Unknown version-2 filesystem method.");
        }
        return new FsCommitted();
    }

    private void DeleteSharingExcept(Handle handle)
    {
        if (_handles.Values.Any(h => !ReferenceEquals(h, handle) && h.Mount == handle.Mount && h.Entry == handle.Entry && !h.Cleaned && !h.Share.Contains("delete")))
            throw Error("SHARING_VIOLATION", "An open handle denies deletion.");
    }

    private FsOpenedV2 OpenV2(FsOpenV2 args, OperatorSession session, CancellationToken token)
    {
        string path = VolumeNames.Normalize(args.Path);
        string[] rights = args.Rights ?? ["read-data", "read-metadata"];
        string[] share = args.Share ?? [];
        if (args.Kind is not ("file" or "directory" or "either") || args.Disposition is not ("open" or "create" or "open-or-create" or "overwrite" or "overwrite-or-create" or "supersede") || rights.Length == 0 || rights.Distinct().Count() != rights.Length || rights.Any(r => !s_rights.Contains(r)) || share.Length > 3 || share.Any(r => r is not ("read" or "write" or "delete")) || share.Distinct().Count() != share.Length)
            throw Error("INVALID_ARGUMENT", "Invalid open options.");
        bool read = Reads(rights), write = Writes(rights);
        using IDisposable reservation = ReserveHandle(session.Id);
        string id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        if (IsSynthetic(path))
        {
            if (args.Disposition != "open" || write) throw Error("ACCESS_DENIED", "Namespace directories are immutable.");
            if (args.Kind == "file") throw Error("IS_DIRECTORY", "Expected a file.");
            _handles[id] = new(session.Id, "/", 0, "metadata", share, session.Valid) { V2 = true, Rights = rights, SyntheticPath = path, Generation = _rootGeneration };
            return new(id, SyntheticStat(path), "opened");
        }
        var resolved = Resolve(path, session, true);
        Mount mount = resolved.Mount;
        CheckAccess(session, mount, read, write);
        if (mount.Volume is null) throw Error("VOLUME_LOCKED", "Volume requires manual unlock.");
        IStorageVolume volume = mount.Volume;
        if (resolved.Relative.Length == 0 && (args.Disposition != "open" || write)) throw Error("ACCESS_DENIED", "Mount roots are protected.");
        if (args.Disposition != "open" && !rights.Contains("write-data")) throw Error("ACCESS_DENIED", "Creating or overwriting requires data-write rights.");
        if (args.Disposition == "supersede" && !rights.Contains("delete")) throw Error("ACCESS_DENIED", "Supersede requires deletion rights.");
        Func<bool> valid = () => !token.IsCancellationRequested && session.Valid() && (!read || Allowed(session, mount, "read")) && (!write || Allowed(session, mount, "write"));
        string action = "opened";
        long? removed = null;
        long entry = volume.Atomic(() =>
        {
            long found;
            try { found = volume.Resolve(resolved.Relative); }
            catch (KernelOperationException ex) when (ex.Code == "NOT_FOUND" && args.Disposition is "create" or "open-or-create" or "overwrite-or-create" or "supersede")
            {
                action = "created";
                return volume.CreateEntry(resolved.Relative, args.Kind == "directory", valid);
            }
            volume.RejectPending(found);
            FsEntry stat = volume.Stat(found);
            if (args.Kind == "file" && stat.Directory) throw Error("IS_DIRECTORY", "Expected a file.");
            if (args.Kind == "directory" && !stat.Directory) throw Error("NOT_DIRECTORY", "Expected a directory.");
            if (args.Disposition == "create") throw Error("ALREADY_EXISTS", "Entry exists.");
            string access = DataAccess(rights);
            Sharing(mount.Info.Mount, found, access, share);
            if (rights.Contains("delete")) DeleteSharing(mount.Info.Mount, found);
            if (!share.Contains("delete") && _handles.Values.Any(h => h.Mount == mount.Info.Mount && h.Entry == found && !h.Cleaned && h.Rights.Contains("delete"))) throw Error("SHARING_VIOLATION", "An existing handle requires delete sharing.");
            if (args.Disposition is "overwrite" or "overwrite-or-create" or "supersede")
            {
                if (stat.Directory) throw Error("IS_DIRECTORY", "Directories cannot be overwritten or superseded.");
                var temporary = new Handle(session.Id, mount.Info.Mount, found, access, share, valid);
                CheckResize(temporary, mount, 0);
                if (args.Disposition == "supersede")
                {
                    DeleteSharing(mount.Info.Mount, found);
                    volume.Delete(found, valid);
                    removed = found;
                    found = volume.CreateEntry(resolved.Relative, false, valid);
                }
                else volume.Truncate(found, 0, valid);
                action = "replaced";
            }
            return found;
        }, valid);
        if (removed is long old) Invalidate(mount.Info.Mount, old);
        volume.Pin(entry);
        _handles[id] = new(session.Id, mount.Info.Mount, entry, DataAccess(rights), share, () => session.Valid() && (!read || Allowed(session, mount, "read")) && (!write || Allowed(session, mount, "write"))) { V2 = true, Rights = rights, Generation = mount.Info.Generation };
        return new(id, volume.Stat(entry), action);
    }

    private static string DataAccess(string[] rights)
    {
        bool read = rights.Contains("read-data") || rights.Contains("list"), write = rights.Contains("write-data") || rights.Contains("append");
        return read ? write ? "read-write" : "read" : write ? "write" : "metadata";
    }

    private string EncodeNameCursor(string handle, string generation, string marker)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(handle + "|" + generation + "|" + marker);
        return Convert.ToBase64String(bytes) + "." + Convert.ToHexString(HMACSHA256.HashData(_cursorKey, bytes));
    }

    private string DecodeNameCursor(string cursor, string handle, string generation)
    {
        try
        {
            if (cursor.Length > 2048) throw new FormatException();
            string[] parts = cursor.Split('.');
            if (parts.Length != 2) throw new FormatException();
            byte[] bytes = Convert.FromBase64String(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(parts[1]), HMACSHA256.HashData(_cursorKey, bytes))) throw new FormatException();
            string prefix = handle + "|" + generation + "|", payload = Encoding.UTF8.GetString(bytes);
            if (!payload.StartsWith(prefix, StringComparison.Ordinal)) throw new FormatException();
            return payload[prefix.Length..];
        }
        catch (FormatException) { throw Error("INVALID_ARGUMENT", "Invalid handle enumeration continuation."); }
    }
}
