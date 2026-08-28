using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class FileHierarchyTests
{
    [Fact]
    public void Build_CreatesExpandableNestedFolders()
    {
        var items = new[]
        {
            Item("root.txt"),
            Item("docs/readme.txt"),
            Item("docs/images/logo.png")
        };

        var collapsed = FileHierarchy.Build(
            items,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var expanded = FileHierarchy.Build(
            items,
            new HashSet<string>(["docs/", "docs/images/"], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["docs/", "root.txt"], collapsed.Select(entry => entry.Path));
        Assert.Equal(
            ["docs/", "docs/images/", "docs/images/logo.png", "docs/readme.txt", "root.txt"],
            expanded.Select(entry => entry.Path));
        Assert.Equal(2, expanded.Single(entry => entry.Path == "docs/images/logo.png").Depth);
    }

    [Fact]
    public void Build_FolderContainsEveryDescendantFilePath()
    {
        var entries = FileHierarchy.Build(
            [Item("docs/a.txt"), Item("docs/nested/b.txt"), Item("other.txt")],
            new HashSet<string>(["docs/"], StringComparer.OrdinalIgnoreCase));

        var folder = entries.Single(entry => entry.Path == "docs/");

        Assert.Equal(["docs/a.txt", "docs/nested/b.txt"], folder.DescendantFilePaths);
    }

    [Fact]
    public void Build_AggregatesFolderDetails()
    {
        var older = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var newer = older.AddMinutes(5);
        var entries = FileHierarchy.Build(
            [
                Item(
                    "folder/a.txt",
                    SyncItemState.InSync,
                    older,
                    10,
                    "None",
                    syncedToSaw: true),
                Item(
                    "folder/b.txt",
                    SyncItemState.Pending,
                    newer,
                    20,
                    "Upload",
                    "Waiting",
                    syncedToSaw: false)
            ],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var folder = Assert.Single(entries);

        Assert.True(folder.IsFolder);
        Assert.Equal(SyncItemState.Mixed, folder.State);
        Assert.Equal(newer, folder.LastModified);
        Assert.Equal(30, folder.Size);
        Assert.Equal("Mixed", folder.Action);
        Assert.Equal("Waiting", folder.Error);
        Assert.False(folder.SyncedToSaw);
    }

    [Fact]
    public void Build_SortsSiblingsAndKeepsFoldersBeforeFiles()
    {
        var items = new[]
        {
            Item("small.txt", size: 1),
            Item("large.txt", size: 20),
            Item("z-folder/child.txt", size: 10),
            Item("a-folder/child.txt", size: 5)
        };

        var ascending = FileHierarchy.Build(
            items,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FileHierarchySortField.Size,
            FileHierarchySortDirection.Ascending);
        var descending = FileHierarchy.Build(
            items,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            FileHierarchySortField.Size,
            FileHierarchySortDirection.Descending);

        Assert.Equal(
            ["a-folder/", "z-folder/", "small.txt", "large.txt"],
            ascending.Select(entry => entry.Path));
        Assert.Equal(
            ["z-folder/", "a-folder/", "large.txt", "small.txt"],
            descending.Select(entry => entry.Path));
    }

    [Fact]
    public void Build_PreservesFileAndFolderWithSameName()
    {
        var entries = FileHierarchy.Build(
            [Item("alpha"), Item("alpha/child.txt")],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["folder:alpha/", "file:alpha"], entries.Select(entry => entry.Key));
    }

    [Fact]
    public void GetFolderPaths_NormalizesWindowsSeparators()
    {
        var items = new[]
        {
            Item(@"\docs\nested\file.txt"),
            Item("/images/logo.png/")
        };

        var folders = FileHierarchy.GetFolderPaths(items);
        var entries = FileHierarchy.Build(
            items,
            new HashSet<string>(
                ["docs/", "docs/nested/", "images/"],
                StringComparer.OrdinalIgnoreCase));

        Assert.Equal(
            ["docs/", "docs/nested/", "images/"],
            folders.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        Assert.Contains(entries, entry => entry.Path == "docs/nested/file.txt");
        Assert.Contains(entries, entry => entry.Path == "images/logo.png");
    }

    [Fact]
    public void GetDeletionPaths_ExpandsFoldersAndDeduplicatesOverlappingSelection()
    {
        var entries = FileHierarchy.Build(
            [Item("docs/a.txt"), Item("docs/nested/b.txt"), Item("remote.txt"), Item("local.txt")],
            new HashSet<string>(["docs/"], StringComparer.OrdinalIgnoreCase));
        var selected = new[]
        {
            entries.Single(entry => entry.Path == "docs/"),
            entries.Single(entry => entry.Path == "docs/a.txt"),
            entries.Single(entry => entry.Path == "remote.txt"),
            entries.Single(entry => entry.Path == "local.txt")
        };

        var paths = FileHierarchy.GetDeletionPaths(
            selected,
            new HashSet<string>(
                ["docs/a.txt", "docs/nested/b.txt", "remote.txt"],
                StringComparer.OrdinalIgnoreCase));

        Assert.Equal(["docs/a.txt", "docs/nested/b.txt", "remote.txt"], paths);
    }

    private static SyncItem Item(
        string path,
        SyncItemState state = SyncItemState.InSync,
        DateTimeOffset? lastModified = null,
        long? size = 1,
        string action = "None",
        string? error = null,
        bool syncedToSaw = false) =>
        new(
            path,
            state,
            lastModified ?? DateTimeOffset.Parse("2026-08-01T10:00:00Z"),
            size,
            action,
            error,
            syncedToSaw);
}
