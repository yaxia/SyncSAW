# `run.ps1` package contract

Place `run.ps1` at the root of the folder published as `cluster_package.zip`.
The cluster runner starts it with 64-bit Windows PowerShell 5.1, sets its
working directory to the extracted package root, waits for it to exit, and does
not poll for another package while it is running.

The adjacent generated `cluster_package.config` contains:

```json
{
  "SchemaVersion": 2,
  "PackageUri": "https://.../cluster_package.zip?<read-only-SAS>",
  "ResultsBlobUri": "https://.../<results-container>/cluster-results/<id>.zip?<create-only-SAS>",
  "IssuedUtc": "...",
  "ExpiresUtc": "..."
}
```

Schema version 2 is required. It replaces the schema version 1
container-scoped result credential with one exact create-only result Blob; old
cluster runners must be upgraded before they are pointed at this package.

`run.ps1` must read `ResultsBlobUri` from
`Join-Path $PSScriptRoot 'cluster_package.config'` whenever it uploads test
results. Never accept the SAS as a source-code constant, copy it into another
file, print it, or include it in an exception. The URL is a rotating seven-day, HTTPS-only, Blob-scoped user delegation SAS
with only create permission (`sp=c`, `sr=b`) for one exact result archive. It
cannot create another Blob, list, read, overwrite, or delete data.

By default, that exact Blob is under `cluster-results/` in the container already
monitored by the desktop app and `Sync-SAW.ps1`. Both clients therefore download
the result archive into their local sync folders for fast automated analysis.
The desktop setting **Different results container** opts out of that replication.

Write all test artifacts beneath `test-results` in the extracted package, then
compress that directory into one ZIP and upload it to the exact
`ResultsBlobUri` as a Block Blob. Send `If-None-Match: *` so an existing result
cannot be replaced. See `run.example.ps1` for the Windows PowerShell
5.1-compatible pattern.
