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
    public void GetPackageContainerName_UsesSeparateValidContainer()
    {
        Assert.Equal("container-package", ClusterPackage.GetPackageContainerName("container"));
        Assert.Throws<InvalidOperationException>(() =>
            ClusterPackage.GetPackageContainerName(new string('a', 56)));
    }

    [Fact]
    public void GetResultsContainerName_UsesNormalSyncContainer()
    {
        Assert.Equal("sync", ClusterPackage.GetResultsContainerName(" sync "));
    }

    [Fact]
    public async Task GetResultBlobPrefix_ReadsAndValidatesTaskConfiguration()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.ResultPrefix.");
        try
        {
            Assert.Equal(
                ClusterPackage.DefaultResultBlobPrefix,
                ClusterPackage.GetResultBlobPrefix(directory.FullName));
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, ClusterPackage.TaskConfigurationName),
                """{"ResultPrefix":"RocksDB-SPDIPerf"}""");
            Assert.Equal(
                "RocksDB-SPDIPerf",
                ClusterPackage.GetResultBlobPrefix(directory.FullName));
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, ClusterPackage.TaskConfigurationName),
                """{"ResultPrefix":"bad prefix"}""");
            Assert.Throws<InvalidDataException>(
                () => ClusterPackage.GetResultBlobPrefix(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void TaskScript_IsExcludedOnlyFromNormalSync()
    {
        Assert.True(ClusterPackage.IsNormalSyncExcludedPath("task.ps1"));
        Assert.True(ClusterPackage.IsNormalSyncExcludedPath(@"\TASK.PS1"));
        Assert.True(ClusterPackage.IsNormalSyncExcludedPath("task.config.json"));
        Assert.False(ClusterPackage.IsReservedPath("task.ps1"));
        Assert.False(ClusterPackage.IsNormalSyncExcludedPath("tools/task.ps1"));
    }

    [Fact]
    public async Task PayloadFingerprint_IgnoresDownloadedClusterResults()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.ClusterPackage.");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory.FullName, ClusterPackage.TaskScriptName),
                "exit 0");
            var publisher = new ClusterPackagePublisher(new PackageRunner(
                string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7),
                Path.Combine(directory.FullName, "unused.zip")));
            var initial = publisher.GetPayloadFingerprint(directory.FullName);
            var results = Directory.CreateDirectory(
                Path.Combine(directory.FullName, "cluster-results"));
            await File.WriteAllTextAsync(Path.Combine(results.FullName, "result.zip"), "result");

            Assert.Equal(initial, publisher.GetPayloadFingerprint(directory.FullName));

            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "payload.txt"), "payload");
            Assert.NotEqual(initial, publisher.GetPayloadFingerprint(directory.FullName));

            var payloadPath = Path.Combine(directory.FullName, "payload.txt");
            var preservedTime = DateTime.UtcNow.AddMinutes(-1);
            await File.WriteAllTextAsync(payloadPath, "alpha");
            File.SetLastWriteTimeUtc(payloadPath, preservedTime);
            var sameMetadataFingerprint = publisher.GetPayloadFingerprint(directory.FullName);
            await File.WriteAllTextAsync(payloadPath, "bravo");
            File.SetLastWriteTimeUtc(payloadPath, preservedTime);

            Assert.NotEqual(
                sameMetadataFingerprint,
                publisher.GetPayloadFingerprint(directory.FullName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PublishAsync_CreatesPackageWithRefreshedReadOnlySasAndUploadsIt()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.ClusterPackage.");
        var capturedArchive = Path.Combine(directory.FullName, "captured.zip");
        try
        {
            var source = Directory.CreateDirectory(Path.Combine(directory.FullName, "source"));
            await File.WriteAllTextAsync(
                Path.Combine(source.FullName, ClusterPackage.TaskScriptName),
                "exit 0");
            await File.WriteAllTextAsync(
                Path.Combine(source.FullName, ClusterPackage.TaskConfigurationName),
                "{}");
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
            Directory.CreateDirectory(Path.Combine(source.FullName, "cluster-results"));
            await File.WriteAllTextAsync(
                Path.Combine(source.FullName, "cluster-results", "old.zip"),
                "old result");
            var pdiDirectory = Directory.CreateDirectory(Path.Combine(source.FullName, "PDITest"));
            await File.WriteAllTextAsync(Path.Combine(pdiDirectory.FullName, "PDI.zip"), "PDI");
            await File.WriteAllTextAsync(Path.Combine(pdiDirectory.FullName, "spdi.zip"), "SPDI");

            var now = DateTimeOffset.Parse("2026-08-27T05:04:03.456Z");
            var expectedStart = DateTimeOffset.Parse("2026-08-27T04:59:03Z");
            var expectedExpiry = expectedStart.AddDays(7);
            var sasUri =
                "https://account123.blob.core.windows.net/container-package/cluster_package.zip" +
                $"?st={Uri.EscapeDataString(expectedStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                $"&se={Uri.EscapeDataString(expectedExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                "&sp=r&spr=https&sv=2026-04-06&sr=b&skoid=00000000-0000-0000-0000-000000000001" +
                "&sktid=00000000-0000-0000-0000-000000000002" +
                $"&skt={Uri.EscapeDataString(expectedStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                $"&ske={Uri.EscapeDataString(expectedExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                "&sks=b&skv=2026-04-06" +
                "&sig=secret";
            var runner = new PackageRunner(
                sasUri,
                expectedStart,
                expectedExpiry,
                capturedArchive);
            var publisher = new ClusterPackagePublisher(runner);
            var settings = CreateSettings(directory, source.FullName);

            var result = await publisher.PublishAsync(settings, now, CancellationToken.None);

            Assert.Equal(3, result.PayloadFileCount);
            Assert.Equal(expectedExpiry, result.SasExpiresUtc);
            Assert.Equal(
                ["storage", "container", "create"],
                runner.Calls[0].Arguments.Skip(2).Take(3));
            Assert.Contains("container-package", runner.Calls[0].Arguments);
            Assert.Contains("off", runner.Calls[0].Arguments);

            Assert.Equal(
                ["storage", "container", "show"],
                runner.Calls[1].Arguments.Skip(2).Take(3));
            Assert.Contains("properties.publicAccess", runner.Calls[1].Arguments);

            Assert.Equal(
                ["storage", "container", "create"],
                runner.Calls[2].Arguments.Skip(2).Take(3));
            Assert.Contains("container", runner.Calls[2].Arguments);

            Assert.Equal(AzCopyProcessMode.SensitiveCaptured, runner.Calls[3].Mode);
            var sasArguments = runner.Calls[3].Arguments;
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

            Assert.Equal(AzCopyProcessMode.SensitiveCaptured, runner.Calls[4].Mode);
            var resultsArguments = runner.Calls[4].Arguments;
            var resultsArgumentArray = resultsArguments.ToArray();
            Assert.Equal(
                "c",
                resultsArguments[Array.IndexOf(resultsArgumentArray, "--permissions") + 1]);
            Assert.Contains("container", resultsArguments);
            Assert.Contains(
                resultsArguments,
                argument => argument.StartsWith("cluster-results/") && argument.EndsWith(".zip"));

            Assert.Equal("copy", runner.Calls[5].Arguments[0]);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container-package/cluster_package.zip",
                runner.Calls[5].Arguments[2]);
            Assert.Equal("AZCLI", runner.Calls[5].Environment?["AZCOPY_AUTO_LOGIN_TYPE"]);

            using var archive = ZipFile.OpenRead(capturedArchive);
            var names = archive.Entries.Select(entry => entry.FullName).ToArray();
            Assert.Contains(ClusterPackage.TaskScriptName, names);
            Assert.Contains(ClusterPackage.TaskConfigurationName, names);
            Assert.Contains("data/payload.txt", names);
            Assert.Contains(ClusterPackage.ConfigurationEntryName, names);
            Assert.DoesNotContain(".syncsaw/private.txt", names);
            Assert.DoesNotContain("cluster-results/old.zip", names);
            Assert.DoesNotContain("PDITest/PDI.zip", names);
            Assert.DoesNotContain("PDITest/spdi.zip", names);
            Assert.Equal(1, names.Count(name =>
                name.Equals(ClusterPackage.ConfigurationEntryName, StringComparison.OrdinalIgnoreCase)));
            var configEntry = Assert.Single(archive.Entries.Where(entry =>
                entry.FullName == ClusterPackage.ConfigurationEntryName));
            using var config = JsonDocument.Parse(configEntry.Open());
            Assert.Equal(
                ClusterPackage.ConfigurationSchemaVersion,
                config.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal(
                sasUri,
                config.RootElement.GetProperty("PackageUri").GetString());
            Assert.Equal(
                runner.GeneratedResultsSasUri,
                config.RootElement.GetProperty("ResultsBlobUri").GetString());
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task PublishAsync_RejectsPublicPackageContainer()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.ClusterPackage.");
        try
        {
            var runner = new PackageRunner(
                string.Empty,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7),
                Path.Combine(directory.FullName, "unused.zip"),
                "container");
            var publisher = new ClusterPackagePublisher(runner);
            var settings = CreateSettings(directory, directory.FullName);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                publisher.PublishAsync(settings, DateTimeOffset.UtcNow, CancellationToken.None));

            Assert.Contains("allows public 'container' access", exception.Message);
            Assert.Equal(2, runner.Calls.Count);
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
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7),
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
        DateTimeOffset sasStart,
        DateTimeOffset sasExpiry,
        string capturedArchive,
        string packagePublicAccess = "") : IAzCopyRunner
    {
        public List<Call> Calls { get; } = [];
        public string? GeneratedResultsSasUri { get; private set; }

        public Task<AzCopyCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            AzCopyProcessMode mode = AzCopyProcessMode.Captured,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Calls.Add(new Call(arguments, mode, environmentVariables));
            if (arguments.Contains("generate-sas") &&
                arguments.Contains(ClusterPackage.BlobName))
            {
                return Task.FromResult(new AzCopyCommandResult(0, sasUri, string.Empty));
            }
            if (arguments.Contains("generate-sas"))
            {
                var values = arguments.ToArray();
                var container = arguments[Array.IndexOf(values, "--container-name") + 1];
                var blobName = arguments[Array.IndexOf(values, "--name") + 1];
                GeneratedResultsSasUri =
                    $"https://account123.blob.core.windows.net/{container}/{blobName}" +
                    $"?st={Uri.EscapeDataString(sasStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                    $"&se={Uri.EscapeDataString(sasExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                    "&sp=c&spr=https&sv=2026-04-06&sr=b" +
                    "&skoid=00000000-0000-0000-0000-000000000001" +
                    "&sktid=00000000-0000-0000-0000-000000000002" +
                    $"&skt={Uri.EscapeDataString(sasStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                    $"&ske={Uri.EscapeDataString(sasExpiry.ToString("yyyy-MM-ddTHH:mm:ssZ"))}" +
                    "&sks=b&skv=2026-04-06&sig=results-secret";
                return Task.FromResult(
                    new AzCopyCommandResult(0, GeneratedResultsSasUri, string.Empty));
            }
            if (arguments.Contains("show"))
            {
                return Task.FromResult(
                    new AzCopyCommandResult(0, packagePublicAccess, string.Empty));
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
