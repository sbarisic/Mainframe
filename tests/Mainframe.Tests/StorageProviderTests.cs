using Mainframe.Core;
using Mainframe.Protocol;
using Mainframe.Storage;

namespace Mainframe.Tests;

public sealed partial class StorageProviderTests : IDisposable
{
    private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Mainframe-Storage-" + Guid.NewGuid().ToString("N"));
    private const string Password = "test password for encrypted volumes";
    public StorageProviderTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    public static IEnumerable<object[]> UnrestrictedPasswords =>
    [
        new object[] { "a" },
        new object[] { "0123456789" },
        new object[] { " \t\r\n'\";=λ😀e\u0301 " },
        new object[] { "\0" },
        new object[] { "\0a\0b\0" },
        new object[] { new string('λ', 4096) }
    ];

    [Theory]
    [MemberData(nameof(UnrestrictedPasswords))]
    public void PasswordsHaveNoLengthOrCharacterPolicy(string password)
    {
        string path = System.IO.Path.Combine(_root, "unrestricted.mfv");
        string id = EncryptedVolume.Create(path, password);
        using (EncryptedVolume volume = EncryptedVolume.Mount(path, password))
        {
            Assert.Equal(id, volume.Id);
            long file = volume.CreateEntry("file", false, () => true);
            volume.Write(file, 0, [1, 2, 3], () => true);
            volume.Checkpoint();
        }
        Assert.Equal("UNLOCK_FAILED", Assert.Throws<KernelOperationException>(() => EncryptedVolume.Mount(path, password + "\0")).Code);
        using EncryptedVolume reopened = EncryptedVolume.Mount(path, password);
        Assert.Equal(new byte[] { 1, 2, 3 }, reopened.Read(reopened.Resolve("file"), 0, 3));
    }

    [Fact]
    public void PersistsSparseWritesTruncationAndAtomicReplacement()
    {
        string path = System.IO.Path.Combine(_root, "data.mfv");
        string uuid = EncryptedVolume.Create(path, Password);
        using (EncryptedVolume volume = EncryptedVolume.Mount(path, Password))
        {
            Assert.Equal(uuid, volume.Id);
            volume.CreateEntry("folder", true, () => true);
            long original = volume.CreateEntry("folder/Hello.txt", false, () => true);
            Assert.Throws<KernelOperationException>(() => volume.Rename(volume.Resolve("folder"), "longer", false, _ => { }, () => true, 10));
            Assert.Equal(original, volume.Resolve("FOLDER/hello.TXT"));
            Assert.Throws<KernelOperationException>(() => volume.CreateEntry("folder/HELLO.txt", false, () => true));
            volume.Write(original, 70000, [1, 2, 3], () => true);
            Assert.Equal(new byte[] { 0, 0, 1, 2, 3 }, volume.Read(original, 69998, 10));
            volume.Truncate(original, 70001, () => true);
            volume.Truncate(original, 70003, () => true);
            Assert.Equal(new byte[] { 1, 0, 0 }, volume.Read(original, 70000, 3));
            long temp = volume.CreateEntry("folder/new.tmp", false, () => true);
            volume.Write(temp, 0, [5, 6], () => true);
            volume.Rename(temp, "folder/Hello.txt", true, _ =>
            {
            }, () => true);
            Assert.Equal(new byte[] { 5, 6 }, volume.Read(volume.Resolve("folder/Hello.txt"), 0, 100));
            volume.Flush();
            volume.Checkpoint();
        }

        Assert.False(File.Exists(path + "-wal"));
        using EncryptedVolume reopened = EncryptedVolume.Mount(path, Password);
        Assert.Equal(uuid, reopened.Id);
        Assert.Equal(new byte[] { 5, 6 }, reopened.Read(reopened.Resolve("folder/hello.txt"), 0, 10));
    }

