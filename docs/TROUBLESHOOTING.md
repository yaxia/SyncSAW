# SyncSAW troubleshooting

[Project overview](../README.md) | [User manual](USER-MANUAL.md) |
[Agent guide](AGENT-GUIDE.md) |
[Development guide](DEVELOPMENT-GUIDE.md)

This guide is for both operators and coding agents. Start with the relevant
table, then collect the listed diagnostics without changing cloud data or
restarting protected processes.

## Diagnostic locations

| Component | Diagnostic source |
| --- | --- |
| Desktop client | `%LOCALAPPDATA%\SyncSAW\Logs` |
| AzCopy | `%USERPROFILE%\.azcopy` and the error displayed by SyncSAW |
| PowerShell SAW client | Daily transcript beside the script, or the configured `LogDirectory` |
| Cluster bootstrap | `syncsaw-package-runner.log` under the bootstrap execution root |
| Cluster task | The newly created result archive under `cluster-results` |

SAS query strings and signatures should always be redacted. Never paste a
complete SAS URL into an issue, chat, test fixture, or log.

## Desktop management client

| Symptom | Resolution |
| --- | --- |
| `AzCopy was not found` | Install AzCopy in a standard Program Files location, configure its full path under **Advanced settings**, set `AZCOPY_PATH`, or add it to `PATH`. |
| `403` or authorization failure | Confirm the identity has a Blob data-plane role at the correct scope and allow time for RBAC propagation. Control-plane Contributor is insufficient. |
| Entra error `530033` | Device-based Conditional Access blocked the attempted flow. Use **Azure CLI / Windows broker** and inspect the correlation ID in Entra sign-in logs. |
| Login spends a long time discovering directories | Set `TenantId` to scope `az login` to one tenant and set `SubscriptionId` to select the desired account context. |
| Files remain pending | Select **Refresh**, inspect the planned action and row error, verify both system clocks, and inspect the desktop and AzCopy logs. |
| Files disappear after collapsing a folder | This is expected. Select the folder chevron to expand it; collapsed descendants remain part of the folder's aggregate state and deletion snapshot. |
| Sorting does not create one flat global order | This is expected. Sorting applies independently within each folder and always keeps folders before files. The active header shows `▲` or `▼`. |
| Folder deletion leaves a newly uploaded Blob | Folder deletion operates on the exact file snapshot shown at confirmation time, not a live prefix. Refresh and explicitly delete the new file if appropriate. |
| A periodic refresh is skipped | Another operation holds the shared gate. SyncSAW intentionally prevents overlap and retries on a later cycle. Confirmed deletion waits instead of being dropped. |
| A second desktop process does not open | This is expected. SyncSAW permits one process per signed-in Windows user and foregrounds the existing window. |
| A remote file opens but edits are not uploaded | **Open** uses a temporary downloaded copy. Save the edited file into the configured local sync folder or use manual upload. |
| Daily cluster package publishing fails | Select **Azure CLI / Windows broker**, sign in again, and confirm Blob write plus user-delegation-key permission at storage-account scope. |
| A public package container is rejected | Disable public access on `<sync-container>-package`. SyncSAW intentionally refuses to publish executable packages publicly. |

Do not diagnose a live desktop installation by stopping, restarting,
reinstalling, or killing `SyncSAW.App.exe`. Read logs and process state, use the
dependency installer's `-WhatIf` mode, and reproduce code changes with unit
tests or a separate development build.

## PowerShell SAW client

| Symptom | Resolution |
| --- | --- |
| `pwsh` was not found or PowerShell 7 is required | On the SAW, install PowerShell 7 manually from **Software Center**. Do not substitute WinGet, an MSI download, or the Microsoft Store. |
| `Az.Accounts` or `Az.Storage` was not found | Run `scripts\Install-SawDependencies.ps1`. If no approved repository is registered, follow `http://aka.ms/sawpwsh`; do not automatically register or trust PSGallery. |
| Windows PowerShell 5.1 reports missing Azure modules | If PowerShell 7 cannot be installed, run `scripts\Install-WindowsPowerShellDependencies.ps1` from 64-bit Windows PowerShell 5.1 as the same user that runs synchronization. |
| Device-code login uses the wrong tenant | Set `TenantId` in the config or pass `-TenantId`. |
| Azure PowerShell selects the wrong context | Set both `TenantId` and `SubscriptionId`. The client rejects a mismatched active context. |
| SAW requests MFA on every start | Confirm the same Windows user runs every job and Az.Accounts can write `%USERPROFILE%\.Azure`. Entra sign-in-frequency or MFA policy can still require interaction. |
| Authentication expires during continuous sync | Complete the Microsoft Entra prompt reopened by `Sync-SAW.ps1`. It rebuilds the storage context and retries. A canceled or temporary failed prompt is retried after a later cycle. |
| SAS authentication returns `403` | Check expiry, HTTPS-only policy, Blob service/resource scope, configured account/container, and permissions. Marker publishing needs `rlcwd`; disable `PublishSyncFlags` for read-only SAS. |
| Another instance is reported | Stop the other client using the same local-folder/account/container tuple, then retry. Do not bypass the mutex. |
| Files repeatedly download to SAW | Cloud is authoritative for paths that already exist remotely. Check local modification behavior and exact timestamps. |

