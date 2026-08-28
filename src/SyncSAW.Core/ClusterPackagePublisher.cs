using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SyncSAW.Core;

public static class ClusterPackage
{
    public const string BlobName = "cluster_package.zip";
    public const string ConfigurationEntryName = "cluster_package.config";
    public const string TaskScriptName = "task.ps1";
    public const int ConfigurationSchemaVersion = 5;
    public static readonly TimeSpan PublishInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan SasLifetime = TimeSpan.FromDays(7);
    public const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumPayloadBytes = 4L * 1024 * 1024 * 1024;
    // The runner's 10,000-entry archive limit includes the embedded config.
    public const int MaximumPayloadFiles = 9_999;
    public const string PackageContainerSuffix = "-package";
    public const string ResultsPrefix = "cluster-results/";
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
               normalized.Equals(TaskScriptName, StringComparison.OrdinalIgnoreCase);
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
}

public sealed record ClusterPackagePublication(
    DateTimeOffset PublishedUtc,
    DateTimeOffset SasExpiresUtc,
    int PayloadFileCount,
    string PayloadFingerprint);

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
        CancellationToken cancellationToken)
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
        var resultBlobPath =
            $"{ClusterPackage.ResultsPrefix}{now.UtcDateTime:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}.zip";
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
            var uploadResult = await runner.RunAsync(
                AzCopyLocator.Find(settings.AzCopyPath),
                AzCopyArguments.Copy(archivePath, blobUri.AbsoluteUri),
                cancellationToken,
                environmentVariables: AzCopyAuthentication.GetEnvironment(settings));
            EnsureSuccess("AzCopy could not publish cluster_package.zip.", uploadResult);
            return new ClusterPackagePublication(
                now.ToUniversalTime(),
                sasExpiresUtc,
                payload.Count,
                payload.Fingerprint);
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
