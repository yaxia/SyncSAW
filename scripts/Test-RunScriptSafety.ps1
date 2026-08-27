<# 
.SYNOPSIS
Checks a cluster run.ps1 against the SyncSAW additive-only safety policy.
#>

#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Sync.ps1')

$validatedPath = Assert-RunScriptSafety -Path $Path
Write-Host "PASS: '$validatedPath' satisfies the run.ps1 safety harness."
