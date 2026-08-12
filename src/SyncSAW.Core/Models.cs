namespace SyncSAW.Core;

public enum EntraLoginMode
{
    AzureCli,
    DeviceCode
}

public enum AppTheme
{
    System,
    Light,
    Dark
}

public enum SyncItemState
{
    InSync,
    Pending,
    LocalOnly,
    RemoteOnly,
    Error
}

public sealed class SyncSettings
{
    public const string DefaultTenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";
    public const string DefaultSubscriptionId = "a0d901ba-9956-4f7d-830c-2d7974c36666";

    public string LocalFolder { get; set; } = string.Empty;
    public string StorageAccount { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public bool PauseSync { get; set; }
    public int AutoSyncIntervalSeconds { get; set; } = 10;
    public bool MinimizeToTray { get; set; } = true;
    public string? AzCopyPath { get; set; }
    public string? AzureCliPath { get; set; }
    public string? TenantId { get; set; } = DefaultTenantId;
    public string? SubscriptionId { get; set; } = DefaultSubscriptionId;
    public EntraLoginMode LoginMode { get; set; } = EntraLoginMode.AzureCli;
    public AppTheme Theme { get; set; } = AppTheme.System;
}

public sealed record LocalFileInfo(string Path, DateTimeOffset LastModified, long Size);

public sealed record RemoteBlobInfo(
    string Path,
    DateTimeOffset? LastModified,
    long? Size,
    bool SyncedToSaw = false);

public sealed record PlannedTransfer(string Path, string Action);

public sealed record SyncItem(
    string Path,
    SyncItemState State,
    DateTimeOffset? LastModified,
    long? Size,
    string Action,
    string? Error,
    bool SyncedToSaw = false);

public sealed record SyncSnapshot(
    IReadOnlyList<SyncItem> Items,
    IReadOnlyList<RemoteBlobInfo> RemoteBlobs,
    IReadOnlyList<PlannedTransfer> Plan);

public sealed record AzCopyCommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool WasCancelled = false)
{
    public bool Succeeded => ExitCode == 0 && !WasCancelled;
}

public sealed class AzCopyException : Exception
{
    public AzCopyException(string message, AzCopyCommandResult result)
        : base(message)
    {
        Result = result;
    }

    public AzCopyCommandResult Result { get; }
}
