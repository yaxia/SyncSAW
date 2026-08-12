namespace SyncSAW.Core;

public sealed class AzCopyService(IAzCopyRunner runner)
{
    public async Task LoginAsync(SyncSettings settings, CancellationToken cancellationToken)
    {
        if (settings.LoginMode == EntraLoginMode.AzureCli)
        {
            var azureCli = AzureCliLocator.ResolveCommand(settings.AzureCliPath);
            var cliResult = await runner.RunAsync(
                azureCli.ExecutablePath,
                [.. azureCli.PrefixArguments, .. AzCopyArguments.AzureCliLogin(settings.TenantId)],
                cancellationToken,
                environmentVariables: AzureCliLoginEnvironment);
            EnsureSuccess("Azure CLI sign-in failed.", cliResult);

            if (!string.IsNullOrWhiteSpace(settings.SubscriptionId))
            {
                var subscriptionResult = await runner.RunAsync(
                    azureCli.ExecutablePath,
                    [
                        .. azureCli.PrefixArguments,
                        .. AzCopyArguments.AzureCliSelectSubscription(settings.SubscriptionId)
                    ],
                    cancellationToken);
                EnsureSuccess(
                    $"Azure CLI could not select subscription '{settings.SubscriptionId}'.",
                    subscriptionResult);
            }
            return;
        }

        var deviceResult = await runner.RunAsync(
            AzCopyLocator.Find(settings.AzCopyPath),
            AzCopyArguments.Login(settings.TenantId),
            cancellationToken,
            AzCopyProcessMode.Interactive);
        EnsureSuccess("AzCopy device-code sign-in failed.", deviceResult);
    }

    public async Task<SyncSnapshot> GetSnapshotAsync(
        SyncSettings settings,
        CancellationToken cancellationToken)
    {
        Validate(settings);
        var executable = AzCopyLocator.Find(settings.AzCopyPath);
        var endpoint = StorageEndpoint.BuildContainerUri(settings.StorageAccount, settings.Container);

        var listResult = await EnsureContainerExistsAsync(
            settings,
            executable,
            endpoint,
            cancellationToken);

        var localFiles = EnumerateLocalFiles(settings.LocalFolder);
        var remoteBlobs = SawSyncFlag.ApplyMarkers(
            AzCopyOutputParser.ParseRemoteList(listResult.StandardOutput));
        var plan = await CreatePlanAsync(
            settings,
            executable,
            endpoint,
            localFiles,
            remoteBlobs,
            cancellationToken);
        var items = SyncStateComparer.Reconcile(localFiles, remoteBlobs, plan);
        return new SyncSnapshot(items, remoteBlobs, plan);
    }

