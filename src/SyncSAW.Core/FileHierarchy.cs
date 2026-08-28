namespace SyncSAW.Core;

public enum FileHierarchySortField
{
    Name,
    State,
    LastModified,
    Size,
    Action,
    SyncedToSaw,
    Error
}

public enum FileHierarchySortDirection
{
    Ascending,
    Descending
}

public sealed record FileHierarchyEntry(
    string Key,
    string Path,
    string Name,
    bool IsFolder,
    int Depth,
    bool IsExpanded,
    SyncItemState State,
    DateTimeOffset? LastModified,
    long? Size,
    string Action,
    string? Error,
    bool SyncedToSaw,
    IReadOnlyList<string> DescendantFilePaths)
{
    public DateTimeOffset? LocalLastModified => LastModified?.ToLocalTime();
}

public static class FileHierarchy
{
    public static IReadOnlySet<string> GetFolderPaths(IReadOnlyList<SyncItem> items)
    {
        var root = BuildTree(items);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddFolderPaths(root, paths);
        return paths;
    }

    public static IReadOnlyList<FileHierarchyEntry> Build(
        IReadOnlyList<SyncItem> items,
        IReadOnlySet<string> expandedFolders,
        FileHierarchySortField sortField = FileHierarchySortField.Name,
        FileHierarchySortDirection sortDirection = FileHierarchySortDirection.Ascending)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(expandedFolders);

