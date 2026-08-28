using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncSAW.Core;

public enum ClusterPackageChangeType
{
    Binary,
    CommandsOnly
}

public sealed record ClusterPackageTaskConfiguration(
    string ResultPrefix,
    ClusterPackageChangeType ChangeType,
    IReadOnlyList<string> ExecutionArguments);

public sealed record ClusterPackageUpdateDescriptor(
    string PackageSha256,
    DateTimeOffset PackageBuiltUtc,
    ClusterPackageChangeType ChangeType,
    IReadOnlyList<string>? ExecutionCommand,
    ClusterPackageBootstrapConfiguration BootstrapConfiguration);

public sealed record ClusterPackageBootstrapConfiguration(
    int SchemaVersion,
    string PackageUri,
    string ResultsBlobUri,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc);

public static class ClusterPackage
{
    public const string BlobName = "cluster_package.zip";
    public const string ConfigurationEntryName = "cluster_package.config";
    public const string TaskScriptName = "task.ps1";
    public const string TaskConfigurationName = "task.config.json";
    public const int ConfigurationSchemaVersion = 6;
    public const int UpdateDescriptorVersion = 1;
    public const int MaximumBlobMetadataBytes = 8 * 1024;
    public const int MaximumExecutionArguments = 32;
    public const int MaximumExecutionArgumentBytes = 1024;
    public const string BootstrapConfigPlaceholder = "{BootstrapConfigPath}";
    public const string DescriptorVersionMetadataKey = "syncsaw_descriptor_version";
    public const string PackageSha256MetadataKey = "syncsaw_package_sha256";
    public const string PackageBuiltUtcMetadataKey = "syncsaw_package_built_utc";
    public const string ChangeTypeMetadataKey = "syncsaw_change_type";
    public const string ExecutionCommandMetadataKey = "syncsaw_execution_command";
    public const string BootstrapConfigurationMetadataKey = "syncsaw_bootstrap_config";
    public static readonly TimeSpan PublishInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan SasLifetime = TimeSpan.FromDays(7);
    public const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumPayloadBytes = 4L * 1024 * 1024 * 1024;
    // The runner's 10,000-entry archive limit includes the embedded config.
    public const int MaximumPayloadFiles = 9_999;
    public const string PackageContainerSuffix = "-package";
    public const string ResultsPrefix = "cluster-results/";
    public const string DefaultResultBlobPrefix = "result";
    private static readonly HashSet<string> ExcludedPayloadPaths =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "PDITest/PDI.zip",
            "PDITest/spdi.zip"
        };

    public static bool IsReservedPath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Trim('"').Replace('\\', '/').TrimStart('/');
        return normalized.Equals(BlobName, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(ConfigurationEntryName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsNormalSyncExcludedPath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Trim('"').Replace('\\', '/').TrimStart('/');
        return IsReservedPath(normalized) ||
               normalized.Equals(TaskScriptName, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(TaskConfigurationName, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsExcludedPayloadPath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Trim('"').Replace('\\', '/').TrimStart('/');
        return ExcludedPayloadPaths.Contains(normalized);
    }

    public static string GetPackageContainerName(string syncContainer)
    {
        var normalized = StorageEndpoint.NormalizeContainer(syncContainer);
        if (normalized.Length + PackageContainerSuffix.Length > 63)
        {
            throw new InvalidOperationException(
                "Daily cluster package publishing requires a sync container name of 55 characters " +
                $"or fewer so the private '{PackageContainerSuffix.TrimStart('-')}' container can be created.");
        }
        return normalized + PackageContainerSuffix;
    }

    public static string GetResultsContainerName(string syncContainer) =>
        StorageEndpoint.NormalizeContainer(syncContainer);

    public static ClusterPackageTaskConfiguration GetTaskConfiguration(string sourceRoot)
    {
        var path = Path.Combine(Path.GetFullPath(sourceRoot), TaskConfigurationName);
        if (!File.Exists(path))
        {
            return new(
                DefaultResultBlobPrefix,
                ClusterPackageChangeType.Binary,
                []);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("task.config.json must contain a JSON object.");
        }

        var prefix = DefaultResultBlobPrefix;
        if (document.RootElement.TryGetProperty("ResultPrefix", out var prefixProperty))
        {
            if (prefixProperty.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("task.config.json ResultPrefix must be a string.");
            }
            prefix = prefixProperty.GetString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(prefix) ||
                prefix.Length > 64 ||
                !char.IsAsciiLetterOrDigit(prefix[0]) ||
                !prefix.All(character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '.' or '_' or '-'))
            {
                throw new InvalidDataException(
                    "task.config.json ResultPrefix must contain 1-64 letters, digits, periods, " +
                    "underscores, or hyphens.");
            }
        }

        var changeType = ClusterPackageChangeType.Binary;
        if (document.RootElement.TryGetProperty("PackageChangeType", out var changeProperty))
        {
            if (changeProperty.ValueKind != JsonValueKind.String ||
                !Enum.TryParse(
                    changeProperty.GetString()?.Replace("-", string.Empty),
                    ignoreCase: true,
                    out changeType))
            {
                throw new InvalidDataException(
                    "task.config.json PackageChangeType must be 'Binary' or 'CommandsOnly'.");
            }
        }

        var executionArguments = new List<string>();
        if (document.RootElement.TryGetProperty(
                "ExecutionArguments",
                out var argumentsProperty))
        {
            if (argumentsProperty.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException(
                    "task.config.json ExecutionArguments must be an array of strings.");
            }
            foreach (var item in argumentsProperty.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException(
                        "task.config.json ExecutionArguments must contain only strings.");
                }
                var argument = item.GetString() ?? string.Empty;
                if (Encoding.UTF8.GetByteCount(argument) > MaximumExecutionArgumentBytes ||
                    argument.Any(char.IsControl))
                {
                    throw new InvalidDataException(
                        $"Each task.config.json execution argument must contain at most " +
                        $"{MaximumExecutionArgumentBytes} UTF-8 bytes and no control characters.");
                }
                if (argument.Equals(
                        "-BootstrapConfigPath",
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "ExecutionArguments cannot replace the required BootstrapConfigPath.");
                }
                executionArguments.Add(argument);
            }
        }
        if (executionArguments.Count > MaximumExecutionArguments)
        {
            throw new InvalidDataException(
                $"task.config.json supports at most {MaximumExecutionArguments} execution arguments.");
        }
        if (changeType == ClusterPackageChangeType.CommandsOnly &&
            executionArguments.Count == 0)
        {
            throw new InvalidDataException(
                "CommandsOnly updates must provide at least one ExecutionArguments value.");
        }
        if (changeType == ClusterPackageChangeType.Binary &&
            executionArguments.Count > 0)
        {
            throw new InvalidDataException(
                "ExecutionArguments are allowed only when PackageChangeType is CommandsOnly.");
        }

        return new(prefix, changeType, executionArguments);
    }

    public static string GetResultBlobPrefix(string sourceRoot) =>
        GetTaskConfiguration(sourceRoot).ResultPrefix;

    public static ClusterPackageUpdateDescriptor CreateUpdateDescriptor(
        string archivePath,
        DateTimeOffset packageBuiltUtc,
        ClusterPackageTaskConfiguration taskConfiguration,
        Uri packageUri,
        Uri resultsBlobUri,
        DateTimeOffset expiresUtc)
    {
        using var stream = File.OpenRead(archivePath);
        var packageSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        IReadOnlyList<string>? executionCommand = null;
        if (taskConfiguration.ChangeType == ClusterPackageChangeType.CommandsOnly)
        {
            executionCommand =
            [
                "powershell.exe",
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "RemoteSigned",
                "-File",
                TaskScriptName,
                "-BootstrapConfigPath",
                BootstrapConfigPlaceholder,
                .. taskConfiguration.ExecutionArguments
            ];
        }
        return new(
            packageSha256,
            packageBuiltUtc.ToUniversalTime(),
            taskConfiguration.ChangeType,
            executionCommand,
            new(
                ConfigurationSchemaVersion,
                packageUri.AbsoluteUri,
                resultsBlobUri.AbsoluteUri,
                packageBuiltUtc.ToUniversalTime(),
                expiresUtc.ToUniversalTime()));
    }

    public static IReadOnlyDictionary<string, string> GetUpdateDescriptorMetadata(
        ClusterPackageUpdateDescriptor descriptor)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DescriptorVersionMetadataKey] = UpdateDescriptorVersion.ToString(),
            [PackageSha256MetadataKey] = descriptor.PackageSha256,
            [PackageBuiltUtcMetadataKey] = descriptor.PackageBuiltUtc.ToString("O"),
            [BootstrapConfigurationMetadataKey] = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(descriptor.BootstrapConfiguration)),
            [ChangeTypeMetadataKey] = descriptor.ChangeType switch
            {
                ClusterPackageChangeType.Binary => "binary",
                ClusterPackageChangeType.CommandsOnly => "commands-only",
                _ => throw new InvalidDataException("Unsupported cluster package change type.")
            }
        };
        if (descriptor.ChangeType == ClusterPackageChangeType.CommandsOnly)
        {
            if (descriptor.ExecutionCommand is null)
            {
                throw new InvalidDataException(
                    "Commands-only descriptors require an execution command.");
            }
            metadata[ExecutionCommandMetadataKey] = Convert.ToBase64String(
                JsonSerializer.SerializeToUtf8Bytes(descriptor.ExecutionCommand));
        }
        else if (descriptor.ExecutionCommand is not null)
        {
            throw new InvalidDataException(
                "Binary descriptors cannot include an execution command.");
        }

        var metadataBytes = metadata.Sum(item =>
            Encoding.UTF8.GetByteCount(item.Key) +
            Encoding.UTF8.GetByteCount(item.Value));
        if (metadataBytes > MaximumBlobMetadataBytes)
        {
            throw new InvalidDataException(
                "Cluster package Blob metadata exceeds the 8,192-byte limit.");
        }
        return metadata;
    }
}

