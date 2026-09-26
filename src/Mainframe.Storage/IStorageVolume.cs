using Mainframe.Protocol;

namespace Mainframe.Storage;

public interface IStorageVolume : IDisposable
{
    string Id
    {
        get;
    }

    string Path
    {
        get;
    }

    string Generation
    {
        get;
    }

    T Atomic<T>(Func<T> action, Func<bool> authorized);
    void Pin(long id);
    void Unpin(long id);
    bool IsLinked(long id);
    bool IsDeletePending(long id);
    void RejectPending(long id);
    FsEntry[] Enumerate(long parent, string? marker, int limit);
    void SetMetadata(long id, FsMetadata metadata, Func<bool> authorized);
    void SetAllocation(long id, long length, Func<bool> authorized);
    void SetDelete(long id, bool delete, Func<bool> authorized);
    void FinalizeDelete(long id);
    long Resolve(string relative);
    FsEntry Stat(long id);
    FsEntry[] List(long parent, long after, int limit);
    long CreateEntry(string path, bool directory, Func<bool> authorized);
    byte[] Read(long id, long offset, int count);
    void Write(long id, long offset, byte[] data, Func<bool> authorized);
    void Truncate(long id, long length, Func<bool> authorized);
    void Delete(long id, Func<bool> authorized);
    void Rename(long source, string target, bool replace, Action<long> checkTarget, Func<bool> authorized, int maximumPathLength = 4096);
    void Maintain();
    void Flush();
    void Checkpoint();
}
