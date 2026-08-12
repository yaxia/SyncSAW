using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class SyncStateComparerTests
{
    [Fact]
    public void Reconcile_PrefersAzCopyPlanForPendingState()
    {
        var local = new[]
        {
            new LocalFileInfo("pending.txt", DateTimeOffset.UtcNow, 10),
            new LocalFileInfo("same.txt", DateTimeOffset.UtcNow, 20)
        };
        var remote = new[]
        {
            new RemoteBlobInfo("pending.txt", DateTimeOffset.UtcNow, 8),
            new RemoteBlobInfo("same.txt", DateTimeOffset.UtcNow, 20, SyncedToSaw: true),
            new RemoteBlobInfo("remote.txt", DateTimeOffset.UtcNow, 30)
        };
        var plan = new[] { new PlannedTransfer("pending.txt", "copy") };

        var result = SyncStateComparer.Reconcile(local, remote, plan);

        Assert.Equal(SyncItemState.Pending, result.Single(item => item.Path == "pending.txt").State);
        Assert.Equal(SyncItemState.InSync, result.Single(item => item.Path == "same.txt").State);
        Assert.True(result.Single(item => item.Path == "same.txt").SyncedToSaw);
        Assert.Equal(SyncItemState.RemoteOnly, result.Single(item => item.Path == "remote.txt").State);
    }

    [Fact]
    public void MetadataMatches_UsesSizeAndTimestampTolerance()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-10T00:00:00Z");
        var local = new LocalFileInfo("file.txt", timestamp, 10);

        Assert.True(SyncStateComparer.MetadataMatches(
            local,
            new RemoteBlobInfo("file.txt", timestamp.AddSeconds(1), 10)));
        Assert.False(SyncStateComparer.MetadataMatches(
            local,
            new RemoteBlobInfo("file.txt", timestamp.AddSeconds(1), 11)));
        Assert.False(SyncStateComparer.MetadataMatches(
            local,
            new RemoteBlobInfo("file.txt", timestamp.AddSeconds(5), 10)));
    }

    [Fact]
    public void SyncItem_ExposesLastModifiedInMachineLocalTime()
    {
        var utc = DateTimeOffset.Parse("2026-08-12T05:00:00Z");
        var item = new SyncItem(
            "file.txt",
            SyncItemState.InSync,
            utc,
            10,
            "None",
            null);

        Assert.Equal(utc.ToLocalTime(), item.LocalLastModified);
        Assert.Equal(TimeZoneInfo.Local.GetUtcOffset(utc), item.LocalLastModified?.Offset);
    }

}
