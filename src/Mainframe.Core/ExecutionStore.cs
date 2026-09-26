using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Mainframe.Protocol;
using Microsoft.Data.Sqlite;

namespace Mainframe.Core;

public sealed class KernelOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed partial class KernelStore
{
    private static void Migrate(SqliteConnection database)
    {
        using SqliteTransaction transaction = database.BeginTransaction();
        using SqliteCommand command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS programs (qualified TEXT PRIMARY KEY, revision TEXT NOT NULL, manifest BLOB NOT NULL, removed INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS program_revisions (revision TEXT PRIMARY KEY, qualified TEXT NOT NULL, manifest BLOB NOT NULL);
            CREATE TABLE IF NOT EXISTS host_roots (name TEXT PRIMARY KEY, path TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS grants (principal TEXT NOT NULL, permission TEXT NOT NULL, PRIMARY KEY(principal,permission));
            INSERT OR IGNORE INTO grants VALUES ('local-admin','*');
            CREATE TABLE IF NOT EXISTS executions (id TEXT PRIMARY KEY, principal TEXT NOT NULL, revision TEXT NOT NULL REFERENCES program_revisions(revision),
                permissions BLOB NOT NULL, state TEXT NOT NULL, exit_code INTEGER, started TEXT NOT NULL, finished TEXT);
            CREATE TABLE IF NOT EXISTS volume_mounts (mount TEXT PRIMARY KEY COLLATE NOCASE, id TEXT NOT NULL UNIQUE, path TEXT NOT NULL);
            PRAGMA user_version = 3;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public bool HasGrant(string principal, string permission) => AuthorizationCount("SELECT COUNT(*) FROM grants WHERE principal=$p AND permission IN ('*',$g)", ("$p", principal), ("$g", permission)) > 0;

    public ProgramRegistration RegisterProgram(ProgramManifest manifest)
    {
        ValidateManifest(manifest);
        string qualified = $"{manifest.Identity}@{manifest.Version}#{Identity.KernelId}";
        string revision = Guid.NewGuid().ToString("N");
        byte[] bytes = ProtocolJson.Serialize(manifest);
        if (bytes.Length > 4096)
        {
            throw new ArgumentException("Local manifests are limited to 4 KiB.");
        }

        using SqliteConnection database = Connect(directory);
        using SqliteTransaction transaction = database.BeginTransaction();
        using SqliteCommand command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM programs WHERE removed=0 AND qualified<>$q";
        command.Parameters.AddWithValue("$q", qualified);
        if ((long)command.ExecuteScalar()! >= 128)
        {
            throw new KernelOperationException("RESOURCE_EXHAUSTED", "Local registration limit is 128.");
        }

        command.CommandText = """
            INSERT INTO program_revisions VALUES ($r,$q,$m);
            INSERT INTO programs VALUES ($q,$r,$m,0) ON CONFLICT(qualified) DO UPDATE SET revision=$r,manifest=$m,removed=0;
            """;
        command.Parameters.AddWithValue("$r", revision);
        command.Parameters.AddWithValue("$m", bytes);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new(qualified, revision, manifest, Incompatibility(manifest));
    }

    public ProgramRegistration[] ListPrograms()
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "SELECT qualified,revision,manifest FROM programs WHERE removed=0 ORDER BY qualified";
        using SqliteDataReader reader = command.ExecuteReader();
        var results = new List<ProgramRegistration>();
        while (reader.Read())
        {
            ProgramManifest manifest = ProtocolJson.Deserialize<ProgramManifest>((byte[])reader[2]);
            results.Add(new(reader.GetString(0), reader.GetString(1), manifest, Incompatibility(manifest)));
        }

        return results.ToArray();
    }

    public ProgramRegistration ResolveProgram(string selector, string principal, string mode)
    {
        ProgramRegistration[] matches = ListPrograms().Where(p => p.QualifiedName == selector || p.Manifest.Identity == selector || p.Manifest.Identity.Split('/')[1] == selector).ToArray();
        ProgramRegistration[] eligible = matches.Where(p => p.Incompatibility is null && p.Manifest.IoModes.Contains(mode) && HasGrant(principal, "program:" + p.Manifest.Identity)).ToArray();
        if (eligible.Length == 1)
        {
            return eligible[0];
        }

        if (eligible.Length > 1)
        {
            throw new KernelOperationException("AMBIGUOUS_PROGRAM", string.Join(", ", eligible.Select(p => p.QualifiedName)));
        }

        throw new KernelOperationException(matches.Length == 0 ? "NOT_FOUND" : "ACCESS_DENIED", "No eligible registration: " + string.Join(", ", matches.Select(p => p.QualifiedName)));
    }

    public void RemoveProgram(string qualified)
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "UPDATE programs SET removed=1 WHERE qualified=$q AND removed=0";
        command.Parameters.AddWithValue("$q", qualified);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new KernelOperationException("NOT_FOUND", "Use an existing qualified program name.");
        }
    }