Use the SAW dependency installer's `-WhatIf` mode to validate PowerShell,
registered repositories, and installed module versions without making changes.

## Cluster bootstrap and task

| Symptom | Resolution |
| --- | --- |
| `bootstrap.ps1` rejects `PackageUri` | Use a complete HTTPS user-delegation SAS URL with exact read-only Blob scope for `<sync-container>-package/cluster_package.zip`. |
| `bootstrap.ps1` rejects `ResultsBlobUri` | Use a complete HTTPS user-delegation SAS URL with exact create-only Blob scope for `<sync-container>/cluster-results/<unique>.zip` in the same storage account. |
| Package downloads stop after seven days | Publish from the desktop at least once every seven days; both update types carry refreshed SAS URLs in metadata. To recover, replace both SAS URLs in external `bootstrap.config.json`. |
| A changed package is not downloaded | First inspect the redacted descriptor change type. `commands-only` intentionally never downloads. For `binary`, check ETag, Last Modified, descriptor SHA-256, and whether the Blob remained unchanged through conditional download. |
| Commands-only update is rejected | Establish or recover state with a binary update. Commands-only requires the prior package directory, its `task.ps1`, and a valid installed binary hash. |
| Commands-only update uses old files or settings | This is by design: only validated metadata arguments are new. Mark the publication `binary` whenever any script, executable, library, input, or package-local config must change. |
| Published package is missing expected files | If `<sync-folder>\.syncsaw\package-source` exists, only that directory is packaged. Put the complete payload and package-root `task.ps1` there, or remove the empty directory to use the legacy sync-folder-root layout. |
| Update descriptor is rejected | Republish with desktop schema 6. Required metadata is descriptor version 1, exact ZIP SHA-256, UTC build time, `binary`/`commands-only` type, valid schema-6 bootstrap configuration, and a valid command for commands-only. Combined metadata is limited to 8,192 UTF-8 bytes. |
| Polling is skipped | This is expected while `task.ps1` is active. Bootstrap does not poll, install, or start another task until it exits. |
| Another bootstrap instance is reported | One runner already owns the mutex for that configuration. Do not start a duplicate. |
| Package schema is rejected | Deploy the current schema-6 `bootstrap.ps1` and config. Older schema-2 through schema-5 runners are intentionally incompatible. |
| Archive validation fails | Check descriptor hash, size, expanded size, entry count, traversal paths, reparse points, root `task.ps1`, schema-6 config, and the task safety harness. |
| `task.ps1` is rejected | Run `scripts\Test-TaskScriptSafety.ps1` locally and fix every additive-only violation before publishing. |
| Result upload returns `409` or `412` | The exact result Blob already exists. Generate a new unique result path and create-only SAS; never overwrite the existing Blob. |
| No result reaches the development machine | Confirm the result Blob is under the normal sync container's `cluster-results` path, then inspect desktop sync status without deleting or replacing the Blob. |

`bootstrap.config.json` contains bearer credentials. Restrict it to
Administrators/SYSTEM, do not commit it, and do not copy complete SAS URLs into
diagnostic output.

## Development failures

| Symptom | Resolution |
| --- | --- |
| Restore fails | Confirm the .NET 8 SDK is installed and the configured package sources are reachable. Do not add unapproved feeds as a shortcut. |
| WPF build fails on a non-Windows host | Build `SyncSAW.App` on Windows with the .NET 8 SDK. |
| Unit tests fail only when run together | Check shared process/environment state, test cleanup, operation-gate ownership, and single-instance identifiers. Do not weaken concurrency assertions. |
| AzCopy parser tests fail after an upgrade | Capture redacted machine-readable output, add it as a fixture, and update parsing without depending on human-formatted text. |
| A PowerShell test changes real cloud data | Stop the test and replace it with mocks, static validation, or newly named local files. Tests must not require destructive cloud operations. |
| Installed binaries do not match a new build | Publish to a temporary directory, stop only if the user explicitly authorizes it, copy runtime files with elevation, preserve configs, and compare hashes. |

## Mandatory agent-safe diagnostics

Agents working in this repository must follow [`AGENTS.md`](../AGENTS.md):

- Do not start, stop, restart, reinstall, or kill the SyncSAW desktop app.
- Do not restart the development machine, cluster node, services, or scheduled
  tasks.
- Do not delete or overwrite Azure Blobs for validation.
- Do not modify, delete, append to, rename, or replace existing cluster files.
- Validate with targeted builds, unit tests, local static checks, and newly
  named files.
- Run the task safety harness before packaging:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Test-TaskScriptSafety.ps1 -Path <path-to-task.ps1>
```

If safe diagnostics cannot distinguish the cause, report the blocking evidence
instead of bypassing a security or immutability restriction.
