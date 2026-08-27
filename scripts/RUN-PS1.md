# `run.ps1` package contract

Place `run.ps1` at the root of the folder published as `cluster_package.zip`.
The cluster runner starts it with 64-bit Windows PowerShell 5.1, sets its
working directory to the extracted package root, waits for it to exit, and does
not poll for another package while it is running.

The adjacent generated `cluster_package.config` contains:

```json
{
  "SchemaVersion": 1,
  "PackageUri": "https://.../cluster_package.zip?<read-only-SAS>",
  "ResultsContainerUri": "https://.../<container>-results?<create-only-SAS>",
  "IssuedUtc": "...",
  "ExpiresUtc": "..."
}
```

`run.ps1` must read `ResultsContainerUri` from
`Join-Path $PSScriptRoot 'cluster_package.config'` whenever it uploads test
results. Never accept the SAS as a source-code constant, copy it into another
file, print it, or include it in an exception. The URL is a rotating seven-day, HTTPS-only, container-scoped user delegation
SAS with only create permission (`sp=c`, `sr=c`) for the separate
`<configured-container>-results` container; it cannot list, read, overwrite, or
delete Blobs.

Write results below a unique Blob prefix such as
`test-results/<machine>/<UTC-run-id>/` to prevent cluster machines from
overwriting one another. Encode every relative path segment, reject `.` and
`..`, upload each file as a Block Blob, and send `If-None-Match: *` so an
existing result cannot be replaced. See `run.example.ps1` for the Windows
PowerShell 5.1-compatible pattern.
