using System.IO.Compression;
using System.Text.Json;

namespace SyncSAW.Core;

public static class ClusterPackage
{
    public const string BlobName = "cluster_package.zip";
    public const string ConfigurationEntryName = "cluster_package.config";
    public static readonly TimeSpan PublishInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan SasLifetime = TimeSpan.FromDays(7);
    public const long MaximumArchiveBytes = 2L * 1024 * 1024 * 1024;
    public const long MaximumPayloadBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumPayloadFiles = 10_000;

    public static bool IsReservedPath(string path)
    {
        var normalized = (path ?? string.Empty).Trim().Trim('"').Replace('\\', '/').TrimStart('/');
        return normalized.Equals(BlobName, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(ConfigurationEntryName, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record ClusterPackagePublication(
    DateTimeOffset PublishedUtc,
    DateTimeOffset SasExpiresUtc,
    int PayloadFileCount);

public sealed class ClusterPackagePublisher(IAzCopyRunner runner)
{
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

        var blobUri = StorageEndpoint.BuildBlobUri(
            settings.StorageAccount,
            settings.Container,
            ClusterPackage.BlobName);
        var unroundedStart = now.UtcDateTime.AddMinutes(-5);
        var sasStartsUtc = new DateTimeOffset(
            unroundedStart.AddTicks(-(unroundedStart.Ticks % TimeSpan.TicksPerSecond)));
        var sasExpiresUtc = sasStartsUtc.Add(ClusterPackage.SasLifetime);
        var azureCli = AzureCliLocator.ResolveCommand(settings.AzureCliPath);
        var sasResult = await runner.RunAsync(
            azureCli.ExecutablePath,
            [
                .. azureCli.PrefixArguments,
                .. AzCopyArguments.AzureCliGenerateBlobReadSas(
                    settings.StorageAccount,
                    settings.Container,
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
            sasStartsUtc,
            sasExpiresUtc);

        var archivePath = Path.Combine(
            Path.GetTempPath(),
            $"syncsaw-cluster-package-{Guid.NewGuid():N}.zip");
        try
        {
            var payloadCount = await CreateArchiveAsync(
                settings.LocalFolder,
                archivePath,
                sasUri,
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
                payloadCount);
        }
        finally
        {
            File.Delete(archivePath);
        }
    }

    private static async Task<int> CreateArchiveAsync(
        string sourceRoot,
        string archivePath,
        Uri packageUri,
        DateTimeOffset issuedUtc,
        DateTimeOffset expiresUtc,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(sourceRoot);
        var payloadFiles = EnumeratePayloadFiles(root).ToArray();
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
                    SchemaVersion = 1,
                    PackageUri = packageUri.AbsoluteUri,
                    IssuedUtc = issuedUtc,
                    ExpiresUtc = expiresUtc
                },
                new JsonSerializerOptions { WriteIndented = true });
        }

        return payloadFiles.Length;
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
                    relative.StartsWith(".syncsaw/", StringComparison.OrdinalIgnoreCase))
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
                if (!ClusterPackage.IsReservedPath(relative))
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
        Uri expectedBlobUri,
        DateTimeOffset expectedStart,
        DateTimeOffset expectedExpiry)
    {
        var candidate = (output ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(line => Uri.TryCreate(line, UriKind.Absolute, out _));
        if (candidate is null || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.Equals(expectedBlobUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.Equals(expectedBlobUri.AbsolutePath, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Azure CLI returned an invalid or unexpected cluster package SAS URL.");
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("sr", out var resource) || resource != "b" ||
            !query.TryGetValue("sp", out var permissions) || permissions != "r" ||
            !query.ContainsKey("sig") ||
            !query.ContainsKey("skoid") ||
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
                "Azure CLI returned a SAS that is not the requested seven-day, read-only Blob SAS.");
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
