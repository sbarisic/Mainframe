using Mainframe.Core;
using Mainframe.Protocol;
using Microsoft.Data.Sqlite;

namespace Mainframe.Storage;
/// <summary>Called only while the owner's operation semaphore is held.</summary>
public sealed partial class EncryptedVolume : IStorageVolume
{
    public const int ChunkSize = 65536;
    private readonly SqliteConnection _database;
    private readonly FileStream _ownership;
    private readonly FileStream _container;
    private bool _disposed;
    public string Id
    {
        get;
    }
    public string Path
    {
        get;
    }
    public string Generation { get; } = Guid.NewGuid().ToString("N");
    public Action<string>? Fault
    {
        get; set;
    }

    private EncryptedVolume(string path, string password, CancellationToken cancellationToken, Action<string>? fault)
    {
        Fault = fault;
        Path = ValidateHostPath(path);
        _container = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            Path = ContainerIdentity.Canonical(_container);
            _ownership = new FileStream(Path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            _database = OpenDatabase(Path, password);
            try
            {
                using var validationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                validationDeadline.CancelAfter(TimeSpan.FromSeconds(10));
                using CancellationTokenRegistration interrupt = validationDeadline.Token.Register(() => SQLitePCL.raw.sqlite3_interrupt(_database.Handle));
                if (Scalar("PRAGMA user_version") is not long version || version is < 1 or > 2)
                {
                    throw Error("UNSUPPORTED_FORMAT", "Unsupported volume schema.");
                }

                Id = (string?)Scalar("SELECT id FROM volume") ?? throw Error("CORRUPT_VOLUME", "Missing volume identity.");
                if (!Guid.TryParseExact(Id, "D", out _))
                {
                    throw Error("CORRUPT_VOLUME", "Invalid volume identity.");
                }

                using SqliteCommand check = Command("PRAGMA cipher_integrity_check");
                check.CommandTimeout = 10;
                using SqliteDataReader reader = check.ExecuteReader();
                if (reader.Read())
                {
                    throw Error("CORRUPT_VOLUME", "Encrypted page integrity check failed.");
                }

                if (!Equals(Scalar("PRAGMA quick_check"), "ok"))
                {
                    throw Error("CORRUPT_VOLUME", "Volume consistency check failed.");
                }

                validationDeadline.Token.ThrowIfCancellationRequested();
                Configure();
                UpgradeSchema();
                RecoverDeletion();
            }
            catch
            {
                _database.Dispose();
                throw;
            }
        }
        catch
        {
            _container.Dispose();
            _ownership?.Dispose();
            throw;
        }
    }

