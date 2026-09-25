using System.Globalization;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Mainframe.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Mainframe.Tests;

public sealed class CoreTests
{
    [Fact]
    public void InitializationPersistsIdentityAndCreatesPurposeBoundCertificates()
    {
        using var state = new TestState();
        var identity = KernelStore.Initialize(state.Directory + Path.DirectorySeparatorChar, "test-mainframe");
        using var first = KernelStore.Open(state.Directory);
        using var second = KernelStore.Open(state.Directory);
        Assert.Equal(identity, first.Identity);
        Assert.Equal(identity, second.Identity);
        Assert.True(Guid.TryParse(identity.MainframeId, out _));
        Assert.True(Guid.TryParse(identity.KernelId, out _));
        using var ca = first.LoadCaCertificate();
        using var server = first.LoadServerCertificate();
        using var client = first.LoadOperatorCertificate();
        Assert.False(ca.HasPrivateKey);
        Assert.True(server.HasPrivateKey);
        Assert.True(client.HasPrivateKey);
        Assert.True(CertificateTrust.Validate(server, ca, CertificateTrust.ServerAuthentication));
        Assert.True(CertificateTrust.Validate(client, ca, CertificateTrust.ClientAuthentication));
        Assert.False(CertificateTrust.Validate(server, ca, CertificateTrust.ClientAuthentication));
        Assert.False(CertificateTrust.Validate(client, ca, CertificateTrust.ServerAuthentication));
        Assert.Equal(KernelStore.InitialOperatorIdentity, first.GetOperatorIdentity(client));
        Assert.InRange(client.NotAfter.ToUniversalTime() - DateTime.UtcNow, TimeSpan.FromDays(6.99), TimeSpan.FromDays(7.01));
    }

    [Fact]
    public void InitializationNeverOverwritesExistingDirectories()
    {
        using var state = new TestState();
        Directory.CreateDirectory(state.Directory);
        var marker = Path.Combine(state.Directory, "preserve.txt");
        File.WriteAllText(marker, "existing work");
        Assert.Throws<IOException>(() => KernelStore.Initialize(state.Directory, "test"));
        Assert.Equal("existing work", File.ReadAllText(marker));
        Assert.Single(Directory.GetFiles(state.Directory));
    }

    [Fact]
    public void NewerMetadataSchemaIsRejected()
    {
        using var state = new TestState();
        KernelStore.Initialize(state.Directory, "test");
        using (var database = OpenDatabase(Path.Combine(state.Directory, "kernel.db")))
        {
            using var command = database.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }
        var error = Assert.Throws<InvalidDataException>(() => KernelStore.Open(state.Directory));
        Assert.Contains("schema 99", error.Message);
    }

    [Fact]
    public void RevocationPersistsAndDoesNotInvalidateTheCertificateChain()
    {
        using var state = new TestState();
        KernelStore.Initialize(state.Directory, "test");
        using var store = KernelStore.Open(state.Directory);
        using var client = store.LoadOperatorCertificate();
        Assert.True(store.IsOperatorAuthorized(client));
        store.RevokeOperator(CertificateTrust.Fingerprint(client));
        Assert.False(store.IsOperatorAuthorized(client));
        using var reopened = KernelStore.Open(state.Directory);
        Assert.Null(reopened.GetOperatorIdentity(client));
        using var ca = reopened.LoadCaCertificate();
        Assert.True(CertificateTrust.Validate(client, ca, CertificateTrust.ClientAuthentication));
    }

    [Fact]
    public void DifferentClusterCertificateIsRejected()
    {
        using var state = new TestState();
        using var other = new TestState();
        KernelStore.Initialize(state.Directory, "first");
        KernelStore.Initialize(other.Directory, "second");
        using var store = KernelStore.Open(state.Directory);
        using var otherStore = KernelStore.Open(other.Directory);
        using var otherClient = otherStore.LoadOperatorCertificate();
        Assert.False(store.IsOperatorAuthorized(otherClient));
    }

    [Fact]
    public void ExpiredCertificateIsRejected()
    {
        using var state = new TestState();
        KernelStore.Initialize(state.Directory, "test");
        using var ca = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(state.Directory, "ca.pfx"), null, X509KeyStorageFlags.EphemeralKeySet);
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=expired", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(CertificateTrust.ClientAuthentication) }, true));
        using var expired = request.Create(ca, DateTimeOffset.UtcNow.AddMinutes(-3), DateTimeOffset.UtcNow.AddMinutes(-1), RandomNumberGenerator.GetBytes(16));
        Assert.False(CertificateTrust.Validate(expired, ca, CertificateTrust.ClientAuthentication));
    }

    [Fact]
    public void BackupContainsCommittedMetadataAndCannotOverwriteAFile()
    {
        using var state = new TestState();
        var identity = KernelStore.Initialize(state.Directory, "test");
        using var store = KernelStore.Open(state.Directory);
        var destination = Path.Combine(state.Directory, "backup.db");
        store.BackupDatabase(destination);
        Assert.Throws<IOException>(() => store.BackupDatabase(destination));
        using var database = OpenDatabase(destination);
        using var command = database.CreateCommand();
        command.CommandText = "SELECT kernel_id FROM kernel_identity;";
        Assert.Equal(identity.KernelId, command.ExecuteScalar());
        command.CommandText = "PRAGMA user_version;";
        Assert.Equal(KernelStore.SchemaVersion, Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        using var live = OpenDatabase(Path.Combine(state.Directory, "kernel.db"));
        using var mode = live.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", mode.ExecuteScalar());
    }

    [Fact]
    public async Task ConcurrentAuthorizationReadsAreSupported()
    {
        using var state = new TestState();
        KernelStore.Initialize(state.Directory, "test");
        using var store = KernelStore.Open(state.Directory);
        using var client = store.LoadOperatorCertificate();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.IsOperatorAuthorized(client))));
        Assert.All(results, result => Assert.True(result));
    }

    [Fact]
    public void StateWithBroadWindowsPermissionsIsRejected()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var state = new TestState();
        KernelStore.Initialize(state.Directory, "test");
        var info = new DirectoryInfo(state.Directory);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(acl);
        Assert.Throws<UnauthorizedAccessException>(() => KernelStore.Open(state.Directory));
    }

    private static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class TestState : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Mainframe-CoreTests-" + Guid.NewGuid().ToString("N"));

        public TestState() => System.IO.Directory.CreateDirectory(root);

        public string Directory => Path.Combine(root, "state");

        public void Dispose()
        {
            var resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(resolved).StartsWith("Mainframe-CoreTests-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the generated test directory.");
            if (System.IO.Directory.Exists(resolved))
                System.IO.Directory.Delete(resolved, recursive: true);
        }
    }
}
