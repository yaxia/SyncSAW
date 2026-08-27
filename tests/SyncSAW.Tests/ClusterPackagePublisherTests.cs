using System.IO.Compression;
using System.Text.Json;
using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class ClusterPackagePublisherTests
{
    [Fact]
    public void PayloadLimit_ReservesOneArchiveEntryForConfiguration()
    {
        Assert.Equal(9_999, ClusterPackage.MaximumPayloadFiles);
    }

    [Fact]
    public void GetResultsContainerName_UsesSeparateValidContainer()
    {
        Assert.Equal("container-results", ClusterPackage.GetResultsContainerName("container"));
        Assert.Throws<InvalidOperationException>(() =>
            ClusterPackage.GetResultsContainerName(new string('a', 56)));
    }

    [Fact]
    public async Task PublishAsync_CreatesPackageWithRefreshedReadOnlySasAndUploadsIt()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.ClusterPackage.");
        var capturedArchive = Path.Combine(directory.FullName, "captured.zip");
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(directory.FullName, "source"));
            await File.WriteAllTextAsync(Path.Combine(source.FullName, "run.ps1"), "exit 0");
            Directory.CreateDirectory(Path.Combine(source.FullName, "data"));
            await File.WriteAllTextAsync(
                Path.Combine(source.FullName, "data", "payload.txt"),
                "payload");
            Directory.CreateDirectory(Path.Combine(source.FullName, ".syncsaw"));
            await File.WriteAllTextAsync(
                Path.Combine(source.FullName, ".syncsaw", "private.txt"),
                "private");
            await File.WriteAllTextAsync(
                Path.Combine(source.FullName, ClusterPackage.BlobName),
                "reserved");
            await File.WriteAllTextAsync(
                Path.Combine(source.FullName, ClusterPackage.ConfigurationEntryName),
                "reserved");

            var now = DateTimeOffset.Parse("2026-08-27T05:04:03.456Z");
            var expectedStart = DateTimeOffset.Parse("2026-08-27T04:59:03Z");
            var expectedExpiry = expectedStart.AddDays(7);
            var sasUri =
                "https://account123.blob.core.windows.net/container/cluster_package.zip" +
                $"?st={Uri.EscapeDataString(expectedStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                $"&se={Uri.EscapeDataString(expectedExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                "&sp=r&spr=https&sv=2026-04-06&sr=b&skoid=00000000-0000-0000-0000-000000000001" +
                "&sktid=00000000-0000-0000-0000-000000000002" +
                $"&skt={Uri.EscapeDataString(expectedStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                $"&ske={Uri.EscapeDataString(expectedExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                "&sks=b&skv=2026-04-06" +
                "&sig=secret";
            var resultsSasUri =
                "https://account123.blob.core.windows.net/container-results" +
                $"?st={Uri.EscapeDataString(expectedStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                $"&se={Uri.EscapeDataString(expectedExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                "&sp=c&spr=https&sv=2026-04-06&sr=c&skoid=00000000-0000-0000-0000-000000000001" +
                "&sktid=00000000-0000-0000-0000-000000000002" +
                $"&skt={Uri.EscapeDataString(expectedStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                $"&ske={Uri.EscapeDataString(expectedExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                "&sks=b&skv=2026-04-06" +
                "&sig=results-secret";
            var resultsSasToken = new Uri(resultsSasUri).Query.TrimStart('?');
            var runner = new PackageRunner(sasUri, resultsSasToken, capturedArchive);
            var publisher = new ClusterPackagePublisher(runner);
            var settings = CreateSettings(directory, source.FullName);

            var result = await publisher.PublishAsync(settings, now, CancellationToken.None);

            Assert.Equal(2, result.PayloadFileCount);
            Assert.Equal(expectedExpiry, result.SasExpiresUtc);
            Assert.Equal(
                ["storage", "container", "create"],
                runner.Calls[0].Arguments.Skip(2).Take(3));
            Assert.Contains("container-results", runner.Calls[0].Arguments);

            Assert.Equal(AzCopyProcessMode.SensitiveCaptured, runner.Calls[1].Mode);
            var sasArguments = runner.Calls[1].Arguments;
            var sasArgumentArray = sasArguments.ToArray();
            Assert.Equal(
                "r",
                sasArguments[Array.IndexOf(sasArgumentArray, "--permissions") + 1]);
            Assert.Equal(
                "2026-08-27T04:59:03Z",
                sasArguments[Array.IndexOf(sasArgumentArray, "--start") + 1]);
            Assert.Equal(
                "2026-09-03T04:59:03Z",
                sasArguments[Array.IndexOf(sasArgumentArray, "--expiry") + 1]);

            Assert.Equal(AzCopyProcessMode.SensitiveCaptured, runner.Calls[2].Mode);
            var resultsArguments = runner.Calls[2].Arguments;
            var resultsArgumentArray = resultsArguments.ToArray();
            Assert.Equal(
                "c",
                resultsArguments[Array.IndexOf(resultsArgumentArray, "--permissions") + 1]);
            Assert.Contains("container-results", resultsArguments);

            Assert.Equal("copy", runner.Calls[3].Arguments[0]);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container/cluster_package.zip",
                runner.Calls[3].Arguments[2]);
            Assert.Equal("AZCLI", runner.Calls[3].Environment?["AZCOPY_AUTO_LOGIN_TYPE"]);

            using var archive = ZipFile.OpenRead(capturedArchive);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains("run.ps1", names);
            Assert.Contains("data/payload.txt", names);
            Assert.Contains(ClusterPackage.ConfigurationEntryName, names);
            Assert.DoesNotContain(".syncsaw/private.txt", names);
            Assert.Equal(1, names.Count(name =>
                name.Equals(ClusterPackage.ConfigurationEntryName, StringComparison.OrdinalIgnoreCase)));
            var configEntry = Assert.Single(archive.Entries.Where(entry =>
                entry.FullName == ClusterPackage.ConfigurationEntryName));
            using var config = JsonDocument.Parse(configEntry.Open());
            Assert.Equal(1, config.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(
                sasUri,
                config.RootElement.GetProperty("PackageUri").GetString());
            Assert.Equal(
                resultsSasUri,
                config.RootElement.GetProperty("ResultsContainerUri").GetString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PublishAsync_RejectsDeviceCodeBecauseItCannotGenerateDelegationSas()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.ClusterPackage.");
        try
        {
            var runner = new PackageRunner(
                string.Empty,
                string.Empty,
                Path.Combine(directory.FullName, "unused.zip"));
            var publisher = new ClusterPackagePublisher(runner);
            var settings = CreateSettings(directory, directory.FullName);
            settings.LoginMode = EntraLoginMode.DeviceCode;

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                publisher.PublishAsync(settings, DateTimeOffset.UtcNow, CancellationToken.None));

            Assert.Contains("Azure CLI / Windows broker", exception.Message);
            Assert.Empty(runner.Calls);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static SyncSettings CreateSettings(DirectoryInfo directory, string source)
    {
        var azCopy = Path.Combine(directory.FullName, "azcopy.exe");
        File.WriteAllText(azCopy, string.Empty);
        var cliDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "cli", "wbin"));
        var cli = Path.Combine(cliDirectory.FullName, "az.cmd");
        File.WriteAllText(cli, string.Empty);
        File.WriteAllText(Path.Combine(cliDirectory.Parent!.FullName, "python.exe"), string.Empty);
        return new SyncSettings
        {
            LocalFolder = source,
            StorageAccount = "account123",
            Container = "container",
            AzCopyPath = azCopy,
            AzureCliPath = cli,
            LoginMode = EntraLoginMode.AzureCli,
            TenantId = "tenant-id",
            PublishClusterPackage = true
        };
    }

    private sealed class PackageRunner(
        string sasUri,
        string resultsSasToken,
        string capturedArchive) : IAzCopyRunner
    {
        public List<Call> Calls { get; } = [];

        public Task<AzCopyCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            AzCopyProcessMode mode = AzCopyProcessMode.Captured,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Calls.Add(new Call(arguments, mode, environmentVariables));
            if (arguments.Contains("generate-sas") && arguments.Contains("blob"))
            {
                return Task.FromResult(new AzCopyCommandResult(0, sasUri, string.Empty));
            }
            if (arguments.Contains("generate-sas") && arguments.Contains("container"))
            {
                return Task.FromResult(new AzCopyCommandResult(0, resultsSasToken, string.Empty));
            }
            if (arguments.Contains("create"))
            {
                return Task.FromResult(new AzCopyCommandResult(0, "{}", string.Empty));
            }

            File.Copy(arguments[1], capturedArchive, overwrite: true);
            return Task.FromResult(new AzCopyCommandResult(0, "{}", string.Empty));
        }

        public void CancelAll()
        {
        }
    }

    private sealed record Call(
        IReadOnlyList<string> Arguments,
        AzCopyProcessMode Mode,
        IReadOnlyDictionary<string, string?>? Environment);
}
