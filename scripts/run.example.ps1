<#
Copy this file to run.ps1 in the package root. Run the test workload in the
marked section and write result files beneath test-results. The upload helper
reads its rotating SAS from cluster_package.config; do not embed or log a SAS.
#>

#requires -Version 5.1

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Send-TestResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BlobSasUri,
        [Parameter(Mandatory)][string]$FilePath
    )

    $request = [Net.HttpWebRequest]::Create($BlobSasUri)
    $request.Method = 'PUT'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.Headers['x-ms-blob-type'] = 'BlockBlob'
    $request.Headers['x-ms-version'] = '2023-11-03'
    $request.Headers['If-None-Match'] = '*'
    $request.ContentType = 'application/zip'
    $input = [IO.File]::OpenRead($FilePath)
    $output = $null
    $response = $null
    try {
        $request.ContentLength = $input.Length
        $output = $request.GetRequestStream()
        $input.CopyTo($output)
        $output.Dispose()
        $output = $null
        $response = [Net.HttpWebResponse]$request.GetResponse()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::Created) {
            throw [InvalidOperationException]::new(
                "Result upload returned HTTP $([int]$response.StatusCode)."
            )
        }
    }
    catch [Net.WebException] {
        $status = if ($null -ne $_.Exception.Response) {
            [int]([Net.HttpWebResponse]$_.Exception.Response).StatusCode
        }
        else {
            'network error'
        }
        throw [InvalidOperationException]::new(
            "Result upload failed: $status."
        )
    }
    finally {
        if ($null -ne $response) {
            $response.Dispose()
        }
        if ($null -ne $output) {
            $output.Dispose()
        }
        $input.Dispose()
    }
}

$configurationPath = Join-Path $PSScriptRoot 'cluster_package.config'
$configuration = Get-Content -LiteralPath $configurationPath -Raw |
    ConvertFrom-Json -ErrorAction Stop
if ([string]::IsNullOrWhiteSpace([string]$configuration.ResultsBlobUri)) {
    throw [IO.InvalidDataException]::new(
        'cluster_package.config does not contain ResultsBlobUri.'
    )
}

$resultRoot = Join-Path $PSScriptRoot 'test-results'
[void](New-Item -ItemType Directory -Path $resultRoot -Force)

# Run the test workload here. Write every result artifact beneath $resultRoot.

$resultFiles = @(Get-ChildItem -LiteralPath $resultRoot -File -Recurse)
if ($resultFiles.Count -eq 0) {
    throw [IO.InvalidDataException]::new(
        "The test workload did not create results under '$resultRoot'."
    )
}

$archivePath = Join-Path ([IO.Path]::GetTempPath()) (
    'syncsaw-results-{0}.zip' -f [guid]::NewGuid().ToString('N')
)
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $resultRoot,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false
    )
    Send-TestResult `
        -BlobSasUri ([string]$configuration.ResultsBlobUri) `
        -FilePath $archivePath
}
finally {
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
}
