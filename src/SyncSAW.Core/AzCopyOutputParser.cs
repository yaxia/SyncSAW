using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SyncSAW.Core;

public static partial class AzCopyOutputParser
{
    [GeneratedRegex(@"DRYRUN:\s*(?<action>[A-Za-z]+)\s+(?<path>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DryRunRegex();

    [GeneratedRegex(
        @"(?:File|Blob):\s*(?<path>[^;]+)(?:;\s*Content Length:\s*(?<size>\d+))?(?:;\s*Last Modified:\s*(?<modified>[^;]+))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ListLineRegex();

    [GeneratedRegex(@"https://[^\s""']+\.blob\.core\.windows\.net/[^\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex BlobUrlRegex();

    public static IReadOnlyList<PlannedTransfer> ParsePlan(string output)
    {
        var items = new Dictionary<string, PlannedTransfer>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in ExpandPayloads(output))
        {
            if (TryParseJson(payload, out var document))
            {
                using (document)
                {
                    CollectPlanObjects(document.RootElement, items);
                }
                continue;
            }

            var match = DryRunRegex().Match(payload.Trim());
            if (match.Success)
            {
                AddPlan(items, match.Groups["path"].Value, match.Groups["action"].Value);
            }
        }

        return items.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<RemoteBlobInfo> ParseRemoteList(string output)
    {
        var items = new Dictionary<string, RemoteBlobInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var payload in ExpandPayloads(output))
        {
            if (TryParseJson(payload, out var document))
            {
                using (document)
                {
                    CollectRemoteObjects(document.RootElement, items);
                }
                continue;
            }

            var match = ListLineRegex().Match(payload.Trim());
            if (match.Success)
            {
                AddRemote(
                    items,
                    match.Groups["path"].Value,
                    ParseLong(match.Groups["size"].Value),
                    ParseDate(match.Groups["modified"].Value));
            }
        }

        return items.Values.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<string> ExpandPayloads(string output)
    {
        foreach (var line in (output ?? string.Empty).Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return line;
            if (!TryParseJson(line, out var document))
            {
                continue;
            }

            using (document)
            {
                foreach (var value in EnumerateStrings(document.RootElement))
                {
                    yield return value;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString() ?? string.Empty;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var value in EnumerateStrings(property.Value))
                {
                    yield return value;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                foreach (var value in EnumerateStrings(child))
                {
                    yield return value;
                }
            }
        }
    }

    private static void CollectPlanObjects(
        JsonElement element,
        IDictionary<string, PlannedTransfer> destination)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var path = ReadString(element, "Path", "RelativePath", "Name");
            var action = ReadString(element, "Action", "Operation", "TransferType");
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(action))
            {
                AddPlan(destination, path, action);
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectPlanObjects(property.Value, destination);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CollectPlanObjects(child, destination);
            }
        }
    }

    private static void CollectRemoteObjects(
        JsonElement element,
        IDictionary<string, RemoteBlobInfo> destination)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var path = ReadString(element, "Path", "RelativePath", "Name");
            if (!string.IsNullOrWhiteSpace(path))
            {
                AddRemote(
                    destination,
                    path,
                    ReadLong(element, "ContentLength", "Content-Length", "Size"),
                    ReadDate(element, "LastModifiedTime", "LastModified", "Last-Modified"));
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectRemoteObjects(property.Value, destination);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                CollectRemoteObjects(child, destination);
            }
        }
    }

    private static void AddPlan(
        IDictionary<string, PlannedTransfer> destination,
        string path,
        string action)
    {
        var normalized = NormalizePlannedPath(path);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            destination[normalized] = new PlannedTransfer(normalized, action.Trim());
        }
    }

    private static void AddRemote(
        IDictionary<string, RemoteBlobInfo> destination,
        string path,
        long? size,
        DateTimeOffset? modified)
    {
        var normalized = NormalizeOutputPath(path);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            destination[normalized] = new RemoteBlobInfo(normalized, modified, size);
        }
    }

    private static string NormalizeOutputPath(string value) =>
        value.Trim().Trim('"').Replace('\\', '/').TrimStart('/');

    private static string NormalizePlannedPath(string value)
    {
        var urlMatch = BlobUrlRegex().Match(value);
        if (urlMatch.Success &&
            Uri.TryCreate(urlMatch.Value.TrimEnd('.', ',', ';', ')'), UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 1)
            {
                return string.Join("/", segments.Skip(1).Select(Uri.UnescapeDataString));
            }
        }

        return NormalizeOutputPath(value);
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!names.Any(name => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number &&
                property.Value.TryGetInt64(out var number))
            {
                return number;
            }

            if (property.Value.ValueKind == JsonValueKind.String)
            {
                return ParseLong(property.Value.GetString());
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, params string[] names)
    {
        var value = ReadString(element, names);
        return ParseDate(value);
    }

    private static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static DateTimeOffset? ParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var result)
            ? result
            : null;

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}
