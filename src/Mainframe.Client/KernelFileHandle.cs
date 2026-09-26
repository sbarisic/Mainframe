using Mainframe.Protocol;

namespace Mainframe.Client;

public sealed partial class KernelFileSystem
{
    public async Task<KernelFileHandle> OpenHandleAsync(FsOpenV2 options, CancellationToken token = default)
    {
        FsOpenedV2 opened = KernelClient.Decode<FsOpenedV2>(await client.CallAsync("fs.open", options, token, 2));
        return new(client, opened);
    }

    public async Task<FsDiscovery> DiscoverAsync(string path = "/", CancellationToken token = default) => KernelClient.Decode<FsDiscovery>(await client.CallAsync("fs.discover", new FsPath(path), token, 2));
    public async Task FlushVolumeAsync(string path, CancellationToken token = default) => _ = await client.CallAsync("fs.flush-volume", new FsPath(path), token, 2);
}

/// <summary>A version-2 filesystem handle. Cleanup releases sharing and locks; final disposal closes the object.</summary>
public sealed class KernelFileHandle : IAsyncDisposable
{
    private readonly KernelClient _client;
    private bool _closed;
    public string Id
    {
        get;
    }
    public FsOpenedV2 OpenResult
    {
        get;
    }

    internal KernelFileHandle(KernelClient client, FsOpenedV2 opened)
    {
        _client = client;
        Id = opened.Handle;
        OpenResult = opened;
    }

    private void Check() => ObjectDisposedException.ThrowIf(_closed, this);
    private async Task<T> Call<T>(string method, object args, CancellationToken token)
    {
        Check();
        return KernelClient.Decode<T>(await _client.CallAsync(method, args, token, 2));
    }

    public Task<FsEntry> StatAsync(CancellationToken token = default) => Call<FsEntry>("fs.stat", new FsStat(Handle: Id), token);
    public Task<FsListing> EnumerateAsync(int limit = 256, bool restart = false, string? marker = null, string? continuation = null, CancellationToken token = default) => Call<FsListing>("fs.enumerate", new FsEnumerate(Id, limit, restart, marker, continuation), token);
    public Task<FsCommitted> RenameAsync(string destination, bool replace = false, CancellationToken token = default) => Call<FsCommitted>("fs.rename", new FsRenameHandle(Id, destination, replace), token);
    public Task<FsCommitted> SetMetadataAsync(FsMetadata metadata, CancellationToken token = default) => Call<FsCommitted>("fs.metadata", metadata with { Handle = Id }, token);
    public Task<FsCommitted> SetSizeAsync(long length, bool allocation = false, CancellationToken token = default) => Call<FsCommitted>("fs.size", new FsSize(Id, FileNumbers.Format(length), allocation), token);
    public Task<FsCommitted> SetDeleteAsync(bool delete, CancellationToken token = default) => Call<FsCommitted>("fs.disposition", new FsDisposition(Id, delete), token);
    public Task<FsCommitted> LockAsync(long offset, long length, bool exclusive = true, CancellationToken token = default) => Call<FsCommitted>("fs.lock", new FsLock(Id, FileNumbers.Format(offset), FileNumbers.Format(length), exclusive), token);
    public Task<FsCommitted> UnlockAsync(long offset, long length, bool exclusive = true, CancellationToken token = default) => Call<FsCommitted>("fs.unlock", new FsLock(Id, FileNumbers.Format(offset), FileNumbers.Format(length), exclusive), token);
    public Task<FsCommitted> FlushAsync(CancellationToken token = default) => Call<FsCommitted>("fs.flush", new FsHandle(Id), token);
    public Task<FsCommitted> CleanupAsync(CancellationToken token = default) => Call<FsCommitted>("fs.cleanup", new FsHandle(Id), token);

    public async Task<byte[]> ReadAsync(long offset, int length, CancellationToken token = default)
    {
        Check();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, 65536);
        KernelInvocation call = await _client.BeginAsync("fs.read", new FsRange(Id, FileNumbers.Format(offset), FileNumbers.Format(length)), token, 2);
        using CancellationTokenRegistration registration = token.Register(() => _ = CancelQuietly(call));
        byte[] data = new byte[length];
        int used = 0;
        if (call.Response.Streaming)
        {
            Stream input = call.Exchange.Channel(1).Input;
            while (used < length)
            {
                int read = await input.ReadAsync(data.AsMemory(used), token);
                if (read == 0) break;
                used += read;
            }
            if (await input.ReadAsync(new byte[1], token) != 0) throw new IOException("Read exceeds requested length.");
        }
        RpcResponse response = await call.Exchange.Completion.Task.WaitAsync(token);
        KernelClient.Check(response);
        if (FileNumbers.Parse(KernelClient.Decode<FsTransferred>(response.Result!.Value).Bytes) != used) throw new IOException("Read completion length mismatch.");
        return data[..used];
    }

    public async Task<int> WriteAsync(long offset, ReadOnlyMemory<byte> bytes, bool append = false, bool constrained = false, CancellationToken token = default)
    {
        Check();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, 65536);
        KernelInvocation call = await _client.BeginAsync("fs.write", new FsWriteV2(Id, FileNumbers.Format(offset), FileNumbers.Format(bytes.Length), append, constrained), token, 2);
        using CancellationTokenRegistration registration = token.Register(() => _ = CancelQuietly(call));
        if (call.Response.Streaming)
        {
            await call.Exchange.Channel(1).SendAsync(bytes, token);
            await call.Exchange.Channel(1).EndAsync();
        }
        RpcResponse response = await call.Exchange.Completion.Task.WaitAsync(token);
        KernelClient.Check(response);
        long written = FileNumbers.Parse(KernelClient.Decode<FsTransferred>(response.Result!.Value).Bytes);
        if (written > bytes.Length) throw new IOException("Invalid write completion length.");
        return (int)written;
    }

    private static async Task CancelQuietly(KernelInvocation call)
    {
        try { await call.CancelAsync(); }
        catch (IOException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_closed) return;
        _closed = true;
        await _client.CallAsync("fs.close", new FsHandle(Id), version: 2);
    }
}
