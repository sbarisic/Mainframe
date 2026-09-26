using Mainframe.Protocol;

namespace Mainframe.Client;

public sealed partial class KernelClient
{
    public KernelFileSystem Files => new(this);

    public async Task<VolumeCreated> CreateVolumeAsync(string path, string password, CancellationToken token = default) => Decode<VolumeCreated>(await CallAsync("volume.create", new VolumeCreateRequest(path, password), token));
    public async Task<VolumeInfo> MountVolumeAsync(string path, string mount, string password, CancellationToken token = default) => Decode<VolumeInfo>(await CallAsync("volume.mount", new VolumeMountRequest(path, mount, password), token));
    public async Task<VolumeInfo[]> ListVolumesAsync(CancellationToken token = default) => Decode<VolumeInfo[]>(await CallAsync("volume.list", cancellationToken: token));
    public async Task UnmountVolumeAsync(string mount, CancellationToken token = default) => _ = await CallAsync("volume.unmount", new FsPath(mount), token);
}

public sealed partial class KernelFileSystem(KernelClient client)
{
    public async Task<FsEntry> StatAsync(string path, CancellationToken token = default) => KernelClient.Decode<FsEntry>(await client.CallAsync("fs.stat", new FsStat(Path: path), token));
    public async Task<FsListing> ListAsync(string path, int limit = 256, string? continuation = null, CancellationToken token = default) => KernelClient.Decode<FsListing>(await client.CallAsync("fs.list", new FsList(path, limit, continuation), token));
    public async Task CreateDirectoryAsync(string path, CancellationToken token = default) => _ = await client.CallAsync("fs.mkdir", new FsPath(path), token);
    public async Task DeleteAsync(string path, CancellationToken token = default) => _ = await client.CallAsync("fs.delete", new FsPath(path), token);
    public async Task RenameAsync(string source, string destination, bool replace = false, CancellationToken token = default) => _ = await client.CallAsync("fs.rename", new FsRename(source, destination, replace), token);
    public async Task<KernelFileStream> OpenAsync(string path, string access = "read", string mode = "open-existing", string[]? share = null, CancellationToken token = default)
    {
        FsOpened opened = KernelClient.Decode<FsOpened>(await client.CallAsync("fs.open", new FsOpen(path, access, mode, share), token));
        return new(client, opened.Handle, access);
    }
}

/// <summary>Uncached kernel file stream. Prefer async calls; mutations are never replayed.</summary>
public sealed class KernelFileStream : Stream
{
    private readonly KernelClient _client;
    private readonly string _handle;
    private readonly string _access;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _position;
    private bool _disposed;
    internal KernelFileStream(KernelClient client, string handle, string access)
    {
        _client = client;
        _handle = handle;
        _access = access;
    }

    public override bool CanRead => !_disposed && _access != "write";
    public override bool CanWrite => !_disposed && _access != "read";
    public override bool CanSeek => !_disposed;

    private void Check() => ObjectDisposedException.ThrowIf(_disposed, this);
    public override long Position
    {
        get
        {
            _gate.Wait();
            try
            {
                Check();
                return _position;
            }
            finally
            {
                _gate.Release();
            }
        }

        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _gate.Wait();
            try
            {
                Check();
                _position = value;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task<long> LengthCore(CancellationToken token) => FileNumbers.Parse(KernelClient.Decode<FsEntry>(await _client.CallAsync("fs.stat", new FsStat(Handle: _handle), token)).Length);
    public override long Length
    {
        get
        {
            _gate.Wait();
            try
            {
                Check();
                return Task.Run(() => LengthCore(default)).GetAwaiter().GetResult();
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _gate.Wait();
        try
        {
            Check();
            long next = checked(offset + (origin switch
            {
                SeekOrigin.Begin => 0,
                SeekOrigin.Current => _position,
                SeekOrigin.End => Task.Run(() => LengthCore(default)).GetAwaiter().GetResult(),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            }));
            if (next < 0)
            {
                throw new IOException("Cannot seek before file start.");
            }

            return _position = next;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Task.Run(() => ReadAsync(buffer.AsMemory(offset, count)).AsTask()).GetAwaiter().GetResult();
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (!CanRead)
            {
                throw new NotSupportedException("Stream was not opened for reading.");
            }

            if (buffer.Length == 0)
            {
                return 0;
            }

            int count = Math.Min(buffer.Length, 65536);
            KernelInvocation call = await _client.BeginAsync("fs.read", new FsRange(_handle, FileNumbers.Format(_position), FileNumbers.Format(count)), cancellationToken).ConfigureAwait(false);
            using CancellationTokenRegistration cancelled = cancellationToken.Register(() => _ = CancelQuietly(call));
            int used = 0;
            if (call.Response.Streaming)
            {
                Stream input = call.Exchange.Channel(1).Input;
                int next;
                while (used < count && (next = await input.ReadAsync(buffer.Slice(used, count - used), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    used += next;
                }

                if (await input.ReadAsync(new byte[1], cancellationToken).ConfigureAwait(false) != 0)
                {
                    throw new IOException("Read exceeded requested range.");
                }

                RpcResponse completed = await call.Exchange.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                KernelClient.Check(completed);
                FsTransferred result = KernelClient.Decode<FsTransferred>(completed.Result!.Value);
                if (FileNumbers.Parse(result.Bytes) != used)
                {
                    throw new IOException("Read completion length does not match output.");
                }
            }

            _position = checked(_position + used);
            return used;
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Task.Run(() => WriteAsync(buffer.AsMemory(offset, count)).AsTask()).GetAwaiter().GetResult();
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (!CanWrite)
            {
                throw new NotSupportedException("Stream was not opened for writing.");
            }

            if (_position > long.MaxValue - buffer.Length)
            {
                throw new IOException("File offset overflow.");
            }

            while (!buffer.IsEmpty)
            {
                int count = Math.Min(buffer.Length, 65536);
                KernelInvocation call = await _client.BeginAsync("fs.write", new FsRange(_handle, FileNumbers.Format(_position), FileNumbers.Format(count)), cancellationToken).ConfigureAwait(false);
                using CancellationTokenRegistration cancelled = cancellationToken.Register(() => _ = CancelQuietly(call));
                if (!call.Response.Streaming)
                {
                    throw new IOException("Write channel was not declared.");
                }

                await call.Exchange.Channel(1).SendAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
                await call.Exchange.Channel(1).EndAsync().ConfigureAwait(false);
                RpcResponse completed = await call.Exchange.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                KernelClient.Check(completed);
                if (FileNumbers.Parse(KernelClient.Decode<FsTransferred>(completed.Result!.Value).Bytes) != count)
                {
                    throw new IOException("Write completion length mismatch.");
                }

                _position += count;
                buffer = buffer[count..];
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task CancelQuietly(KernelInvocation call)
    {
        try
        {
            await call.CancelAsync().ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }

    public async Task SetLengthAsync(long value, CancellationToken token = default)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Check();
            if (!CanWrite)
            {
                throw new NotSupportedException();
            }

            await _client.CallAsync("fs.truncate", new FsTruncate(_handle, FileNumbers.Format(value)), token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override void SetLength(long value) => Task.Run(() => SetLengthAsync(value)).GetAwaiter().GetResult();
    public override void Flush() => Task.Run(() => FlushAsync()).GetAwaiter().GetResult();
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check();
            if (CanWrite)
            {
                await _client.CallAsync("fs.flush", new FsHandle(_handle), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Task.Run(CloseAsync).GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    private async Task CloseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            await _client.CallAsync("fs.close", new FsHandle(_handle)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
