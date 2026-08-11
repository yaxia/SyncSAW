using System.Text.RegularExpressions;

namespace SyncSAW.Core;

public static partial class StorageEndpoint
{
    [GeneratedRegex("^[a-z0-9]{3,24}$", RegexOptions.CultureInvariant)]
    private static partial Regex AccountRegex();

    [GeneratedRegex("^(?!.*--)[a-z0-9](?:[a-z0-9-]{1,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerRegex();

    public static string NormalizeAccount(string value)
    {
        var account = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (account.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase))
        {
            account = account[..^".blob.core.windows.net".Length];
        }

        if (!AccountRegex().IsMatch(account))
        {
            throw new ArgumentException(
                "Storage account names must contain 3-24 lowercase letters or numbers.",
                nameof(value));
        }

        return account;
    }

    public static string NormalizeContainer(string value)
    {
        var container = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (container.Length is < 3 or > 63 || !ContainerRegex().IsMatch(container))
        {
            throw new ArgumentException(
                "Container names must contain 3-63 lowercase letters, numbers, or single hyphens.",
                nameof(value));
        }

        return container;
    }

    public static Uri BuildContainerUri(string account, string container)
    {
        var normalizedAccount = NormalizeAccount(account);
        var normalizedContainer = NormalizeContainer(container);
        return new Uri($"https://{normalizedAccount}.blob.core.windows.net/{normalizedContainer}");
    }

    public static Uri BuildBlobUri(string account, string container, string blobPath)
    {
        var containerUri = BuildContainerUri(account, container);
        var segments = NormalizeBlobPath(blobPath)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return new Uri($"{containerUri.AbsoluteUri}/{string.Join("/", segments)}");
    }

    public static string NormalizeBlobPath(string value)
    {
        var path = (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(path) ||
            path.Split('/').Any(segment => segment is "." or ".." || segment.Length == 0))
        {
            throw new ArgumentException("Blob paths must be non-empty relative paths without traversal.", nameof(value));
        }

        return path;
    }
}
