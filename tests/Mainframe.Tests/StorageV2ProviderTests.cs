using Mainframe.Core;
using Mainframe.Protocol;
using Mainframe.Storage;
using Microsoft.Data.Sqlite;

namespace Mainframe.Tests;

public sealed partial class StorageProviderTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/VOL//a/.././", "/vol")]
    [InlineData("/vol/../unknown", "/unknown")]
    [InlineData("//vol/one/../../", "/")]
    public void NormalizesNamespacePaths(string path, string expected) => Assert.Equal(expected, VolumeNames.Normalize(path));

    [Fact]
    public void MigrationPreservesIdentityBytesAndRollsBackOnFailure()
    {
        string path = Path.Combine(_root, "legacy.mfv");
        string uuid = Guid.NewGuid().ToString("D");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Password = Password, Pooling = false }.ToString()))
        {
            db.Open();
            db.CreateCollation("MF_NAME", StringComparer.OrdinalIgnoreCase.Compare);
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE volume(id TEXT);
                INSERT INTO volume VALUES($uuid);
                CREATE TABLE entries(id INTEGER PRIMARY KEY AUTOINCREMENT,parent INTEGER REFERENCES entries(id),name TEXT COLLATE MF_NAME NOT NULL,directory INTEGER NOT NULL,epoch INTEGER NOT NULL DEFAULT 0,length INTEGER NOT NULL DEFAULT 0,created TEXT NOT NULL,modified TEXT NOT NULL,UNIQUE(parent,name));
                CREATE TABLE chunks(file INTEGER,part INTEGER,bytes BLOB,epoch INTEGER,PRIMARY KEY(file,part));
                CREATE TABLE erasures(file INTEGER,part INTEGER,epoch INTEGER,PRIMARY KEY(file,epoch));
                INSERT INTO entries VALUES(1,NULL,'',1,0,0,$time,$time),(17,1,'Saved',0,0,3,$time,$time);
                INSERT INTO chunks VALUES(17,0,x'010203',0);
                PRAGMA user_version=1;
                """;
            command.Parameters.AddWithValue("$uuid", uuid);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UnixEpoch.ToString("O"));
            command.ExecuteNonQuery();
        }
        Assert.Throws<IOException>(() => EncryptedVolume.Mount(path, Password, fault: point =>
        {
            if (point == "migration-before-commit") throw new IOException("interrupted migration");
        }));
        using EncryptedVolume volume = EncryptedVolume.Mount(path, Password);
        Assert.Equal(uuid, volume.Id);
        Assert.Equal(17, volume.Resolve("saved"));
        FsEntry entry = volume.Stat(17);
        Assert.Equal(uuid + ":17", entry.ResourceId);
        Assert.Equal(entry.Modified, entry.Changed);
        Assert.Equal(entry.Modified, entry.Accessed);
        Assert.Equal("3", entry.AllocationLength);
        Assert.Equal(new byte[] { 1, 2, 3 }, volume.Read(17, 0, 3));
    }

    [Fact]
    public void DeletionIntentRecoveryAndRetainedObjectsAreDurable()
    {
        string path = Path.Combine(_root, "delete.mfv");
        EncryptedVolume.Create(path, Password);
        long retained;
        using (EncryptedVolume volume = EncryptedVolume.Mount(path, Password))
        {
            retained = volume.CreateEntry("old", false, () => true);
            volume.Write(retained, 0, [4, 5, 6], () => true);
            volume.Pin(retained);
            volume.SetDelete(retained, true, () => true);
            volume.FinalizeDelete(retained);
            volume.Maintain();
            Assert.Equal(new byte[] { 4, 5, 6 }, volume.Read(retained, 0, 3));
            volume.Unpin(retained);
            volume.Maintain();
            Assert.Equal("INVALID_HANDLE", Assert.Throws<KernelOperationException>(() => volume.Stat(retained)).Code);
            long pending = volume.CreateEntry("pending", false, () => true);
            volume.SetDelete(pending, true, () => true);
            long cancelled = volume.CreateEntry("cancelled", false, () => true);
            volume.SetDelete(cancelled, true, () => true);
            volume.SetDelete(cancelled, false, () => true);
        }
        using EncryptedVolume reopened = EncryptedVolume.Mount(path, Password);
        Assert.Equal("NOT_FOUND", Assert.Throws<KernelOperationException>(() => reopened.Resolve("pending")).Code);
        Assert.True(reopened.Resolve("cancelled") > 1);
    }

    [Fact]
    public void ProviderMetadataAllocationAndAtomicOpenRollback()
    {
        string path = Path.Combine(_root, "metadata.mfv");
        EncryptedVolume.Create(path, Password);
        using EncryptedVolume volume = EncryptedVolume.Mount(path, Password);
        long id = volume.CreateEntry("file", false, () => true);
        volume.Write(id, 0, [1, 2, 3], () => true);
        Assert.Throws<KernelOperationException>(() => volume.Atomic(() =>
        {
            volume.Truncate(id, 0, () => true);
            return volume.CreateEntry("new", false, () => true);
        }, () => false));
        Assert.Equal("3", volume.Stat(id).Length);
        Assert.Equal("NOT_FOUND", Assert.Throws<KernelOperationException>(() => volume.Resolve("new")).Code);
        volume.SetAllocation(id, 1000000, () => true);
        Assert.Equal("3", volume.Stat(id).Length);
        Assert.Equal("1000000", volume.Stat(id).AllocationLength);
        Assert.Equal("65536", volume.Stat(id).StoredBytes);
        volume.SetAllocation(id, 1, () => true);
        volume.Truncate(id, 3, () => true);
        Assert.Equal(new byte[] { 1, 0, 0 }, volume.Read(id, 0, 3));
        volume.SetMetadata(id, new("", Attributes: 1), () => true);
        Assert.Equal("ACCESS_DENIED", Assert.Throws<KernelOperationException>(() => volume.Delete(id, () => true)).Code);
        volume.SetMetadata(id, new("", Attributes: 0), () => true);
        volume.Delete(id, () => true);
    }
}
