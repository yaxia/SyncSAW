namespace SyncSAW.Core;

public static class ApplicationDataPaths
{
    public static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SyncSAW");

    public static string SettingsPath => Path.Combine(RootDirectory, "settings.json");

    public static string LogDirectory => Path.Combine(RootDirectory, "Logs");
}
