using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class AzCopyServiceTests
{
    [Fact]
    public async Task LoginAsync_UsesInteractiveProcessMode()
    {
        var runner = new RecordingRunner();
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        var azCopyPath = Path.Combine(temporaryDirectory.FullName, "azcopy.exe");
        await File.WriteAllTextAsync(azCopyPath, string.Empty);
        try
        {
            var settings = new SyncSettings
            {
                AzCopyPath = azCopyPath,
                TenantId = "tenant-id",
                LoginMode = EntraLoginMode.DeviceCode
            };

            await service.LoginAsync(settings, CancellationToken.None);

            Assert.Equal(AzCopyProcessMode.Interactive, runner.Mode);
            Assert.Equal(
                ["login", "--login-type=DEVICE", "--tenant-id", "tenant-id"],
                runner.Arguments);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoginAsync_UsesAzureCliShellForConditionalAccess()
    {
        var runner = new RecordingRunner();
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        var azureCliPath = CreateFakeAzureCli(temporaryDirectory);
        try
        {
            var settings = new SyncSettings
            {
                AzureCliPath = azureCliPath,
                TenantId = "tenant-id",
                SubscriptionId = null,
                LoginMode = EntraLoginMode.AzureCli
            };

            await service.LoginAsync(settings, CancellationToken.None);

            Assert.Equal(AzCopyProcessMode.Captured, runner.Mode);
            Assert.Equal(
                [
                    "-IBm",
                    "azure.cli",
                    "login",
                    "--allow-no-subscriptions",
                    "--output",
                    "none",
                    "--tenant",
                    "tenant-id"
                ],
                runner.Arguments);
            Assert.Equal("off", runner.EnvironmentVariables?["AZURE_CORE_LOGIN_EXPERIENCE_V2"]);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task LoginAsync_SelectsConfiguredSubscription()
    {
        var runner = new RecordingRunner();
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        var azureCliPath = CreateFakeAzureCli(temporaryDirectory);
        try
        {
            var settings = new SyncSettings
            {
                AzureCliPath = azureCliPath,
                TenantId = "tenant-id",
                SubscriptionId = "subscription-id",
                LoginMode = EntraLoginMode.AzureCli
            };

            await service.LoginAsync(settings, CancellationToken.None);

            Assert.Equal(
                ["-IBm", "azure.cli", "account", "set", "--subscription", "subscription-id"],
                runner.Arguments);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_PassesAzureCliTokenEnvironmentToAzCopy()
    {
        var runner = new RecordingRunner();
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                TenantId = "tenant-id",
                LoginMode = EntraLoginMode.AzureCli
            };

            await service.GetSnapshotAsync(settings, CancellationToken.None);

            Assert.Equal("AZCLI", runner.EnvironmentVariables?["AZCOPY_AUTO_LOGIN_TYPE"]);
            Assert.Equal("tenant-id", runner.EnvironmentVariables?["AZCOPY_TENANT_ID"]);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_CreatesAndVerifiesMissingContainer()
    {
        var runner = new QueuedRunner(
            new AzCopyCommandResult(1, string.Empty, "ContainerNotFound (404)"),
            new AzCopyCommandResult(0, string.Empty, string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            await service.GetSnapshotAsync(settings, CancellationToken.None);

            Assert.Equal(["list", "make", "list", "sync"], runner.Commands);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_HidesMarkersAndReportsCurrentSawStatus()
    {
        const string sourcePath = "folder/report.csv";
        var sourceTime = DateTimeOffset.Parse("2026-08-10T01:00:00Z");
        var markerPath = SawSyncFlag.GetMarkerPath(sourcePath);
        var listOutput =
            $$"""{"Items":[{"Path":"{{sourcePath}}","ContentLength":42,"LastModifiedTime":"{{sourceTime:O}}"},{"Path":"{{markerPath}}","ContentLength":0,"LastModifiedTime":"{{sourceTime.AddMinutes(1):O}}"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            var snapshot = await service.GetSnapshotAsync(settings, CancellationToken.None);

            var blob = Assert.Single(snapshot.RemoteBlobs);
            Assert.Equal(sourcePath, blob.Path);
            Assert.True(blob.SyncedToSaw);
            Assert.DoesNotContain(snapshot.Items, item => SawSyncFlag.IsMarker(item.Path));
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_PlansDownloadOnlyForCloudOnlyFiles()
    {
        const string listOutput =
            """{"Items":[{"Path":"cloud-only.txt","ContentLength":42,"LastModifiedTime":"2026-08-10T01:00:00Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            var snapshot = await service.GetSnapshotAsync(settings, CancellationToken.None);

            var transfer = Assert.Single(snapshot.Plan);
            Assert.Equal("cloud-only.txt", transfer.Path);
            Assert.Equal("Download", transfer.Action);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_PlansUploadWhenNewerBlobHasDifferentSize()
    {
        const string listOutput =
            """{"Items":[{"Path":"changed.txt","ContentLength":5,"LastModifiedTime":"2026-08-11T02:49:26Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty),
            new AzCopyCommandResult(0, "INFO: no changes", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var localPath = Path.Combine(temporaryDirectory.FullName, "changed.txt");
            await File.WriteAllTextAsync(localPath, "new content");
            File.SetLastWriteTimeUtc(
                localPath,
                DateTime.Parse("2026-08-11T02:40:58Z").ToUniversalTime());
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            var snapshot = await service.GetSnapshotAsync(settings, CancellationToken.None);

            var transfer = Assert.Single(snapshot.Plan);
            Assert.Equal("changed.txt", transfer.Path);
            Assert.Equal("Upload (local changed)", transfer.Action);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_PlansUploadWhenLocalTimestampIsNewer()
    {
        const string listOutput =
            """{"Items":[{"Path":"changed.txt","ContentLength":7,"LastModifiedTime":"2026-08-11T02:40:00Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty),
            new AzCopyCommandResult(0, "INFO: no changes", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var localPath = Path.Combine(temporaryDirectory.FullName, "changed.txt");
            await File.WriteAllTextAsync(localPath, "changed");
            File.SetLastWriteTimeUtc(
                localPath,
                DateTime.Parse("2026-08-11T02:41:00Z").ToUniversalTime());
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            var snapshot = await service.GetSnapshotAsync(settings, CancellationToken.None);

            var transfer = Assert.Single(snapshot.Plan);
            Assert.Equal("changed.txt", transfer.Path);
            Assert.Equal("Upload (local changed)", transfer.Action);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetSnapshotAsync_DeletionModePlansBothDeletesWithoutSync()
    {
        const string listOutput =
            """{"Items":[{"Path":"cloud-only.txt","ContentLength":3,"LastModifiedTime":"2026-08-10T01:00:00Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory.FullName, "local-only.txt"),
                "local");
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode,
                DeletionMode = true
            };

            var snapshot = await service.GetSnapshotAsync(settings, CancellationToken.None);

            Assert.Equal(["list"], runner.Commands);
            Assert.Contains(
                snapshot.Plan,
                item => item.Path == "local-only.txt" && item.Action == "Delete local");
            Assert.Contains(
                snapshot.Plan,
                item => item.Path == "cloud-only.txt" && item.Action == "Delete cloud");
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SynchronizeAsync_UploadsThenDownloadsOnlyMissingFiles()
    {
        const string uploadPlan =
            "DRYRUN: copy https://account123.blob.core.windows.net/container/local.txt";
        const string listOutput =
            """{"Items":[{"Path":"cloud-only.txt","ContentLength":42,"LastModifiedTime":"2026-08-10T01:00:00Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty),
            new AzCopyCommandResult(0, uploadPlan, string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(temporaryDirectory.FullName, "local.txt"),
                "local");
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            await service.SynchronizeAsync(settings, CancellationToken.None);

            Assert.Equal(["list", "sync", "copy", "copy"], runner.Commands);
            Assert.Contains("--delete-destination=false", runner.Arguments[1]);
            Assert.Contains("--overwrite=true", runner.Arguments[2]);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container/local.txt",
                runner.Arguments[2][2]);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container/cloud-only.txt",
                runner.Arguments[3][1]);
            Assert.Equal(
                Path.Combine(temporaryDirectory.FullName, "cloud-only.txt"),
                runner.Arguments[3][2]);
            Assert.Contains("--overwrite=false", runner.Arguments[3]);
            Assert.Contains("--preserve-last-modified-time=true", runner.Arguments[3]);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SynchronizeAsync_OverwritesNewerBlobWhenSizeDiffers()
    {
        const string listOutput =
            """{"Items":[{"Path":"changed.txt","ContentLength":5,"LastModifiedTime":"2026-08-11T02:49:26Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty),
            new AzCopyCommandResult(0, "INFO: no changes", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var localPath = Path.Combine(temporaryDirectory.FullName, "changed.txt");
            await File.WriteAllTextAsync(localPath, "new content");
            File.SetLastWriteTimeUtc(
                localPath,
                DateTime.Parse("2026-08-11T02:40:58Z").ToUniversalTime());
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            await service.SynchronizeAsync(settings, CancellationToken.None);

            Assert.Equal(["list", "sync", "copy"], runner.Commands);
            Assert.Equal(localPath, runner.Arguments[2][1]);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container/changed.txt",
                runner.Arguments[2][2]);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task SynchronizeAsync_DeletionModeDeletesBothSidesWithoutSync()
    {
        const string listOutput =
            """{"Items":[{"Path":"cloud-only.txt","ContentLength":3,"LastModifiedTime":"2026-08-10T01:00:00Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, listOutput, string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var localOnlyPath = Path.Combine(temporaryDirectory.FullName, "local-only.txt");
            await File.WriteAllTextAsync(localOnlyPath, "local");
            var settings = new SyncSettings
            {
                LocalFolder = temporaryDirectory.FullName,
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode,
                DeletionMode = true
            };

            await service.SynchronizeAsync(settings, CancellationToken.None);

            Assert.Equal(["list", "copy", "remove", "list"], runner.Commands);
            Assert.False(File.Exists(localOnlyPath));
            Assert.DoesNotContain(runner.Commands, command => command == "sync");
            Assert.Contains(
                SawSyncFlag.DeletionPrefix,
                runner.Arguments[1][2],
                StringComparison.Ordinal);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container/cloud-only.txt",
                runner.Arguments[2][1]);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeleteRemoteBatchAsync_DeletesEverySelectionAndVerifiesImmediately()
    {
        const string remainingList =
            """{"Items":[{"Path":"keep.txt","ContentLength":5,"LastModifiedTime":"2026-08-10T01:00:00Z"}]}""";
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, remainingList, string.Empty));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var settings = new SyncSettings
            {
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            await service.DeleteRemoteBatchAsync(
                settings,
                ["folder/a.txt", "folder/b.txt"],
                CancellationToken.None);

            Assert.Equal(["copy", "copy", "remove", "remove", "list"], runner.Commands);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container/folder/a.txt",
                runner.Arguments[2][1]);
            Assert.Equal(
                "https://account123.blob.core.windows.net/container/folder/b.txt",
                runner.Arguments[3][1]);
            Assert.EndsWith(
                SawSyncFlag.GetDeletionMarkerPath("folder/a.txt"),
                runner.Arguments[0][2],
                StringComparison.Ordinal);
            Assert.EndsWith(
                SawSyncFlag.GetDeletionMarkerPath("folder/b.txt"),
                runner.Arguments[1][2],
                StringComparison.Ordinal);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DeleteRemoteBatchAsync_PublishesEveryMarkerBeforeDeletingAnyBlob()
    {
        var runner = new QueuedRunner(
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(0, "{}", string.Empty),
            new AzCopyCommandResult(1, string.Empty, "remove failed"));
        var service = new AzCopyService(runner);
        var temporaryDirectory = Directory.CreateTempSubdirectory("SyncSAW.Tests.");
        try
        {
            var settings = new SyncSettings
            {
                StorageAccount = "account123",
                Container = "container",
                AzCopyPath = CreateFakeAzCopy(temporaryDirectory),
                LoginMode = EntraLoginMode.DeviceCode
            };

            await Assert.ThrowsAsync<AzCopyException>(() =>
                service.DeleteRemoteBatchAsync(
                    settings,
                    ["folder/a.txt", "folder/b.txt"],
                    CancellationToken.None));

            Assert.Equal(["copy", "copy", "remove"], runner.Commands);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    private static string CreateFakeAzCopy(DirectoryInfo directory)
    {
        var path = Path.Combine(directory.FullName, "azcopy.exe");
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static string CreateFakeAzureCli(DirectoryInfo directory)
    {
        var commandDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "wbin"));
        var commandPath = Path.Combine(commandDirectory.FullName, "az.cmd");
        File.WriteAllText(commandPath, string.Empty);
        File.WriteAllText(Path.Combine(directory.FullName, "python.exe"), string.Empty);
        return commandPath;
    }

    private sealed class RecordingRunner : IAzCopyRunner
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public AzCopyProcessMode Mode { get; private set; }

        public IReadOnlyDictionary<string, string?>? EnvironmentVariables { get; private set; }

        public Task<AzCopyCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            AzCopyProcessMode mode = AzCopyProcessMode.Captured,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Arguments = arguments;
            Mode = mode;
            EnvironmentVariables = environmentVariables;
            return Task.FromResult(new AzCopyCommandResult(0, string.Empty, string.Empty));
        }

        public void CancelAll()
        {
        }
    }

    private sealed class QueuedRunner(params AzCopyCommandResult[] results) : IAzCopyRunner
    {
        private readonly Queue<AzCopyCommandResult> _results = new(results);

        public List<string> Commands { get; } = [];
        public List<IReadOnlyList<string>> Arguments { get; } = [];

        public Task<AzCopyCommandResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            AzCopyProcessMode mode = AzCopyProcessMode.Captured,
            IReadOnlyDictionary<string, string?>? environmentVariables = null)
        {
            Commands.Add(arguments[0]);
            Arguments.Add(arguments);
            return Task.FromResult(_results.Dequeue());
        }

        public void CancelAll()
        {
        }
    }
}