    [Fact]
    public void RollbackPreservesOldContentAndAuthorizationIsCheckedAtCommit()
    {
        string path = System.IO.Path.Combine(_root, "data.mfv");
        EncryptedVolume.Create(path, Password);
        using EncryptedVolume volume = EncryptedVolume.Mount(path, Password);
        long id = volume.CreateEntry("data", false, () => true);
        volume.Write(id, 0, [1, 2, 3], () => true);
        Assert.Throws<KernelOperationException>(() => volume.Write(id, 0, [9], () => false));
        volume.Fault = point =>
        {
            if (point == "before-commit")
            {
                throw new IOException("simulated flush failure");
            }
        };
        Assert.Throws<IOException>(() => volume.Write(id, 0, [8], () => true));
        volume.Fault = null;
        Assert.Equal(new byte[] { 1, 2, 3 }, volume.Read(id, 0, 3));
    }

    [Fact]
    public void RejectsCorruptHeadersUnsupportedSchemasAndDuplicateOwners()
    {
        string path = System.IO.Path.Combine(_root, "data.mfv");
        EncryptedVolume.Create(path, Password);
        using (EncryptedVolume mounted = EncryptedVolume.Mount(path, Password))
            Assert.Throws<IOException>(() => EncryptedVolume.Mount(path, Password));
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Password = Password, Pooling = false }.ToString()))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA user_version=999";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal("UNSUPPORTED_FORMAT", Assert.Throws<KernelOperationException>(() => EncryptedVolume.Mount(path, Password)).Code);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[300] ^= 0x55;
        File.WriteAllBytes(path, bytes);
        Assert.Equal("UNLOCK_FAILED", Assert.Throws<KernelOperationException>(() => EncryptedVolume.Mount(path, Password)).Code);
    }

    [Fact]
    public void FailedCreateNeverPublishesAndTruncationReclaimsInBoundedBatches()
    {
        string path = System.IO.Path.Combine(_root, "data.mfv");
        Assert.Throws<IOException>(() => EncryptedVolume.Create(path, Password, point =>
        {
            if (point == "create-publish")
            {
                throw new IOException("failure");
            }
        }));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetDirectories(_root, ".mfv-create-*"));
        EncryptedVolume.Create(path, Password);
        using EncryptedVolume volume = EncryptedVolume.Mount(path, Password);
        long id = volume.CreateEntry("file", false, () => true);
        for (int i = 0; i < 130; i++)
        {
            volume.Write(id, i * 65536L, [42], () => true);
        }

        volume.Truncate(id, 0, () => true);
        volume.Truncate(id, 130 * 65536L, () => true);
        Assert.Equal(new byte[] { 0 }, volume.Read(id, 129 * 65536L, 1));
        volume.Maintain();
        volume.Maintain();
        Assert.Equal(new byte[] { 0 }, volume.Read(id, 0, 1));
    }

    [Fact]
    public void DiskFullAndFlushFailuresAreNotAcknowledged()
    {
        string path = System.IO.Path.Combine(_root, "data.mfv");
        EncryptedVolume.Create(path, Password);
        using EncryptedVolume volume = EncryptedVolume.Mount(path, Password);
        long id = volume.CreateEntry("file", false, () => true);
        volume.Fault = point =>
        {
            if (point == "before-commit")
            {
                throw new Microsoft.Data.Sqlite.SqliteException("simulated full disk", 13);
            }
        };
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => volume.Write(id, 0, [5], () => true));
        Assert.Empty(volume.Read(id, 0, 1));
        volume.Fault = point =>
        {
            if (point == "flush")
            {
                throw new IOException("simulated flush failure");
            }
        };
        Assert.Throws<IOException>(volume.Flush);
    }

    [Theory]
    [InlineData("/vol/../../escape")]
    [InlineData("/vol/test/CON")]
    [InlineData("/vol/test/name.")]
    [InlineData("/vol/test/a:b")]
    public void RejectsInvalidNames(string path) => Assert.Throws<KernelOperationException>(() => VolumeNames.Normalize(path));
}
