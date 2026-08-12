namespace SyncSAW.Core;

public static class SyncStateComparer
{
    public static IReadOnlyList<SyncItem> Reconcile(
        IReadOnlyList<LocalFileInfo> localFiles,
        IReadOnlyList<RemoteBlobInfo> remoteBlobs,
        IReadOnlyList<PlannedTransfer> plan)
    {
        var local = localFiles.ToDictionary(item => Normalize(item.Path), StringComparer.OrdinalIgnoreCase);
        var remote = remoteBlobs.ToDictionary(item => Normalize(item.Path), StringComparer.OrdinalIgnoreCase);
        var planned = plan.ToDictionary(item => Normalize(item.Path), StringComparer.OrdinalIgnoreCase);
        var allPaths = local.Keys.Concat(remote.Keys).Concat(planned.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        return allPaths.Select(path =>
        {
            local.TryGetValue(path, out var localItem);
            remote.TryGetValue(path, out var remoteItem);
            if (planned.TryGetValue(path, out var plannedItem))
            {
                return new SyncItem(
                    path,
                    SyncItemState.Pending,
                    localItem?.LastModified ?? remoteItem?.LastModified,
                    localItem?.Size ?? remoteItem?.Size,
                    plannedItem.Action,
                    null,
                    remoteItem?.SyncedToSaw ?? false);
            }

            if (localItem is not null && remoteItem is not null)
            {
                return new SyncItem(
                    path,
                    SyncItemState.InSync,
                    localItem.LastModified,
                    localItem.Size,
                    "None",
                    null,
                    remoteItem.SyncedToSaw);
            }

            if (localItem is not null)
            {
                return new SyncItem(
                    path,
                    SyncItemState.LocalOnly,
                    localItem.LastModified,
                    localItem.Size,
                    "Upload",
                    null,
                    false);
            }

            return new SyncItem(
                path,
                SyncItemState.RemoteOnly,
                remoteItem?.LastModified,
                remoteItem?.Size,
                "Download",
                null,
                remoteItem?.SyncedToSaw ?? false);
        }).ToArray();
    }

    public static bool MetadataMatches(
        LocalFileInfo local,
        RemoteBlobInfo remote,
        TimeSpan? timestampTolerance = null)
    {
        if (remote.Size is not null && local.Size != remote.Size)
        {
            return false;
        }

        if (remote.LastModified is null)
        {
            return remote.Size is not null;
        }

        var tolerance = timestampTolerance ?? TimeSpan.FromSeconds(2);
        return (local.LastModified.ToUniversalTime() - remote.LastModified.Value.ToUniversalTime()).Duration() <= tolerance;
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}
