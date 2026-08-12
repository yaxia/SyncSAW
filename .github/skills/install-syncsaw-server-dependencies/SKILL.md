---
name: install-syncsaw-server-dependencies
description: Prepares a Windows management server or workstation to run the SyncSAW WPF client by validating and installing the .NET 8 Desktop Runtime, Azure CLI 2.61 or later, and AzCopy v10 from Microsoft's official WinGet source. Use when setting up, repairing, validating, or troubleshooting SyncSAW.App.exe prerequisites.
compatibility: 64-bit Windows 10 or later with WinGet/App Installer and permission to approve installer elevation when required.
metadata:
  author: SyncSAW
  version: "1.0"
---

# Install SyncSAW management server dependencies

Run the bundled installer from a source checkout:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\.github\skills\install-syncsaw-server-dependencies\scripts\Install-ServerDependencies.ps1
```

When working from the packaged application, run:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Install-ServerDependencies.ps1
```

## Required procedure

1. Run `Install-ServerDependencies.ps1` as the same Windows user that will run
   `SyncSAW.App.exe`. WinGet may display a UAC prompt for machine-wide packages.
2. Let the installer validate the 64-bit Windows platform and actual installed
   versions before changing anything.
3. When installation is needed, the installer must validate the registered
   `winget` source as Microsoft's trusted HTTPS source before invoking these
   exact package IDs:
   - `Microsoft.DotNet.DesktopRuntime.8`
   - `Microsoft.AzureCLI`
   - `Microsoft.Azure.AZCopy.10`
4. Confirm the final readiness output reports:
   - Microsoft Windows Desktop Runtime 8.
   - Azure CLI 2.61.0 or later.
   - AzCopy 10.0.0 or later.
5. Start a new SyncSAW process after installation so it receives any PATH
   updates. Configure the local folder, storage account, and container, then
   complete Microsoft Entra sign-in.

Use `-WhatIf` to validate the platform, current versions, WinGet, and source
without installing packages.

## Security and policy

- Do not use unofficial package IDs, third-party download sites, package-source
  trust overrides, `Invoke-Expression`, or shell command concatenation.
- Do not install the .NET SDK on a deployed management server; the WPF client
  requires only the .NET 8 Desktop Runtime. The SDK is needed only to build from
  source.
- Do not install or persist storage account keys, SAS tokens, passwords, client
  secrets, or other credentials.
- Do not disable TLS, certificate, installer signature, or WinGet source
  validation.
- If WinGet is unavailable, stop and ask the user to install or repair
  Microsoft's **App Installer**. Do not bootstrap WinGet from an unverified
  source.
- Package installation does not grant Azure permissions. The signed-in identity
  still needs **Storage Blob Data Reader** for read-only use or **Storage Blob
  Data Contributor** for synchronization and deletion.

## Success handoff

Report the installed versions and resolved executable paths for `dotnet`, `az`,
and `azcopy`. Do not claim the server is ready if post-install validation fails.

