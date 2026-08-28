# SyncSAW

SyncSAW is a Windows Azure Blob synchronization solution for development
machines, Secure Admin Workstations (SAWs), and identity-less test clusters.

It includes:

- A .NET 8 WPF desktop publisher/client that uses AzCopy for planning and
  transfers.
- A standalone PowerShell 7 SAW client that uses Azure PowerShell.
- A Windows PowerShell 5.1 cluster bootstrap that securely polls and executes
  versioned test packages without an Entra identity or Azure modules.
- A testable core library for validation, process execution, parsing,
  synchronization state, scheduling, settings, and package publication.

Desktop-local files are authoritative when they exist, while cloud-only files
download without overwriting desktop content. On SAW devices, cloud is
authoritative for paths already present remotely. Deletion is always explicit
and confirmed.

## Documentation

| Audience | Guide | Contents |
| --- | --- | --- |
| Operators and users | [User manual](docs/USER-MANUAL.md) | Prerequisites, Azure roles, desktop setup, SAW setup, configuration, operation, sync semantics, and limitations |
| Coding agents | [Agent guide](docs/AGENT-GUIDE.md) | Cluster entities/data diagram, package protocol, SAS rollover, `bootstrap.ps1`, `task.ps1`, result return, and iteration workflow |
| Contributors | [Development guide](docs/DEVELOPMENT-GUIDE.md) | Architecture, solution structure, build, tests, publishing, conventions, and coverage expectations |
| Humans and agents | [Troubleshooting](docs/TROUBLESHOOTING.md) | Desktop, SAW, cluster, and development failures; logs; safe diagnostics |

Cluster workload authors must also follow the mandatory
[agent safety harness](AGENTS.md), the
[`task.ps1` contract](scripts/TASK-PS1.md), and the
[`generate-syncsaw-cluster-package` skill](.github/skills/generate-syncsaw-cluster-package/SKILL.md).

## Quick start

For an installed desktop release, prepare dependencies:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Install-ServerDependencies.ps1
```

Configure and run the standalone SAW client:

```powershell
pwsh .\scripts\Install-SawDependencies.ps1
pwsh .\scripts\Sync-SAW.ps1
```

Build and test from source:

```powershell
dotnet restore .\SyncSAW.sln
dotnet build .\SyncSAW.sln --configuration Release
dotnet test .\tests\SyncSAW.Tests\SyncSAW.Tests.csproj --configuration Release
```

Read the [user manual](docs/USER-MANUAL.md) before deployment and the
[troubleshooting guide](docs/TROUBLESHOOTING.md) before changing authentication,
dependencies, or synchronization state to resolve a failure.

## Security summary

- Prefer Microsoft Entra authentication; never persist storage account keys,
  passwords, or client secrets.
- Grant **Storage Blob Data Reader** for read-only use or
  **Storage Blob Data Contributor** for synchronization and deletion.
- Cluster package publishing requires data-plane permission at storage-account
  scope to create a user delegation key.
- Keep package containers private and treat package write access as privileged
  code-deployment access.
- Treat every SAS as a bearer credential: scope it narrowly, require HTTPS,
  protect its config file, redact it from logs, and rotate it before expiry.
