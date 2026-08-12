using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SyncSAW.Core;
using Forms = System.Windows.Forms;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace SyncSAW.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<SyncItem> _items = [];
    private readonly SettingsStore _settingsStore = new();
    private readonly OperationLog _operationLog;
    private readonly AzCopyProcessRunner _processRunner;
    private readonly AzCopyService _azCopy;
    private readonly NonOverlappingOperationScheduler _scheduler = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Icon _applicationIcon;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly HashSet<string> _remotePaths = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _currentOperationCts;
    private AppTheme _currentTheme = AppTheme.System;
    private bool _initialized;
    private bool _isShuttingDown;
    private bool _credentialValid;
    private bool _deletionQueuedOrRunning;
    private bool _restoringFileSelection;
    private bool _settingsPersistenceWarningShown;
    private DateTimeOffset _nextAutomaticSyncUtc;

    public MainWindow()
    {
        _operationLog = new OperationLog();
        _processRunner = new AzCopyProcessRunner(_operationLog);
        _azCopy = new AzCopyService(_processRunner);
        InitializeComponent();
        OperationLogLocationTextBlock.Text =
            $"Operation logs: {OperationLog.DefaultDirectory}";
        FilesDataGrid.ItemsSource = _items;
        UpdateFileActionButtons();
        LoginModeComboBox.SelectedIndex = 0;
        ThemeComboBox.SelectedIndex = 0;
        UpdateConfigurationSummary();

        _applicationIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!)
            ?? throw new InvalidOperationException("The SyncSAW executable icon could not be loaded.");
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = "SyncSAW Azure Blob Sync",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        Loaded += Window_Loaded;
        Closing += Window_Closing;
        StateChanged += Window_StateChanged;
        SourceInitialized += (_, _) => ThemeManager.Apply(_currentTheme, this);
        PreviewKeyDown += Window_PreviewKeyDown;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _operationLog.WriteEventAsync(
                $"Application started. Logs: {OperationLog.DefaultDirectory}");
            ApplySettings(await _settingsStore.LoadAsync(_lifetime.Token));
            _initialized = true;
            _nextAutomaticSyncUtc =
                DateTimeOffset.UtcNow.AddSeconds(GetSelectedIntervalSeconds());
            _ = PeriodicLoopAsync(_lifetime.Token);
            if (IsConfigured(CaptureSettings()))
            {
                await RefreshExclusiveAsync(showSkippedMessage: false);
            }
        }
        catch (OperationCanceledException)
        {
            // App shutdown.
        }
        catch (Exception exception)
        {
            await LogErrorAsync("Application startup", exception);
            ShowError(exception);
        }
    }

    private async Task PeriodicLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (DateTimeOffset.UtcNow < _nextAutomaticSyncUtc)
                {
                    continue;
                }

                var settings = CaptureSettings();
                _nextAutomaticSyncUtc =
                    DateTimeOffset.UtcNow.AddSeconds(settings.AutoSyncIntervalSeconds);
                if (!IsConfigured(settings))
                {
                    continue;
                }

                try
                {
                    await _scheduler.TryRunAsync(
                        async token =>
                        {
                            var snapshot = await RefreshCoreAsync(settings, token);
                            if (!settings.PauseSync && snapshot.Plan.Count > 0)
                            {
                                await SetBusyAsync("Synchronizing with AzCopy...");
                                await _azCopy.SynchronizeAsync(settings, token);
                                await RefreshCoreAsync(settings, token);
                            }
                        },
                        cancellationToken);
                }
                catch (Exception exception)
                {
                    await LogErrorAsync("Automatic synchronization", exception);
                    await Dispatcher.InvokeAsync(() => ReportBackgroundError(exception));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // App shutdown.
        }
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the local synchronization folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(LocalFolderTextBox.Text) ? LocalFolderTextBox.Text : string.Empty
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            LocalFolderTextBox.Text = dialog.SelectedPath;
            await SaveSettingsAsync();
        }
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(
            "Opening Microsoft Entra sign-in...",
            async (settings, token) =>
            {
                if (settings.LoginMode == EntraLoginMode.DeviceCode)
                {
                    Process.Start(new ProcessStartInfo("https://microsoft.com/devicelogin")
                    {
                        UseShellExecute = true
                    });
                    SetStatus("Enter the code shown in the AzCopy sign-in window in the browser.");
                }
                else
                {
                    SetStatus($"Complete Windows sign-in for tenant {settings.TenantId}.");
                }

                await _azCopy.LoginAsync(settings, token);
                SetStatus(
                    settings.LoginMode == EntraLoginMode.AzureCli
                        ? string.IsNullOrWhiteSpace(settings.SubscriptionId)
                            ? "Signed in with Azure CLI; its existing subscription selection was kept."
                            : $"Signed in with Azure CLI; subscription {settings.SubscriptionId} is selected."
                        : "Signed in with AzCopy device code. No account keys are stored.");
                await RefreshCoreAsync(settings, token);
            });
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshExclusiveAsync(showSkippedMessage: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Refresh cancelled.");
        }
        catch (Exception exception)
        {
            await LogErrorAsync("Refresh", exception);
            ShowError(exception);
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        await RunExclusiveAsync(
            "Uploading local changes and downloading cloud-only files...",
            async (current, token) =>
            {
                await _azCopy.SynchronizeAsync(current, token);
                await RefreshCoreAsync(current, token);
                SetStatus($"Synchronization completed at {DateTime.Now:T}.");
                ShowTrayBalloon(
                    "Synchronization complete",
                    "Local changes were uploaded and cloud-only files were downloaded.");
            });
    }

    private async void UploadFile_Click(object sender, RoutedEventArgs e)
    {
        var settings = CaptureSettings();
        var dialog = new OpenFileDialog
        {
            Title = "Choose a file to upload or update",
            InitialDirectory = Directory.Exists(settings.LocalFolder) ? settings.LocalFolder : null
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var blobPath = GetSafeRelativePath(settings.LocalFolder, dialog.FileName);
        await RunExclusiveAsync(
            $"Uploading {blobPath}...",
            async (current, token) =>
            {
                await _azCopy.UploadAsync(current, dialog.FileName, blobPath, token);
                await RefreshCoreAsync(current, token);
                SetStatus($"Uploaded {blobPath}.");
            });
    }

    private async void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRemote(out var item))
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Download remote blob",
            FileName = Path.GetFileName(item.Path),
            InitialDirectory = Directory.Exists(LocalFolderTextBox.Text) ? LocalFolderTextBox.Text : null
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunExclusiveAsync(
            $"Downloading {item.Path}...",
            async (settings, token) =>
            {
                await _azCopy.DownloadAsync(settings, item.Path, dialog.FileName, token);
                await RefreshCoreAsync(settings, token);
                SetStatus($"Downloaded {item.Path}.");
            });
    }

    private async void OpenRemote_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedRemote(out var item))
        {
            return;
        }

        await RunExclusiveAsync(
            $"Downloading {item.Path} for read-only viewing...",
            async (settings, token) =>
            {
                var tempFolder = Path.Combine(Path.GetTempPath(), "SyncSAW", Guid.NewGuid().ToString("N"));
                var localPath = Path.Combine(tempFolder, Path.GetFileName(item.Path));
                await _azCopy.DownloadAsync(settings, item.Path, localPath, token);
                Process.Start(new ProcessStartInfo(localPath) { UseShellExecute = true });
                SetStatus($"Opened a temporary copy of {item.Path}.");
            });
    }

    private async void DeleteRemote_Click(object sender, RoutedEventArgs e)
    {
        var items = GetSelectedRemoteItems();
        if (items.Count == 0)
        {
            ShowRemoteSelectionRequired(singleSelection: false);
            return;
        }

        var settings = CaptureSettings();
        var paths = items
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var localFiles = GetExistingLocalFiles(settings.LocalFolder, paths);
        var preview = string.Join(
            Environment.NewLine,
            paths.Take(5).Select(path => $"• {path}"));
        if (paths.Length > 5)
        {
            preview += $"{Environment.NewLine}• …and {paths.Length - 5:N0} more";
        }
        var localImpact = localFiles.Count == 0
            ? string.Empty
            : $"{Environment.NewLine}{Environment.NewLine}" +
              $"{localFiles.Count:N0} matching local " +
              (localFiles.Count == 1 ? "file will" : "files will") +
              " also be deleted to prevent automatic re-upload.";
        var sawImpact = $"{Environment.NewLine}{Environment.NewLine}" +
                        "Any matching copy on SAW will be deleted on its next synchronization check.";
        if (!ConfirmationDialog.Show(
                this,
                paths.Length == 1 ? "Delete selected file?" : $"Delete {paths.Length:N0} selected files?",
                $"This immediately and permanently deletes the selected Blob" +
                (paths.Length == 1 ? string.Empty : "s") +
                $" from Azure Storage.{localImpact}{sawImpact}{Environment.NewLine}{Environment.NewLine}{preview}",
                paths.Length == 1 ? "Delete file" : $"Delete {paths.Length:N0} files"))
        {
            return;
        }

        _deletionQueuedOrRunning = true;
        UpdateFileActionButtons();
        try
        {
            await RunExclusiveAsync(
                $"Deleting {paths.Length:N0} selected " +
                (paths.Length == 1 ? "file" : "files") + "...",
                async (current, token) =>
                {
                    DeleteLocalFiles(localFiles);
                    await _azCopy.DeleteRemoteBatchAsync(current, paths, token);

                    await RefreshCoreAsync(current, token);
                    SetStatus(
                        $"Deleted {paths.Length:N0} " +
                        (paths.Length == 1 ? "file" : "files") +
                        " from Azure immediately; SAW cleanup is queued for its next running cycle.");
                },
                queueIfBusy: true,
                queuedStatus: $"Deletion of {paths.Length:N0} selected " +
                              (paths.Length == 1 ? "file" : "files") +
                              " is queued behind the active synchronization.");
        }
        finally
        {
            _deletionQueuedOrRunning = false;
            UpdateFileActionButtons();
        }
    }

    private async Task RefreshExclusiveAsync(bool showSkippedMessage)
    {
        var ran = await _scheduler.TryRunAsync(
            token => RefreshCoreAsync(CaptureSettings(), token),
            _lifetime.Token);
        if (!ran && showSkippedMessage)
        {
            SetStatus("Another AzCopy operation is already running; refresh was skipped.");
        }
    }

    private async Task<SyncSnapshot> RefreshCoreAsync(
        SyncSettings settings,
        CancellationToken cancellationToken)
    {
        await SetBusyAsync("Refreshing remote files and AzCopy plan...");
        try
        {
            var snapshot = await _azCopy.GetSnapshotAsync(settings, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                ReplaceFileItemsPreservingSelection(snapshot.Items);

                _remotePaths.Clear();
                foreach (var blob in snapshot.RemoteBlobs)
                {
                    _remotePaths.Add(blob.Path);
                }
                UpdateFileActionButtons();

                FileCountTextBlock.Text = $"{snapshot.Items.Count:N0} " +
                    (snapshot.Items.Count == 1 ? "file" : "files");
                SyncStatusTitleTextBlock.Text = snapshot.Plan.Count == 0
                    ? "Everything is up to date"
                    : $"{snapshot.Plan.Count:N0} pending " +
                      (snapshot.Plan.Count == 1 ? "action" : "actions");
                SyncStatusDetailTextBlock.Text =
                    $"{snapshot.RemoteBlobs.Count:N0} remote blobs • checked {DateTime.Now:t}";
                SetSyncStatusVisual("SuccessBackgroundBrush", "SuccessBrush", "\uE73E");
                SetConnectionStatus(true);
                SetStatus(
                    $"Updated {DateTime.Now:T} • {snapshot.RemoteBlobs.Count:N0} remote blobs • " +
                    $"{snapshot.Plan.Count:N0} planned actions");
            });
            return snapshot;
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => SetBusy(false));
        }
    }

    private async Task RunExclusiveAsync(
        string activity,
        Func<SyncSettings, CancellationToken, Task> operation,
        bool queueIfBusy = false,
        string? queuedStatus = null)
    {
        CancellationTokenSource? operationCts = null;
        try
        {
            await SaveSettingsAsync();
            var settings = CaptureSettings();
            operationCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            async Task RunOperationAsync(CancellationToken token)
            {
                _currentOperationCts = operationCts;
                await SetBusyAsync(activity);
                try
                {
                    await operation(settings, token);
                }
                finally
                {
                    await Dispatcher.InvokeAsync(() => SetBusy(false));
                }
            }

            if (queueIfBusy)
            {
                SetStatus(queuedStatus ?? "Operation queued behind the active synchronization.");
                await _scheduler.RunAsync(RunOperationAsync, operationCts.Token);
                return;
            }

            var ran = await _scheduler.TryRunAsync(
                RunOperationAsync,
                operationCts.Token);

            if (!ran)
            {
                SetStatus("Another AzCopy operation is already running. Wait for it to finish.");
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation cancelled.");
        }
        catch (Exception exception)
        {
            await LogErrorAsync(activity, exception);
            ShowError(exception);
        }
        finally
        {
            if (ReferenceEquals(_currentOperationCts, operationCts))
            {
                _currentOperationCts = null;
            }
            operationCts?.Dispose();
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("Cancelling the active operation...");
        _currentOperationCts?.Cancel();
        _processRunner.CancelAll();
    }

    private async void SettingsChanged(object sender, RoutedEventArgs e)
    {
        UpdateConfigurationSummary();

        if (ReferenceEquals(sender, AutoSyncIntervalComboBox))
        {
            _nextAutomaticSyncUtc =
                DateTimeOffset.UtcNow.AddSeconds(GetSelectedIntervalSeconds());
        }

        if (ReferenceEquals(sender, LoginModeComboBox) ||
            ReferenceEquals(sender, TenantIdTextBox) ||
            ReferenceEquals(sender, AzureCliPathTextBox))
        {
            SetConnectionStatus(false);
        }

        if (_initialized)
        {
            await SaveSettingsAsync();
        }
    }

    private async void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        _currentTheme = GetSelectedTheme();
        ThemeManager.Apply(_currentTheme, this);
        if (_initialized)
        {
            await SaveSettingsAsync();
        }
    }

    private void OpenAdvancedSettings_Click(object sender, RoutedEventArgs e)
    {
        AdvancedSettingsOverlay.Visibility = Visibility.Visible;
        ThemeComboBox.Focus();
    }

    private void CloseAdvancedSettings_Click(object sender, RoutedEventArgs e) =>
        AdvancedSettingsOverlay.Visibility = Visibility.Collapsed;

    private void AdvancedSettingsOverlay_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, AdvancedSettingsOverlay))
        {
            AdvancedSettingsOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private async Task SaveSettingsAsync()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            await _settingsStore.SaveAsync(CaptureSettings(), _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App shutdown.
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Settings could not be saved: {exception.Message}");
            await TryLogErrorAsync("Save settings", exception);
            if (!_settingsPersistenceWarningShown)
            {
                _settingsPersistenceWarningShown = true;
                MessageBox.Show(
                    this,
                    $"SyncSAW could not save settings to:{Environment.NewLine}" +
                    $"{SettingsStore.DefaultPath}{Environment.NewLine}{Environment.NewLine}" +
                    $"{exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                    "The application will continue running, but these changes may not be " +
                    "available after restart.",
                    "Settings were not saved",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    private SyncSettings CaptureSettings() => new()
    {
        LocalFolder = LocalFolderTextBox.Text.Trim(),
        StorageAccount = StorageAccountTextBox.Text.Trim().ToLowerInvariant(),
        Container = ContainerTextBox.Text.Trim().ToLowerInvariant(),
        PauseSync = PauseSyncCheckBox.IsChecked == true,
        AutoSyncIntervalSeconds = GetSelectedIntervalSeconds(),
        MinimizeToTray = MinimizeToTrayCheckBox.IsChecked == true,
        AzCopyPath = NullIfWhiteSpace(AzCopyPathTextBox.Text),
        AzureCliPath = NullIfWhiteSpace(AzureCliPathTextBox.Text),
        TenantId = NullIfWhiteSpace(TenantIdTextBox.Text),
        SubscriptionId = NullIfWhiteSpace(SubscriptionIdTextBox.Text),
        LoginMode = (LoginModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "DeviceCode"
            ? EntraLoginMode.DeviceCode
            : EntraLoginMode.AzureCli,
        Theme = GetSelectedTheme()
    };

    private void ApplySettings(SyncSettings settings)
    {
        LocalFolderTextBox.Text = settings.LocalFolder;
        StorageAccountTextBox.Text = settings.StorageAccount.ToLowerInvariant();
        ContainerTextBox.Text = settings.Container.ToLowerInvariant();
        PauseSyncCheckBox.IsChecked = settings.PauseSync;
        SelectAutoSyncInterval(settings.AutoSyncIntervalSeconds);
        MinimizeToTrayCheckBox.IsChecked = settings.MinimizeToTray;
        AzCopyPathTextBox.Text = settings.AzCopyPath ?? string.Empty;
        AzureCliPathTextBox.Text = settings.AzureCliPath ?? string.Empty;
        TenantIdTextBox.Text = string.IsNullOrWhiteSpace(settings.TenantId)
            ? SyncSettings.DefaultTenantId
            : settings.TenantId;
        SubscriptionIdTextBox.Text = settings.SubscriptionId ?? SyncSettings.DefaultSubscriptionId;
        LoginModeComboBox.SelectedIndex = settings.LoginMode == EntraLoginMode.AzureCli ? 0 : 1;
        _currentTheme = settings.Theme;
        ThemeComboBox.SelectedIndex = settings.Theme switch
        {
            AppTheme.Light => 1,
            AppTheme.Dark => 2,
            _ => 0
        };
        ThemeManager.Apply(_currentTheme, this);
        UpdateConfigurationSummary();
    }

    private bool TryGetSelectedRemote(out SyncItem item)
    {
        var items = GetSelectedRemoteItems();
        item = items.Count == 1 ? items[0] : null!;
        if (item is not null)
        {
            return true;
        }

        ShowRemoteSelectionRequired(singleSelection: true);
        return false;
    }

    private IReadOnlyList<SyncItem> GetSelectedRemoteItems() =>
        FilesDataGrid.SelectedItems
            .OfType<SyncItem>()
            .Where(item => _remotePaths.Contains(item.Path))
            .ToArray();

    private void ShowRemoteSelectionRequired(bool singleSelection)
    {
        MessageBox.Show(
            this,
            singleSelection
                ? "Select exactly one row that exists in Azure Blob Storage."
                : "Select one or more rows that exist in Azure Blob Storage.",
            "Remote file required",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void FilesDataGrid_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_restoringFileSelection)
        {
            UpdateFileActionButtons();
        }
    }

    private void ReplaceFileItemsPreservingSelection(IReadOnlyList<SyncItem> items)
    {
        var selectedPaths = FilesDataGrid.SelectedItems
            .OfType<SyncItem>()
            .Select(item => item.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _restoringFileSelection = true;
        try
        {
            FilesDataGrid.SelectedItems.Clear();
            _items.Clear();
            foreach (var item in items)
            {
                _items.Add(item);
            }

            if (selectedPaths.Count == 0)
            {
                return;
            }

            foreach (var item in _items)
            {
                if (selectedPaths.Contains(item.Path))
                {
                    FilesDataGrid.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _restoringFileSelection = false;
        }
    }

    private void FileSelectionCheckBox_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.CheckBox { DataContext: SyncItem item })
        {
            return;
        }

        if (FilesDataGrid.SelectedItems.Contains(item))
        {
            FilesDataGrid.SelectedItems.Remove(item);
        }
        else
        {
            FilesDataGrid.SelectedItems.Add(item);
        }
        e.Handled = true;
    }

    private void UpdateFileActionButtons()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(UpdateFileActionButtons);
            return;
        }

        var remoteCount = GetSelectedRemoteItems().Count;
        DownloadRemoteButton.IsEnabled = remoteCount == 1;
        OpenRemoteButton.IsEnabled = remoteCount == 1;
        DeleteRemoteButton.IsEnabled = remoteCount > 0 && !_deletionQueuedOrRunning;
        DeleteRemoteButton.ToolTip = _deletionQueuedOrRunning
            ? "A deletion is queued or running."
            : "Delete the selected Azure files and queue matching SAW cleanup.";
        DeleteRemoteButton.Content = remoteCount > 1
            ? $"Delete selected ({remoteCount:N0})"
            : "Delete selected";
    }

    private static IReadOnlyList<string> GetExistingLocalFiles(
        string root,
        IEnumerable<string> relativePaths)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var fullRoot = Path.GetFullPath(root);
        var rootPrefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) +
                         Path.DirectorySeparatorChar;
        var files = new List<string>();
        foreach (var relativePath in relativePaths)
        {
            var candidate = Path.GetFullPath(Path.Combine(
                fullRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Selected Blob path escapes the local folder: '{relativePath}'.");
            }
            if (File.Exists(candidate))
            {
                files.Add(candidate);
            }
        }

        return files;
    }

    private static void DeleteLocalFiles(IEnumerable<string> files)
    {
        var failures = new List<Exception>();
        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file))
                {
                    continue;
                }

                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
                File.Delete(file);
                if (File.Exists(file))
                {
                    throw new IOException($"Windows still reports the file after deletion: {file}");
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new IOException($"Could not delete local file '{file}'.", exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new IOException(
                "No Azure Blobs were deleted because one or more matching local files " +
                "could not be removed.",
                new AggregateException(failures));
        }
    }

    private static string GetSafeRelativePath(string root, string file)
    {
        if (Directory.Exists(root))
        {
            var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(file));
            if (!relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                relative != "..")
            {
                return relative.Replace('\\', '/');
            }
        }

        return Path.GetFileName(file);
    }

    private void ShowError(Exception exception)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowError(exception));
            return;
        }

        if (IsAuthenticationFailure(exception))
        {
            SetConnectionStatus(false);
        }
        SetBusy(false);
        MarkPendingItemsFailed(exception.Message);
        SyncStatusTitleTextBlock.Text = "Attention required";
        SyncStatusDetailTextBlock.Text = exception.Message;
        SetSyncStatusVisual("DangerBackgroundBrush", "DangerBrush", "\uEA39");
        SetStatus(exception.Message);
        MessageBox.Show(this, exception.Message, "SyncSAW error", MessageBoxButton.OK, MessageBoxImage.Error);
        ShowTrayBalloon("SyncSAW error", exception.Message);
    }

    private Task LogErrorAsync(string context, Exception exception) =>
        _operationLog.WriteEventAsync($"ERROR {context}: {exception}");

    private async Task TryLogErrorAsync(string context, Exception exception)
    {
        try
        {
            await LogErrorAsync(context, exception);
        }
        catch (Exception logException) when (
            logException is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to write SyncSAW operation log: {logException}");
        }
    }

    private void ReportBackgroundError(Exception exception)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ReportBackgroundError(exception));
            return;
        }

        if (IsAuthenticationFailure(exception))
        {
            SetConnectionStatus(false);
        }
        SetBusy(false);
        MarkPendingItemsFailed(exception.Message);
        SyncStatusTitleTextBlock.Text = "Background sync failed";
        SyncStatusDetailTextBlock.Text = exception.Message;
        SetSyncStatusVisual("DangerBackgroundBrush", "DangerBrush", "\uEA39");
        SetStatus($"Background synchronization failed: {exception.Message}");
        ShowTrayBalloon("Background synchronization failed", exception.Message);
    }

    private void MarkPendingItemsFailed(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => MarkPendingItemsFailed(message));
            return;
        }

        for (var index = 0; index < _items.Count; index++)
        {
            if (_items[index].State == SyncItemState.Pending)
            {
                _items[index] = _items[index] with
                {
                    State = SyncItemState.Error,
                    Error = message
                };
            }
        }
    }

    private Task SetBusyAsync(string activity) => Dispatcher.InvokeAsync(() =>
    {
        SetBusy(true);
        SetStatus(activity);
    }).Task;

    private void SetBusy(bool isBusy)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetBusy(isBusy));
            return;
        }

        BusyProgressBar.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        SignInButton.IsEnabled = !isBusy && !_credentialValid;
        CancelButton.Visibility = isBusy && _currentOperationCts is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetStatus(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(message));
            return;
        }

        StatusTextBlock.Text = message;
    }

    private Forms.ContextMenuStrip CreateTrayMenu()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SyncSAW", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Sync now", null, (_, _) => Dispatcher.Invoke(() => SyncNow_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        return menu;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ShowTrayBalloon(string title, string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowTrayBalloon(title, message));
            return;
        }

        _trayIcon.BalloonTipTitle = title;
        _trayIcon.BalloonTipText = message.Length > 240 ? message[..240] : message;
        _trayIcon.ShowBalloonTip(3000);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && MinimizeToTrayCheckBox.IsChecked == true)
        {
            Hide();
            ShowTrayBalloon(
                "SyncSAW is still running",
                $"Background status checks continue every {FormatInterval(GetSelectedIntervalSeconds())}.");
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        _lifetime.Cancel();
        _currentOperationCts?.Cancel();
        _processRunner.CancelAll();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _applicationIcon.Dispose();
    }

    private void ExitApplication()
    {
        Close();
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private AppTheme GetSelectedTheme() =>
        (ThemeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Light" => AppTheme.Light,
            "Dark" => AppTheme.Dark,
            _ => AppTheme.System
        };

    private void UpdateConfigurationSummary()
    {
        AutoSyncDetailTextBlock.Text = PauseSyncCheckBox.IsChecked == true
            ? "Synchronization paused"
            : "Running automatically";
    }

    private void SetConnectionStatus(bool isConnected)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetConnectionStatus(isConnected));
            return;
        }

        _credentialValid = isConnected;
        ConnectionStatusTextBlock.Text = isConnected ? "Azure ready" : "Not signed in";
        SignInButton.Content = isConnected ? "Signed in" : "Sign in";
        SignInButton.IsEnabled = !isConnected && BusyProgressBar.Visibility != Visibility.Visible;
        ConnectionStatusTextBlock.SetResourceReference(
            ForegroundProperty,
            isConnected ? "SuccessBrush" : "MutedBrush");
        ConnectionStatusDot.SetResourceReference(
            System.Windows.Shapes.Shape.FillProperty,
            isConnected ? "SuccessBrush" : "MutedBrush");
    }

    private int GetSelectedIntervalSeconds()
    {
        if (AutoSyncIntervalComboBox.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out var seconds) &&
            seconds is 5 or 10 or 30 or 60)
        {
            return seconds;
        }

        return 10;
    }

    private void SelectAutoSyncInterval(int seconds)
    {
        var selectedSeconds = seconds is 5 or 10 or 30 or 60 ? seconds : 10;
        AutoSyncIntervalComboBox.SelectedItem = AutoSyncIntervalComboBox.Items
            .OfType<ComboBoxItem>()
            .Single(item => item.Tag?.ToString() == selectedSeconds.ToString());
    }

    private static string FormatInterval(int seconds) =>
        seconds == 60 ? "1 minute" : $"{seconds} seconds";

    private static bool IsAuthenticationFailure(Exception exception)
    {
        var message = exception.ToString();
        return message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("AuthenticationFailed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Authorization", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not logged in", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("sign in", StringComparison.OrdinalIgnoreCase);
    }

    private void SetSyncStatusVisual(string backgroundResource, string foregroundResource, string glyph)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() =>
                SetSyncStatusVisual(backgroundResource, foregroundResource, glyph));
            return;
        }

        SyncStatusIconBorder.SetResourceReference(
            BackgroundProperty,
            backgroundResource);
        SyncStatusIconTextBlock.SetResourceReference(
            TextBlock.ForegroundProperty,
            foregroundResource);
        SyncStatusIconTextBlock.Text = glyph;
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape &&
            AdvancedSettingsOverlay.Visibility == Visibility.Visible)
        {
            AdvancedSettingsOverlay.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void SystemEvents_UserPreferenceChanged(
        object sender,
        Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (_currentTheme == AppTheme.System &&
            e.Category is Microsoft.Win32.UserPreferenceCategory.General
                or Microsoft.Win32.UserPreferenceCategory.Color
                or Microsoft.Win32.UserPreferenceCategory.VisualStyle)
        {
            _ = Dispatcher.InvokeAsync(() => ThemeManager.Apply(_currentTheme, this));
        }
    }

    private static bool IsConfigured(SyncSettings settings) =>
        Directory.Exists(settings.LocalFolder) &&
        !string.IsNullOrWhiteSpace(settings.StorageAccount) &&
        !string.IsNullOrWhiteSpace(settings.Container);
}
