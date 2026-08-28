---
name: generate-syncsaw-cluster-package
description: Generate or update a SyncSAW identity-less cluster package, bootstrap deployment, or task.ps1 workload. Use whenever an agent prepares bootstrap.ps1, task.ps1, cluster_package.zip, or cluster result upload logic.
compatibility: Windows cluster targets using 64-bit Windows PowerShell 5.1; publishing requires the SyncSAW desktop app and Azure CLI authentication.
metadata:
  author: SyncSAW
  version: "1.0"
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
3. Put the replaceable workload at the package root as `task.ps1`. The cluster
   poller executes only that filename. Normal desktop and PowerShell
   synchronization exclude root `task.ps1`; only the cluster package publisher
   includes it.
4. The package contains generated schema-5 `cluster_package.config`. After a
   successful download, the poller reads its refreshed `PackageUri` and
   atomically saves it to the external `bootstrap.config.json` before starting
   the task.
5. The same generated config contains `ResultsBlobUri`, an HTTPS exact-Blob user
   delegation SAS with create-only permission (`sp=c`, `sr=b`). The poller also
   saves it to the external config and starts `task.ps1` with
   `-BootstrapConfigPath <external-config>`.
6. `task.ps1` reads `ResultsBlobUri` from that supplied path, creates one new
   result ZIP, and uploads it with `PUT` plus `If-None-Match: *`. It must not
   embed, copy, or log either SAS.

`ResultsBlobUri` always targets
`<sync-container>/cluster-results/<unique>.zip`, allowing the desktop app and
SAW client to download results for the next agent iteration.

## Safety

Follow `AGENTS.md` and `scripts\TASK-PS1.md`. Validate generated workloads with:

```powershell
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
  .\scripts\Test-TaskScriptSafety.ps1 -Path <path-to-task.ps1>
```

Do not restart SyncSAW or a cluster node, delete or overwrite cloud Blobs, or
modify existing cluster files during validation.