    public static string ValidateHostPath(string path)
    {
        if (!System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\") || path.StartsWith("//"))
        {
            throw Error("INVALID_PATH", "Container must be on a local disk.");
        }

        string full = System.IO.Path.GetFullPath(path);
        var drive = new DriveInfo(System.IO.Path.GetPathRoot(full)!);
        if (drive.DriveType != DriveType.Fixed)
        {
            throw Error("INVALID_PATH", "Container must be on a fixed local disk.");
        }

        PrivateStateDirectory.RejectLinks(System.IO.Path.GetDirectoryName(full)!);
        foreach (string suffix in new[]
        {
            "",
            "-wal",
            "-shm",
            "-journal",
            ".lock"
        })
        {
            if (File.Exists(full + suffix) && (File.GetAttributes(full + suffix) & FileAttributes.ReparsePoint) != 0)
            {
                throw Error("INVALID_PATH", "Container and sidecars must not be links.");
            }
        }

        return full;
    }

    private static SqliteConnection OpenDatabase(string path, string password)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            connection.Open();
            NativeSqlite.ApplyPassword(connection, password);
            NativeSqlite.Verify(connection);
            connection.CreateFunction<string, long>("mf_utf16_length", value => value.Length, true);
            connection.CreateCollation("MF_NAME", (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using SqliteCommand keyCheck = connection.CreateCommand();
            keyCheck.CommandText = "SELECT count(*) FROM sqlite_master";
            keyCheck.ExecuteScalar();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static string Create(string path, string password, Action<string>? fault = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        path = ValidateHostPath(path);
        if (File.Exists(path))
        {
            throw Error("ALREADY_EXISTS", "Container already exists.");
        }

        string parent = System.IO.Path.GetDirectoryName(path)!;
        if (!Directory.Exists(parent))
        {
            throw Error("NOT_FOUND", "Container parent does not exist.");
        }

        string staging = System.IO.Path.Combine(parent, ".mfv-create-" + Guid.NewGuid().ToString("N"));
        PrivateStateDirectory.Create(staging);
        string staged = System.IO.Path.Combine(staging, "volume.mfv");
        PrivateStateDirectory.Write(staging, "volume.mfv", []);
        string id = Guid.NewGuid().ToString("D");
        using (SqliteConnection database = OpenDatabase(staged, password))
        {
            using SqliteCommand schema = database.CreateCommand();
            schema.CommandText = """
                PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA trusted_schema=OFF; PRAGMA cache_size=-256;
                BEGIN IMMEDIATE;
                CREATE TABLE volume (id TEXT NOT NULL);
                CREATE TABLE entries (id INTEGER PRIMARY KEY AUTOINCREMENT, parent INTEGER REFERENCES entries(id), name TEXT COLLATE MF_NAME NOT NULL,
                    directory INTEGER NOT NULL, epoch INTEGER NOT NULL DEFAULT 0, length INTEGER NOT NULL DEFAULT 0 CHECK(length>=0), created TEXT NOT NULL, modified TEXT NOT NULL,
                    accessed TEXT NOT NULL, changed TEXT NOT NULL, allocation INTEGER NOT NULL DEFAULT 0 CHECK(allocation>=0), attributes INTEGER NOT NULL DEFAULT 0, linked INTEGER NOT NULL DEFAULT 1, pending_delete INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(parent,name));
                CREATE TABLE chunks (file INTEGER NOT NULL, part INTEGER NOT NULL, bytes BLOB NOT NULL CHECK(length(bytes)<=65536), epoch INTEGER NOT NULL, PRIMARY KEY(file,part));
                CREATE TABLE erasures (file INTEGER NOT NULL, part INTEGER NOT NULL, epoch INTEGER NOT NULL, PRIMARY KEY(file,epoch));
                INSERT INTO entries(id,parent,name,directory,length,created,modified,accessed,changed) VALUES(1,NULL,'',1,0,$now,$now,$now,$now);
                INSERT INTO volume VALUES($id);
                PRAGMA user_version=2;
                COMMIT;
                """;
            schema.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            schema.Parameters.AddWithValue("$id", id);
            schema.ExecuteNonQuery();
            fault?.Invoke("create-committed");
            schema.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
            using SqliteDataReader check = schema.ExecuteReader();
            if (!check.Read() || check.GetInt32(0) != 0)
            {
                throw Error("IO_ERROR", "Creation checkpoint failed.");
            }
        }

        fault?.Invoke("create-publish");
        File.Move(staged, path, false);
        Directory.Delete(staging); // Only our now-empty staging directory.
        return id;
    }

    public static EncryptedVolume Mount(string path, string password, CancellationToken cancellationToken = default, Action<string>? fault = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (!File.Exists(path))
        {
            throw Error("NOT_FOUND", "Container does not exist.");
        }

        try
        {
            return new(path, password, cancellationToken, fault);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 26 or 11)
        {
            throw Error("UNLOCK_FAILED", "Container could not be unlocked or validated.");
        }
    }

    private void Configure()
    {
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA trusted_schema=OFF; PRAGMA cache_size=-256");
        if (!Equals(Scalar("PRAGMA journal_mode"), "wal") || !Equals(Scalar("PRAGMA synchronous"), 2L) || !Equals(Scalar("PRAGMA foreign_keys"), 1L) || !Equals(Scalar("PRAGMA temp_store"), 2L))
        {
            throw Error("IO_ERROR", "Required database settings were not accepted.");
        }
    }

    private SqliteCommand Command(string sql, params (string, object)[] parameters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SqliteCommand command = _database.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private object? Scalar(string sql, params (string, object)[] p)
    {
        using SqliteCommand c = Command(sql, p);
        return c.ExecuteScalar();
    }

    private void Execute(string sql, params (string, object)[] p)
    {
        using SqliteCommand c = Command(sql, p);
        c.ExecuteNonQuery();
    }

    public static KernelOperationException Error(string code, string message) => new(code, message);
    public long Resolve(string relative)
    {
        long id = 1;
        string[] components = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < components.Length; index++)
        {
            string name = components[index];
            if (!Stat(id).Directory)
            {
                throw Error("NOT_DIRECTORY", "Parent is not a directory.");
            }

            id = Scalar("SELECT id FROM entries WHERE parent=$p AND name=$n", ("$p", id), ("$n", VolumeNames.Component(name))) is long found ? found : throw Error(index < components.Length - 1 ? "PATH_NOT_FOUND" : "NOT_FOUND", "Entry does not exist.");
        }

        return id;
    }

    public FsEntry Stat(long id)
    {
        using SqliteCommand c = Command("SELECT id,name,directory,length,created,modified,accessed,changed,allocation,attributes FROM entries WHERE id=$id", ("$id", id));
        using SqliteDataReader r = c.ExecuteReader();
        if (!r.Read())
        {
            throw Error("INVALID_HANDLE", "Entry no longer exists.");
        }

        return Entry(r);
    }

    private FsEntry Entry(SqliteDataReader r) => new(FileNumbers.Format(r.GetInt64(0)), r.GetString(1), r.GetInt64(2) != 0, FileNumbers.Format(r.GetInt64(3)), r.GetString(4), r.GetString(5), Id + ":" + r.GetInt64(0), r.GetString(6), r.GetString(7), FileNumbers.Format(r.GetInt64(8)), FileNumbers.Format((long)Scalar("SELECT coalesce(sum(length(bytes)),0) FROM chunks c WHERE file=$f AND NOT EXISTS (SELECT 1 FROM erasures e WHERE e.file=c.file AND e.part<=c.part AND e.epoch>c.epoch)", ("$f", r.GetInt64(0)))!), r.GetInt32(9), Generation: Generation);
    public FsEntry[] List(long parent, long after, int limit)
    {
        if (!Stat(parent).Directory)
        {
            throw Error("NOT_DIRECTORY", "Entry is not a directory.");
        }

        using SqliteCommand c = Command("SELECT id,name,directory,length,created,modified,accessed,changed,allocation,attributes FROM entries WHERE parent=$p AND id>$a ORDER BY id LIMIT $l", ("$p", parent), ("$a", after), ("$l", limit));
        using SqliteDataReader r = c.ExecuteReader();
        var entries = new List<FsEntry>();
        while (r.Read())
        {
            entries.Add(Entry(r));
        }

        return entries.ToArray();
    }

    private (long Parent, string Name) Parent(string relative)
    {
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw Error("INVALID_PATH", "Cannot mutate a volume root.");
        }

        long parent;
        try { parent = Resolve(string.Join('/', parts[..^1])); }
        catch (KernelOperationException ex) when (ex.Code == "NOT_FOUND") { throw Error("PATH_NOT_FOUND", "Parent directory does not exist."); }
        if (!Stat(parent).Directory)
        {
            throw Error("NOT_DIRECTORY", "Parent is not a directory.");
        }

        RejectPending(parent);
        return (parent, VolumeNames.Component(parts[^1]));
    }

    private T Transaction<T>(Func<T> action, Func<bool> valid)
    {
        if (_transactionDepth != 0)
        {
            return action();
        }
        using SqliteTransaction tx = _database.BeginTransaction();
        _transactionDepth++;
        try
        {
        T result = action();
        Fault?.Invoke("before-commit");
        if (!valid())
        {
            throw Error("ACCESS_DENIED", "Authorization ended before commit.");
        }

        tx.Commit();
        Fault?.Invoke("after-commit");
        return result;
        }
        finally { _transactionDepth--; }
    }

    public long CreateEntry(string path, bool directory, Func<bool> valid) => Transaction(() =>
    {
        (long parent, string name) = Parent(path);
        if (Scalar("SELECT id FROM entries WHERE parent=$p AND name=$n", ("$p", parent), ("$n", name)) is not null)
        {
            throw Error("ALREADY_EXISTS", "Entry already exists.");
        }

        string now = DateTimeOffset.UtcNow.ToString("O");
        Execute("INSERT INTO entries(parent,name,directory,created,modified,accessed,changed) VALUES($p,$n,$d,$t,$t,$t,$t)", ("$p", parent), ("$n", name), ("$d", directory ? 1 : 0), ("$t", now));
        return (long)Scalar("SELECT last_insert_rowid()")!;
    }, valid);
    private byte[]? Chunk(long id, long part) => Scalar("SELECT bytes FROM chunks c WHERE file=$f AND part=$p AND NOT EXISTS (SELECT 1 FROM erasures e WHERE e.file=c.file AND e.part<=c.part AND e.epoch>c.epoch)", ("$f", id), ("$p", part)) as byte[];
    public byte[] Read(long id, long offset, int count)
    {
        if (offset < 0 || count is < 0 or > ChunkSize)
        {
            throw Error("INVALID_ARGUMENT", "Invalid read range.");
        }

        FsEntry entry = Stat(id);
        if (entry.Directory)
        {
            throw Error("IS_DIRECTORY", "Cannot read a directory.");
        }

        long length = FileNumbers.Parse(entry.Length);
        if (offset >= length)
        {
            return [];
        }

        byte[] output = new byte[(int)Math.Min(count, length - offset)];
        for (int used = 0; used < output.Length;)
        {
            long position = offset + used;
            int within = (int)(position % ChunkSize), take = Math.Min(ChunkSize - within, output.Length - used);
            if (Chunk(id, position / ChunkSize) is byte[] bytes && within < bytes.Length)
            {
                bytes.AsSpan(within, Math.Min(take, bytes.Length - within)).CopyTo(output.AsSpan(used));
            }

            used += take;
        }

        return output;
    }

    public void Write(long id, long offset, byte[] data, Func<bool> valid)
    {
        if (data.Length > ChunkSize || offset < 0 || offset > long.MaxValue - data.Length)
        {
            throw Error("INVALID_ARGUMENT", "Invalid write range.");
        }

        Transaction(() =>
        {
            Writable(id);
            FsEntry entry = Stat(id);
            if (entry.Directory)
            {
                throw Error("IS_DIRECTORY", "Cannot write a directory.");
            }

            long epoch = (long)Scalar("UPDATE entries SET epoch=epoch+1 WHERE id=$f RETURNING epoch", ("$f", id))!;
            for (int used = 0; used < data.Length;)
            {
                long position = offset + used, part = position / ChunkSize;
                int within = (int)(position % ChunkSize), take = Math.Min(ChunkSize - within, data.Length - used);
                byte[] chunk = new byte[ChunkSize];
                if (Chunk(id, part) is byte[] old)
                {
                    old.CopyTo(chunk, 0);
                }

                data.AsSpan(used, take).CopyTo(chunk.AsSpan(within));
                Execute("INSERT OR REPLACE INTO chunks VALUES($f,$p,$b,$e)", ("$f", id), ("$p", part), ("$b", chunk), ("$e", epoch));
                used += take;
            }

            if (data.Length > 0)
            {
                Execute("UPDATE entries SET length=max(length,$l),allocation=max(allocation,$l),modified=$t,changed=$t WHERE id=$f", ("$l", offset + data.Length), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$f", id));
            }

            return 0;
        }, valid);
    }

    public void Truncate(long id, long length, Func<bool> valid) => Transaction(() =>
    {
        if (length < 0)
        {
            throw Error("INVALID_ARGUMENT", "Invalid file length.");
        }

        if (Stat(id).Directory)
        {
            throw Error("IS_DIRECTORY", "Cannot truncate a directory.");
        }

        Writable(id);
        long oldLength = FileNumbers.Parse(Stat(id).Length);
        long epoch = (long)Scalar("UPDATE entries SET epoch=epoch+1 WHERE id=$f RETURNING epoch", ("$f", id))!;
        if (length < oldLength)
        {
            Execute("INSERT INTO erasures VALUES($f,$p,$e)", ("$f", id), ("$p", length / ChunkSize + (length % ChunkSize == 0 ? 0 : 1)), ("$e", epoch));
        }

        if (length % ChunkSize != 0 && Chunk(id, length / ChunkSize) is byte[] chunk)
        {
            Array.Clear(chunk, (int)(length % ChunkSize), chunk.Length - (int)(length % ChunkSize));
            Execute("UPDATE chunks SET bytes=$b,epoch=$e WHERE file=$f AND part=$p", ("$b", chunk), ("$f", id), ("$p", length / ChunkSize), ("$e", epoch));
        }

        Execute("UPDATE entries SET length=$l,allocation=max(allocation,$l),modified=$t,changed=$t WHERE id=$f", ("$l", length), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$f", id));
        return 0;
    }, valid);
    public void Delete(long id, Func<bool> valid) => Transaction(() =>
    {
        if (id == 1)
        {
            throw Error("INVALID_PATH", "Cannot delete volume root.");
        }

        RejectPending(id);
        if ((long)Scalar("SELECT count(*) FROM entries WHERE parent=$id", ("$id", id))! != 0)
        {
            throw Error("DIRECTORY_NOT_EMPTY", "Directory is not empty.");
        }

        Writable(id);
        Execute("UPDATE entries SET parent=NULL,linked=0,pending_delete=0 WHERE id=$id", ("$id", id));
        return 0;
    }, valid);
    public void Rename(long source, string target, bool replace, Action<long> checkTarget, Func<bool> valid, int maximumPathLength = 4096)
    {
        Transaction(() =>
    {
        if (source == 1)
        {
            throw Error("INVALID_PATH", "Cannot rename a volume root.");
        }

        if (!IsLinked(source)) throw Error("NOT_FOUND", "Entry has no name.");
        Writable(source);
        RejectPending(source);
        (long parent, string name) = Parent(target);
        long suffix = (long)Scalar("WITH RECURSIVE descendants(id,size) AS (SELECT $id,0 UNION ALL SELECT e.id,d.size+1+mf_utf16_length(e.name) FROM entries e JOIN descendants d ON e.parent=d.id) SELECT max(size) FROM descendants", ("$id", source))!;
        if (target.Length + suffix > maximumPathLength)
            throw Error("INVALID_PATH", "Rename would exceed the logical path limit.");
        for (long current = parent; current != 1; current = (long)Scalar("SELECT parent FROM entries WHERE id=$id", ("$id", current))!)
        {
            if (current == source)
            {
                throw Error("INVALID_PATH", "Cannot move a directory into itself.");
            }
        }

        if (parent == source)
        {
            throw Error("INVALID_PATH", "Cannot move a directory into itself.");
        }

        if (Scalar("SELECT id FROM entries WHERE parent=$p AND name=$n", ("$p", parent), ("$n", name)) is long existing && existing != source)
        {
            if (!replace)
            {
                throw Error("ALREADY_EXISTS", "Destination exists.");
            }

            checkTarget(existing);
            if (Stat(existing).Directory || Stat(source).Directory)
            {
                throw Error("NOT_SUPPORTED", "Replacement supports files only.");
            }

            Writable(existing);
            RejectPending(existing);
            Execute("UPDATE entries SET parent=NULL,linked=0,pending_delete=0 WHERE id=$id", ("$id", existing));
        }

        Execute("UPDATE entries SET parent=$p,name=$n,changed=$t WHERE id=$id", ("$p", parent), ("$n", name), ("$t", DateTimeOffset.UtcNow.ToString("O")), ("$id", source));
        Fault?.Invoke("replace-before-commit");
        return 0;
    }, valid);
        Fault?.Invoke("replace-committed");
    }
    public void Maintain()
    {
        using SqliteTransaction transaction = _database.BeginTransaction();
        ReclaimObjects();
        Execute("DELETE FROM chunks WHERE rowid IN (SELECT rowid FROM chunks c WHERE file NOT IN (SELECT id FROM entries) OR EXISTS (SELECT 1 FROM erasures e WHERE e.file=c.file AND e.part<=c.part AND e.epoch>c.epoch) LIMIT 128)");
        Execute("DELETE FROM erasures WHERE rowid IN (SELECT rowid FROM erasures e WHERE NOT EXISTS (SELECT 1 FROM chunks c WHERE c.file=e.file AND c.part>=e.part AND c.epoch<e.epoch) LIMIT 128)");
        transaction.Commit();
    }

    public void Flush()
    {
        Fault?.Invoke("flush");
        Execute("BEGIN IMMEDIATE; COMMIT;");
    }

    public void Checkpoint()
    {
        Fault?.Invoke("checkpoint");
        using SqliteCommand command = Command("PRAGMA wal_checkpoint(TRUNCATE)");
        using SqliteDataReader r = command.ExecuteReader();
        if (!r.Read() || r.GetInt32(0) != 0)
        {
            throw Error("VOLUME_BUSY", "Volume checkpoint could not complete.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _database.Dispose();
        _container.Dispose();
        _ownership.Dispose();
        _disposed = true;
    }
}
