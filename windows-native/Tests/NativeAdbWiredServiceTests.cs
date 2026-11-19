using TwinDesk.Native.Core;
using Xunit;

namespace TwinDesk.Native.Tests;

public sealed class NativeAdbWiredServiceTests
{
    [Fact]
    public void InitialSnapshotReflectsConfiguration()
    {
        var service = new NativeAdbWiredService("host-123", "TestHost", 47800);
        var snapshot = service.Snapshot;

        Assert.NotNull(snapshot);
        Assert.NotNull(snapshot.ConnectedSerials);
        Assert.NotNull(snapshot.Status);
    }

    [Fact]
    public void FindAdbPath_ReturnsExistingFileOrNull()
    {
        var adbPath = NativeAdbWiredService.FindAdbPath();
        if (adbPath is not null)
        {
            Assert.True(File.Exists(adbPath));
            Assert.EndsWith("adb.exe", adbPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TriggerScanAsync_CompletesGracefully()
    {
        await using var service = new NativeAdbWiredService("host-abc", "MyComputer", 47800);
        await service.TriggerScanAsync(CancellationToken.None);
        var snapshot = service.Snapshot;
        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var service = new NativeAdbWiredService("host-xyz", "Workstation", 47800);
        service.Start();
        await service.DisposeAsync();
        await service.DisposeAsync(); // Second dispose should not throw
    }
}
