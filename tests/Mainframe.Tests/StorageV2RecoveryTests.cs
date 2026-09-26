using System.Diagnostics;
using Mainframe.Client;

namespace Mainframe.Tests;

public sealed partial class StorageIntegrationTests
{
    [Theory]
    [InlineData("delete-intent-committed")]
    [InlineData("delete-cleanup")]
    [InlineData("replace-before-commit")]
    [InlineData("replace-committed")]
    [InlineData("reclaim")]
    public async Task KilledHostRecoversDeletionReplacementAndReclamation(string point)
    {
        await using (KernelFileHandle original = await OpenV2("old")) await original.WriteAsync(0, new byte[] { 1, 2, 3 });
        await using (KernelFileHandle temporary = await OpenV2("temporary")) await temporary.WriteAsync(0, new byte[] { 7, 8, 9 });
        await _client.DisposeAsync();
        await _server.DisposeAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "Mainframe.Fixtures.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "storage-host", Path.Combine(_root, "state"), point }) start.ArgumentList.Add(arg);
        using Process child = Process.Start(start)!;
        try
        {
            string ready = (await child.StandardOutput.ReadLineAsync(timeout.Token))!;
            using var ca = _store.LoadCaCertificate();
            using var certificate = _store.LoadOperatorCertificate();
            await using KernelClient remote = await KernelClient.ConnectAsync("127.0.0.1", int.Parse(ready[6..]), certificate, ca);
            await remote.MountVolumeAsync(Container, "/vol/data", Password);
            KernelFileHandle handle = await remote.Files.OpenHandleAsync(new("/vol/data/" + (point.StartsWith("replace") ? "temporary" : "old"), Rights: s_allRights, Share: s_allShares));
            Task operation;
            if (point.StartsWith("replace")) operation = handle.RenameAsync("/vol/data/old", true);
            else if (point == "delete-intent-committed") operation = handle.SetDeleteAsync(true);
            else
            {
                await handle.SetDeleteAsync(true);
                if (point == "delete-cleanup") operation = handle.CleanupAsync();
                else
                {
                    await handle.CleanupAsync();
                    operation = handle.DisposeAsync().AsTask();
                }
            }
            Assert.Equal("POINT:" + point, await child.StandardOutput.ReadLineAsync(timeout.Token));
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(timeout.Token);
            try { await operation; }
            catch (IOException) { }
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            await Start();
        }
        await _client.MountVolumeAsync(Container, "/vol/data", Password);
        if (point.StartsWith("replace"))
        {
            await using KernelFileHandle file = await OpenV2("old", "open");
            Assert.Equal(point == "replace-committed" ? new byte[] { 7, 8, 9 } : new byte[] { 1, 2, 3 }, await file.ReadAsync(0, 3));
        }
        else Assert.Equal("NOT_FOUND", (await Assert.ThrowsAsync<KernelRpcException>(() => _client.Files.StatAsync("/vol/data/old"))).Code);
    }
}
