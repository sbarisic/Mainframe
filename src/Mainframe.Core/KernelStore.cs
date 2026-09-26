using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;

namespace Mainframe.Core;
/// <summary>Persistent identity for the first, local-only kernel. Network enrollment is a later milestone.</summary>
public sealed partial class KernelStore : IDisposable
{
    public const int SchemaVersion = 2;
    public const string InitialOperatorIdentity = "local-admin";
    private readonly string directory;
    private bool disposed;
    private KernelStore(string directory, KernelIdentity identity)
    {
        this.directory = directory;
        Identity = identity;
    }

    public KernelIdentity Identity
    {
        get;
    }

    public static KernelIdentity Initialize(string directory, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 128 || name.Any(char.IsControl))
            throw new ArgumentException("The mainframe name must be at most 128 characters and contain no control characters.", nameof(name));
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (Directory.Exists(directory) || File.Exists(directory))
            throw new IOException("The state directory already exists. Initialization never overwrites existing data.");
        var parent = Path.GetDirectoryName(directory) ?? throw new IOException("The state directory cannot be a filesystem root.");
        PrivateStateDirectory.RejectLinks(parent);
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, $".mainframe-init-{Guid.NewGuid():N}");
        PrivateStateDirectory.Create(staging);
        // Publish a complete state directory atomically. If anything fails, keep the protected
        // staging directory for diagnosis; never delete or overwrite an existing installation.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var identity = new KernelIdentity(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), name.Trim(), now);
        using var caKey = RSA.Create(3072);
        var caRequest = new CertificateRequest($"CN=Mainframe {identity.MainframeId} Root", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using X509Certificate2 ca = caRequest.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        using X509Certificate2 server = IssueLeaf(ca, $"CN={identity.KernelId}", CertificateTrust.ServerAuthentication, now, server: true);
        using X509Certificate2 client = IssueLeaf(ca, $"CN={InitialOperatorIdentity}", CertificateTrust.ClientAuthentication, now, server: false);
        WritePrivateCertificate(staging, "ca.pfx", ca);
        PrivateStateDirectory.Write(staging, "ca.cer", ca.Export(X509ContentType.Cert));
        WritePrivateCertificate(staging, "server.pfx", server);
        WritePrivateCertificate(staging, "operator.pfx", client);
        PrivateStateDirectory.Write(staging, "kernel.db", []);
        using (SqliteConnection database = Connect(staging, initializing: true))
        {
            using SqliteTransaction transaction = database.BeginTransaction();
            using SqliteCommand schema = database.CreateCommand();
            schema.Transaction = transaction;
            schema.CommandText = """
                CREATE TABLE kernel_identity (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    mainframe_id TEXT NOT NULL,
                    kernel_id TEXT NOT NULL,
                    name TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                CREATE TABLE operator_identities (
                    thumbprint TEXT PRIMARY KEY,
                    principal_id TEXT NOT NULL,
                    certificate_serial TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    disabled INTEGER NOT NULL DEFAULT 0 CHECK (disabled IN (0,1))
                );
                PRAGMA user_version = 1;
                """;
            schema.ExecuteNonQuery();
            using SqliteCommand record = database.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO kernel_identity VALUES (1, $mainframe, $kernel, $name, $created);
                INSERT INTO operator_identities (thumbprint, principal_id, certificate_serial, expires_at)
                VALUES ($thumbprint, $principal, $serial, $expires);
                """;
            record.Parameters.AddWithValue("$mainframe", identity.MainframeId);
            record.Parameters.AddWithValue("$kernel", identity.KernelId);
            record.Parameters.AddWithValue("$name", identity.Name);
            record.Parameters.AddWithValue("$created", identity.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
            record.Parameters.AddWithValue("$thumbprint", CertificateTrust.Fingerprint(client));
            record.Parameters.AddWithValue("$principal", InitialOperatorIdentity);
            record.Parameters.AddWithValue("$serial", client.SerialNumber);
            record.Parameters.AddWithValue("$expires", new DateTimeOffset(client.NotAfter.ToUniversalTime()).ToString("O", CultureInfo.InvariantCulture));
            record.ExecuteNonQuery();
            transaction.Commit();
        }

        PrivateStateDirectory.Validate(staging);
        Directory.Move(staging, directory);
        return identity;
    }

    public static KernelStore Open(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        PrivateStateDirectory.Validate(directory);
        foreach (var name in new[]
        {
            "kernel.db",
            "ca.cer",
            "ca.pfx",
            "server.pfx",
            "operator.pfx"
        }

        )
            PrivateStateDirectory.ValidateFile(directory, name);
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "SELECT mainframe_id, kernel_id, name, created_at FROM kernel_identity WHERE singleton = 1;";
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException("Kernel metadata has no identity.");
        return new KernelStore(directory, new KernelIdentity(reader.GetString(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)));
    }

    public X509Certificate2 LoadServerCertificate() => LoadLeaf("server.pfx", CertificateTrust.ServerAuthentication);
    public X509Certificate2 LoadOperatorCertificate() => LoadLeaf("operator.pfx", CertificateTrust.ClientAuthentication);
    public X509Certificate2 LoadCaCertificate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return X509CertificateLoader.LoadCertificateFromFile(PrivateStateDirectory.ValidateFile(directory, "ca.cer"));
    }

    public bool IsOperatorAuthorized(X509Certificate2 certificate) => GetOperatorIdentity(certificate) is not null;
    public string? GetOperatorIdentity(X509Certificate2 certificate)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using X509Certificate2 ca = LoadCaCertificate();
        if (!CertificateTrust.Validate(certificate, ca, CertificateTrust.ClientAuthentication))
            return null;
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "SELECT principal_id FROM operator_identities WHERE thumbprint = $thumbprint AND certificate_serial = $serial AND disabled = 0;";
        command.Parameters.AddWithValue("$thumbprint", CertificateTrust.Fingerprint(certificate));
        command.Parameters.AddWithValue("$serial", certificate.SerialNumber);
        return command.ExecuteScalar() as string;
    }

    public void RevokeOperator(string thumbprint)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        using SqliteConnection database = Connect(directory);
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = "UPDATE operator_identities SET disabled = 1 WHERE thumbprint = $thumbprint;";
        command.Parameters.AddWithValue("$thumbprint", thumbprint.ToUpperInvariant());
        if (command.ExecuteNonQuery() != 1)
            throw new KeyNotFoundException("The operator certificate is not registered.");
    }

    /// <summary>Creates a consistent private metadata backup. Certificate private keys are not included.</summary>
    public void BackupDatabase(string destination)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        destination = Path.GetFullPath(destination);
        var parent = Path.GetDirectoryName(destination)!;
        PrivateStateDirectory.Validate(parent);
        PrivateStateDirectory.Write(parent, Path.GetFileName(destination), []);
        using SqliteConnection source = Connect(directory);
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
    }

    public void Dispose() => disposed = true;
    private X509Certificate2 LoadLeaf(string fileName, string purpose)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        // Windows Schannel cannot use an ephemeral private key for TLS. UserKeySet
        // creates a temporary user key container that is removed when this certificate
        // is disposed; deliberately do not set PersistKeySet. Other platforms can keep
        // the key entirely ephemeral.
        X509KeyStorageFlags keyStorage = OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;
        X509Certificate2 certificate = X509CertificateLoader.LoadPkcs12FromFile(PrivateStateDirectory.ValidateFile(directory, fileName), password: null, keyStorage);
        try
        {
            using X509Certificate2 ca = LoadCaCertificate();
            if (!certificate.HasPrivateKey || !CertificateTrust.Validate(certificate, ca, purpose))
                throw new InvalidDataException($"The {fileName} certificate is expired, invalid, or missing its private key. Expired identities require reenrollment; local renewal only accepts unexpired certificates in their final 24 hours.");
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static SqliteConnection Connect(string directory, bool initializing = false)
    {
        PrivateStateDirectory.Validate(directory);
        var path = PrivateStateDirectory.ValidateFile(directory, "kernel.db");
        foreach (var suffix in new[]
        {
            "-wal",
            "-shm",
            "-journal"
        }

        )
        {
            // SQLite may remove a sidecar when the last of our non-pooled connections
            // closes. Its disappearance during validation is not state corruption.
            try
            {
                if (File.Exists(path + suffix))
                    PrivateStateDirectory.ValidateFile(directory, "kernel.db" + suffix);
            }
            catch (FileNotFoundException)
            {
            }
        }

        var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5 }.ToString());
        try
        {
            database.Open();
            // Refuse incompatible schemas before changing journal mode or performing any writes.
            using SqliteCommand version = database.CreateCommand();
            version.CommandText = "PRAGMA user_version;";
            var actualVersion = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);
            if (initializing ? actualVersion != 0 : actualVersion is < 1 or > SchemaVersion)
                throw new InvalidDataException($"Unsupported kernel metadata schema {actualVersion}; this build requires schema {SchemaVersion}.");
            using SqliteCommand settings = database.CreateCommand();
            settings.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            settings.ExecuteNonQuery();
            if (!initializing && actualVersion < SchemaVersion)
                Migrate(database);
            return database;
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }

    private static X509Certificate2 IssueLeaf(X509Certificate2 ca, string subject, string purpose, DateTimeOffset now, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(purpose) }, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (server)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            san.AddIpAddress(IPAddress.IPv6Loopback);
            request.CertificateExtensions.Add(san.Build());
        }

        using X509Certificate2 issued = request.Create(ca, now.AddMinutes(-5), now.AddDays(7), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    private static void WritePrivateCertificate(string directory, string name, X509Certificate2 certificate)
    {
        var bytes = certificate.Export(X509ContentType.Pfx);
        try
        {
            PrivateStateDirectory.Write(directory, name, bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
