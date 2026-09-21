---
name: generate-syncsaw-cluster-package
description: Generate or update a SyncSAW identity-less cluster package, bootstrap deployment, or task.ps1 workload. Use whenever an agent prepares bootstrap.ps1, task.ps1, cluster_package.zip, or cluster result upload logic.
compatibility: Windows cluster targets using 64-bit Windows PowerShell 5.1; publishing requires the SyncSAW desktop app and Azure CLI authentication.
metadata:
  author: SyncSAW
  version: "1.3"
---

# Generate a SyncSAW cluster package

Read this contract before generating, modifying, packaging, or deploying
`bootstrap.ps1` or `task.ps1`.

## Fixed protocol

1. Deploy the repository's stable `scripts\bootstrap.ps1` beside its external
   `bootstrap.config.json`. Do not put the poller inside the package.
2. Read the package only from
   `https://<account>.blob.core.windows.net/<sync-container>-package/cluster_package.zip`.
   `PackageUri` in the external config is the complete exact-Blob read-only user
   delegation SAS URL for that endpoint.
3. Put the replaceable workload under
   `<sync-folder>\.syncsaw\package-source`, with `task.ps1` at that package
   source root. When this directory exists, the publisher packages only its
   contents. If it does not exist, the publisher retains the legacy behavior of
   building from the sync-folder root. The cluster poller executes only
   package-root `task.ps1`. The `.syncsaw` directory is excluded from normal
   desktop and PowerShell synchronization.
   Put workload-only settings in root `task.config.json`; it follows the same
   package-only synchronization rule. Keep general `TaskExecutionPath` and
   `OutputPath` settings in external `bootstrap.config.json`.
4. Every package Blob has descriptor-version-1 metadata. Read and follow the
   update descriptor protocol below; do not assume every changed ETag requires
   a download.
5. A binary package contains generated schema-6 `cluster_package.config`. After
   a successful hash-verified download, the poller reads its refreshed `PackageUri` and
   atomically saves it to the external `bootstrap.config.json` before starting
   the task.
6. The same generated config contains `ResultsBlobUri`, an HTTPS exact-Blob user
   delegation SAS with create-only permission (`sp=c`, `sr=b`). The poller also
   saves it to the external config and starts `task.ps1` with
   `-BootstrapConfigPath <external-config>`.
7. `task.ps1` reads `ResultsBlobUri` from that supplied path, creates one new
   result ZIP, and uploads it with `PUT` plus `If-None-Match: *`. It must not
   embed, copy, or log either SAS.

`ResultsBlobUri` always targets
`<sync-container>/cluster-results/<unique>.zip`, allowing the desktop app and
SAW client to download results for the next agent iteration.

## Update descriptor protocol

Set these Blob metadata fields when publishing `cluster_package.zip`:

| Key | Value |
| --- | --- |
| `syncsaw_descriptor_version` | `1` |
| `syncsaw_package_sha256` | Lowercase SHA-256 of the exact uploaded ZIP |
| `syncsaw_package_built_utc` | UTC round-trip build timestamp |
| `syncsaw_change_type` | `binary` or `commands-only` |
| `syncsaw_execution_command` | Commands-only Base64 UTF-8 JSON string array; omit for binary |
| `syncsaw_bootstrap_config` | Base64 GZip-compressed UTF-8 schema-6 JSON with refreshed package-read and exact result-create SAS URLs plus issue/expiry times |

The combined UTF-8 metadata names and values must not exceed 8,192 bytes.
SyncSAW generates and atomically uploads these fields; do not hand-edit them.

Choose the update type in package-root `task.config.json`:

```json
{
  "PackageChangeType": "CommandsOnly",
  "ExecutionArguments": ["-Mode", "quick-run"]
}
```

- Use `Binary` (the default) if any package file, script, executable, library, or
  input changed. Bootstrap downloads with ETag protection, verifies
  `syncsaw_package_sha256`, extracts, adopts schema-6 SAS rollover, and executes
  the new package-root `task.ps1`.
- Use `CommandsOnly` only if the cluster already has the required binary
  package and only task parameters changed. Bootstrap must not download,
  extract, or replace the ZIP contents. It runs the existing installed
  `task.ps1` using only the command arguments validated from Blob metadata.
- A first deployment and recovery after installed state/package loss must be
  `Binary`. A commands-only descriptor without a valid installed package fails
  closed.
- The desktop forces its first publication after startup to `Binary` because it
  has no in-memory publication baseline. To issue a command-only round, let that
  baseline publish first and then change `ExecutionArguments`.
- Commands-only does not load the new ZIP's `task.config.json`. It does validate
  `syncsaw_bootstrap_config`, atomically persists the refreshed SAS URLs, and
  gives the existing task a new one-use result upload target. For binary
  updates, the ZIP's `cluster_package.config` must match the metadata copy.
- GZip and Base64 are not encryption. The metadata configuration contains
  bearer credentials; never log, print, commit, or copy it to agent output.

The execution command has a fixed PowerShell 5.1 and `task.ps1` prefix. An agent
may provide at most 32 additional strings, each at most 1,024 UTF-8 bytes,
without control characters, and may not provide `-BootstrapConfigPath`.
Arbitrary executables, script paths, command strings, pipelines, and shell
operators are not part of this protocol.

## Observe execution; never wait blindly

`bootstrap.ps1` writes important lifecycle messages to its console with the
stable prefix `SYNCSAW_BOOTSTRAP` and persists the same critical state in
`syncsaw-package-status.json` under `TaskExecutionRoot`. The status JSON contains
no SAS values and is the machine-readable source for the current iteration.

After publishing, an agent must confirm one of these states instead of waiting
only for a result ZIP:

| State | Agent action |
| --- | --- |
| `PackageDetected`, `PackageInstalled`, `TaskRunning` | Continue monitoring. `TaskRunning` is refreshed as a heartbeat each polling interval. |
| `TaskSucceeded` | Wait only for normal synchronization to deliver the result ZIP. |
| `CycleFailed`, `TaskFailed` | Critical terminal condition for the current iteration. Stop waiting immediately, read `Message`, fix or republish, and do not wait for a timeout. |
| `NoTask` | The package cannot produce a result. Fix the package layout and republish. |

The console prints `SYNCSAW_BOOTSTRAP [ERROR]` for the first occurrence of a
failure. Identical retries remain in `syncsaw-package-runner.log` and increment
`ConsecutiveFailures` in the status JSON without flooding the console. If the
agent cannot access the console, status JSON, or runner log, it must report that
observability is blocked rather than waiting indefinitely. Before publishing,
always run the local task safety harness; a harness rejection is a
`CycleFailed` result and no task result ZIP will be created.

## Safety

Follow `AGENTS.md` and `scripts\TASK-PS1.md`. Validate generated workloads with:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Test-TaskScriptSafety.ps1 -Path <path-to-task.ps1>
```

Do not restart SyncSAW or a cluster node, delete or overwrite cloud Blobs, or
modify existing cluster files during validation.
