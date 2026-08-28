# SyncSAW development guide

[Project overview](../README.md) | [User manual](USER-MANUAL.md) |
[Agent guide](AGENT-GUIDE.md) | [Troubleshooting](TROUBLESHOOTING.md)

This guide covers the solution structure, architectural boundaries, local
build, tests, and implementation conventions.

## Development prerequisites

- Windows 10 or later.
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
- PowerShell 7 for development and direct testing of `Sync-SAW.ps1`.
- AzCopy v10 and Azure CLI 2.61 or later for interactive end-to-end desktop
  testing.
- Windows PowerShell 5.1 for cluster bootstrap compatibility testing.

Azure credentials are not required for the unit test suite. Do not put storage
keys, SAS URLs, passwords, client secrets, or other credentials in source,
fixtures, logs, or package payloads.

## Solution structure

| Path | Responsibility |
| --- | --- |
| `src\SyncSAW.App` | .NET 8 WPF desktop UI, notification-area behavior, authentication orchestration, and user interaction |
| `src\SyncSAW.Core` | Validation, endpoints, AzCopy argument construction/execution/parsing, sync planning, scheduling, settings, logs, SAW markers, package publishing, and single-instance coordination |
| `tests\SyncSAW.Tests` | Unit tests for the core and process boundaries |
| `tests\PowerShell` | PowerShell behavior and cluster protocol tests |
| `scripts\Sync-SAW.ps1` | Standalone PowerShell SAW synchronization client |
| `scripts\bootstrap.ps1` | Identity-less Windows PowerShell 5.1 cluster package poller |
| `scripts\Test-TaskScriptSafety.ps1` | Static additive-only task validator |
| `.github\skills` | Dependency preparation and cluster-package generation procedures |
| `docs` | User, agent, development, and troubleshooting documentation |

## Architecture

`SyncSAW.App` owns presentation and application lifetime. It depends on
`SyncSAW.Core`; the core library does not depend on WPF. Process execution and
other external behavior are behind interfaces so argument construction,
parsing, cancellation, failures, and scheduling remain unit-testable.

The desktop uses AzCopy for transfer and dry-run planning. It starts AzCopy
directly with `ProcessStartInfo.ArgumentList`, captures standard output/error
and exit codes, and terminates the child process tree on cancellation. Never
replace this with shell command concatenation or reimplement file copying.

`NonOverlappingOperationScheduler` provides one operation gate for refresh,
sync, and destructive work. `SingleInstanceCoordinator` limits the desktop to
one process per signed-in Windows user and foregrounds the existing window.

`Sync-SAW.ps1` intentionally uses Azure PowerShell rather than AzCopy. It
supports PowerShell 7 on SAW devices and remains syntactically compatible with
Windows PowerShell 5.1 where the required Az modules are available.

The cluster workflow is separate from normal synchronization. The stable
`bootstrap.ps1` runner validates the versioned Blob metadata descriptor first.
Binary updates are conditionally downloaded, hash-verified, installed, and used
for SAS rollover. Commands-only updates skip download and run the last installed
package's `task.ps1` with validated metadata arguments. See the
[agent guide](AGENT-GUIDE.md) before changing this protocol.

## Build and test

Restore, build, and run the .NET unit tests:

```powershell
dotnet restore .\SyncSAW.sln
dotnet build .\SyncSAW.sln --configuration Release
dotnet test .\tests\SyncSAW.Tests\SyncSAW.Tests.csproj --configuration Release
```

Run the desktop from source:

```powershell
dotnet run --project .\src\SyncSAW.App\SyncSAW.App.csproj
```

Run the PowerShell test suite when Pester is available:

```powershell
Invoke-Pester -Path .\tests\PowerShell
```

Use the smallest targeted test selection that covers a change before running a
larger suite.

## Publishing

Create a framework-dependent Windows x64 build:

```powershell
dotnet publish .\src\SyncSAW.App\SyncSAW.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained false `
  --output .\artifacts\SyncSAW-win-x64
```

The target machine requires Microsoft Windows Desktop Runtime 8. Preserve
existing `settings.json` and other configuration when updating an installed
copy. Do not automatically stop or restart a running desktop process. When
assembling a release archive, include the root `README.md`, `AGENTS.md`, and the
complete `docs` directory so documentation links remain valid.

## Implementation conventions

- Validate and normalize storage account, Blob host, container, relative path,
  and SAS input before use.
- Pass child-process arguments as discrete values. Do not invoke a shell or use
  `Invoke-Expression`.
- Prefer Microsoft Entra authentication. Do not persist account keys or
  application secrets.
- Redact SAS query strings and signatures from all logs and error messages.
- Keep external process execution behind `IAzCopyRunner` or an equivalent
  testable boundary.
- Preserve cancellation and propagate nonzero exit codes with actionable
  errors.
- Keep synchronization deterministic: desktop-local files are authoritative
  on the development machine; cloud is authoritative for existing SAW paths.
- Require explicit selection and confirmation for deletion. Do not introduce
  broad mirroring deletion.
- Keep scheduled jobs non-overlapping.
- Maintain Windows PowerShell 5.1 compatibility in cluster scripts.
- Treat root `task.ps1`, package configuration, marker paths, and deletion
  requests as reserved protocol artifacts.

## Test coverage expectations

Add or update targeted tests for changes involving:

- AzCopy argument construction and safe process invocation.
- Storage URL, account, container, path, and SAS validation.
- Machine-readable AzCopy output parsing.
- State comparison and synchronization planning.
- Cancellation, errors, and exit-code propagation.
- Non-overlapping scheduling and single-instance activation.
- Settings migration and secret redaction.
- SAW marker/deletion behavior.
- Cluster archive construction, update descriptor metadata/limits, binary
  hash verification, command-only no-download selection, SAS scope, rollover,
  safety checks, and execution gating.

Documentation-only changes do not require a product build, but links, fenced
blocks, and repository-relative paths should still be checked.

## Documentation ownership

- End-user installation and operation: [user manual](USER-MANUAL.md).
- Coding-agent and cluster-package protocol: [agent guide](AGENT-GUIDE.md).
- Build and architecture: this guide.
- Failures and diagnostics: [troubleshooting](TROUBLESHOOTING.md).
- Mandatory cluster task restrictions: [`AGENTS.md`](../AGENTS.md) and
  [`scripts\TASK-PS1.md`](../scripts/TASK-PS1.md).
