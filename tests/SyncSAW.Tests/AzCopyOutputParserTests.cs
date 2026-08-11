using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class AzCopyOutputParserTests
{
    [Fact]
    public void ParsePlan_ReadsJsonLogPayloadAndDeduplicates()
    {
        const string output = """
            {"TimeStamp":"2026-08-10T00:00:00Z","MessageContent":"DRYRUN: copy folder/report.csv"}
            DRYRUN: remove old.txt
            DRYRUN: copy folder/report.csv
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

    [Fact]
    public void ParsePlan_ReadsStructuredMachineOutput()
    {
        const string output = """
            {"Transfers":[{"RelativePath":"alpha.txt","Operation":"Upload"}]}
            """;

        var item = Assert.Single(AzCopyOutputParser.ParsePlan(output));

        Assert.Equal("alpha.txt", item.Path);
        Assert.Equal("Upload", item.Action);
    }

    [Fact]
    public void ParsePlan_ExtractsRelativeBlobPathFromAzCopyDryRunMessage()
    {
        const string output =
            "DRYRUN: copy C:\\Source\\a report.txt to https://account123.blob.core.windows.net/container/folder/a%20report.txt";

        var item = Assert.Single(AzCopyOutputParser.ParsePlan(output));

        Assert.Equal("folder/a report.txt", item.Path);
    }

    [Fact]
    public void ParseRemoteList_ReadsStructuredAndMachineReadableLines()
    {
        const string output = """
            {"Items":[{"Path":"folder/a.txt","ContentLength":42,"LastModifiedTime":"2026-08-10T01:02:03Z"}]}
            File: b.txt; Content Length: 7; Last Modified: 2026-08-09T00:00:00Z
            """;

        var result = AzCopyOutputParser.ParseRemoteList(output);

        Assert.Equal(2, result.Count);
        Assert.Equal(42, result[1].Size);
        Assert.Equal("folder/a.txt", result[1].Path);
        Assert.Equal(DateTimeOffset.Parse("2026-08-10T01:02:03Z"), result[1].LastModified);
    }

    [Fact]
    public void ParseRemoteList_ReadsAzCopyEnvelopeWithStringContentLength()
    {
        const string output =
            """{"MessageType":"ListObject","MessageContent":"{\"Path\":\"folder/a.txt\",\"LastModifiedTime\":\"2026-08-10T01:02:03Z\",\"ContentLength\":\"42\"}"}""";

        var item = Assert.Single(AzCopyOutputParser.ParseRemoteList(output));

        Assert.Equal("folder/a.txt", item.Path);
        Assert.Equal(42, item.Size);
        Assert.Equal(DateTimeOffset.Parse("2026-08-10T01:02:03Z"), item.LastModified);
    }
}
