using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Mainframe.Core;
using Mainframe.Protocol;
using Microsoft.Data.Sqlite;

namespace Mainframe.Tests;

public sealed class ExecutionMetadataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Mainframe-Metadata-" + Guid.NewGuid().ToString("N"));
    private readonly KernelStore store;
    public ExecutionMetadataTests()
    {
        KernelStore.Initialize(root, "metadata");
        store = KernelStore.Open(root);
    }

    private void Execute(string sql)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "kernel.db")};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private X509Certificate2 IssueOperator(DateTimeOffset expiry)
    {
        using X509Certificate2 ca = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(root, "ca.pfx"), null, X509KeyStorageFlags.EphemeralKeySet);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=local-admin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(CertificateTrust.ClientAuthentication) }, true));
        using X509Certificate2 certificate = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-4), expiry, RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }

    private void InstallOperator(X509Certificate2 certificate)
    {
        File.WriteAllBytes(Path.Combine(root, "operator.pfx"), certificate.Export(X509ContentType.Pfx));
        Execute($"DELETE FROM operator_identities; INSERT INTO operator_identities VALUES ('{CertificateTrust.Fingerprint(certificate)}','local-admin','{certificate.SerialNumber}','{certificate.NotAfter:O}',0);");
    }

    [Fact]
    public void RenewalPreservesIdentitiesAndRevokesOldCertificate()
    {
        using X509Certificate2 certificate = IssueOperator(DateTimeOffset.UtcNow.AddHours(12));
        InstallOperator(certificate);
        KernelIdentity identity = store.Identity;
        using X509Certificate2 oldCa = store.LoadCaCertificate();
        Assert.True(store.RenewCertificates());
        using X509Certificate2 next = store.LoadOperatorCertificate();
        Assert.NotEqual(CertificateTrust.Fingerprint(certificate), CertificateTrust.Fingerprint(next));
        Assert.False(store.IsOperatorAuthorized(certificate));
        Assert.True(store.IsOperatorAuthorized(next));
        using var reopened = KernelStore.Open(root);
        using X509Certificate2 ca = reopened.LoadCaCertificate();
        Assert.Equal(identity, reopened.Identity);
        Assert.Equal(oldCa.RawData, ca.RawData);
        Assert.False(store.RenewCertificates());
        Assert.False(File.Exists(Path.Combine(root, "renewal.json")));
    }

    [Fact]
    public void RenewalRefusesLiveHostAndRevokedIdentity()
    {
        using (store.AcquireHostOwnership())
            Assert.Throws<IOException>(() => store.RenewCertificates());
        using X509Certificate2 certificate = store.LoadOperatorCertificate();
        store.RevokeOperator(CertificateTrust.Fingerprint(certificate));
        Assert.Throws<UnauthorizedAccessException>(() => store.RenewCertificates());
    }

    [Fact]
    public void InterruptedPublicationIsRecoveredBeforeHostStarts()
    {
        using X509Certificate2 previous = store.LoadOperatorCertificate();
        using X509Certificate2 next = IssueOperator(DateTimeOffset.UtcNow.AddDays(7));
        string generation = "renew-" + Guid.NewGuid().ToString("N");
        string staging = Path.Combine(root, generation);
        Directory.CreateDirectory(staging);
        File.WriteAllBytes(Path.Combine(staging, "operator.pfx"), next.Export(X509ContentType.Pfx));
        File.WriteAllText(Path.Combine(root, "renewal.json"), JsonSerializer.Serialize(new { Generation = generation, Server = false, Client = true, OldFingerprint = CertificateTrust.Fingerprint(previous), Principal = "local-admin" }));
        File.Copy(Path.Combine(staging, "operator.pfx"), Path.Combine(root, "operator.pfx"), true);
        Assert.False(store.IsOperatorAuthorized(next));
        using (store.AcquireHostOwnership())
        {
        }

        Assert.True(store.IsOperatorAuthorized(next));
        Assert.False(store.IsOperatorAuthorized(previous));
        Assert.False(File.Exists(Path.Combine(root, "renewal.json")));
    }

    [Fact]
    public void MigrationPreservesIdentityAndRecoversExecutionsWithoutRetry()
    {
        KernelIdentity identity = store.Identity;
        Execute("DROP TABLE executions; DROP TABLE programs; DROP TABLE program_revisions; DROP TABLE host_roots; DROP TABLE grants; PRAGMA user_version=1;");
        using var migrated = KernelStore.Open(root);
        Assert.Equal(identity, migrated.Identity);
        Assert.True(migrated.HasGrant("local-admin", "kernel.describe"));
        var manifest = new ProgramManifest(1, "test/program", "1.0.0", Environment.ProcessPath!, [], root, ["windows"], ["x64"], ["pipes"], [], [], []);
        ProgramRegistration registration = migrated.RegisterProgram(manifest);
        migrated.RecordExecution("job", "local-admin", registration.Revision, []);
        migrated.RecoverExecutions();
        using var db = new SqliteConnection($"Data Source={Path.Combine(root, "kernel.db")};Pooling=False");
        db.Open();
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = "SELECT state FROM executions WHERE id='job'";
        Assert.Equal("outcome_unknown", command.ExecuteScalar());
    }

    [Fact]
    public void RootsRejectTraversalAliasesAndLinks()
    {
        store.AddHostRoot(new("workspace", root));
        Assert.Equal(root, store.ResolveDirectory("/", "/host/workspace", "local-admin").Host);
        Assert.Null(store.ResolveDirectory("/host/workspace", "..", "local-admin").Host);
        Assert.Throws<ArgumentException>(() => store.ResolveDirectory("/", "/host/workspace/a:stream", "local-admin"));
        Assert.Throws<KernelOperationException>(() => store.ResolveDirectory("/", "/host/workspace/../../Windows", "local-admin"));
        Assert.Throws<KernelOperationException>(() => store.ResolveDirectory("/", "/host/workspace", "ungranted"));
    }

    public void Dispose()
    {
        store.Dispose();
        var full = Path.GetFullPath(root);
        if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("Mainframe-Metadata-"))
            throw new InvalidOperationException();
        Directory.Delete(full, true);
    }
}
