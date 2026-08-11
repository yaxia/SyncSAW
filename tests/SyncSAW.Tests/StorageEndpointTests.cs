using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class StorageEndpointTests
{
    [Fact]
    public void BuildContainerUri_NormalizesAccountEndpointAndContainer()
    {
        var uri = StorageEndpoint.BuildContainerUri(
            "  Contoso123.blob.core.windows.net ",
            " Reports-2026 ");

        Assert.Equal("https://contoso123.blob.core.windows.net/reports-2026", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("ab", "container")]
    [InlineData("UPPER_case", "container")]
    [InlineData("validaccount", "-container")]
    [InlineData("validaccount", "two--hyphens")]
    [InlineData("validaccount", "ab")]
    [InlineData("validaccount", "a")]
    public void BuildContainerUri_RejectsInvalidNames(string account, string container)
    {
        Assert.Throws<ArgumentException>(() => StorageEndpoint.BuildContainerUri(account, container));
    }

    [Fact]
    public void BuildBlobUri_EscapesEachPathSegment()
    {
        var uri = StorageEndpoint.BuildBlobUri("account123", "container", "folder/a report#.txt");

        Assert.Equal(
            "https://account123.blob.core.windows.net/container/folder/a%20report%23.txt",
            uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("folder/../secret.txt")]
    [InlineData("")]
    public void BuildBlobUri_RejectsTraversal(string path)
    {
        Assert.Throws<ArgumentException>(() =>
            StorageEndpoint.BuildBlobUri("account123", "container", path));
    }
}
