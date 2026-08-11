using SyncSAW.Core;

namespace SyncSAW.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public async Task LoadAsync_MigratesLegacySettingsBesideExecutable()
    {
        var directory = Directory.CreateTempSubdirectory("SyncSAW.Settings.");
        var currentPath = Path.Combine(directory.FullName, "current", "settings.json");
        var legacyPath = Path.Combine(directory.FullName, "legacy.json");
        try
        {
            await File.WriteAllTextAsync(
                legacyPath,
                """{"StorageAccount":"account123","Container":"container"}""");
            var store = new SettingsStore(currentPath, legacyPath);

            var settings = await store.LoadAsync();

            Assert.Equal("account123", settings.StorageAccount);
            Assert.True(File.Exists(currentPath));
            Assert.False(File.Exists(legacyPath));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Defaults_TargetTheExecutableDirectory()
    {
        Assert.Equal(AppContext.BaseDirectory, OperationLog.DefaultDirectory);
        Assert.Equal(
            Path.Combine(AppContext.BaseDirectory, "settings.json"),
            SettingsStore.DefaultPath);
    }
}
