using System.Globalization;
using Mainframe.Protocol;
using Microsoft.Data.Sqlite;

namespace Mainframe.Storage;

public sealed partial class EncryptedVolume
{
    private int _transactionDepth;
    private readonly Dictionary<long, int> _references = new();

    private void UpgradeSchema()
    {
        if (Equals(Scalar("PRAGMA user_version"), 2L))
        {
            return;
        }

        using SqliteTransaction transaction = _database.BeginTransaction();
        Execute("""
            ALTER TABLE entries ADD COLUMN accessed TEXT NOT NULL DEFAULT '';
            ALTER TABLE entries ADD COLUMN changed TEXT NOT NULL DEFAULT '';
            ALTER TABLE entries ADD COLUMN allocation INTEGER NOT NULL DEFAULT 0 CHECK(allocation>=0);
            ALTER TABLE entries ADD COLUMN attributes INTEGER NOT NULL DEFAULT 0;
            ALTER TABLE entries ADD COLUMN linked INTEGER NOT NULL DEFAULT 1;
            ALTER TABLE entries ADD COLUMN pending_delete INTEGER NOT NULL DEFAULT 0;
            UPDATE entries SET accessed=modified,changed=modified,allocation=length;
            PRAGMA user_version=2;
            """);
        Fault?.Invoke("migration-before-commit");
        transaction.Commit();
        Fault?.Invoke("migration-after-commit");
    }

    public T Atomic<T>(Func<T> action, Func<bool> authorized) => Transaction(action, authorized);

    public void Pin(long id)
    {
        Stat(id);
        _references[id] = _references.GetValueOrDefault(id) + 1;
    }

    public void Unpin(long id)
    {
        if (_references.TryGetValue(id, out int count))
        {
            if (count == 1) _references.Remove(id);
            else _references[id] = count - 1;
        }
    }

    public bool IsLinked(long id) => Equals(Scalar("SELECT linked FROM entries WHERE id=$id", ("$id", id)), 1L);
    public bool IsDeletePending(long id) => Equals(Scalar("SELECT pending_delete FROM entries WHERE id=$id", ("$id", id)), 1L);

    public void RejectPending(long id)
    {
        if (IsDeletePending(id)) throw Error("DELETE_PENDING", "Entry has an accepted deletion disposition.");
    }

    private void Writable(long id)
    {
        if ((Stat(id).Attributes & 1) != 0) throw Error("ACCESS_DENIED", "Entry is read-only.");
    }

    public FsEntry[] Enumerate(long parent, string? marker, int limit)
    {
        if (limit is < 1 or > 256) throw Error("INVALID_ARGUMENT", "Invalid enumeration limit.");
        if (!Stat(parent).Directory) throw Error("NOT_DIRECTORY", "Entry is not a directory.");
        using SqliteCommand command = Command("SELECT id,name,directory,length,created,modified,accessed,changed,allocation,attributes FROM entries WHERE parent=$p AND ($a IS NULL OR name>$a COLLATE MF_NAME) ORDER BY name COLLATE MF_NAME LIMIT $l", ("$p", parent), ("$a", (object?)marker ?? DBNull.Value), ("$l", limit));
        using SqliteDataReader reader = command.ExecuteReader();
        var entries = new List<FsEntry>();
        while (reader.Read()) entries.Add(Entry(reader));
        return entries.ToArray();
    }

    public void SetMetadata(long id, FsMetadata metadata, Func<bool> authorized) => Transaction(() =>
    {
        FsEntry current = Stat(id);
        int attributes = metadata.Attributes ?? current.Attributes;
        if ((attributes & ~0x127) != 0) throw Error("INVALID_ARGUMENT", "Unsupported attributes.");
        static string Timestamp(string? value, string fallback)
        {
            if (value is null) return fallback;
            if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed))
                throw Error("INVALID_ARGUMENT", "Timestamps must be ISO 8601 round-trip values.");
            return parsed.ToUniversalTime().ToString("O");
        }
        Execute("UPDATE entries SET attributes=$a,created=$c,modified=$m,accessed=$r,changed=$t WHERE id=$id", ("$a", attributes), ("$c", Timestamp(metadata.Created, current.Created)), ("$m", Timestamp(metadata.Modified, current.Modified)), ("$r", Timestamp(metadata.Accessed, current.Accessed!)), ("$t", Timestamp(metadata.Changed, DateTimeOffset.UtcNow.ToString("O"))), ("$id", id));
        return 0;
    }, authorized);

    public void SetAllocation(long id, long length, Func<bool> authorized) => Transaction(() =>
    {
        if (length < 0) throw Error("INVALID_ARGUMENT", "Invalid allocation length.");
        Writable(id);
        if (Stat(id).Directory) throw Error("IS_DIRECTORY", "Directories have no allocation length.");
        if (length < FileNumbers.Parse(Stat(id).Length)) Truncate(id, length, authorized);
        Execute("UPDATE entries SET allocation=$l,changed=$t WHERE id=$id", ("$l", length), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));
        return 0;
    }, authorized);

    public void SetDelete(long id, bool delete, Func<bool> authorized)
    {
        Transaction(() =>
    {
        if (id == 1 || !IsLinked(id)) throw Error("ACCESS_DENIED", "Cannot change deletion of this entry.");
        Writable(id);
        if (delete && (long)Scalar("SELECT count(*) FROM entries WHERE parent=$id", ("$id", id))! != 0)
            throw Error("DIRECTORY_NOT_EMPTY", "Directory is not empty.");
        Execute("UPDATE entries SET pending_delete=$d,changed=$t WHERE id=$id", ("$d", delete ? 1 : 0), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));
        Fault?.Invoke("delete-intent");
        return 0;
    }, authorized);
        Fault?.Invoke("delete-intent-committed");
    }

    public void FinalizeDelete(long id)
    {
        Transaction(() =>
        {
            Execute("UPDATE entries SET parent=NULL,linked=0,pending_delete=0 WHERE id=$id AND pending_delete=1", ("$id", id));
            Fault?.Invoke("delete-cleanup");
            return 0;
        }, () => true);
    }

    private void RecoverDeletion()
    {
        if (Equals(Scalar("SELECT count(*) FROM entries WHERE pending_delete=1"), 0L)) return;
        Transaction(() =>
        {
            Execute("UPDATE entries SET parent=NULL,linked=0,pending_delete=0 WHERE pending_delete=1");
            return 0;
        }, () => true);
    }

    private void ReclaimObjects()
    {
        // The pin table is bounded by the host's handle quota; the SQL batch is bounded too.
        string pins = _references.Count == 0 ? "0" : string.Join(',', _references.Keys);
        Execute($"DELETE FROM entries WHERE id IN (SELECT id FROM entries WHERE linked=0 AND id NOT IN ({pins}) LIMIT 128)");
        if ((long)Scalar("SELECT changes()")! > 0) Fault?.Invoke("reclaim");
    }
}
