---
name: install-syncsaw-saw-dependencies
description: Prepares a Secure Admin Workstation (SAW) to run SyncSAW after PowerShell 7 is manually installed from Software Center, then validates approved registered PowerShell module repositories and minimum Az.Accounts and Az.Storage modules. Use when setting up, repairing, validating, or troubleshooting Sync-SAW.ps1 dependencies on a SAW.
compatibility: Windows SAW with PowerShell 7 manually installed from Software Center, access to a registered organization-approved PowerShell repository, and permission to install modules for CurrentUser.
metadata:
  author: SyncSAW
  version: "1.0"
---

# Install SyncSAW dependencies on SAW

PowerShell 7 is a manual SAW prerequisite. Open **Software Center** on the SAW
and install PowerShell 7 before using this skill. The installer cannot install
or upgrade PowerShell itself.

After PowerShell 7 is installed, use the bundled installer instead of manually
installing the full `Az` rollup module:

```powershell
pwsh -NoLogo -NoProfile -File `
  .\.github\skills\install-syncsaw-saw-dependencies\scripts\Install-SawDependencies.ps1
```

When working from the packaged application, run:

```powershell
pwsh -NoLogo -NoProfile -File .\scripts\Install-SawDependencies.ps1
```

## Required procedure

1. Confirm PowerShell 7 was manually installed from **Software Center** on the
   SAW. If `pwsh` is unavailable, stop and direct the user to Software Center;
   do not download an MSI or use WinGet, the Microsoft Store, or another package
   source.
2. Run the installer in PowerShell 7 as the same Windows user that will run
   `Sync-SAW.ps1`. Administrator elevation is not required.
3. Let the installer inventory both PSResourceGet and PowerShellGet repositories.
   It must successfully find the required module versions in one registered
   repository before installing anything.
4. If more than one approved repository is available, rerun with
   `-Repository '<registered-name>'` to select one explicitly.
5. If the SAW-approved repository is registered but marked untrusted, obtain
   user confirmation and rerun with `-AllowUntrustedRepository`. This permits
   that installation only and does not persist a repository trust change.
6. Confirm the final readiness output reports:
   - PowerShell edition `Core`, version 7 or later.
   - `Az.Accounts` 5.5.0 or later.
   - `Az.Storage` 9.4.0 or later.
   - Every cmdlet required by `Sync-SAW.ps1`.
7. After readiness succeeds, review `Sync-SAW.config.json`, then start
   `Sync-SAW.ps1`. Its first Entra sign-in may require MFA.

## Security and SAW policy

- Do not register PSGallery, persist a repository trust change, disable signature or TLS
  validation, use `-SkipPublisherCheck`, or install as administrator.
- Do not attempt to install or upgrade PowerShell 7 automatically. On a SAW,
  the user must install it manually through Software Center.
- Do not install storage account keys, AzCopy, Azure CLI, the full `Az` rollup,
  or unrelated modules for the SAW client.
- If no approved repository is registered or the approved repository cannot be
  queried, stop. Ask the user to complete the internal SAW PowerShell packaging
  process at <http://aka.ms/sawpwsh>, then rerun the installer.
- The guidance URL requires an authenticated Microsoft corporate session. Do
  not attempt to bypass its access controls.

## Success handoff

Report the selected repository, installed module versions, and the path to
`Sync-SAW.ps1`. Do not claim the synchronization job is ready if repository
probing, module import, or required-command validation failed.
