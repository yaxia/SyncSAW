using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class AzCopyRealOutputFixtureTests
{
    [Fact]
    public void ParsePlan_ReadsActualTextModeDryRunShape()
    {
        const string output = """
            INFO: Scanning...
            DRYRUN: copy https://account123.blob.core.windows.net/container/folder/report.csv
            DRYRUN: remove https://account123.blob.core.windows.net/container/old.txt
            """;

        var result = AzCopyOutputParser.ParsePlan(output);

        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal("folder/report.csv", item.Path);
                Assert.Equal("copy", item.Action, ignoreCase: true);
            },
            item =>
            {
                Assert.Equal("old.txt", item.Path);
                Assert.Equal("remove", item.Action, ignoreCase: true);
            });
    }
}
