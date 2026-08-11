using System.Text.Json;

namespace SyncSAW.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;
    private readonly string? _legacySettingsPath;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public SettingsStore(
        string? settingsPath = null,
        string? legacySettingsPath = null)
    {
        _settingsPath = settingsPath ?? DefaultPath;
        _legacySettingsPath = legacySettingsPath ?? LegacyPath;
    }

    public static string DefaultPath => Path.Combine(
        AppContext.BaseDirectory,
        "settings.json");

    private static string LegacyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SyncSAW",
        "settings.json");

    public async Task<SyncSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_settingsPath))
        {
            if (string.IsNullOrWhiteSpace(_legacySettingsPath) ||
                !File.Exists(_legacySettingsPath))
            {
                return new SyncSettings();
            }

            var migratedSettings = await ReadAsync(_legacySettingsPath, cancellationToken);
            await SaveAsync(migratedSettings, cancellationToken);
            File.Delete(_legacySettingsPath);
            return migratedSettings;
        }

        return await ReadAsync(_settingsPath, cancellationToken);
    }

    private static async Task<SyncSettings> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<SyncSettings>(
                   stream,
                   SerializerOptions,
                   cancellationToken) ?? new SyncSettings();
    }

    public async Task SaveAsync(SyncSettings settings, CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var temporaryPath = $"{_settingsPath}.tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(stream, settings, SerializerOptions, cancellationToken);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            _saveGate.Release();
        }
    }
}
