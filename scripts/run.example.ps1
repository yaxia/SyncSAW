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

function New-ResultBlobUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerSasUri,
        [Parameter(Mandatory)][string]$BlobPath
    )

    $normalized = $BlobPath.Replace('\', '/').TrimStart('/')
    $segments = @($normalized.Split('/'))
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        $segments -contains '.' -or
        $segments -contains '..' -or
        @($segments | Where-Object {
            [string]::IsNullOrWhiteSpace($_)
        }).Count -gt 0) {
        throw [ArgumentException]::new('BlobPath must be a safe relative path.')
    }

    $container = [uri]$ContainerSasUri
    $escapedPath = ($segments | ForEach-Object {
        [uri]::EscapeDataString($_)
    }) -join '/'
    $builder = [UriBuilder]::new($container)
    $builder.Path = $container.AbsolutePath.TrimEnd('/') + '/' + $escapedPath
    return $builder.Uri.AbsoluteUri
}

function Send-TestResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerSasUri,
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string]$BlobPath
    )

    $request = [Net.HttpWebRequest]::Create(
        (New-ResultBlobUri -ContainerSasUri $ContainerSasUri -BlobPath $BlobPath)
    )
    $request.Method = 'PUT'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.Headers['x-ms-blob-type'] = 'BlockBlob'
    $request.Headers['x-ms-version'] = '2023-11-03'
    $request.Headers['If-None-Match'] = '*'
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
if ([string]::IsNullOrWhiteSpace([string]$configuration.ResultsContainerUri)) {
    throw [IO.InvalidDataException]::new(
        'cluster_package.config does not contain ResultsContainerUri.'
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

$runId = '{0}-{1}' -f [DateTimeOffset]::UtcNow.ToString('yyyyMMddTHHmmssZ'),
    [guid]::NewGuid().ToString('N').Substring(0, 8)
$machine = [regex]::Replace($env:COMPUTERNAME, '[^A-Za-z0-9._-]', '_')
foreach ($file in $resultFiles) {
    $relative = $file.FullName.Substring($resultRoot.Length).TrimStart(
        [char[]]@('\', '/')
    )
    $blobPath = 'test-results/{0}/{1}/{2}' -f $machine, $runId,
        $relative.Replace('\', '/')
    Send-TestResult `
        -ContainerSasUri ([string]$configuration.ResultsContainerUri) `
        -FilePath $file.FullName `
        -BlobPath $blobPath
}
