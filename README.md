# SyncSAW

SyncSAW is a Windows Azure Blob synchronization solution. The WPF management
application uses AzCopy, while the standalone SAW client uses Azure PowerShell.
It includes:

- A .NET 8 WPF management application with a configurable 5-second to 1-minute background interval, optional automatic sync, notification-area behavior, dry-run status planning, and remote file management.
- A standalone PowerShell 7 SAW client for one-shot or continuous synchronization.
- A testable core library that owns input validation, safe AzCopy argument construction, process execution, output parsing, and concurrency control.

SyncSAW uses one deterministic merge policy: local files are authoritative for paths that exist locally, while cloud-only files download without overwriting local content. Files are deleted only through an explicit selection and destructive-action confirmation in the management application.

## Prerequisites

- Windows 10 or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build the GUI
- PowerShell 7 for `scripts\Sync-SAW.ps1`; on a SAW, install it manually from **Software Center**
- 64-bit Windows PowerShell 5.1 for the optional cluster `scripts\Sync.ps1` client
- [AzCopy v10](https://learn.microsoft.com/azure/storage/common/storage-use-azcopy-v10) for the WPF management application only
- Azure PowerShell `Az.Accounts` 5.5.0+ and `Az.Storage` 9.4.0+ for the standalone SAW client; use the included dependency installer below
- [Azure CLI 2.61+](https://learn.microsoft.com/cli/azure/install-azure-cli-windows) for the GUI's Windows broker login
- A Microsoft Entra identity with Azure Blob data-plane access

Assign the role at the storage account or container scope:

| Use | Minimum typical RBAC role |
| --- | --- |
| List and download | **Storage Blob Data Reader** |
| Upload, update, synchronize, or delete | **Storage Blob Data Contributor** |
| Create a missing container | **Storage Blob Data Contributor** |

Azure control-plane roles such as Owner or Contributor do not automatically grant Blob data access. RBAC changes can take several minutes to propagate.

### Prepare management server dependencies

On the Windows computer that runs `SyncSAW.App.exe`, use the included
`install-syncsaw-server-dependencies` agent skill or run its installer directly:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\.github\skills\install-syncsaw-server-dependencies\scripts\Install-ServerDependencies.ps1
```

From the extracted release package:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Install-ServerDependencies.ps1
```

The idempotent installer validates 64-bit Windows and actual installed versions,
then installs only missing or outdated prerequisites from Microsoft's official
WinGet HTTPS source using exact package IDs: .NET 8 Desktop Runtime, Azure CLI
2.61+, and AzCopy v10. WinGet may request elevation for machine-wide packages.
Use `-WhatIf` for a non-modifying readiness and source check. If WinGet is
missing, install or repair Microsoft **App Installer** first.

## Build and test

```powershell
dotnet restore .\SyncSAW.sln
dotnet build .\SyncSAW.sln --configuration Release
dotnet test .\tests\SyncSAW.Tests\SyncSAW.Tests.csproj --configuration Release
```

Run the GUI:

```powershell
dotnet run --project .\src\SyncSAW.App\SyncSAW.App.csproj
```

## AzCopy installation and discovery

Install AzCopy v10, then use one of these discovery options:

1. Install it under `%ProgramFiles%\AzCopy` or a standard versioned
   `%ProgramFiles%\azcopy_windows_amd64_*` folder; the GUI discovers these automatically.
2. Put `azcopy.exe` on `PATH`.
3. Set the `AZCOPY_PATH` environment variable to the executable.
4. Put `azcopy.exe` beside `SyncSAW.App.exe`.
5. Enter its full path under **Advanced** in the GUI.

The WPF application launches AzCopy directly with `ProcessStartInfo.ArgumentList`; it does not build shell command strings. Standard output, standard error, cancellation, and exit codes are captured. The SAW PowerShell client does not use or require AzCopy.

## GUI use

1. Choose an existing local folder.
2. Enter the storage account name (or its standard `*.blob.core.windows.net` host) and container name.
3. Open **Advanced settings** to choose **Use Windows setting**, **Light**, or **Dark** appearance. System mode follows Windows changes while the app is running. Windows 11 uses the native Mica backdrop where supported, with a neutral Fluent surface fallback on earlier Windows versions.
4. Under **Advanced settings**, keep **Azure CLI / Windows broker** selected when the tenant requires a compliant or joined device. The tenant defaults to `72f988bf-86f1-41af-91ab-2d7cd011db47`, limiting account discovery to that tenant. The subscription defaults to `a0d901ba-9956-4f7d-830c-2d7974c36666`; replace it with another subscription ID or clear it to keep Azure CLI's interactive selection.
5. Select **Sign in** and complete the Windows account prompt. Azure CLI 2.61+ uses WAM on Windows, allowing Conditional Access to evaluate device claims. SyncSAW selects the configured subscription, and AzCopy reuses the brokered token.
6. Select **Refresh** to list remote blobs and run AzCopy dry-run planning.
7. Review each file's state, time, size, planned action, and **Synced to SAW** flag. Synchronization runs automatically while the app is open at the selected 5-, 10-, 30-, or 60-second interval. Use the toggle only to pause/resume transfers.

The GUI refreshes at the selected interval and synchronizes unless `PauseSync` is enabled in its persisted settings. It uses one shared operation gate, so jobs never overlap. Periodic refreshes are skipped when another job is active; a confirmed **Delete selected** operation instead waits behind the active job and runs as soon as the gate is available. The delete button is disabled while that request is queued or running to prevent duplicate submissions. Use **Cancel** to stop an active login or transfer; SyncSAW terminates the complete child-process tree. Minimizing can keep the app in the notification area, while closing the window always cancels background work and exits.

Remote file controls support upload/update, download, opening a temporary downloaded copy, and delete. Use the **Select** checkboxes or Ctrl/Shift row selection to build an explicit batch; the **Delete selected** button shows its item count. The styled confirmation lists the selected Blobs and warns when matching local files will also be removed. Matching server-local files are removed before the remote batch so automatic synchronization cannot recreate the Blobs. Every durable SAW deletion request is published before any Blob is removed, and deletion is verified against Azure before the view refreshes. The requests tell SAW to remove corresponding local copies and any Blob recreated by an older SAW process, then consume each request. There is no broad deletion mode; only explicitly selected paths are deleted. When an AzCopy command fails, planned rows are marked as errors and the original error is shown.

Every AzCopy child-process invocation, exit code, duration, standard output, and
standard error is appended to daily local logs:

The GUI stores non-secret settings at
`%LOCALAPPDATA%\SyncSAW\settings.json` and writes daily operation logs under
`%LOCALAPPDATA%\SyncSAW\Logs`. It can therefore run as a standard user when
installed under `C:\Program Files`. On first launch after upgrading, an older
`settings.json` beside `SyncSAW.App.exe` is copied to LocalAppData when no
LocalAppData settings file exists; the original is left untouched because the
installation directory may be read-only.

SAS query strings and signatures are redacted before writing. AzCopy also keeps
its own diagnostic logs under `%USERPROFILE%\.azcopy`.

The GUI never requests or stores account keys, SAS tokens, passwords, or client secrets. AzCopy owns its Microsoft Entra token cache independently of the application. The PowerShell client can alternatively load a SAS from its config as described below.

## PowerShell SAW client

### Prepare SAW dependencies

On the SAW, open **Software Center** and manually install PowerShell 7 first.
The SyncSAW dependency installer does not install or upgrade PowerShell. Do not
substitute WinGet, an MSI download, or the Microsoft Store on a SAW.

After `pwsh` is available, follow
[the SAW PowerShell packaging guidance](http://aka.ms/sawpwsh) so an approved
module repository is registered. Then run the included idempotent installer
from the extracted release:

```powershell
pwsh .\scripts\Install-SawDependencies.ps1
```

From a source checkout, the same installer is part of the
`install-syncsaw-saw-dependencies` agent skill:

```powershell
pwsh .\.github\skills\install-syncsaw-saw-dependencies\scripts\Install-SawDependencies.ps1
```

The installer validates PowerShell 7, probes the registered HTTPS repository,
installs only `Az.Accounts` 5.5.0+ and `Az.Storage` 9.4.0+ at `CurrentUser`
scope, imports both modules, and verifies every Azure cmdlet used by
`Sync-SAW.ps1`. It does not install the full `Az` rollup, register repositories,
persist repository trust changes, use `SkipPublisherCheck`, request elevation,
or store credentials. Use `-Repository '<name>'` when several repositories are
registered. If an approved repository is marked untrusted, confirm it against
the SAW guidance before explicitly adding `-AllowUntrustedRepository`; that
switch trusts only the current install operations. Use `-WhatIf` for a
non-modifying repository and version check.

Edit `scripts\Sync-SAW.config.json` once, then start the configured job without
repeating parameters:

```powershell
pwsh .\scripts\Sync-SAW.ps1
```

The JSON file supports `LocalFolder`, `StorageAccount`, `Container`,
`AuthenticationMode`, `SasToken`,
`IntervalSeconds`, `Continuous`, `PauseSync`, `PublishSyncFlags`, `LogDirectory`, `TenantId`, and
`SubscriptionId`. `AuthenticationMode` defaults to `AzurePowerShell`. The script
enables Az.Accounts CurrentUser context autosave, selects a saved context matching
the configured tenant/subscription, and silently requests a Storage token. It calls
`Connect-AzAccount` only when that cache is missing or cannot refresh, then creates
an `Az.Storage` context with `-UseConnectedAccount` and performs Blob operations
through `Get/Set/Remove-AzStorageBlob*` cmdlets. On supported Windows systems,
current Azure PowerShell versions use WAM for interactive login. The included
config contains the corporate tenant and default subscription IDs above.

To use an account- or container-scoped SAS without interactive login, set
`AuthenticationMode` to `Sas` and put either the SAS query string or its full
HTTPS account/container URL in `SasToken`:

```json
{
  "AuthenticationMode": "Sas",
  "SasToken": "?sv=...&ss=b&srt=co&sp=rlcw&se=...&sig=..."
}
```

The script passes the SAS only to `New-AzStorageContext`, validates
that full SAS URLs match the configured account/container, and redacts SAS
values from console logs. A SAS is still a bearer credential stored as
plain text in the JSON file: restrict the file's Windows ACL, never commit or
share it, grant only the required permissions, set a short expiry, require
HTTPS, and rotate it if exposed. With `PublishSyncFlags` enabled, the SAS needs
read, list, create, write, and delete permissions (`rlcwd`) because the script
creates current marker blobs and removes stale ones. Set `PublishSyncFlags` to
`false` for a read-only SAS; the GUI will then show **Not yet**. Creating a
missing container generally requires an account SAS with Blob service,
container resource type, and create permission. A container SAS that cannot
create its target will fail with an Azure Storage authorization error.
Explicit command-line parameters override matching config values, and another
file can be selected with `-ConfigPath`.

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

Specify a tenant explicitly:

```powershell
pwsh .\scripts\Sync-SAW.ps1 `
  -LocalFolder 'D:\Publish' `
  -StorageAccount 'contosodata' `
  -Container 'releases' `
  -TenantId '00000000-0000-0000-0000-000000000000'
```

The script validates config and command-line inputs, rejects unknown config properties, acquires a per-folder/container mutex, signs in through Azure PowerShell by default, performs all transfers with Az.Storage cmdlets, writes a daily transcript beside the script by default (or to `LogDirectory` when configured), publishes SAW status markers, and stops cleanly on Ctrl+C. Storage operations retry transient HTTP/network failures up to four times with exponential backoff. In continuous mode, an exhausted transient failure is logged and retried on the next cycle. If an Entra access or refresh token expires, the script forces a new interactive/WAM sign-in, rebuilds its storage context, and retries the interrupted cycle. Closing or failing that sign-in does not terminate continuous mode; it prompts again after a later cycle. Authorization, invalid configuration, and invalid deletion requests remain fatal. Set `PauseSync` to `true` to keep a continuous client running without transfers. Storage account keys and application secrets are not accepted. For upgrade compatibility, an old config containing `DeletionMode: false` is accepted and ignored; `DeletionMode: true` is rejected with guidance to use explicit management-client deletion.

## Windows PowerShell 5.1 cluster client

Cluster machines that cannot use PowerShell 7 can run `scripts\Sync.ps1` with
64-bit Windows PowerShell 5.1. `Sync.ps1` delegates to the same synchronization
implementation as `Sync-SAW.ps1`, so upload/download authority, explicit
deletion requests, cloud-update detection, sync markers, non-overlap protection,
continuous polling, and authentication recovery remain identical.
Keep `Sync.ps1`, `Sync-SAW.ps1`, and `Sync.config.json` together in the same
directory when copying the cluster client.

Install the cluster dependencies once as the same Windows user that will run
the synchronization job:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Install-ClusterDependencies.ps1
```

The installer validates a 64-bit Windows PowerShell 5.1 Desktop host, enables
TLS 1.2 for its process, verifies the selected registered HTTPS PowerShellGet
repository, and installs only compatible `Az.Accounts` 5.3.4+ and
`Az.Storage` 9.4.0+ modules at `CurrentUser` scope. It validates every Azure
cmdlet and parameter used by the synchronization client. It does not install
PowerShell, persist repository trust changes, or install the full `Az` rollup.
Use `-Repository '<name>'` for an internal repository. A custom untrusted
repository additionally requires `-AllowUntrustedRepository`; the official
HTTPS PSGallery endpoint is accepted without persisting a trust change.

Edit `scripts\Sync.config.json`, then start the job:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Sync.ps1
```

The cluster config has the same fields and override rules as
`Sync-SAW.config.json`. Command-line parameters can also be passed directly to
`Sync.ps1`. Interactive Microsoft Entra sign-in and cached CurrentUser contexts
are supported. A non-interactive scheduled task must run as the same Windows
identity that established the cached context; otherwise use a suitably scoped,
protected SAS config and secure the config file with Windows ACLs. No storage
account keys are supported.

## Synchronization semantics

- **WPF Sync mode**: AzCopy dry-run identifies local upload paths, then per-file copy commands guarantee those selected local files are written to cloud. Cloud-only Blobs are downloaded individually to their exact relative local paths with overwrite disabled and Last Modified time preserved. This prevents the container name from becoming a local subdirectory and being uploaded repeatedly.
- **SAW Sync mode**: local-only files upload as new Blobs. Once a path exists in cloud, cloud is authoritative: any size or exact modified-time difference downloads and overwrites the SAW-local file. Downloaded files receive the Blob timestamp, so same-size rapid updates and stale or locally edited SAW copies cannot be missed or revert a server update.
- **Explicit GUI deletion**: only selected and confirmed paths are deleted. The operation waits behind an active sync rather than overlapping or being dropped. Each Blob is removed and verified, and a durable deletion request keeps an existing SAW-local copy from recreating it; on the next SAW check, the script deletes that local copy, removes any Blob recreated by an older client, and consumes the request.
- Azure Blob deletions are idempotent. A `404`, `BlobNotFound`, or `ContainerNotFound` response means the target is already absent and does not stop the SAW client.
- Missing containers are created through the active client's transfer provider and verified before synchronization.
- In the WPF server app, an existing source-local file is authoritative and is never overwritten by cloud download. The server supplements AzCopy planning with size and newer-local-timestamp checks. On SAW, cloud is authoritative for every path that already exists remotely.
- Folder structure is preserved. Manual upload keeps a path relative to the selected local root; files chosen outside that root upload at the container root.
- After each successful PowerShell SAW cycle, the script synchronizes sidecar marker blobs under `.syncsaw/saw-flags/`. The marker name is a SHA-256 hash of the case-sensitive blob path. A marker is published only when the local size and exact timestamp match the current Blob, then the source is listed again to detect an update that raced marker publication. The GUI reports **Synced to SAW: Yes** only when this verified marker is at least as new as the source blob.
- `.syncsaw/saw-flags/` and `.syncsaw/deletions/` are reserved for SyncSAW internals. These blobs are hidden from the GUI and excluded from normal synchronization.

## Limitations

- Only the public Azure Blob endpoint suffix `blob.core.windows.net` is currently generated; sovereign cloud endpoint suffixes are not configurable.
- Blob snapshots, versions, leases, and virtual-directory ACL concepts are not managed.
- Status is polling-based, not a filesystem watcher, so changes appear on the next configured interval.
- AzCopy output fields can evolve. The parser supports AzCopy v10 JSON envelopes, structured records, and current machine-readable list/dry-run messages.
- Opening a remote file downloads a temporary copy and launches its Windows-associated application. Editing that temporary copy does not upload it automatically.
- SyncSAW cannot guarantee a stable snapshot if source files are being actively modified during an AzCopy run.

## Troubleshooting

| Symptom | Resolution |
| --- | --- |
| `AzCopy was not found` | Install it in a standard Program Files location, configure the executable path, set `AZCOPY_PATH`, or add AzCopy to `PATH`. |
| `pwsh` was not found or PowerShell 7 is required | On the SAW, open **Software Center** and manually install PowerShell 7. Do not use WinGet, an MSI download, or the Microsoft Store. Then reopen a terminal and run `pwsh .\scripts\Install-SawDependencies.ps1`. |
| Cluster `Sync.ps1` reports missing Azure modules | Run `scripts\Install-ClusterDependencies.ps1` from 64-bit Windows PowerShell 5.1 as the same user that runs the sync job. |
| `Az.Accounts` or `Az.Storage` module was not found | In PowerShell 7, run `scripts\Install-SawDependencies.ps1`. If no approved repository is registered, follow `http://aka.ms/sawpwsh`; do not automatically register or trust PSGallery. |
| `403` or authorization failure | Confirm the Blob data role, resource scope, tenant, and RBAC propagation; control-plane Contributor is insufficient. |
| Entra error `530033` | Remote device flow is blocked by device-based Conditional Access. The SAW script uses Azure PowerShell browser/WAM authentication; the GUI uses **Azure CLI / Windows broker**. If it still fails, use the correlation ID in Entra sign-in logs to identify the applied policy. |
| Device-code login uses the wrong tenant | Set the tenant ID in Advanced settings or pass `-TenantId`. |
| Azure PowerShell selects the wrong context | Set `TenantId` and `SubscriptionId` in the config. Sync-SAW passes both to `Connect-AzAccount` and rejects a mismatched active context. |
| SAW requests MFA on every start | Confirm Az.Accounts can write `%USERPROFILE%\\.Azure` and that the same Windows user runs each job. The script reuses a matching CurrentUser context and silently refreshes its Storage token. Entra Conditional Access sign-in frequency or MFA policy can still require interaction after the allowed session expires. |
| SAW authentication expires during continuous sync | Complete the Microsoft Entra prompt that Sync-SAW reopens. The process remains running, rebuilds its `Az.Storage` context, and retries the interrupted cycle. If the prompt is canceled or temporarily fails, sign-in is retried after a later cycle. |
| GUI Azure CLI login spends a long time discovering directories | Set `TenantId` so `az login` is scoped to one tenant. Set `SubscriptionId` to select the desired account context after login. |
| SAS authentication returns `403` | Check SAS expiry, HTTPS-only policy, Blob service/resource scope, and permissions. SAW marker publishing needs `rlcwd`; set `PublishSyncFlags` to `false` when using a read-only SAS. |
| Files remain pending | Run Refresh, inspect the planned action, verify system clocks, and review AzCopy output/error text. |
| A GUI refresh is skipped | Another synchronization job is still active. SyncSAW intentionally prevents overlap and retries on a later cycle. Confirmed manual deletion is queued instead of skipped. |
| PowerShell client reports another instance | Stop the other process using the same folder/container, then retry. |

For detailed AzCopy diagnostics, inspect its standard error shown by SyncSAW and the AzCopy logs under `%USERPROFILE%\.azcopy`.
