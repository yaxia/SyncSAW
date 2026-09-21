# `task.ps1` package contract

Place `task.ps1` at the root of the folder published as `cluster_package.zip`.
It is package-only: desktop and PowerShell normal synchronization exclude the
root file, while the package publisher includes it in the ZIP. Nested files
named `task.ps1` are not entrypoints and synchronize normally.
The cluster runner starts it with 64-bit Windows PowerShell 5.1, sets its
working directory to the extracted package root, waits for it to exit, and does
not poll or start another package while it is running. Before execution, the runner
applies the additive-only safety harness described below.

The package's generated `cluster_package.config` contains:

```json
{
  "SchemaVersion": 6,
  "PackageUri": "https://.../cluster_package.zip?<read-only-SAS>",
  "ResultsBlobUri": "https://.../<results-container>/cluster-results/<id>.zip?<create-only-SAS>",
  "IssuedUtc": "...",
  "ExpiresUtc": "..."
}
```

Schema version 6 is required. It separates the persistent `bootstrap.ps1`
poller from the package's `task.ps1` workload.

The runner starts `task.ps1` with `-BootstrapConfigPath` pointing to its
long-lived `bootstrap.config.json`. `task.ps1` must read `ResultsBlobUri` from
that path whenever it uploads test results. Never accept the SAS as a
source-code constant, copy it into another file, print it, or include it in an
exception. The URL is a rotating seven-day, HTTPS-only, Blob-scoped user delegation SAS
with only create permission (`sp=c`, `sr=b`) for one exact result archive. It
cannot create another Blob, list, read, overwrite, or delete data.

Workload-specific settings such as input data paths belong in package-local
`task.config.json`, not in the external workflow `bootstrap.config.json`.
General settings shared by every task, including `TaskExecutionPath` and
`OutputPath`, remain in `bootstrap.config.json` and survive SAS rollover. Root
`task.config.json` is package-only like `task.ps1`: normal synchronization
excludes it, while the cluster package publisher includes it.

The publisher also reads these reserved `task.config.json` properties:

```json
{
  "ResultPrefix": "result",
  "PackageChangeType": "CommandsOnly",
  "ExecutionArguments": ["-Mode", "quick-run"]
}
```

`Binary` is the default and is required whenever any package content changes.
`CommandsOnly` is valid only for a parameter-only round using the previously
installed package. In that mode bootstrap does not download the new ZIP or
updated `task.config.json`; it obtains the validated argument tokens from the
Blob update descriptor and invokes the installed `task.ps1`. Therefore declare
the corresponding parameters in the already installed task script. A
commands-only descriptor cannot change the executable, script entrypoint,
execution policy, or required `-BootstrapConfigPath`.

Before either update type runs, bootstrap validates
`syncsaw_bootstrap_config` from Blob metadata and persists its refreshed
package-read and exact result-create SAS URLs to external
`bootstrap.config.json`. This gives every command-only round a new result Blob
without downloading package content. The Base64 metadata value is not
encryption and must never be logged or copied.

By default, that exact Blob is under `cluster-results/` in the container already
monitored by the desktop app and `Sync-SAW.ps1`. Both clients therefore download
the result archive into their local sync folders for fast automated analysis.
Write all test artifacts beneath `test-results` in the extracted package, then
compress that directory into one ZIP and upload it to the exact
`ResultsBlobUri` as a Block Blob. Send `If-None-Match: *` so an existing result
cannot be replaced. See `task.example.ps1` for the Windows PowerShell
5.1-compatible pattern.

Return exit code `0` only after the expected result upload succeeds. On any
validation, workload, packaging, or upload failure, write an actionable error
without exposing SAS values and exit nonzero. Bootstrap records a nonzero exit
as machine-readable `TaskFailed` in `syncsaw-package-status.json`; agents treat
that state as terminal for the current iteration rather than waiting for a
result timeout.

## Safety harness

An agent creating or validating `task.ps1` must not restart the SyncSAW desktop
app, restart the devbox, or delete cloud Blobs. Use local builds, tests, and the
static harness instead:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Test-TaskScriptSafety.ps1 -Path <path-to-task.ps1>
```

`task.ps1` must treat the cluster node as immutable. It may create only new,
uniquely named files beneath a newly created `test-results` directory. It must
not delete, overwrite, truncate, append to, rename, move, or change permissions
on existing files; restart or stop the node, services, or processes; invoke a
nested shell or dynamic command; use remoting or system-management tools; or
issue any cloud delete operation. Invoke a bundled workload directly with a
literal package-relative executable path and give it only new output paths.

The cluster runner refuses scripts containing common destructive commands,
overwrite-capable commands, restart/process-control commands, dynamic
invocation, file redirection, destructive .NET file methods, or HTTP `DELETE`.
This static check is defense in depth, not a sandbox. A native executable can
still perform privileged operations, so package contents and publishers remain
trusted code.
