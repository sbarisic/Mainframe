using Fsp;
using Mainframe.Client;
using Mainframe.Protocol;

namespace Mainframe.WinFsp.Tests;

public sealed class MappingTests
{
    [Theory]
    [InlineData("\\vol\\data\\file", "/", "/vol/data/file")]
    [InlineData("\\nested\\..\\file", "/vol/data", "/vol/data/file")]
    [InlineData("\\", "/vol/data", "/vol/data")]
    public void PathsStayInsideExport(string input, string root, string expected) => Assert.Equal(expected, VirtualPath.Map(root, input));

    [Theory]
    [InlineData("\\..\\escape")]
    [InlineData("\\a\\..\\..\\escape")]
    [InlineData("\\file:stream")]
    [InlineData("\\CON.txt")]
    [InlineData("\\trailing.")]
    [InlineData("\\\\server\\share")]
    public void InvalidPathsAreRejected(string input) => Assert.Throws<ArgumentException>(() => VirtualPath.Map("/vol/data", input));

    [Fact]
    public void RightsDoNotGrantDirectoryChildFlagsAsDirectoryMutation()
    {
        Assert.Equal(["read-metadata", "list"], WindowsMapping.Rights(7, true));
        Assert.Equal(["read-metadata", "read-data", "write-data", "append", "write-metadata", "delete"], WindowsMapping.Rights(0x10107, false));
    }

    [Fact]
    public void MetadataPreservesIdentityTimeAndAllocation()
    {
        string date = DateTimeOffset.UtcNow.ToString("O");
        var entry = new FsEntry("1", "x", false, "12", date, date, "volume:1", date, date, "4096", Attributes: 2);
        var info = WindowsMapping.Info(entry);
        Assert.Equal(12ul, info.FileSize);
        Assert.Equal(4096ul, info.AllocationSize);
        Assert.Equal(2u, info.FileAttributes);
        Assert.Equal(WindowsMapping.Identity("volume:1"), info.IndexNumber);
        Assert.Equal(DateTimeOffset.Parse(date), DateTimeOffset.Parse(WindowsMapping.Timestamp(info.CreationTime)!));
        Assert.Null(WindowsMapping.Timestamp(0));
        Assert.Null(WindowsMapping.Timestamp(ulong.MaxValue));
        Assert.Throws<NotSupportedException>(() => WindowsMapping.Attributes(0x400));
    }

    [Theory]
    [InlineData("VOLUME_LOCKED", unchecked((int)0xc00000a3))]
    [InlineData("ACCESS_DENIED", unchecked((int)0xc0000022))]
    [InlineData("SHARING_VIOLATION", unchecked((int)0xc0000043))]
    [InlineData("DIRECTORY_NOT_EMPTY", unchecked((int)0xc0000101))]
    public void KernelFailuresKeepWindowsMeaning(string code, int expected) => Assert.Equal(expected, WindowsMapping.Status(new KernelRpcException(code, "test", "not_committed")));

    [Fact]
    public void OptionsRejectRemoteEndpointsAndOccupiedMounts()
    {
        Assert.Throws<ArgumentException>(() => MountOptions.Parse(["mount", "M:", "--endpoint", "example.com:7443"]));
        Assert.Throws<ArgumentException>(() => MountOptions.Parse(["mount", "M:", "--root", "/", "--root", "/vol"]));
        var options = MountOptions.Parse(["mount", Path.GetTempPath()]);
        Assert.Throws<ArgumentException>(() => options.ValidateMountPoint());
        Assert.Equal("/", options.Root);
        Assert.Equal(7443, options.Port);
        Assert.Throws<ArgumentException>(() => MountOptions.Parse(["mount", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))]).ValidateMountPoint());
    }
}
