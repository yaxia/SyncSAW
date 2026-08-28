# SyncSAW user manual

[Project overview](../README.md) | [Agent guide](AGENT-GUIDE.md) |
[Development guide](DEVELOPMENT-GUIDE.md) |
[Troubleshooting](TROUBLESHOOTING.md)

This guide covers installation and daily operation of the SyncSAW desktop
management client and the PowerShell SAW client.

## Prerequisites

### Desktop management client

- Windows 10 or later.
- Microsoft Windows Desktop Runtime 8.
- [AzCopy v10](https://learn.microsoft.com/azure/storage/common/storage-use-azcopy-v10).
- [Azure CLI 2.61 or later](https://learn.microsoft.com/cli/azure/install-azure-cli-windows)
  for Windows broker authentication.
- A Microsoft Entra identity with Azure Blob data-plane access.

Run the packaged dependency installer as the same Windows user that will run
`SyncSAW.App.exe`:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Install-ServerDependencies.ps1
```

From a source checkout:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\.github\skills\install-syncsaw-server-dependencies\scripts\Install-ServerDependencies.ps1
```

The installer validates 64-bit Windows and installed versions before changing
anything. It installs only missing or outdated prerequisites from Microsoft's
official WinGet HTTPS source. WinGet may request elevation for machine-wide
packages. Use `-WhatIf` for a non-modifying readiness check. If WinGet is
missing, install or repair Microsoft **App Installer** first.

### PowerShell SAW client

- PowerShell 7, installed manually from **Software Center** on a SAW.
- Azure PowerShell `Az.Accounts` 5.5.0 or later.
- Azure PowerShell `Az.Storage` 9.4.0 or later.
- An approved registered PowerShell module repository.

After installing `pwsh`, follow the
[SAW PowerShell packaging guidance](http://aka.ms/sawpwsh), then run:

```powershell
pwsh .\scripts\Install-SawDependencies.ps1
```

From a source checkout:

```powershell
pwsh .\.github\skills\install-syncsaw-saw-dependencies\scripts\Install-SawDependencies.ps1
```

The installer validates the registered HTTPS repository, installs only the
minimum modules at `CurrentUser` scope, imports them, and verifies every Azure
cmdlet used by `Sync-SAW.ps1`. It does not install the full `Az` rollup,
register or persist trust for repositories, request elevation, or store
credentials. Use `-Repository '<name>'` when several repositories are
registered. If an approved repository is marked untrusted, confirm it against
the SAW guidance before explicitly using `-AllowUntrustedRepository`; that
switch trusts only the current installation operations. Use `-WhatIf` for a
non-modifying check.

When PowerShell 7 cannot be installed, `Sync-SAW.ps1` remains syntactically
compatible with Windows PowerShell 5.1 if the required Az modules are prepared
with `scripts\Install-WindowsPowerShellDependencies.ps1`.

## Azure permissions

Assign the role at the storage account or container scope:

| Use | Minimum typical RBAC role |
| --- | --- |
| List and download | **Storage Blob Data Reader** |
| Upload, update, synchronize, or delete | **Storage Blob Data Contributor** |
| Create a missing container | **Storage Blob Data Contributor** |
| Publish cluster packages and user delegation SAS URLs | **Storage Blob Data Contributor** at storage-account scope |

Azure control-plane roles such as Owner or Contributor do not automatically
grant Blob data access. RBAC changes can take several minutes to propagate.
Generating a user delegation key is a storage-account operation, so a role
assigned only at container scope is insufficient for cluster package
publishing.

## AzCopy installation and discovery

The desktop client discovers AzCopy in this order:

1. A path entered under **Advanced settings**.
2. The `AZCOPY_PATH` environment variable.
3. `azcopy.exe` on `PATH`.
4. `azcopy.exe` beside `SyncSAW.App.exe`.
5. Standard `%ProgramFiles%\AzCopy` and versioned
   `%ProgramFiles%\azcopy_windows_amd64_*` folders.

The desktop launches AzCopy directly with
`ProcessStartInfo.ArgumentList`; it never builds a shell command string.
Standard output, standard error, cancellation, and exit codes are captured.
The PowerShell SAW client does not use or require AzCopy.

## Desktop management client

1. Choose an existing local folder.
2. Enter the storage account name, or its standard
   `*.blob.core.windows.net` host, and the Blob container name.
3. Open **Advanced settings** to choose **Use Windows setting**, **Light**, or
   **Dark** appearance. System mode follows Windows changes while the app runs.
   Windows 11 uses Mica where supported and a neutral Fluent surface otherwise.
4. Keep **Azure CLI / Windows broker** selected when the tenant requires a
   compliant or joined device. The tenant defaults to
   `72f988bf-86f1-41af-91ab-2d7cd011db47` and the subscription defaults to
   `a0d901ba-9956-4f7d-830c-2d7974c36666`. Replace either value as required,
   or clear the subscription to keep Azure CLI's interactive selection.
5. Select **Sign in** and complete the Windows account prompt.
6. Select **Refresh** to list remote Blobs and run AzCopy dry-run planning.
7. Review each file's state, time, size, planned action, error, and
   **Synced to SAW** status.
8. Use the synchronization toggle to pause or resume automatic transfers.
9. To serve cluster machines, enable **Publish cluster package on changes and
   daily** under **Advanced settings**. This requires **Azure CLI / Windows
   broker** authentication. Follow the [agent guide](AGENT-GUIDE.md) for the
   package protocol.

The configured interval can be 5, 10, 30, or 60 seconds. One shared operation
gate prevents overlapping jobs. Periodic refreshes are skipped while another
job is active; a confirmed **Delete selected** operation waits behind the
active job. **Cancel** terminates the complete child-process tree for an active
login or transfer.

Minimizing can keep the app in the notification area. Closing the window
cancels background work and exits. Only one desktop process can run for each
signed-in Windows user. Opening the app again restores and foregrounds the
existing process instead of starting another one.

### Remote file operations

The desktop supports upload/update, download, opening a temporary downloaded
copy, and delete. Use the **Select** checkboxes or Ctrl/Shift row selection to
build a batch. Destructive actions always require confirmation.

For deletion, SyncSAW removes matching desktop-local files before deleting the
selected Blobs so automatic synchronization cannot recreate them. It publishes
a durable SAW deletion request before deleting each Blob and verifies the
remote deletion before refreshing. There is no broad deletion mode.

### Settings, logs, and credentials

Non-secret settings are stored at:

```text
%LOCALAPPDATA%\SyncSAW\settings.json
```

Daily desktop operation logs are stored under:

```text
%LOCALAPPDATA%\SyncSAW\Logs
```

On first launch after upgrading, an older `settings.json` beside the executable
is copied to LocalAppData only when no LocalAppData settings file exists. The
original remains unchanged.

The desktop never requests or stores account keys, passwords, or client
secrets. SAS query strings and signatures are redacted from logs. AzCopy owns
its Microsoft Entra token cache independently of SyncSAW. Output from the Azure
CLI command that creates a SAS is deliberately omitted from operation logs.

When cluster publishing is enabled, the desktop generates seven-day package
read and result upload SAS URLs in memory, writes them only into package
configuration, and removes its temporary archive after upload. Neither SAS is
persisted in GUI settings or logs.

## PowerShell SAW client

Edit `scripts\Sync-SAW.config.json` once, then start the configured job without
repeating parameters:

```powershell
pwsh .\scripts\Sync-SAW.ps1
```

The JSON file supports `LocalFolder`, `StorageAccount`, `Container`,
`AuthenticationMode`, `SasToken`, `IntervalSeconds`, `Continuous`,
`PauseSync`, `PublishSyncFlags`, `LogDirectory`, `TenantId`, and
`SubscriptionId`. Explicit command-line parameters override matching config
values. Select another config with `-ConfigPath`.

```powershell
pwsh .\scripts\Sync-SAW.ps1 -ConfigPath 'D:\SyncJobs\archive.json'
```

One synchronization cycle:

```powershell
pwsh .\scripts\Sync-SAW.ps1 `
  -LocalFolder 'D:\Publish' `
  -StorageAccount 'contosodata' `
  -Container 'releases'
```

Continuous synchronization with the default 10-second interval:

```powershell
pwsh .\scripts\Sync-SAW.ps1 `
  -LocalFolder 'D:\Mirror' `
  -StorageAccount 'contosodata' `
  -Container 'archive' `
  -Continuous
```

### Microsoft Entra authentication

`AuthenticationMode` defaults to `AzurePowerShell`. The script enables
Az.Accounts `CurrentUser` context autosave, selects a cached context matching
the configured tenant and subscription, and silently requests a Storage token.
It calls `Connect-AzAccount` only when the cache is missing or cannot refresh.
Current Azure PowerShell versions use WAM on supported Windows systems.

If an access or refresh token expires, the script reopens interactive sign-in,
rebuilds its `Az.Storage` context, and retries the interrupted cycle. Canceling
or temporarily failing that prompt does not terminate continuous mode.

Storage operations retry transient HTTP and network failures up to four times
with exponential backoff. In continuous mode, an exhausted transient failure
is logged and retried on the next cycle. Authorization failures, invalid
configuration, and invalid deletion requests remain fatal. Set `PauseSync` to
`true` to keep the process running without transfers.

### SAS authentication

Set `AuthenticationMode` to `Sas` and provide either the SAS query string or
its complete HTTPS account/container URL:

```json
{
  "AuthenticationMode": "Sas",
  "SasToken": "?sv=...&ss=b&srt=co&sp=rlcw&se=...&sig=..."
}
```

The script passes the SAS only to `New-AzStorageContext`, validates that a full
URL matches the configured account and container, and redacts it from console
logs. A SAS remains a bearer credential stored as plain text: restrict the
config's Windows ACL, never commit or share it, grant only required
permissions, require HTTPS, use a short expiry, and rotate it if exposed.

With `PublishSyncFlags` enabled, the SAS needs read, list, create, write, and
delete permissions (`rlcwd`). Set `PublishSyncFlags` to `false` for a
read-only SAS; the GUI then reports **Synced to SAW: Not yet**.

## Synchronization semantics

- **Desktop mode:** AzCopy dry-run identifies local upload paths, then
  per-file copy commands write those selected local files to cloud. Cloud-only
  Blobs download without overwriting existing desktop-local files.
- **SAW mode:** Local-only files upload as new Blobs. Once a path exists in
  cloud, cloud is authoritative; a size or exact modified-time difference
  downloads and overwrites the SAW-local file.
- **Deletion:** Only explicitly selected and confirmed desktop paths are
  deleted. Durable requests prevent an older SAW copy from recreating a Blob.
- **Status markers:** After a successful SAW cycle, sidecar marker Blobs under
  `.syncsaw/saw-flags/` record files whose local size and exact timestamp match
  the source Blob.
- **Excluded paths:** `.syncsaw/saw-flags/`, `.syncsaw/deletions/`,
  `cluster_package.zip`, `cluster_package.config`, root `task.ps1`, and root
  `task.config.json` are excluded from normal synchronization.
- Folder structure is preserved. Manual upload keeps a path relative to the
  selected local root; files outside that root upload at the container root.

## Limitations

- Only the public Azure Blob endpoint suffix `blob.core.windows.net` is
  currently generated; sovereign cloud suffixes are not configurable.
- Blob snapshots, versions, leases, and virtual-directory ACL concepts are
  not managed.
- Status is polling-based, not a filesystem watcher.
- AzCopy output fields can evolve. SyncSAW supports AzCopy v10 JSON envelopes,
  structured records, and current machine-readable list/dry-run messages.
- Editing a temporary copy opened from a remote file does not upload it.
- SyncSAW cannot guarantee a stable snapshot while source files are changing
  during a transfer.

See the [troubleshooting guide](TROUBLESHOOTING.md) for operational failures,
log locations, and safe diagnostic procedures.
