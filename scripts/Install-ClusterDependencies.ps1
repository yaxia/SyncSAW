<#
.SYNOPSIS
Installs the minimum SyncSAW dependencies for Windows PowerShell 5.1 clusters.

.DESCRIPTION
Validates 64-bit Windows PowerShell 5.1, an HTTPS PowerShellGet repository, and
the tested Az.Accounts and Az.Storage versions. Missing modules are installed
for CurrentUser, imported, and checked for every command and parameter used by
Sync.ps1.

The installer does not install PowerShell, persist repository trust changes,
install the full Az rollup module, or store Azure credentials.

.PARAMETER Repository
Registered PowerShellGet repository. Defaults to PSGallery.

.PARAMETER AllowUntrustedRepository
Allows an explicitly selected custom repository whose InstallationPolicy is
Untrusted. PSGallery is accepted only when it resolves to the official HTTPS
PowerShell Gallery endpoint.

.EXAMPLE
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File .\Install-ClusterDependencies.ps1

.EXAMPLE
.\Install-ClusterDependencies.ps1 -Repository ContosoPowerShell -AllowUntrustedRepository
#>

#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Repository = 'PSGallery',

    [Parameter()]
    [ValidateNotNull()]
    [version]$MinimumAzAccountsVersion = '5.3.4',

    [Parameter()]
    [ValidateNotNull()]
    [version]$MinimumAzStorageVersion = '9.4.0',

    [Parameter()]
    [switch]$AllowUntrustedRepository
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LatestInstalledClusterModule {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    return Get-Module -Name $Name -ListAvailable |
        Where-Object {
            ($null -eq $_.PowerShellVersion -or $_.PowerShellVersion -le $PSVersionTable.PSVersion) -and
            (
                $null -eq $_.CompatiblePSEditions -or
                $_.CompatiblePSEditions.Count -eq 0 -or
                $_.CompatiblePSEditions -contains 'Desktop'
            )
        } |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

function Assert-ClusterCommands {
    [CmdletBinding()]
    param()

    $requirements = @{
        'Connect-AzAccount'          = @('Tenant', 'Subscription', 'Force')
        'Enable-AzContextAutosave'  = @('Scope')
        'Get-AzAccessToken'         = @('ResourceUrl', 'TenantId', 'DefaultProfile')
        'Get-AzContext'             = @('ListAvailable')
        'Set-AzContext'             = @('Context', 'Tenant', 'Subscription', 'Scope')
        'Get-AzStorageBlob'         = @('Container', 'Context')
        'Get-AzStorageBlobContent'  = @('Container', 'Blob', 'Destination', 'Context', 'Force')
        'Get-AzStorageContainer'    = @('Name', 'Context')
        'New-AzStorageContainer'    = @('Name', 'Context')
        'New-AzStorageContext'      = @('StorageAccountName', 'UseConnectedAccount', 'SasToken')
        'Remove-AzStorageBlob'      = @('Container', 'Blob', 'Context', 'Force')
        'Set-AzStorageBlobContent'  = @('File', 'Container', 'Blob', 'Context', 'Force')
    }

    foreach ($entry in $requirements.GetEnumerator()) {
        $command = Get-Command -Name $entry.Key -ErrorAction SilentlyContinue
        if ($null -eq $command) {
            throw [System.InvalidOperationException]::new(
                "Required command '$($entry.Key)' is unavailable."
            )
        }
        $missingParameters = @($entry.Value | Where-Object {
            -not $command.Parameters.ContainsKey($_)
        })
        if ($missingParameters.Count -gt 0) {
            throw [System.InvalidOperationException]::new(
                "Command '$($entry.Key)' is missing required parameters: " +
                ($missingParameters -join ', ')
            )
        }
    }
}

if ($PSVersionTable.PSEdition -ne 'Desktop') {
    throw [System.PlatformNotSupportedException]::new(
        'Run this cluster installer with 64-bit Windows PowerShell 5.1 (powershell.exe).'
    )
}
if (-not [Environment]::Is64BitProcess) {
    throw [System.PlatformNotSupportedException]::new(
        'Run the 64-bit Windows PowerShell host from System32, not SysWOW64.'
    )
}

$installModuleCommand = Get-Command Install-Module -ErrorAction SilentlyContinue
$findModuleCommand = Get-Command Find-Module -ErrorAction SilentlyContinue
if ($null -eq $installModuleCommand -or $null -eq $findModuleCommand) {
    throw [System.IO.FileNotFoundException]::new(
        'PowerShellGet is required. Repair or update Windows Management Framework ' +
        'before running the cluster dependency installer.'
    )
}

$registeredRepository = Get-PSRepository -Name $Repository -ErrorAction SilentlyContinue
if ($null -eq $registeredRepository) {
    throw [System.InvalidOperationException]::new(
        "PowerShell repository '$Repository' is not registered."
    )
}
$repositoryUri = $null
if (
    -not [uri]::TryCreate(
        [string]$registeredRepository.SourceLocation,
        [System.UriKind]::Absolute,
        [ref]$repositoryUri
    ) -or
    $repositoryUri.Scheme -ne [System.Uri]::UriSchemeHttps
) {
    throw [System.InvalidOperationException]::new(
        "PowerShell repository '$Repository' must use an HTTPS source."
    )
}

$isOfficialGallery = $registeredRepository.Name -eq 'PSGallery' -and
    $repositoryUri.Host -ieq 'www.powershellgallery.com'
if (
    $registeredRepository.InstallationPolicy -ne 'Trusted' -and
    -not $isOfficialGallery -and
    -not $AllowUntrustedRepository
) {
    throw [System.InvalidOperationException]::new(
        "Repository '$Repository' is untrusted. Confirm it is approved, then rerun " +
        'with -AllowUntrustedRepository. Trust is not persisted.'
    )
}

$originalSecurityProtocol = [Net.ServicePointManager]::SecurityProtocol
[Net.ServicePointManager]::SecurityProtocol =
    $originalSecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

try {
    $nugetProvider = Get-PackageProvider -Name NuGet -ErrorAction SilentlyContinue
    if ($null -eq $nugetProvider -or [version]$nugetProvider.Version -lt [version]'2.8.5.201') {
        if ($PSCmdlet.ShouldProcess('NuGet package provider 2.8.5.201+', 'Install for CurrentUser')) {
            [void](Install-PackageProvider `
                -Name NuGet `
                -MinimumVersion '2.8.5.201' `
                -Scope CurrentUser `
                -Force `
                -ErrorAction Stop)
        }
    }

    $requirements = [ordered]@{
        'Az.Accounts' = $MinimumAzAccountsVersion
        'Az.Storage'  = $MinimumAzStorageVersion
    }
    foreach ($requirement in $requirements.GetEnumerator()) {
        $available = Find-Module `
            -Name $requirement.Key `
            -Repository $registeredRepository.Name `
            -MinimumVersion $requirement.Value `
            -ErrorAction Stop
        if ($null -eq $available) {
            throw [System.InvalidOperationException]::new(
                "$($requirement.Key) $($requirement.Value) or later was not found " +
                "in '$($registeredRepository.Name)'."
            )
        }
    }

    foreach ($requirement in $requirements.GetEnumerator()) {
        $installed = Get-LatestInstalledClusterModule -Name $requirement.Key
        if ($null -ne $installed -and [version]$installed.Version -ge $requirement.Value) {
            Write-Host "$($requirement.Key) $($installed.Version) already satisfies the requirement."
            continue
        }

        if ($PSCmdlet.ShouldProcess(
                "$($requirement.Key) $($requirement.Value)+ from $($registeredRepository.Name)",
                'Install for CurrentUser')) {
            $installParameters = @{
                Name           = $requirement.Key
                MinimumVersion = $requirement.Value
                Repository     = $registeredRepository.Name
                Scope          = 'CurrentUser'
                AllowClobber   = $true
                Force          = $true
                ErrorAction    = 'Stop'
            }
            if ($installModuleCommand.Parameters.ContainsKey('AcceptLicense')) {
                $installParameters.AcceptLicense = $true
            }
            Install-Module @installParameters
        }
    }

    if ($WhatIfPreference) {
        Write-Host 'WhatIf completed; module import validation was skipped.'
        return
    }

    $results = foreach ($requirement in $requirements.GetEnumerator()) {
        Remove-Module -Name $requirement.Key -Force -ErrorAction SilentlyContinue
        $installed = Get-LatestInstalledClusterModule -Name $requirement.Key
        if ($null -eq $installed -or [version]$installed.Version -lt $requirement.Value) {
            throw [System.InvalidOperationException]::new(
                "$($requirement.Key) did not meet version $($requirement.Value) after installation."
            )
        }
        Import-Module -Name $installed.Path -Force -ErrorAction Stop
        [pscustomobject]@{
            Module  = $requirement.Key
            Version = [string]$installed.Version
            Path    = [string]$installed.ModuleBase
        }
    }

    Assert-ClusterCommands
    Write-Host ''
    Write-Host 'SyncSAW cluster dependency readiness: PASS' -ForegroundColor Green
    $results | Format-Table Module, Version, Path -AutoSize | Out-Host
    Write-Host 'Review Sync.config.json, then run Sync.ps1 with Windows PowerShell 5.1.'
}
finally {
    [Net.ServicePointManager]::SecurityProtocol = $originalSecurityProtocol
}
