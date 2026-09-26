using Mainframe.Client;
using Mainframe.Protocol;

namespace Mainframe.Tests;

public sealed partial class StorageIntegrationTests
{
    private static readonly string[] s_allRights = ["read-data", "write-data", "append", "read-metadata", "write-metadata", "delete"];
    private static readonly string[] s_allShares = ["read", "write", "delete"];
    private Task<KernelFileHandle> OpenV2(string name, string disposition = "open-or-create") => _client.Files.OpenHandleAsync(new("/vol/data/" + name, "file", disposition, s_allRights, s_allShares));

    [Fact]
    public async Task UnknownOptionalFieldsCannotChangeVolumeRouting()
    {
        System.Text.Json.Nodes.JsonNode open = System.Text.Json.Nodes.JsonNode.Parse(ProtocolJson.Serialize(new FsOpenV2("/vol/data/routing", "file", "create", s_allRights, s_allShares)))!;
        open["handle"] = "this-unknown-field-must-not-select-a-volume";
        FsOpenedV2 opened = KernelClient.Decode<FsOpenedV2>(await _client.CallAsync("fs.open", open, version: 2));
        System.Text.Json.Nodes.JsonNode metadata = System.Text.Json.Nodes.JsonNode.Parse(ProtocolJson.Serialize(new FsMetadata(opened.Handle, Attributes: 2)))!;
        metadata["path"] = "/vol/missing/ignored";
        await _client.CallAsync("fs.metadata", metadata, version: 2);
        Assert.Equal(2, (await _client.Files.StatAsync("/vol/data/routing")).Attributes);
        await _client.CallAsync("fs.close", new FsHandle(opened.Handle), version: 2);
    }

    [Fact]
    public async Task UnicodeMountAliasesPreserveConfigurationAndUnmountDurably()
    {
        await _client.UnmountVolumeAsync("/vol/data");
        await _client.MountVolumeAsync(Container, "/vol/Σ", Password);
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        await Start();
        Assert.Equal("/vol/Σ", (await _client.MountVolumeAsync(Container, "/VOL/σ", Password)).Mount);
        await _client.UnmountVolumeAsync("/vol/σ");
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        await Start();
        Assert.Empty(await _client.ListVolumesAsync());
    }

