using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_MigratesReadOnlyLegacySettingsWithoutDeletingSource()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.Settings.");
        var currentPath = Path.Combine(directory.FullName, "current", "settings.json");
        var legacyPath = Path.Combine(directory.FullName, "legacy.json");
        try
        {
            await File.WriteAllTextAsync(
                legacyPath,
                """{"StorageAccount":"account123","Container":"container","DeletionMode":true}""");
            File.SetAttributes(legacyPath, FileAttributes.ReadOnly);
            var store = new SettingsStore(currentPath, legacyPath);

            var settings = await store.LoadAsync();

            Assert.Equal("account123", settings.StorageAccount);
            Assert.True(File.Exists(currentPath));
            Assert.True(File.Exists(legacyPath));
            Assert.DoesNotContain(
                "DeletionMode",
                await File.ReadAllTextAsync(currentPath),
                StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(legacyPath))
            {
                File.SetAttributes(legacyPath, FileAttributes.Normal);
            }
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Defaults_TargetLocalApplicationData()
    {
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SyncSAW",
                "settings.json"),
            SettingsStore.DefaultPath);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SyncSAW",
                "Logs"),
            OperationLog.DefaultDirectory);
    }
}
