namespace SyncSAW.Core;

public static class AzCopyArguments
{
    public static IReadOnlyList<string> Login(string? tenantId)
    {
        var arguments = new List<string> { "login", "--login-type=DEVICE" };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            arguments.Add("--tenant-id");
            arguments.Add(tenantId.Trim());
        }

        return arguments;
    }

    public static IReadOnlyList<string> AzureCliLogin(string? tenantId)
    {
        var arguments = new List<string> { "login", "--allow-no-subscriptions", "--output", "none" };
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            arguments.Add("--tenant");
            arguments.Add(tenantId.Trim());
        }

        return arguments;
    }

    public static IReadOnlyList<string> AzureCliSelectSubscription(string subscriptionId) =>
    [
        "account",
        "set",
        "--subscription",
        subscriptionId.Trim()
    ];

    public static IReadOnlyList<string> AzureCliGenerateBlobReadSas(
        string account,
        string container,
        string blobName,
        DateTimeOffset startsUtc,
        DateTimeOffset expiresUtc) =>
    [
        "storage",
        "blob",
        "generate-sas",
        "--account-name",
        StorageEndpoint.NormalizeAccount(account),
        "--container-name",
        StorageEndpoint.NormalizeContainer(container),
        "--name",
        StorageEndpoint.NormalizeBlobPath(blobName),
        "--permissions",
        "r",
        "--start",
        startsUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        "--expiry",
        expiresUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        "--https-only",
        "--auth-mode",
        "login",
        "--as-user",
        "--full-uri",
        "--output",
        "tsv"
    ];

    public static IReadOnlyList<string> AzureCliGenerateBlobCreateSas(
        string account,
        string container,
        string blobName,
        DateTimeOffset startsUtc,
        DateTimeOffset expiresUtc) =>
    [
        "storage",
        "blob",
        "generate-sas",
        "--account-name",
        StorageEndpoint.NormalizeAccount(account),
        "--container-name",
        StorageEndpoint.NormalizeContainer(container),
        "--name",
        StorageEndpoint.NormalizeBlobPath(blobName),
        "--permissions",
        "c",
        "--start",
        startsUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        "--expiry",
        expiresUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        "--https-only",
        "--auth-mode",
        "login",
        "--as-user",
        "--full-uri",
        "--output",
        "tsv"
    ];

    public static IReadOnlyList<string> AzureCliEnsureContainer(
        string account,
        string container) =>
    [
        "storage",
        "container",
        "create",
        "--account-name",
        StorageEndpoint.NormalizeAccount(account),
        "--name",
        StorageEndpoint.NormalizeContainer(container),
        "--auth-mode",
        "login",
        "--output",
        "none"
    ];

    public static IReadOnlyList<string> AzureCliCreatePrivateContainer(
        string account,
        string container) =>
    [
        "storage",
        "container",
        "create",
        "--account-name",
        StorageEndpoint.NormalizeAccount(account),
        "--name",
        StorageEndpoint.NormalizeContainer(container),
        "--public-access",
        "off",
        "--auth-mode",
        "login",
        "--output",
        "none"
    ];

    public static IReadOnlyList<string> AzureCliGetContainerPublicAccess(
        string account,
        string container) =>
    [
        "storage",
        "container",
        "show",
        "--account-name",
        StorageEndpoint.NormalizeAccount(account),
        "--name",
        StorageEndpoint.NormalizeContainer(container),
        "--auth-mode",
        "login",
        "--query",
        "properties.publicAccess",
        "--output",
        "tsv"
    ];

    public static IReadOnlyList<string> List(Uri containerUri) =>
    [
        "list",
        containerUri.AbsoluteUri,
        "--properties=LastModifiedTime",
        "--machine-readable",
        "--output-type=json",
        "--log-level=ERROR"
    ];

    public static IReadOnlyList<string> MakeContainer(Uri containerUri) =>
    [
        "make",
        containerUri.AbsoluteUri,
        "--output-type=json",
        "--log-level=ERROR"
    ];

    public static IReadOnlyList<string> PlanUpload(
        string localFolder,
        Uri containerUri,
        bool deleteDestination)
    {
        var arguments = Upload(localFolder, containerUri, deleteDestination).ToList();
        arguments[arguments.IndexOf("--output-type=json")] = "--output-type=text";
        arguments.Add("--dry-run");
        return arguments;
    }

    public static IReadOnlyList<string> Upload(
        string localFolder,
        Uri containerUri,
        bool deleteDestination)
    {
        var localPath = Path.GetFullPath(localFolder);

        return
        [
            "sync",
            localPath,
            containerUri.AbsoluteUri,
            "--recursive=true",
            $"--exclude-path=.syncsaw;{ClusterPackage.BlobName};" +
            $"{ClusterPackage.ConfigurationEntryName};{ClusterPackage.TaskScriptName};" +
            $"{ClusterPackage.TaskConfigurationName}",
            $"--delete-destination={deleteDestination.ToString().ToLowerInvariant()}",
            "--output-type=json",
            "--log-level=ERROR"
        ];
    }

    public static IReadOnlyList<string> DownloadMissingFile(
        Uri source,
        string destination) =>
    [
        "copy",
        source.AbsoluteUri,
        Path.GetFullPath(destination),
        "--overwrite=false",
        "--preserve-last-modified-time=true",
        "--output-type=json",
        "--log-level=ERROR"
    ];

    public static IReadOnlyList<string> Copy(
        string source,
        string destination,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var arguments = new List<string>
        {
            "copy",
            source,
            destination,
            "--overwrite=true",
            "--output-type=json",
            "--log-level=ERROR"
        };
        if (metadata is not null && metadata.Count > 0)
        {
            arguments.Add("--metadata");
            arguments.Add(string.Join(
                ';',
                metadata
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => $"{item.Key}={item.Value}")));
        }
        return arguments;
    }

    public static IReadOnlyList<string> Remove(Uri blobUri) =>
    [
        "remove",
        blobUri.AbsoluteUri,
        "--output-type=json",
        "--log-level=ERROR"
    ];
}
