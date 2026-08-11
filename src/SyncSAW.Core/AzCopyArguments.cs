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
            "--exclude-path=.syncsaw",
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

    public static IReadOnlyList<string> Copy(string source, string destination) =>
    [
        "copy",
        source,
        destination,
        "--overwrite=true",
        "--output-type=json",
        "--log-level=ERROR"
    ];

    public static IReadOnlyList<string> Remove(Uri blobUri) =>
    [
        "remove",
        blobUri.AbsoluteUri,
        "--output-type=json",
        "--log-level=ERROR"
    ];
}
