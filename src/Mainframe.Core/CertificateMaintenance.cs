using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Mainframe.Core;

public sealed partial class KernelStore
{
    public FileStream AcquireHostOwnership()
    {
        string path = Path.Combine(directory, "host.lock");
        if (File.Exists(path))
            PrivateStateDirectory.ValidateFile(directory, "host.lock");
        var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            RecoverRenewal();
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public bool RenewCertificates()
    {
        using FileStream ownership = AcquireHostOwnership();
        using X509Certificate2 server = LoadServerCertificate();
        using X509Certificate2 client = LoadOperatorCertificate();
        if (!IsOperatorAuthorized(client))
            throw new UnauthorizedAccessException("Revoked identities must reenroll.");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool renewServer = server.NotAfter.ToUniversalTime() - now.UtcDateTime <= TimeSpan.FromHours(24);
        bool renewClient = client.NotAfter.ToUniversalTime() - now.UtcDateTime <= TimeSpan.FromHours(24);
        if (!renewServer && !renewClient)
            return false;
        using X509Certificate2 ca = X509CertificateLoader.LoadPkcs12FromFile(PrivateStateDirectory.ValidateFile(directory, "ca.pfx"), null, X509KeyStorageFlags.EphemeralKeySet);
        string generation = "renew-" + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(directory, generation);
        PrivateStateDirectory.Create(staging);
        using X509Certificate2? nextServer = renewServer ? IssueLeaf(ca, server.Subject, CertificateTrust.ServerAuthentication, now, true) : null;
        using X509Certificate2? nextClient = renewClient ? IssueLeaf(ca, client.Subject, CertificateTrust.ClientAuthentication, now, false) : null;
        if (nextServer is not null)
            WritePrivateCertificate(staging, "server.pfx", nextServer);
        if (nextClient is not null)
            WritePrivateCertificate(staging, "operator.pfx", nextClient);
        var journal = new RenewalJournal(generation, renewServer, renewClient, CertificateTrust.Fingerprint(client), GetOperatorIdentity(client)!);
        string pendingJournal = "journal-" + Guid.NewGuid().ToString("N") + ".json";
        PrivateStateDirectory.Write(directory, pendingJournal, JsonSerializer.SerializeToUtf8Bytes(journal, MaintenanceJson.Default.RenewalJournal));
        File.Move(Path.Combine(directory, pendingJournal), Path.Combine(directory, "renewal.json"));
        RecoverRenewal();
        return true;
    }

    private void RecoverRenewal()
    {
        if (!File.Exists(Path.Combine(directory, "renewal.json")))
            return;
        string journalPath = PrivateStateDirectory.ValidateFile(directory, "renewal.json");
        RenewalJournal journal = JsonSerializer.Deserialize(File.ReadAllBytes(journalPath), MaintenanceJson.Default.RenewalJournal) ?? throw new InvalidDataException("Invalid renewal journal.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(journal.Generation, "^renew-[0-9a-f]{32}$"))
            throw new InvalidDataException("Invalid renewal generation.");
        string staging = Path.Combine(directory, journal.Generation);
        PrivateStateDirectory.Validate(staging);
        using X509Certificate2 ca = LoadCaCertificate();
        using SqliteConnection database = Connect(directory);
        using SqliteTransaction transaction = database.BeginTransaction();
        using SqliteCommand command = database.CreateCommand();
        command.Transaction = transaction;
        if (journal.Client)
        {
            using X509Certificate2 next = X509CertificateLoader.LoadPkcs12FromFile(PrivateStateDirectory.ValidateFile(staging, "operator.pfx"), null, X509KeyStorageFlags.EphemeralKeySet);
            if (!CertificateTrust.Validate(next, ca, CertificateTrust.ClientAuthentication))
                throw new InvalidDataException("Invalid staged operator certificate.");
            command.CommandText = "SELECT disabled FROM operator_identities WHERE thumbprint=$old";
            command.Parameters.AddWithValue("$old", journal.OldFingerprint);
            object? disabled = command.ExecuteScalar();
            // The journal may be replayed after the database commit but before journal removal.
            command.CommandText = "SELECT COUNT(*) FROM operator_identities WHERE thumbprint=$new AND disabled=0";
            command.Parameters.AddWithValue("$new", CertificateTrust.Fingerprint(next));
            bool installed = (long)command.ExecuteScalar()! == 1;
            if (!installed && (disabled is null || Convert.ToInt64(disabled) != 0))
                throw new UnauthorizedAccessException("The renewal identity is revoked.");
            command.CommandText = """
                INSERT OR IGNORE INTO operator_identities VALUES ($new,$principal,$serial,$expiry,0);
                UPDATE operator_identities SET disabled=1 WHERE thumbprint=$old;
                """;
            command.Parameters.AddWithValue("$principal", journal.Principal);
            command.Parameters.AddWithValue("$serial", next.SerialNumber);
            command.Parameters.AddWithValue("$expiry", next.NotAfter.ToUniversalTime().ToString("O"));
            command.ExecuteNonQuery();
        }

        foreach (string name in new[]
        {
            "server.pfx",
            "operator.pfx"
        }

        )
        {
            if (!(name == "server.pfx" ? journal.Server : journal.Client))
                continue;
            string source = PrivateStateDirectory.ValidateFile(staging, name);
            using X509Certificate2 cert = X509CertificateLoader.LoadPkcs12FromFile(source, null, X509KeyStorageFlags.EphemeralKeySet);
            if (!cert.HasPrivateKey || !CertificateTrust.Validate(cert, ca, name == "server.pfx" ? CertificateTrust.ServerAuthentication : CertificateTrust.ClientAuthentication))
                throw new InvalidDataException("Invalid staged certificate.");
            string temporary = "publish-" + Guid.NewGuid().ToString("N") + ".pfx";
            PrivateStateDirectory.Write(directory, temporary, File.ReadAllBytes(source));
            File.Move(Path.Combine(directory, temporary), PrivateStateDirectory.ValidateFile(directory, name), true);
        }

        transaction.Commit();
        File.Delete(journalPath);
        // Remove only known files in this validated, private generation; never recurse.
        foreach (string name in new[]
        {
            "server.pfx",
            "operator.pfx"
        }

        )
            if (File.Exists(Path.Combine(staging, name)))
                File.Delete(PrivateStateDirectory.ValidateFile(staging, name));
        Directory.Delete(staging);
    }
}

internal sealed record RenewalJournal(string Generation, bool Server, bool Client, string OldFingerprint, string Principal);
[JsonSerializable(typeof(RenewalJournal))]
internal partial class MaintenanceJson : JsonSerializerContext;