    public HostRoot AddHostRoot(HostRoot root)
    {
        if (!Regex.IsMatch(root.Name, "^[a-z][a-z0-9_-]{0,63}$"))
        {
            throw new ArgumentException("Root names must be lowercase names, at most 64 characters.");
        }

        ValidateDirectory(root.Path);
        if (root.Path.Length > 4096)
        {
            throw new ArgumentException("Root path exceeds 4096 characters.");
        }

        root = root with
        {
            Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path))
        };
        using SqliteConnection database = Connect(directory);
        using SqliteTransaction transaction = database.BeginTransaction();
        using SqliteCommand command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM host_roots WHERE name<>$n";
        command.Parameters.AddWithValue("$n", root.Name);
        if ((long)command.ExecuteScalar()! >= 128)
        {
            throw new KernelOperationException("RESOURCE_EXHAUSTED", "Host root limit is 128.");
        }

        command.CommandText = "INSERT INTO host_roots VALUES ($n,$p) ON CONFLICT(name) DO UPDATE SET path=$p";
        command.Parameters.AddWithValue("$p", root.Path);
        command.ExecuteNonQuery();
        transaction.Commit();
        return root;
    }

    public HostRoot[] ListHostRoots()
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "SELECT name,path FROM host_roots ORDER BY name";
        using SqliteDataReader reader = command.ExecuteReader();
        var roots = new List<HostRoot>();
        while (reader.Read())
        {
            roots.Add(new(reader.GetString(0), reader.GetString(1)));
        }

        return roots.ToArray();
    }

    public void RemoveHostRoot(string name)
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "DELETE FROM host_roots WHERE name=$n";
        command.Parameters.AddWithValue("$n", name);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new KernelOperationException("NOT_FOUND", "Unknown host root.");
        }
    }

    public (string Logical, string? Host, string? Root) ResolveDirectory(string current, string requested, string principal)
    {
        if (requested.Contains('\\') || requested.Contains(':') || requested.Any(char.IsControl))
        {
            throw new ArgumentException("Use /host/name paths.");
        }

        var parts = new List<string>();
        foreach (var part in (requested.StartsWith('/') ? requested : current.TrimEnd('/') + "/" + requested).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }

                continue;
            }

            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.EndsWith('.') || part.EndsWith(' '))
            {
                throw new ArgumentException("Invalid directory component.");
            }

            parts.Add(part.Normalize(NormalizationForm.FormC));
        }

        string logical = "/" + string.Join('/', parts);
        if (parts.Count == 0 || logical == "/host")
        {
            return (logical, null, null);
        }

        if (parts[0] != "host" || parts.Count < 2)
        {
            throw new KernelOperationException("NOT_FOUND", "Use /host/<name>.");
        }

        HostRoot root = ListHostRoots().SingleOrDefault(r => r.Name == parts[1]) ?? throw new KernelOperationException("NOT_FOUND", "Host root is unavailable.");
        if (!HasGrant(principal, "host-root:" + root.Name))
        {
            throw new KernelOperationException("ACCESS_DENIED", "Host root is not granted.");
        }

        string host = Path.GetFullPath(Path.Combine(new[] { root.Path }.Concat(parts.Skip(2)).ToArray()));
        if (host != root.Path && !host.StartsWith(root.Path.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new KernelOperationException("ACCESS_DENIED", "Path escapes the approved root.");
        }

        ValidateDirectory(host);
        return (logical, host, root.Name);
    }

    public void RecordExecution(string id, string principal, string revision, string[] permissions)
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "INSERT INTO executions VALUES ($i,$p,$r,$g,'starting',NULL,$t,NULL)";
        command.Parameters.AddWithValue("$i", id);
        command.Parameters.AddWithValue("$p", principal);
        command.Parameters.AddWithValue("$r", revision);
        command.Parameters.AddWithValue("$g", ProtocolJson.Serialize(permissions));
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void FinishExecution(string id, int? exitCode, string state)
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "UPDATE executions SET state=$s,exit_code=$e,finished=$t WHERE id=$i";
        command.Parameters.AddWithValue("$s", state);
        command.Parameters.AddWithValue("$e", (object?)exitCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$i", id);
        command.ExecuteNonQuery();
    }

    public void MarkExecutionRunning(string id)
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "UPDATE executions SET state='running' WHERE id=$i AND state='starting'";
        command.Parameters.AddWithValue("$i", id);
        command.ExecuteNonQuery();
    }

    public void RecoverExecutions()
    {
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "UPDATE executions SET state='outcome_unknown',finished=$t WHERE state IN ('starting','running')";
        command.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public static void ValidateDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new ArgumentException("An existing absolute directory is required.");
        }

        PrivateStateDirectory.RejectLinks(path);
    }

    public static void ValidateManifest(ProgramManifest m)
    {
        if (m.ManifestVersion != 1 || !Regex.IsMatch(m.Identity, "^[a-z][a-z0-9_.-]*/[a-z][a-z0-9_.-]*$") || m.Identity.Length > 128 || m.Version.Length > 128 || !Regex.IsMatch(m.Version, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\\+[0-9A-Za-z.-]+)?$"))
        {
            throw new ArgumentException("Expected manifestVersion 1, namespace/name, and a semantic version.");
        }

        var versionParts = m.Version.Split('+');
        if (versionParts.Length == 2 && versionParts[1].Split('.').Any(s => s.Length == 0))
        {
            throw new ArgumentException("Empty version build identifier.");
        }

        int prerelease = versionParts[0].IndexOf('-');
        if (prerelease >= 0 && versionParts[0][(prerelease + 1)..].Split('.').Any(s => s.Length == 0 || s.Length > 1 && s[0] == '0' && s.All(char.IsDigit)))
        {
            throw new ArgumentException("Invalid semantic version prerelease identifier.");
        }

        foreach (var list in new[]
        {
            m.FixedArguments,
            m.OperatingSystems,
            m.Architectures,
            m.IoModes,
            m.EnvironmentAllowlist,
            m.Permissions,
            m.HostRoots
        }

        )
        {
            if (list is null || list.Length > 128 || list.Any(s => s is null || s.Length > 4096 || s.Contains('\0')))
            {
                throw new ArgumentException("Invalid manifest array.");
            }
        }

        if (m.IoModes.Length == 0 || m.IoModes.Any(s => s is not ("pipes" or "terminal")) || m.OperatingSystems.Length == 0 || m.Architectures.Length == 0)
        {
            throw new ArgumentException("Declare supported platforms and I/O modes.");
        }

        if (m.EnvironmentAllowlist.Any(s => !Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_]*$") || s.StartsWith("MF_", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Invalid environment name.");
        }

        if (m.Permissions.Any(s => s is not ("kernel.describe" or "kernel.health" or "kernel.capabilities") && !Regex.IsMatch(s, "^volume:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:(read|write)$")))
        {
            throw new ArgumentException("Unknown program RPC permission.");
        }

        if (m.HostRoots.Any(s => !Regex.IsMatch(s, "^[a-z][a-z0-9_-]{0,63}$")))
        {
            throw new ArgumentException("Invalid manifest host-root name.");
        }

        // Foreign-platform absolute paths are retained for visible incompatible registrations.
        if (m.OperatingSystems.Contains("windows"))
        {
            if (!Path.IsPathFullyQualified(m.Executable) || !Path.IsPathFullyQualified(m.WorkingDirectory) || (m.Interpreter is not null && !Path.IsPathFullyQualified(m.Interpreter)))
            {
                throw new ArgumentException("Executable, interpreter, and working directory must be absolute.");
            }
        }
        else if (!m.Executable.StartsWith('/') || !m.WorkingDirectory.StartsWith('/'))
        {
            throw new ArgumentException("Absolute paths required.");
        }
    }

    public static string? Incompatibility(ProgramManifest m)
    {
        if (!OperatingSystem.IsWindows() || !m.OperatingSystems.Contains("windows"))
        {
            return "This execution adapter requires Windows.";
        }

        if (!m.Architectures.Contains(RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()))
        {
            return "Architecture is unavailable.";
        }

        if (m.Runtime is not (null or "native" or "dotnet10"))
        {
            return "Runtime requirement is unsupported.";
        }

        if (!File.Exists(m.Interpreter ?? m.Executable) || !File.Exists(m.Executable))
        {
            return "Executable or interpreter is unavailable.";
        }

        if (!Directory.Exists(m.WorkingDirectory))
        {
            return "Working directory is unavailable.";
        }

        return null;
    }
}
