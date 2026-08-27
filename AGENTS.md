# SyncSAW agent safety harness

These rules apply to every agent that creates, edits, packages, or validates a
cluster `run.ps1`.

## Devbox restrictions

- Do not start, stop, restart, reinstall, or kill `SyncSAW.App.exe`.
- Do not restart its service, scheduled task, host, or the development computer.
- Do not delete or overwrite Azure Blobs for validation. In particular, do not
  run `az storage blob delete`, `azcopy remove`, `Remove-AzStorageBlob`, or an
  HTTP `DELETE` request.
- Validate with builds, unit tests, local static checks, and newly named local
  files. Leave the running desktop app and existing cloud data untouched.

## Generated `run.ps1` restrictions

- Treat the cluster node as immutable. Do not restart, shut down, stop, drain,
  reimage, or reconfigure the node or its services.
- Create new files only. Do not delete, truncate, rename, move, overwrite,
  append to, or change permissions on any existing file or directory.
- Put result artifacts in a newly created `test-results` directory and open
  every output with create-new semantics. Use unique names for temporary files.
- Upload only to the exact `ResultsBlobUri` from `cluster_package.config`, using
  `PUT` with `If-None-Match: *`. Do not list, read, overwrite, or delete cloud
  data.
- Invoke bundled test executables directly by a literal package-relative path.
  Do not use nested shells, dynamic command construction, remoting, process
  control, or system-management tools.
- Before packaging, run:

  ```powershell
  powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File `
    .\scripts\Test-RunScriptSafety.ps1 -Path <path-to-run.ps1>
  ```

The cluster runner applies the same static check immediately before execution.
The check is a defense-in-depth guardrail, not a security boundary: packaged
native executables still run as local administrator and must be trusted.
