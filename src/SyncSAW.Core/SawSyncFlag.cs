using System.Security.Cryptography;
using System.Text;

namespace SyncSAW.Core;

public static class SawSyncFlag
{
    public const string Prefix = ".syncsaw/saw-flags/";
    public const string Extension = ".flag";
    public const string DeletionPrefix = ".syncsaw/deletions/";
    public const string DeletionExtension = ".delete";

    public static string GetMarkerPath(string blobPath)
    {
        var normalized = NormalizePath(blobPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Blob path must not be empty.", nameof(blobPath));
        }

        return $"{Prefix}{GetPathHash(normalized)}{Extension}";
    }

    public static string GetDeletionMarkerPath(string blobPath)
    {
        var normalized = NormalizePath(blobPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Blob path must not be empty.", nameof(blobPath));
        }

        return $"{DeletionPrefix}{GetPathHash(normalized)}{DeletionExtension}";
    }

    public static IReadOnlyList<RemoteBlobInfo> ApplyMarkers(
        IReadOnlyList<RemoteBlobInfo> listedBlobs)
    {
        var markerTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var blob in listedBlobs)
        {
            if (!TryGetMarkerHash(blob.Path, out var hash) || blob.LastModified is null)
            {
                continue;
            }

            if (!markerTimes.TryGetValue(hash, out var current) || blob.LastModified > current)
            {
                markerTimes[hash] = blob.LastModified.Value;
            }
        }

        return listedBlobs
            .Where(blob => !IsInternal(blob.Path))
            .Select(blob =>
            {
                var markerHash = Path.GetFileNameWithoutExtension(GetMarkerPath(blob.Path));
                var isSynced = blob.LastModified is not null &&
                               markerTimes.TryGetValue(markerHash, out var markerTime) &&
                               markerTime >= blob.LastModified.Value;
                return blob with { SyncedToSaw = isSynced };
            })
            .ToArray();
    }

    public static bool IsMarker(string blobPath) =>
        NormalizePath(blobPath).StartsWith(Prefix, StringComparison.Ordinal);

    public static bool IsInternal(string blobPath)
    {
        var normalized = NormalizePath(blobPath);
        return normalized.StartsWith(Prefix, StringComparison.Ordinal) ||
               normalized.StartsWith(DeletionPrefix, StringComparison.Ordinal);
    }

    private static bool TryGetMarkerHash(string blobPath, out string hash)
    {
        var normalized = NormalizePath(blobPath);
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal) ||
            !normalized.EndsWith(Extension, StringComparison.Ordinal))
        {
            hash = string.Empty;
            return false;
        }

        hash = normalized[Prefix.Length..^Extension.Length];
        return hash.Length == 64 && hash.All(Uri.IsHexDigit);
    }

    private static string NormalizePath(string value) =>
        (value ?? string.Empty).Trim().Trim('"').Replace('\\', '/').TrimStart('/');

    private static string GetPathHash(string normalizedPath)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