        var root = BuildTree(items);
        PopulateSummaries(root);
        var result = new List<FileHierarchyEntry>();
        AddVisibleChildren(
            root,
            depth: 0,
            expandedFolders,
            sortField,
            sortDirection,
            result);
        return result;
    }

    public static IReadOnlyList<string> GetDeletionPaths(
        IEnumerable<FileHierarchyEntry> selectedEntries,
        IReadOnlySet<string> remotePaths)
    {
        ArgumentNullException.ThrowIfNull(selectedEntries);
        ArgumentNullException.ThrowIfNull(remotePaths);

        return selectedEntries
            .SelectMany(entry =>
                entry.IsFolder
                    ? entry.DescendantFilePaths
                    : remotePaths.Contains(entry.Path)
                        ? [entry.Path]
                        : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Node BuildTree(IReadOnlyList<SyncItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var root = new Node(string.Empty, string.Empty, isFolder: true);
        foreach (var item in items)
        {
            var normalized = NormalizeFilePath(item.Path);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            var segments = normalized.Split('/');
            var parent = root;
            var parentPath = string.Empty;
            for (var index = 0; index < segments.Length - 1; index++)
            {
                var name = segments[index];
                parentPath = string.IsNullOrEmpty(parentPath)
                    ? name
                    : $"{parentPath}/{name}";
                parent = parent.GetOrAddFolder(name, parentPath);
            }
            parent.AddFile(segments[^1], normalized, item with { Path = normalized });
        }
        return root;
    }

    private static void AddFolderPaths(Node parent, ISet<string> paths)
    {
        foreach (var folder in parent.Children.Values.Where(child => child.IsFolder))
        {
            paths.Add(ToFolderPath(folder.Path));
            AddFolderPaths(folder, paths);
        }
    }

    private static Summary PopulateSummaries(Node node)
    {
        if (!node.IsFolder)
        {
            node.Summary = Summary.FromFile(node.Item!);
            return node.Summary;
        }

        var descendants = new List<SyncItem>();
        foreach (var child in node.Children.Values)
        {
            PopulateSummaries(child);
            descendants.AddRange(child.Summary.Files);
        }
        node.Summary = Summary.FromFiles(descendants);
        return node.Summary;
    }

    private static void AddVisibleChildren(
        Node parent,
        int depth,
        IReadOnlySet<string> expandedFolders,
        FileHierarchySortField sortField,
        FileHierarchySortDirection sortDirection,
        ICollection<FileHierarchyEntry> result)
    {
        foreach (var node in SortChildren(parent.Children.Values, sortField, sortDirection))
        {
            if (node.IsFolder)
            {
                var folderPath = ToFolderPath(node.Path);
                var isExpanded = expandedFolders.Contains(folderPath);
                result.Add(new(
                    $"folder:{folderPath}",
                    folderPath,
                    node.Name,
                    IsFolder: true,
                    depth,
                    isExpanded,
                    node.Summary.State,
                    node.Summary.LastModified,
                    node.Summary.Size,
                    node.Summary.Action,
                    node.Summary.Error,
                    node.Summary.SyncedToSaw,
                    node.Summary.Files
                        .Select(file => file.Path)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray()));
                if (isExpanded)
                {
                    AddVisibleChildren(
                        node,
                        depth + 1,
                        expandedFolders,
                        sortField,
                        sortDirection,
                        result);
                }
                continue;
            }

            var item = node.Item!;
            result.Add(new(
                $"file:{item.Path}",
                item.Path,
                node.Name,
                IsFolder: false,
                depth,
                IsExpanded: false,
                item.State,
                item.LastModified,
                item.Size,
                item.Action,
                item.Error,
                item.SyncedToSaw,
                [item.Path]));
        }
    }

    private static IEnumerable<Node> SortChildren(
        IEnumerable<Node> children,
        FileHierarchySortField sortField,
        FileHierarchySortDirection sortDirection) =>
        children.Order(new NodeComparer(sortField, sortDirection));

    private static string NormalizeFilePath(string path) =>
        (path ?? string.Empty)
            .Trim()
            .Replace('\\', '/')
            .Trim('/');

    private static string ToFolderPath(string path) => $"{path.TrimEnd('/')}/";

    private sealed class Node(string name, string path, bool isFolder)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public bool IsFolder { get; } = isFolder;
        public SyncItem? Item { get; private set; }
        public Dictionary<string, Node> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Summary Summary { get; set; } = Summary.Empty;

        public Node GetOrAddFolder(string childName, string childPath)
        {
            var key = $"folder:{childName}";
            if (!Children.TryGetValue(key, out var folder))
            {
                folder = new Node(childName, childPath, isFolder: true);
                Children.Add(key, folder);
            }
            return folder;
        }

        public void AddFile(string childName, string childPath, SyncItem item)
        {
            var key = $"file:{childName}";
            var file = new Node(childName, childPath, isFolder: false)
            {
                Item = item
            };
            Children[key] = file;
        }
    }

    private sealed record Summary(
        IReadOnlyList<SyncItem> Files,
        SyncItemState State,
        DateTimeOffset? LastModified,
        long? Size,
        string Action,
        string? Error,
        bool SyncedToSaw)
    {
        public static Summary Empty { get; } =
            new([], SyncItemState.InSync, null, null, "None", null, false);

        public static Summary FromFile(SyncItem item) =>
            new(
                [item],
                item.State,
                item.LastModified,
                item.Size,
                item.Action,
                item.Error,
                item.SyncedToSaw);

        public static Summary FromFiles(IReadOnlyList<SyncItem> files)
        {
            if (files.Count == 0)
            {
                return Empty;
            }

            var states = files.Select(file => file.State).Distinct().ToArray();
            var state = states.Length == 1
                ? states[0]
                : states.Contains(SyncItemState.Error)
                    ? SyncItemState.Error
                    : SyncItemState.Mixed;
            var sizes = files
                .Where(file => file.Size.HasValue)
                .Select(file => file.Size!.Value)
                .ToArray();
            long? totalSize = null;
            if (sizes.Length > 0)
            {
                var total = 0L;
                foreach (var size in sizes)
                {
                    total = size > long.MaxValue - total
                        ? long.MaxValue
                        : total + size;
                }
                totalSize = total;
            }

            var actions = files
                .Select(file => file.Action)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var errors = files
                .Select(file => file.Error)
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            return new(
                files.ToArray(),
                state,
                files
                    .Where(file => file.LastModified.HasValue)
                    .Select(file => file.LastModified)
                    .Max(),
                totalSize,
                actions.Length == 1 ? actions[0] : "Mixed",
                errors.Length switch
                {
                    0 => null,
                    1 => errors[0],
                    _ => $"{errors.Length:N0} errors"
                },
                files.All(file => file.SyncedToSaw));
        }
    }

    private sealed class NodeComparer(
        FileHierarchySortField field,
        FileHierarchySortDirection direction) : IComparer<Node>
    {
        public int Compare(Node? first, Node? second)
        {
            if (ReferenceEquals(first, second))
            {
                return 0;
            }
            if (first is null)
            {
                return 1;
            }
            if (second is null)
            {
                return -1;
            }
            if (first.IsFolder != second.IsFolder)
            {
                return first.IsFolder ? -1 : 1;
            }

            var comparison = field switch
            {
                FileHierarchySortField.Name => CompareStrings(first.Name, second.Name),
                FileHierarchySortField.State =>
                    first.Summary.State.CompareTo(second.Summary.State),
                FileHierarchySortField.LastModified =>
                    CompareNullable(first.Summary.LastModified, second.Summary.LastModified),
                FileHierarchySortField.Size =>
                    CompareNullable(first.Summary.Size, second.Summary.Size),
                FileHierarchySortField.Action =>
                    CompareStrings(first.Summary.Action, second.Summary.Action),
                FileHierarchySortField.SyncedToSaw =>
                    first.Summary.SyncedToSaw.CompareTo(second.Summary.SyncedToSaw),
                FileHierarchySortField.Error =>
                    CompareStrings(first.Summary.Error, second.Summary.Error),
                _ => 0
            };
            if (comparison != 0 &&
                direction == FileHierarchySortDirection.Descending)
            {
                comparison = -comparison;
            }
            return comparison != 0
                ? comparison
                : CompareStrings(first.Name, second.Name);
        }

        private static int CompareStrings(string? first, string? second)
        {
            if (first is null)
            {
                return second is null ? 0 : 1;
            }
            return second is null
                ? -1
                : StringComparer.OrdinalIgnoreCase.Compare(first, second);
        }

        private static int CompareNullable<T>(T? first, T? second)
            where T : struct, IComparable<T>
        {
            if (!first.HasValue)
            {
                return second.HasValue ? 1 : 0;
            }
            return !second.HasValue
                ? -1
                : first.Value.CompareTo(second.Value);
        }
    }
}
