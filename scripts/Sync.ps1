<#
.SYNOPSIS
Runs the SyncSAW Azure Blob synchronization client on cluster machines.

.DESCRIPTION
Sync.ps1 is the Windows PowerShell 5.1 cluster entry point for the same
synchronization implementation used by Sync-SAW.ps1. It uses Az.Accounts and
Az.Storage, supports adjacent JSON configuration, prevents overlapping jobs,
and keeps the same upload, download, deletion-request, marker, and
reauthentication semantics.

Run Install-ClusterDependencies.ps1 once as the same Windows user before
starting this script.

.EXAMPLE
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File .\Sync.ps1

.EXAMPLE
.\Sync.ps1 -LocalFolder 'D:\ClusterData' -StorageAccount 'contosodata' -Container 'files' -Continuous
#>

#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'Sync.config.json'),

    [Parameter()]
    [string]$LocalFolder,

    [Parameter()]
    [string]$StorageAccount,

    [Parameter()]
    [string]$Container,

    [Parameter()]
    [ValidateSet('AzurePowerShell', 'Sas')]
    [string]$AuthenticationMode = 'AzurePowerShell',

    [Parameter()]
    [ValidateRange(1, 86400)]
    [int]$IntervalSeconds = 10,

    [Parameter()]
    [switch]$Continuous,

    [Parameter()]
    [switch]$PauseSync,

    [Parameter()]
    [switch]$PublishSyncFlags = $true,

    [Parameter()]
    [string]$TenantId = '72f988bf-86f1-41af-91ab-2d7cd011db47',

    [Parameter()]
    [string]$SubscriptionId = 'a0d901ba-9956-4f7d-830c-2d7974c36666',

    [Parameter()]
    [string]$LogDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$implementationPath = Join-Path $PSScriptRoot 'Sync-SAW.ps1'
if (-not (Test-Path -LiteralPath $implementationPath -PathType Leaf)) {
    throw [System.IO.FileNotFoundException]::new(
        "The shared synchronization implementation was not found: $implementationPath"
    )
}

$arguments = @{}
foreach ($entry in $PSBoundParameters.GetEnumerator()) {
    $arguments[$entry.Key] = $entry.Value
}
$arguments.ConfigPath = $ConfigPath

& $implementationPath @arguments
