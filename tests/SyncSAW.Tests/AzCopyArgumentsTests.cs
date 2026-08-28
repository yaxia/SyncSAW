using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class AzCopyArgumentsTests
{
    private static readonly Uri ContainerUri =
        new("https://account123.blob.core.windows.net/container");

    [Fact]
    public void Upload_UsesSeparateSourceAndDestinationArguments()
    {
        var arguments = AzCopyArguments.Upload(
            @"C:\Data Folder & Reports",
            ContainerUri,
            deleteDestination: false);

        Assert.Equal("sync", arguments[0]);
        Assert.Equal(Path.GetFullPath(@"C:\Data Folder & Reports"), arguments[1]);
        Assert.Equal(ContainerUri.AbsoluteUri, arguments[2]);
        Assert.Contains("--delete-destination=false", arguments);
        Assert.Contains(
            "--exclude-path=.syncsaw;cluster_package.zip;cluster_package.config;" +
            "task.ps1;task.config.json",
            arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains('"'));
    }

    [Fact]
    public void DownloadMissingFile_MapsBlobToExactLocalPathAndPreservesTime()
    {
        var source = new Uri($"{ContainerUri.AbsoluteUri}/folder/report.txt");
        var arguments = AzCopyArguments.DownloadMissingFile(
            source,
            @"C:\Mirror\folder\report.txt");

        Assert.Equal("copy", arguments[0]);
        Assert.Equal(source.AbsoluteUri, arguments[1]);
        Assert.Equal(Path.GetFullPath(@"C:\Mirror\folder\report.txt"), arguments[2]);
        Assert.Contains("--overwrite=false", arguments);
        Assert.Contains("--preserve-last-modified-time=true", arguments);
        Assert.DoesNotContain("--recursive=true", arguments);
    }

    [Fact]
    public void PlanUpload_AddsDryRunWithoutChangingTransferArguments()
    {
        var arguments = AzCopyArguments.PlanUpload(
            @"C:\Mirror",
            ContainerUri,
            deleteDestination: true);

        Assert.Equal("--dry-run", arguments[^1]);
        Assert.Contains("--delete-destination=true", arguments);
        Assert.Contains("--output-type=text", arguments);
    }

    [Fact]
    public void Login_PreservesTenantAsSingleArgument()
    {
        var arguments = AzCopyArguments.Login("tenant value; not a shell command");

        Assert.Equal(
            ["login", "--login-type=DEVICE", "--tenant-id", "tenant value; not a shell command"],
            arguments);
    }

    [Fact]
    public void List_UsesSupportedMachineReadableFlags()
    {
        var arguments = AzCopyArguments.List(ContainerUri);

        Assert.Contains("--machine-readable", arguments);
        Assert.Contains("--properties=LastModifiedTime", arguments);
        Assert.DoesNotContain("--recursive=true", arguments);
    }

    [Fact]
    public void Copy_ExplicitlyOverwritesForManualFileManagement()
    {
        var arguments = AzCopyArguments.Copy(
            @"C:\file.txt",
            "https://account123.blob.core.windows.net/container/file.txt");

        Assert.Contains("--overwrite=true", arguments);
    }

    [Fact]
    public void AzureCliLogin_UsesTenantAsASeparateArgument()
    {
        var arguments = AzCopyArguments.AzureCliLogin("tenant-id");

        Assert.Equal(
            ["login", "--allow-no-subscriptions", "--output", "none", "--tenant", "tenant-id"],
            arguments);
    }

    [Fact]
    public void AzureCliSelectSubscription_UsesSeparateArgument()
    {
        var arguments = AzCopyArguments.AzureCliSelectSubscription(" subscription-id ");

        Assert.Equal(["account", "set", "--subscription", "subscription-id"], arguments);
    }

    [Fact]
    public void SyncSettings_HaveScopedCorporateDefaults()
    {
        var settings = new SyncSettings();

        Assert.Equal("72f988bf-86f1-41af-91ab-2d7cd011db47", settings.TenantId);
        Assert.Equal("a0d901ba-9956-4f7d-830c-2d7974c36666", settings.SubscriptionId);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(10, settings.AutoSyncIntervalSeconds);
        Assert.False(settings.PauseSync);
        Assert.False(settings.PublishClusterPackage);
    }

    [Fact]
    public void AzureCliGenerateBlobReadSas_UsesSevenDayReadOnlyBlobScope()
    {
        var starts = DateTimeOffset.Parse("2026-08-27T01:00:00Z");
        var expires = starts.AddDays(7);

        var arguments = AzCopyArguments.AzureCliGenerateBlobReadSas(
            "account123",
            "container",
            ClusterPackage.BlobName,
            starts,
            expires);
        var argumentArray = arguments.ToArray();

        Assert.Equal("storage", arguments[0]);
        Assert.Equal("blob", arguments[1]);
        Assert.Equal("generate-sas", arguments[2]);
        Assert.Equal("r", arguments[Array.IndexOf(argumentArray, "--permissions") + 1]);
        Assert.Equal("2026-08-27T01:00:00Z", arguments[Array.IndexOf(argumentArray, "--start") + 1]);
        Assert.Equal("2026-09-03T01:00:00Z", arguments[Array.IndexOf(argumentArray, "--expiry") + 1]);
        Assert.Contains("--as-user", arguments);
        Assert.Contains("--full-uri", arguments);
        Assert.DoesNotContain("--account-key", arguments);
    }

    [Fact]
    public void AzureCliGenerateBlobCreateSas_UsesCreateOnlyBlobScope()
    {
        var starts = DateTimeOffset.Parse("2026-08-27T01:00:00Z");
        var expires = starts.AddDays(7);

        var arguments = AzCopyArguments.AzureCliGenerateBlobCreateSas(
            "account123",
            "container",
            "cluster-results/result.zip",
            starts,
            expires);
        var argumentArray = arguments.ToArray();

        Assert.Equal(["storage", "blob", "generate-sas"], arguments.Take(3));
        Assert.Equal("c", arguments[Array.IndexOf(argumentArray, "--permissions") + 1]);
        Assert.Contains("cluster-results/result.zip", arguments);
        Assert.Contains("--https-only", arguments);
        Assert.Contains("--as-user", arguments);
        Assert.Contains("--full-uri", arguments);
        Assert.DoesNotContain("--account-key", arguments);
    }

    [Fact]
    public void AzureCliCreatePrivateContainer_DisablesPublicAccessAtCreation()
    {
        var arguments = AzCopyArguments.AzureCliCreatePrivateContainer(
            "account123",
            "container-package");
        var argumentArray = arguments.ToArray();

        Assert.Equal(["storage", "container", "create"], arguments.Take(3));
        Assert.Contains("--public-access", arguments);
        Assert.Equal("off", arguments[Array.IndexOf(argumentArray, "--public-access") + 1]);
        Assert.Contains("--auth-mode", arguments);
        Assert.Contains("login", arguments);
    }

    [Fact]
    public void AzureCliGetContainerPublicAccess_UsesOAuthQuery()
    {
        var arguments = AzCopyArguments.AzureCliGetContainerPublicAccess(
            "account123",
            "container-package");

        Assert.Equal(["storage", "container", "show"], arguments.Take(3));
        Assert.Contains("properties.publicAccess", arguments);
        Assert.Contains("--auth-mode", arguments);
        Assert.Contains("login", arguments);
    }

    [Fact]
    public void AzureCliEnsureContainer_UsesLoginWithoutSecrets()
    {
        var arguments = AzCopyArguments.AzureCliEnsureContainer(
            "account123",
            "container-results");

        Assert.Equal(["storage", "container", "create"], arguments.Take(3));
        Assert.Contains("--auth-mode", arguments);
        Assert.Contains("login", arguments);
        Assert.Contains("container-results", arguments);
        Assert.DoesNotContain("--account-key", arguments);
    }

    [Fact]
    public void MakeContainer_UsesSafeMachineReadableArguments()
    {
        var arguments = AzCopyArguments.MakeContainer(ContainerUri);

        Assert.Equal("make", arguments[0]);
        Assert.Equal(ContainerUri.AbsoluteUri, arguments[1]);
        Assert.Contains("--output-type=json", arguments);
    }
}
