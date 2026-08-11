using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class SawSyncFlagTests
{
    [Fact]
    public void GetMarkerPath_IsDeterministicAndCaseSensitive()
    {
        var first = SawSyncFlag.GetMarkerPath("Reports/August.xlsx");
        var repeat = SawSyncFlag.GetMarkerPath(@"Reports\August.xlsx");
        var differentCase = SawSyncFlag.GetMarkerPath("reports/August.xlsx");

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, differentCase);
        Assert.StartsWith(SawSyncFlag.Prefix, first);
        Assert.EndsWith(SawSyncFlag.Extension, first);
        Assert.StartsWith(
            SawSyncFlag.DeletionPrefix,
            SawSyncFlag.GetDeletionMarkerPath("Reports/August.xlsx"));
        Assert.EndsWith(
            SawSyncFlag.DeletionExtension,
            SawSyncFlag.GetDeletionMarkerPath("Reports/August.xlsx"));
    }

    [Fact]
    public void ApplyMarkers_HidesInternalBlobsAndUsesMarkerFreshness()
    {
        var blobTime = DateTimeOffset.Parse("2026-08-10T01:00:00Z");
        var syncedPath = "Reports/synced.txt";
        var stalePath = "Reports/stale.txt";
        var listed = new[]
        {
            new RemoteBlobInfo(syncedPath, blobTime, 10),
            new RemoteBlobInfo(stalePath, blobTime.AddMinutes(2), 20),
            new RemoteBlobInfo(
                SawSyncFlag.GetDeletionMarkerPath("Reports/deleted.txt"),
                blobTime.AddMinutes(2),
                19),
            new RemoteBlobInfo(
                SawSyncFlag.GetMarkerPath(syncedPath),
                blobTime.AddMinutes(1),
                10),
            new RemoteBlobInfo(
                SawSyncFlag.GetMarkerPath(stalePath),
                blobTime.AddMinutes(1),
                10)
        };

        var result = SawSyncFlag.ApplyMarkers(listed);

        Assert.Equal(2, result.Count);
        Assert.True(result.Single(item => item.Path == syncedPath).SyncedToSaw);
        Assert.False(result.Single(item => item.Path == stalePath).SyncedToSaw);
        Assert.DoesNotContain(result, item => SawSyncFlag.IsMarker(item.Path));
        Assert.DoesNotContain(result, item => SawSyncFlag.IsInternal(item.Path));
    }

    [Fact]
    public void ApplyMarkers_IgnoresMalformedInternalMarker()
    {
        var result = SawSyncFlag.ApplyMarkers(
        [
            new RemoteBlobInfo("file.txt", DateTimeOffset.UtcNow, 1),
            new RemoteBlobInfo($"{SawSyncFlag.Prefix}not-a-hash.flag", DateTimeOffset.UtcNow, 1)
        ]);

        var file = Assert.Single(result);
        Assert.Equal("file.txt", file.Path);
        Assert.False(file.SyncedToSaw);
    }
}
