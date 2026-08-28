<#
.SYNOPSIS
Checks a cluster task.ps1 against the SyncSAW additive-only safety policy.
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

. (Join-Path $PSScriptRoot 'bootstrap.ps1')

$validatedPath = Assert-TaskScriptSafety -Path $Path
Write-Host "PASS: '$validatedPath' satisfies the task.ps1 safety harness."
