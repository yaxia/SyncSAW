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
        Assert.Contains("--exclude-path=.syncsaw", arguments);
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