    public async Task SynchronizeAsync(SyncSettings settings, CancellationToken cancellationToken)
    {
        Validate(settings);
        var executable = AzCopyLocator.Find(settings.AzCopyPath);
        var endpoint = StorageEndpoint.BuildContainerUri(settings.StorageAccount, settings.Container);
        var listResult = await EnsureContainerExistsAsync(
            settings,
            executable,
            endpoint,
            cancellationToken);

        var uploadPlanResult = await runner.RunAsync(
            executable,
            AzCopyArguments.PlanUpload(
                settings.LocalFolder,
                endpoint,
                deleteDestination: false),
            cancellationToken,
            environmentVariables: AuthenticationEnvironment(settings));
        EnsureSuccess("AzCopy could not plan local uploads.", uploadPlanResult);

        var localFiles = EnumerateLocalFiles(settings.LocalFolder);
        var remoteBlobs = SawSyncFlag.ApplyMarkers(
            AzCopyOutputParser.ParseRemoteList(listResult.StandardOutput));
        var uploadPaths = AzCopyOutputParser.ParsePlan(uploadPlanResult.StandardOutput)
            .Where(transfer => !IsDeletion(transfer))
            .Select(transfer => ValidateRelativeTransferPath(transfer.Path))
            .Concat(GetAuthoritativeLocalUploads(localFiles, remoteBlobs)
                .Select(transfer => transfer.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var uploadPath in uploadPaths)
        {
            var localFile = ResolveLocalTransferPath(settings.LocalFolder, uploadPath);
            if (!File.Exists(localFile))
            {
                throw new FileNotFoundException(
                    $"AzCopy planned '{uploadPath}', but the local file no longer exists.",
                    localFile);
            }

            var uploadResult = await runner.RunAsync(
                executable,
                AzCopyArguments.Copy(
                    localFile,
                    StorageEndpoint.BuildBlobUri(
                        settings.StorageAccount,
                        settings.Container,
                        uploadPath).AbsoluteUri),
                cancellationToken,
                environmentVariables: AuthenticationEnvironment(settings));
            EnsureSuccess($"AzCopy upload failed for '{uploadPath}'.", uploadResult);
        }

        var localPaths = EnumerateLocalFiles(settings.LocalFolder)
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var remoteBlob in remoteBlobs.Where(blob => !localPaths.Contains(blob.Path)))
        {
            var destination = ResolveLocalTransferPath(settings.LocalFolder, remoteBlob.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var downloadResult = await runner.RunAsync(
                executable,
                AzCopyArguments.DownloadMissingFile(
                    StorageEndpoint.BuildBlobUri(
                        settings.StorageAccount,
                        settings.Container,
                        remoteBlob.Path),
                    destination),
                cancellationToken,
                environmentVariables: AuthenticationEnvironment(settings));
            EnsureSuccess($"AzCopy could not download '{remoteBlob.Path}'.", downloadResult);
        }
    }

    public async Task UploadAsync(
        SyncSettings settings,
        string localFile,
        string blobPath,
        CancellationToken cancellationToken)
    {
        Validate(settings);
        if (!File.Exists(localFile))
        {
            throw new FileNotFoundException("The local file does not exist.", localFile);
        }

        var destination = StorageEndpoint.BuildBlobUri(
            settings.StorageAccount,
            settings.Container,
            blobPath);
        var executable = AzCopyLocator.Find(settings.AzCopyPath);
        _ = await EnsureContainerExistsAsync(
            settings,
            executable,
            StorageEndpoint.BuildContainerUri(settings.StorageAccount, settings.Container),
            cancellationToken);
        var result = await runner.RunAsync(
            executable,
            AzCopyArguments.Copy(Path.GetFullPath(localFile), destination.AbsoluteUri),
            cancellationToken,
            environmentVariables: AuthenticationEnvironment(settings));
        EnsureSuccess("AzCopy upload failed.", result);
    }

    public async Task DownloadAsync(
        SyncSettings settings,
        string blobPath,
        string localFile,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(settings);
        var source = StorageEndpoint.BuildBlobUri(
            settings.StorageAccount,
            settings.Container,
            blobPath);
        var destination = Path.GetFullPath(localFile);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var result = await runner.RunAsync(
            AzCopyLocator.Find(settings.AzCopyPath),
            AzCopyArguments.Copy(source.AbsoluteUri, destination),
            cancellationToken,
            environmentVariables: AuthenticationEnvironment(settings));
        EnsureSuccess("AzCopy download failed.", result);
    }

    public async Task DeleteRemoteAsync(
        SyncSettings settings,
        string blobPath,
        CancellationToken cancellationToken) =>
        await DeleteRemoteBatchAsync(settings, [blobPath], cancellationToken);

    public async Task DeleteRemoteBatchAsync(
        SyncSettings settings,
        IEnumerable<string> blobPaths,
        CancellationToken cancellationToken)
    {
        ValidateEndpoint(settings);
        var paths = blobPaths
            .Select(ValidateRelativeTransferPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (paths.Length == 0)
        {
            throw new ArgumentException("Choose at least one remote file to delete.", nameof(blobPaths));
        }

        var executable = AzCopyLocator.Find(settings.AzCopyPath);
        var deletionMarkerFile = Path.GetTempFileName();
        try
        {
            foreach (var path in paths)
            {
                await File.WriteAllTextAsync(
                    deletionMarkerFile,
                    System.Text.Json.JsonSerializer.Serialize(path),
                    cancellationToken);
                var markerResult = await runner.RunAsync(
                    executable,
                    AzCopyArguments.Copy(
                        deletionMarkerFile,
                        StorageEndpoint.BuildBlobUri(
                            settings.StorageAccount,
                            settings.Container,
                            SawSyncFlag.GetDeletionMarkerPath(path)).AbsoluteUri),
                    cancellationToken,
                    environmentVariables: AuthenticationEnvironment(settings));
                EnsureSuccess(
                    $"AzCopy could not publish the deletion marker for '{path}'.",
                    markerResult);
            }

            foreach (var path in paths)
            {
                var result = await runner.RunAsync(
                    executable,
                    AzCopyArguments.Remove(StorageEndpoint.BuildBlobUri(
                        settings.StorageAccount,
                        settings.Container,
                        path)),
                    cancellationToken,
                    environmentVariables: AuthenticationEnvironment(settings));
                EnsureSuccess($"AzCopy could not delete '{path}'.", result);
            }
        }
        finally
        {
            File.Delete(deletionMarkerFile);
        }

        var listResult = await runner.RunAsync(
            executable,
            AzCopyArguments.List(StorageEndpoint.BuildContainerUri(
                settings.StorageAccount,
                settings.Container)),
            cancellationToken,
            environmentVariables: AuthenticationEnvironment(settings));
        EnsureSuccess("AzCopy could not verify remote deletion.", listResult);

        var remainingPaths = AzCopyOutputParser.ParseRemoteList(listResult.StandardOutput)
            .Select(blob => blob.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notDeleted = paths.Where(remainingPaths.Contains).ToArray();
        if (notDeleted.Length > 0)
        {
            throw new InvalidOperationException(
                $"Azure still reports the following Blob(s) after deletion: {string.Join(", ", notDeleted)}");
        }
    }

    public static void Validate(SyncSettings settings)
    {
        ValidateEndpoint(settings);
        if (string.IsNullOrWhiteSpace(settings.LocalFolder) || !Directory.Exists(settings.LocalFolder))
        {
            throw new DirectoryNotFoundException("Choose an existing local folder.");
        }
    }

    private static void ValidateEndpoint(SyncSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _ = StorageEndpoint.BuildContainerUri(settings.StorageAccount, settings.Container);
    }

    private static IReadOnlyList<LocalFileInfo> EnumerateLocalFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new LocalFileInfo(
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    info.LastWriteTimeUtc,
                    info.Length);
            })
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<IReadOnlyList<PlannedTransfer>> CreatePlanAsync(
        SyncSettings settings,
        string executable,
        Uri endpoint,
        IReadOnlyList<LocalFileInfo> localFiles,
        IReadOnlyList<RemoteBlobInfo> remoteBlobs,
        CancellationToken cancellationToken)
    {
        var environment = AuthenticationEnvironment(settings);
        var uploadPlanResult = await runner.RunAsync(
            executable,
            AzCopyArguments.PlanUpload(
                settings.LocalFolder,
                endpoint,
                deleteDestination: false),
            cancellationToken,
            environmentVariables: environment);
        EnsureSuccess("AzCopy could not create an upload/deletion plan.", uploadPlanResult);

        var uploadPlan = AzCopyOutputParser.ParsePlan(uploadPlanResult.StandardOutput);
        var localPaths = localFiles
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cloudOnlyDownloads = remoteBlobs
            .Where(item => !localPaths.Contains(item.Path))
            .Select(item => new PlannedTransfer(item.Path, "Download"));
        return uploadPlan
            .Concat(GetAuthoritativeLocalUploads(localFiles, remoteBlobs))
            .Concat(cloudOnlyDownloads)
            .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PlannedTransfer> GetAuthoritativeLocalUploads(
        IReadOnlyList<LocalFileInfo> localFiles,
        IReadOnlyList<RemoteBlobInfo> remoteBlobs)
    {
        var remoteByPath = remoteBlobs.ToDictionary(
            item => item.Path,
            StringComparer.OrdinalIgnoreCase);
        return localFiles
            .Where(local =>
                remoteByPath.TryGetValue(local.Path, out var remote) &&
                (remote.Size is not null && remote.Size.Value != local.Size ||
                 remote.LastModified is not null &&
                 local.LastModified > remote.LastModified.Value.AddSeconds(2)))
            .Select(local => new PlannedTransfer(local.Path, "Upload (local changed)"));
    }

    private static bool IsDeletion(PlannedTransfer transfer) =>
        transfer.Action.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
        transfer.Action.Contains("remove", StringComparison.OrdinalIgnoreCase);

    private static string ValidateRelativeTransferPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized) ||
            Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(segment => segment is "." or ".."))
        {
            throw new InvalidDataException(
                $"AzCopy returned an unsafe relative transfer path: '{path}'.");
        }