    [Fact]
    public async Task ObservedGrantLossPermanentlyInvalidatesAHandle()
    {
        KernelFileHandle handle = await OpenV2("revoked");
        using var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state", "kernel.db"), Pooling = false }.ToString());
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "DELETE FROM grants WHERE principal='local-admin'";
        command.ExecuteNonQuery();
        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(() => handle.StatAsync())).Code);
        command.CommandText = "INSERT INTO grants VALUES('local-admin','*')";
        command.ExecuteNonQuery();
        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(() => handle.StatAsync())).Code);
        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(() => handle.DisposeAsync().AsTask())).Code);
    }

    [Fact]
    public async Task AllCreationDispositionsReportTheirCommittedResult()
    {
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<KernelRpcException>(() => OpenV2("missing", "open"))).Code);
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<KernelRpcException>(() => OpenV2("missing", "overwrite"))).Code);
        string identity;
        await using (KernelFileHandle file = await OpenV2("disposition", "create"))
        {
            Assert.Equal("created", file.OpenResult.Action);
            identity = file.OpenResult.Entry.ResourceId!;
            await file.WriteAsync(0, new byte[] { 1, 2, 3 });
        }
        Assert.Equal("ALREADY_EXISTS", (await Assert.ThrowsAsync<KernelRpcException>(() => OpenV2("disposition", "create"))).Code);
        foreach (string disposition in new[] { "open", "open-or-create", "overwrite", "overwrite-or-create", "supersede" })
        {
            await using KernelFileHandle file = await OpenV2("disposition", disposition);
            bool truncates = disposition is "overwrite" or "overwrite-or-create" or "supersede";
            Assert.Equal(truncates ? "replaced" : "opened", file.OpenResult.Action);
            Assert.Equal(truncates ? "0" : "3", file.OpenResult.Entry.Length);
            if (disposition == "supersede") Assert.NotEqual(identity, file.OpenResult.Entry.ResourceId);
            else Assert.Equal(identity, file.OpenResult.Entry.ResourceId);
            await file.WriteAsync(0, new byte[] { 1, 2, 3 });
        }
        foreach (string disposition in new[] { "open-or-create", "overwrite-or-create", "supersede" })
        {
            await using KernelFileHandle file = await OpenV2(disposition, disposition);
            Assert.Equal("created", file.OpenResult.Action);
        }
    }

    [Fact]
    public async Task LockQuotasAreSharedAcrossHandlesAndCleanupReturnsCapacity()
    {
        var handles = new List<KernelFileHandle>();
        try
        {
            for (int index = 0; index < 17; index++) handles.Add(await OpenV2("locks"));
            for (int index = 0; index < 16; index++)
                for (int count = 0; count < 256; count++) await handles[index].LockAsync(count, 1, exclusive: false);
            Assert.Equal("RESOURCE_UNAVAILABLE", (await Assert.ThrowsAsync<KernelRpcException>(() => handles[0].LockAsync(1000, 1))).Code);
            Assert.Equal("RESOURCE_UNAVAILABLE", (await Assert.ThrowsAsync<KernelRpcException>(() => handles[16].LockAsync(1000, 1))).Code);
            await handles[0].CleanupAsync();
            await handles[16].LockAsync(1000, 1);
            await handles[16].UnlockAsync(1000, 1);
        }
        finally
        {
            foreach (KernelFileHandle handle in handles) await handle.DisposeAsync();
        }
    }

    [Fact]
    public async Task DirectoryEnumerationIsBoundedAcrossConcurrentChanges()
    {
        await _client.Files.CreateDirectoryAsync("/vol/data/directory");
        for (int index = 0; index < 260; index++)
            await _client.Files.CreateDirectoryAsync("/vol/data/directory/entry" + index.ToString("D3"));
        await using KernelFileHandle directory = await _client.Files.OpenHandleAsync(new("/vol/data/directory", "directory", Rights: ["list", "read-metadata", "delete"], Share: s_allShares));
        FsListing first = await directory.EnumerateAsync();
        Assert.Equal(256, first.Entries.Length);
        await _client.Files.CreateDirectoryAsync("/vol/data/directory/z-added");
        FsListing second = await directory.EnumerateAsync(continuation: first.Continuation);
        Assert.Equal(5, second.Entries.Length);
        Assert.Empty(first.Entries.Select(e => e.ResourceId).Intersect(second.Entries.Select(e => e.ResourceId)));
        Assert.Equal("DIRECTORY_NOT_EMPTY", (await Assert.ThrowsAsync<KernelRpcException>(() => directory.SetDeleteAsync(true))).Code);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.CreateDirectoryAsync("/vol/forbidden"))).Code);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.DeleteAsync("/vol/data"))).Code);
        await using KernelFileHandle other = await _client.Files.OpenHandleAsync(new("/vol/data/directory", "directory", Rights: ["list"], Share: s_allShares));
        Assert.Equal("INVALID_ARGUMENT", (await Assert.ThrowsAsync<KernelRpcException>(() => other.EnumerateAsync(continuation: first.Continuation))).Code);
    }

    [Fact]
    public async Task DiscoveryDeduplicatesCapacityAndLockedAccessDoesNotBypassGrants()
    {
        string otherPath = Path.Combine(_root, "second.mfv");
        await _client.CreateVolumeAsync(otherPath, Password);
        await _client.MountVolumeAsync(otherPath, "/vol/second", Password);
        FsDiscovery discovery = await _client.Files.DiscoverAsync();
        Assert.Single(discovery.BackingStorage);
        Assert.True(FileNumbers.Parse(discovery.BackingStorage[0].TotalBytes) > 0);
        Assert.Contains("persistent-acls", discovery.Capabilities.Unsupported);
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        await Start();
        using (var database = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state", "kernel.db"), Pooling = false }.ToString()))
        {
            database.Open();
            using var command = database.CreateCommand();
            command.CommandText = "DELETE FROM grants WHERE principal='local-admin'; INSERT INTO grants VALUES('local-admin',$grant)";
            command.Parameters.AddWithValue("$grant", "volume:" + _volume.Id + ":read");
            command.ExecuteNonQuery();
        }
        Assert.Equal("data", Assert.Single((await _client.Files.ListAsync("/vol")).Entries).Name);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.ListAsync("/vol/second"))).Code);
        Assert.Equal("VOLUME_LOCKED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.ListAsync("/vol/data"))).Code);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.CreateDirectoryAsync("/vol/data/child"))).Code);
    }

    [Fact]
    public async Task CleanupReleasesSharingAndSharedLocksDenyOwnerWrites()
    {
        await using KernelFileHandle file = await _client.Files.OpenHandleAsync(new("/vol/data/exclusive", "file", "create", s_allRights));
        await file.WriteAsync(0, new byte[] { 1, 2, 3 });
        Assert.Equal("SHARING_VIOLATION", (await Assert.ThrowsAsync<KernelRpcException>(() => OpenV2("exclusive", "open"))).Code);
        await file.LockAsync(0, 2, exclusive: false);
        Assert.Equal(new byte[] { 1, 2 }, await file.ReadAsync(0, 2));
        Assert.Equal("LOCK_CONFLICT", (await Assert.ThrowsAsync<KernelRpcException>(() => file.WriteAsync(0, new byte[] { 4 }))).Code);
        await file.CleanupAsync();
        await using KernelFileHandle other = await OpenV2("exclusive", "open");
        await other.WriteAsync(0, new byte[] { 4 });
        Assert.Equal(new byte[] { 4, 2 }, await file.ReadAsync(0, 2));
        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(() => file.LockAsync(0, 1))).Code);
    }

    [Fact]
    public async Task NamespaceRootsLockedMountsAndFilesystemRole()
    {
        Assert.Equal("vol", Assert.Single((await _client.Files.ListAsync("/")).Entries).Name);
        FsEntry root = await _client.Files.StatAsync("/VOL/../");
        Assert.True(root.Directory);
        Assert.NotNull(root.ResourceId);
        Assert.True(DateTimeOffset.TryParse(root.Created, out _));
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.StatAsync("/unknown"))).Code);
        Assert.Equal("INVALID_PATH", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.StatAsync("/../../x"))).Code);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.DeleteAsync("/"))).Code);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.CreateDirectoryAsync("/vol"))).Code);
        using var ca = _store.LoadCaCertificate();
        using var certificate = _store.LoadOperatorCertificate();
        await using (KernelClient filesystem = await KernelClient.ConnectFilesystemAsync("127.0.0.1", _server.Port, certificate, ca))
        {
            Assert.True((await filesystem.Files.StatAsync("/")).Directory);
            Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => filesystem.ListProgramsAsync())).Code);
            Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => filesystem.ListVolumesAsync())).Code);
            await using KernelFileHandle handle = await filesystem.Files.OpenHandleAsync(new("/", "directory", Rights: ["list", "read-metadata"]));
            Assert.Equal("vol", Assert.Single((await handle.EnumerateAsync()).Entries).Name);
            Assert.True((await handle.StatAsync()).Directory);
        }
        string generation = (await _client.Files.StatAsync("/vol/data")).Generation!;
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        await Start();
        Assert.Equal("locked", Assert.Single((await _client.Files.ListAsync("/vol")).Entries).State);
        Assert.Equal("locked", (await _client.Files.StatAsync("/vol/data")).State);
        Assert.Equal("VOLUME_LOCKED", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.ListAsync("/vol/data"))).Code);
        await _client.MountVolumeAsync(Container, "/VOL/DATA", Password);
        Assert.Equal("data", (await _client.Files.StatAsync("/vol/data")).Name);
        Assert.NotEqual(generation, (await _client.Files.StatAsync("/vol/data")).Generation);
    }

    [Fact]
    public async Task RetainedReplacementCleanupDeletionAndLegacyCompatibility()
    {
        await using KernelFileHandle old = await OpenV2("target");
        await old.WriteAsync(0, new byte[] { 1, 2, 3 });
        string identity = (await old.StatAsync()).ResourceId!;
        KernelFileStream legacy = await _client.Files.OpenAsync("/vol/data/target", share: s_allShares);
        await using KernelFileHandle source = await OpenV2("temp");
        await source.WriteAsync(0, new byte[] { 7, 8 });
        await source.RenameAsync("/vol/data/target", true);
        Assert.Equal(new byte[] { 1, 2, 3 }, await old.ReadAsync(0, 3));
        Assert.Equal(identity, (await old.StatAsync()).ResourceId);
        Assert.NotEqual(identity, (await _client.Files.StatAsync("/vol/data/target")).ResourceId);
        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(async () => await legacy.ReadExactlyAsync(new byte[3]))).Code);
        Assert.Equal("INVALID_HANDLE", (await Assert.ThrowsAsync<KernelRpcException>(() => legacy.DisposeAsync().AsTask())).Code);
        await source.SetDeleteAsync(true);
        Assert.Equal("DELETE_PENDING", (await Assert.ThrowsAsync<KernelRpcException>(() => OpenV2("target", "open"))).Code);
        await source.SetDeleteAsync(false);
        await source.SetDeleteAsync(true);
        await source.CleanupAsync();
        Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.StatAsync("/vol/data/target"))).Code);
        Assert.Equal(new byte[] { 7, 8 }, await source.ReadAsync(0, 2));
        await source.WriteAsync(0, new byte[] { 9 });
        Assert.Equal(new byte[] { 9, 8 }, await source.ReadAsync(0, 2));
    }

    [Fact]
    public async Task MetadataAllocationAppendAndRangeLocksAcrossConnections()
    {
        await using KernelFileHandle file = await OpenV2("data");
        await file.WriteAsync(0, new byte[] { 1, 2, 3 });
        await file.SetSizeAsync(100000, allocation: true);
        Assert.Equal("3", (await file.StatAsync()).Length);
        Assert.Equal("100000", (await file.StatAsync()).AllocationLength);
        Assert.Equal(1, await file.WriteAsync(2, new byte[] { 8, 9 }, constrained: true));
        await using KernelClient other = await Connect();
        await using KernelFileHandle second = await other.Files.OpenHandleAsync(new("/vol/data/data", Rights: s_allRights, Share: s_allShares));
        await file.LockAsync(0, 5);
        Assert.Equal("LOCK_CONFLICT", (await Assert.ThrowsAsync<KernelRpcException>(() => second.ReadAsync(0, 1))).Code);
        Assert.Equal("LOCK_CONFLICT", (await Assert.ThrowsAsync<KernelRpcException>(() => second.WriteAsync(1, new byte[] { 4 }))).Code);
        Assert.Equal("LOCK_CONFLICT", (await Assert.ThrowsAsync<KernelRpcException>(() => second.SetSizeAsync(1))).Code);
        await file.UnlockAsync(0, 5);
        await Task.WhenAll(file.WriteAsync(0, new byte[] { 4 }, append: true), second.WriteAsync(0, new byte[] { 5 }, append: true));
        Assert.Equal("5", (await file.StatAsync()).Length);
        byte[] result = await file.ReadAsync(0, 5);
        Assert.Equal(new byte[] { 1, 2, 8 }, result[..3]);
        Assert.Equal(new byte[] { 4, 5 }, result[3..].Order().ToArray());
        string time = DateTimeOffset.UnixEpoch.AddDays(17).ToString("O");
        await file.SetMetadataAsync(new("", Attributes: 1 | 2, Accessed: time));
        Assert.Equal(time, (await file.StatAsync()).Accessed);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => file.WriteAsync(0, new byte[] { 0 }))).Code);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => file.SetDeleteAsync(true))).Code);
        await file.SetMetadataAsync(new("", Attributes: 2));
        await file.SetSizeAsync(2, allocation: true);
        Assert.Equal("2", (await file.StatAsync()).Length);
        await file.LockAsync(0, 2);
        await file.CleanupAsync();
        Assert.Equal(new byte[] { 1, 2 }, await second.ReadAsync(0, 2));
        await file.FlushAsync();
        await _client.Files.FlushVolumeAsync("/vol/data");
    }

    [Fact]
    public async Task DirectoryHandlesMarkersDispositionsAndMetadataOnlyAccess()
    {
        foreach (string name in new[] { "z", "B", "a" })
            await using (KernelFileHandle file = await OpenV2(name, "create")) { }
        await using KernelFileHandle directory = await _client.Files.OpenHandleAsync(new("/vol/data", "directory", Rights: ["list", "read-metadata"], Share: s_allShares));
        FsListing first = await directory.EnumerateAsync(2);
        Assert.Equal(new[] { "a", "B" }, first.Entries.Select(e => e.Name));
        Assert.Equal("z", Assert.Single((await directory.EnumerateAsync(2, continuation: first.Continuation)).Entries).Name);
        Assert.Equal("B", (await directory.EnumerateAsync(1, restart: true, marker: "a")).Entries[0].Name);
        await using KernelFileHandle metadata = await _client.Files.OpenHandleAsync(new("/vol/data/a", Rights: ["read-metadata"], Share: s_allShares));
        Assert.Equal("a", (await metadata.StatAsync()).Name);
        Assert.Equal("ACCESS_DENIED", (await Assert.ThrowsAsync<KernelRpcException>(() => metadata.ReadAsync(0, 1))).Code);
        await using KernelFileHandle superseded = await OpenV2("a", "supersede");
        Assert.Equal("replaced", superseded.OpenResult.Action);
        Assert.NotEqual((await metadata.StatAsync()).ResourceId, (await superseded.StatAsync()).ResourceId);
        await superseded.RenameAsync("/vol/data/A");
        Assert.Equal("A", (await superseded.StatAsync()).Name);
        Assert.Equal("PATH_NOT_FOUND", (await Assert.ThrowsAsync<KernelRpcException>(() => OpenV2("missing/file", "create"))).Code);
    }

    [Fact]
    public async Task TenThousandCallsAndLargeSparseReadUseOneConnection()
    {
        Assert.Contains("exchange-retire-v1", _client.Welcome.Features);
        for (int index = 0; index < 10050; index++) Assert.True((await _client.Files.StatAsync("/")).Directory);
        await using KernelFileHandle file = await OpenV2("large");
        const long length = 257L * 1024 * 1024;
        await file.SetSizeAsync(length);
        await file.WriteAsync(length - 1, new byte[] { 42 });
        for (long offset = 0; offset < length; offset += 65536)
        {
            byte[] bytes = await file.ReadAsync(offset, 65536);
            Assert.Equal(65536, bytes.Length);
            if (offset + bytes.Length == length) { Assert.Equal(42, bytes[^1]); bytes[^1] = 0; }
            Assert.True(bytes.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        }
        Assert.Equal(length.ToString(), (await file.StatAsync()).Length);
    }
}