public sealed record ClusterPackagePublication(
    DateTimeOffset PublishedUtc,
    DateTimeOffset SasExpiresUtc,
    int PayloadFileCount,
    string PayloadFingerprint,
    ClusterPackageUpdateDescriptor UpdateDescriptor);

public sealed class ClusterPackagePublisher(IAzCopyRunner runner)
{
    public string GetPayloadFingerprint(
        string sourceRoot,
        CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(sourceRoot);
        return ComputePayloadFingerprint(
            EnumeratePayloadFiles(root)
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase),
            cancellationToken);
    }

    public async Task<ClusterPackagePublication> PublishAsync(
        SyncSettings settings,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool forceBinaryUpdate = false)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AzCopyService.Validate(settings);
        if (settings.LoginMode != EntraLoginMode.AzureCli)
        {
            throw new InvalidOperationException(
                "Daily cluster package publishing requires Azure CLI / Windows broker authentication.");
        }

        var packageContainer = ClusterPackage.GetPackageContainerName(settings.Container);
        var resultsContainer = ClusterPackage.GetResultsContainerName(settings.Container);
        var taskConfiguration = ClusterPackage.GetTaskConfiguration(settings.LocalFolder);
        if (forceBinaryUpdate &&
            taskConfiguration.ChangeType == ClusterPackageChangeType.CommandsOnly)
        {
            taskConfiguration = taskConfiguration with
            {
                ChangeType = ClusterPackageChangeType.Binary,
                ExecutionArguments = []
            };
        }
        var resultBlobPrefix = taskConfiguration.ResultPrefix;
        var resultBlobPath =
            $"{ClusterPackage.ResultsPrefix}{resultBlobPrefix}-" +
            $"{now.UtcDateTime:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.zip";
        var blobUri = StorageEndpoint.BuildBlobUri(
            settings.StorageAccount,
            packageContainer,
            ClusterPackage.BlobName);
        var expectedResultBlobUri = StorageEndpoint.BuildBlobUri(
            settings.StorageAccount,
            resultsContainer,
            resultBlobPath);
        var unroundedStart = now.UtcDateTime.AddMinutes(-5);
        var sasStartsUtc = new DateTimeOffset(
            unroundedStart.AddTicks(-(unroundedStart.Ticks % TimeSpan.TicksPerSecond)));
        var sasExpiresUtc = sasStartsUtc.Add(ClusterPackage.SasLifetime);
        var azureCli = AzureCliLocator.ResolveCommand(settings.AzureCliPath);
        var createPackageContainer = await runner.RunAsync(
            azureCli.ExecutablePath,
            [
                .. azureCli.PrefixArguments,
                .. AzCopyArguments.AzureCliCreatePrivateContainer(
                    settings.StorageAccount,
                    packageContainer)
            ],
            cancellationToken);
        EnsureSuccess(
            $"Azure CLI could not create or validate package container '{packageContainer}'.",
            createPackageContainer);
        var packageContainerAccess = await runner.RunAsync(
            azureCli.ExecutablePath,
            [
                .. azureCli.PrefixArguments,
                .. AzCopyArguments.AzureCliGetContainerPublicAccess(
                    settings.StorageAccount,
                    packageContainer)
            ],
            cancellationToken);
        EnsureSuccess(
            $"Azure CLI could not verify package container '{packageContainer}' access.",
            packageContainerAccess);
        var publicAccess = packageContainerAccess.StandardOutput.Trim();
        if (!string.IsNullOrEmpty(publicAccess) &&
            !publicAccess.Equals("none", StringComparison.OrdinalIgnoreCase) &&
            !publicAccess.Equals("null", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Package container '{packageContainer}' allows public '{publicAccess}' access. " +
                "Disable public access before publishing cluster packages.");
        }
        var createResultsContainer = await runner.RunAsync(
            azureCli.ExecutablePath,
            [
                .. azureCli.PrefixArguments,
                .. AzCopyArguments.AzureCliEnsureContainer(
                    settings.StorageAccount,
                    resultsContainer)
            ],
            cancellationToken);
        EnsureSuccess(
            $"Azure CLI could not create or validate results container '{resultsContainer}'.",
            createResultsContainer);
        var sasResult = await runner.RunAsync(
            azureCli.ExecutablePath,
            [
                .. azureCli.PrefixArguments,
                .. AzCopyArguments.AzureCliGenerateBlobReadSas(
                    settings.StorageAccount,
                    packageContainer,
                    ClusterPackage.BlobName,
                    sasStartsUtc,
                    sasExpiresUtc)
            ],
            cancellationToken,
            AzCopyProcessMode.SensitiveCaptured);
        EnsureSuccess("Azure CLI could not generate the cluster package SAS.", sasResult);
        var sasUri = ParseAndValidateSasUri(
            sasResult.StandardOutput,
            blobUri,
            "b",
            "r",
            sasStartsUtc,
            sasExpiresUtc);
        var resultsSasResult = await runner.RunAsync(
            azureCli.ExecutablePath,
            [
                .. azureCli.PrefixArguments,
                .. AzCopyArguments.AzureCliGenerateBlobCreateSas(
                    settings.StorageAccount,
                    resultsContainer,
                    resultBlobPath,
                    sasStartsUtc,
                    sasExpiresUtc)
            ],
            cancellationToken,
            AzCopyProcessMode.SensitiveCaptured);
        EnsureSuccess(
            "Azure CLI could not generate the cluster results upload SAS.",
            resultsSasResult);
        var resultsBlobUri = ParseAndValidateSasUri(
            resultsSasResult.StandardOutput,
            expectedResultBlobUri,
            "b",
            "c",
            sasStartsUtc,
            sasExpiresUtc);

        var archivePath = Path.Combine(
            Path.GetTempPath(),
            $"syncsaw-cluster-package-{Guid.NewGuid():N}.zip");
        try
        {
            var payload = await CreateArchiveAsync(
                settings.LocalFolder,
                archivePath,
                sasUri,
                resultsBlobUri,
                now.ToUniversalTime(),
                sasExpiresUtc,
                cancellationToken);
            if (new FileInfo(archivePath).Length > ClusterPackage.MaximumArchiveBytes)
            {
                throw new InvalidDataException(
                    $"The compressed cluster package exceeds the " +
                    $"{ClusterPackage.MaximumArchiveBytes}-byte client limit.");
            }
            var updateDescriptor = ClusterPackage.CreateUpdateDescriptor(
                archivePath,
                now,
                taskConfiguration,
                sasUri,
                resultsBlobUri,
                sasExpiresUtc);
            var uploadResult = await runner.RunAsync(
                AzCopyLocator.Find(settings.AzCopyPath),
                AzCopyArguments.Copy(
                    archivePath,
                    blobUri.AbsoluteUri,
                    ClusterPackage.GetUpdateDescriptorMetadata(updateDescriptor)),
                cancellationToken,
                AzCopyProcessMode.SensitiveCaptured,
                environmentVariables: AzCopyAuthentication.GetEnvironment(settings));
            EnsureSuccess("AzCopy could not publish cluster_package.zip.", uploadResult);
            return new ClusterPackagePublication(
                now.ToUniversalTime(),
                sasExpiresUtc,
                payload.Count,
                payload.Fingerprint,
                updateDescriptor);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private static async Task<(int Count, string Fingerprint)> CreateArchiveAsync(
        string sourceRoot,
        string archivePath,
        Uri packageUri,
        Uri resultsBlobUri,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(sourceRoot);
        var payloadFiles = EnumeratePayloadFiles(root)
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (payloadFiles.Length > ClusterPackage.MaximumPayloadFiles)
        {
            throw new InvalidDataException(
                $"The cluster package exceeds the {ClusterPackage.MaximumPayloadFiles}-file limit.");
        }
        var payloadBytes = payloadFiles.Sum(item => new FileInfo(item.Path).Length);
        if (payloadBytes > ClusterPackage.MaximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"The cluster package payload exceeds the " +
                $"{ClusterPackage.MaximumPayloadBytes}-byte limit.");
        }
        var payloadFingerprint = ComputePayloadFingerprint(payloadFiles, cancellationToken);

        using var archiveStream = new FileStream(
            archivePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var (path, relativePath) in payloadFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);
            entry.LastWriteTime = ClampZipTimestamp(File.GetLastWriteTimeUtc(path));
            using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var destination = entry.Open();
            await source.CopyToAsync(destination, cancellationToken);
        }

        var configurationEntry = archive.CreateEntry(
            ClusterPackage.ConfigurationEntryName,
            CompressionLevel.Optimal);
        configurationEntry.LastWriteTime = ClampZipTimestamp(issuedUtc.UtcDateTime);
        using (var configurationStream = configurationEntry.Open())
        {
            JsonSerializer.Serialize(
                configurationStream,
                new
                {
                    SchemaVersion = ClusterPackage.ConfigurationSchemaVersion,
                    PackageUri = packageUri.AbsoluteUri,
                    ResultsBlobUri = resultsBlobUri.AbsoluteUri,
                    IssuedUtc = issuedUtc,
                    ExpiresUtc = expiresUtc
                },
                new JsonSerializerOptions { WriteIndented = true });
        }

        return (
            payloadFiles.Length,
            payloadFingerprint);
    }

    private static string ComputePayloadFingerprint(
        IEnumerable<(string Path, string RelativePath)> payloadFiles,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        foreach (var (path, relativePath) in payloadFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            var descriptor =
                $"{relativePath}\0{info.Length}\0{info.LastWriteTimeUtc.Ticks}\n";
            hash.AppendData(Encoding.UTF8.GetBytes(descriptor));
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, bytesRead);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static IEnumerable<(string Path, string RelativePath)> EnumeratePayloadFiles(string root)
    {
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(childDirectory);
                var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    relative.Equals(".syncsaw", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(".syncsaw/", StringComparison.OrdinalIgnoreCase) ||
                    relative.Equals(
                        ClusterPackage.ResultsPrefix.TrimEnd('/'),
                        StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(
                        ClusterPackage.ResultsPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                pending.Push(info.FullName);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var info = new FileInfo(file);
                if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                    !info.FullName.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
                if (!ClusterPackage.IsReservedPath(relative) &&
                    !ClusterPackage.IsExcludedPayloadPath(relative) &&
                    !relative.StartsWith(
                        ClusterPackage.ResultsPrefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    yield return (info.FullName, relative);
                }
            }
        }
    }

    private static DateTimeOffset ClampZipTimestamp(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        var minimum = new DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var maximum = new DateTime(2107, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        return new DateTimeOffset(utc < minimum ? minimum : utc > maximum ? maximum : utc);
    }

    private static Uri ParseAndValidateSasUri(
        string output,
        Uri expectedResourceUri,
        string expectedResource,
        string expectedPermissions,
        DateTimeOffset expectedStart,
        DateTimeOffset expectedExpiry)
    {
        var candidate = (output ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => Uri.TryCreate(line, UriKind.Absolute, out _));
        if (candidate is null || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.Host.Equals(expectedResourceUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals(expectedResourceUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Azure CLI returned an invalid or unexpected cluster package SAS URL.");
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("sr", out var resource) || resource != expectedResource ||
            !query.TryGetValue("sp", out var permissions) || permissions != expectedPermissions ||
            !query.TryGetValue("spr", out var protocol) || protocol != "https" ||
            !query.ContainsKey("sig") ||
            !query.ContainsKey("skoid") ||
            !query.ContainsKey("sktid") ||
            !query.TryGetValue("sks", out var keyService) || keyService != "b" ||
            !query.ContainsKey("skv") ||
            !TryGetUtc(query, "st", out var actualStart) ||
            !TryGetUtc(query, "se", out var actualExpiry) ||
            !TryGetUtc(query, "skt", out var actualKeyStart) ||
            !TryGetUtc(query, "ske", out var actualKeyExpiry) ||
            actualStart != expectedStart ||
            actualExpiry != expectedExpiry ||
            actualKeyStart != expectedStart ||
            actualKeyExpiry != expectedExpiry)
        {
            throw new InvalidDataException(
                $"Azure CLI returned a SAS that is not the requested seven-day " +
                $"{expectedPermissions} {expectedResource} SAS.");
        }

        return uri;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2)
            {
                values[Uri.UnescapeDataString(pair[0])] = Uri.UnescapeDataString(pair[1]);
            }
        }
        return values;
    }

    private static bool TryGetUtc(
        IReadOnlyDictionary<string, string> values,
        string name,
        out DateTimeOffset result)
    {
        result = default;
        return values.TryGetValue(name, out var value) &&
               DateTimeOffset.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal |
            System.Globalization.DateTimeStyles.AdjustToUniversal,
            out result);
    }

    private static void EnsureSuccess(string message, AzCopyCommandResult result)
    {
        if (result.Succeeded)
        {
            return;
        }

        var detail = result.WasCancelled
            ? "The operation was cancelled."
            : (result.StandardError ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? "The command returned a non-zero exit code.";
        throw new AzCopyException($"{message} {detail}", result);
    }
}