        return normalized;
    }

    private static string ResolveLocalTransferPath(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"AzCopy returned a transfer path outside the local root: '{relativePath}'.");
        }

        return candidate;
    }

    private static void EnsureSuccess(string message, AzCopyCommandResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var detail = result.WasCancelled
            ? "The operation was cancelled."
            : FirstUsefulLine(result.StandardError, result.StandardOutput);
        throw new AzCopyException($"{message} {detail}".Trim(), result);
    }

    private static string FirstUsefulLine(params string[] values) =>
        values.SelectMany(value => (value ?? string.Empty).Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .FirstOrDefault() ?? "AzCopy returned a non-zero exit code.";

    private async Task<AzCopyCommandResult> EnsureContainerExistsAsync(
        SyncSettings settings,
        string executable,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        var environment = AuthenticationEnvironment(settings);
        var listResult = await runner.RunAsync(
            executable,
            AzCopyArguments.List(endpoint),
            cancellationToken,
            environmentVariables: environment);
        if (listResult.Succeeded)
        {
            return listResult;
        }

        if (!ContainsAzureError(listResult, "ContainerNotFound", "404"))
        {
            EnsureSuccess("AzCopy could not list the container.", listResult);
        }

        var makeResult = await runner.RunAsync(
            executable,
            AzCopyArguments.MakeContainer(endpoint),
            cancellationToken,
            environmentVariables: environment);
        if (!makeResult.Succeeded &&
            !ContainsAzureError(makeResult, "ContainerAlreadyExists", "409"))
        {
            EnsureSuccess("AzCopy could not create the missing container.", makeResult);
        }

        var refreshedList = await runner.RunAsync(
            executable,
            AzCopyArguments.List(endpoint),
            cancellationToken,
            environmentVariables: environment);
        EnsureSuccess("The container was created, but AzCopy could not list it.", refreshedList);
        return refreshedList;
    }

    private static bool ContainsAzureError(AzCopyCommandResult result, params string[] markers)
    {
        var output = $"{result.StandardError}\n{result.StandardOutput}";
        return markers.Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyDictionary<string, string?> AuthenticationEnvironment(SyncSettings settings) =>
        settings.LoginMode == EntraLoginMode.AzureCli
            ? new Dictionary<string, string?>
            {
                ["AZCOPY_AUTO_LOGIN_TYPE"] = "AZCLI",
                ["AZCOPY_TENANT_ID"] = string.IsNullOrWhiteSpace(settings.TenantId)
                    ? null
                    : settings.TenantId.Trim()
            }
            : new Dictionary<string, string?>
            {
                ["AZCOPY_AUTO_LOGIN_TYPE"] = null,
                ["AZCOPY_TENANT_ID"] = null
            };

    private static IReadOnlyDictionary<string, string?> AzureCliLoginEnvironment { get; } =
        new Dictionary<string, string?>
        {
            ["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off"
        };
}
