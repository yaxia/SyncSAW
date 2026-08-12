<#
.SYNOPSIS
Validates and installs the minimum PowerShell dependencies for Sync-SAW.

.DESCRIPTION
PowerShell 7 must first be installed manually from Software Center on the SAW.
The installer inventories registered PSResourceGet and PowerShellGet
repositories, validates and probes an approved HTTPS repository for the tested
minimum Az.Accounts and Az.Storage versions, installs missing modules for
CurrentUser, and verifies every command used by Sync-SAW.ps1.

It never installs or upgrades PowerShell, registers a repository, changes
repository trust, installs the full Az rollup module, or requests administrator
elevation.

.PARAMETER Repository
Optional registered repository name. When omitted, the first eligible repository
that provides both required module versions is selected.

.PARAMETER MinimumAzAccountsVersion
Minimum Az.Accounts version. Defaults to the tested version 5.5.0.

.PARAMETER MinimumAzStorageVersion
Minimum Az.Storage version. Defaults to the tested version 9.4.0.

.PARAMETER AllowUntrustedRepository
Allows installation from the selected registered repository when its PowerShell
trust flag is false. Use only after confirming the repository is SAW-approved.
The installer does not persist a trust change.

.EXAMPLE
.\Install-SawDependencies.ps1

.EXAMPLE
.\Install-SawDependencies.ps1 -Repository ContosoPowerShell
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$Repository,

    [Parameter()]
    [ValidateNotNull()]
    [version]$MinimumAzAccountsVersion = '5.5.0',

    [Parameter()]
    [ValidateNotNull()]
    [version]$MinimumAzStorageVersion = '9.4.0',

    [Parameter()]
    [switch]$AllowUntrustedRepository
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:SawPowerShellGuidance = 'http://aka.ms/sawpwsh'

function Get-ObjectPropertyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }
    return $property.Value
}

function Get-RegisteredModuleRepositories {
    [CmdletBinding()]
    param()

    $repositories = [System.Collections.Generic.List[object]]::new()
    if (
        $null -ne (Get-Command Get-PSResourceRepository -ErrorAction SilentlyContinue) -and
        $null -ne (Get-Command Find-PSResource -ErrorAction SilentlyContinue) -and
        $null -ne (Get-Command Install-PSResource -ErrorAction SilentlyContinue)
    ) {
        foreach ($item in @(Get-PSResourceRepository -ErrorAction Stop)) {
            $trusted = Get-ObjectPropertyValue -InputObject $item -Name 'Trusted'
            $uri = Get-ObjectPropertyValue -InputObject $item -Name 'Uri'
            $repositories.Add([pscustomobject]@{
                Name     = [string]$item.Name
                Provider = 'PSResourceGet'
                Uri      = [string]$uri
                Trusted  = $trusted -eq $true
            })
        }
    }

    if (
        $null -ne (Get-Command Get-PSRepository -ErrorAction SilentlyContinue) -and
        $null -ne (Get-Command Find-Module -ErrorAction SilentlyContinue) -and
        $null -ne (Get-Command Install-Module -ErrorAction SilentlyContinue)
    ) {
        foreach ($item in @(Get-PSRepository -ErrorAction Stop)) {
            $repositories.Add([pscustomobject]@{
                Name     = [string]$item.Name
                Provider = 'PowerShellGet'
                Uri      = [string]$item.SourceLocation
                Trusted  = $item.InstallationPolicy -eq 'Trusted'
            })
        }
    }

    return @($repositories)
}

function Find-RequiredModule {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$RepositoryInfo,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][version]$MinimumVersion
    )

    if ($RepositoryInfo.Provider -eq 'PSResourceGet') {
        $resource = Find-PSResource `
            -Name $Name `
            -Type Module `
            -Repository $RepositoryInfo.Name `
            -ErrorAction Stop |
            Sort-Object Version -Descending |
            Select-Object -First 1
    }
    else {
        $resource = Find-Module `
            -Name $Name `
            -Repository $RepositoryInfo.Name `
            -ErrorAction Stop |
            Sort-Object Version -Descending |
            Select-Object -First 1
    }

    if ($null -eq $resource -or [version]$resource.Version -lt $MinimumVersion) {
        return $null
    }
    return $resource
}

function Select-ModuleRepository {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Repositories,
        [Parameter()][AllowNull()][string]$RequestedName,
        [Parameter(Mandatory)][System.Collections.IDictionary]$Requirements,
        [Parameter(Mandatory)][bool]$PermitUntrusted
    )

    $candidates = @($Repositories | Where-Object {
        [string]::IsNullOrWhiteSpace($RequestedName) -or
        $_.Name -ieq $RequestedName
    })
    if ($candidates.Count -eq 0) {
        $registered = @($Repositories | Select-Object -ExpandProperty Name -Unique)
        $registeredText = if ($registered.Count -eq 0) {
            '<none>'
        }
        else {
            $registered -join ', '
        }
        throw [System.InvalidOperationException]::new(
            "The requested PowerShell repository is not registered. Registered: " +
            "$registeredText. Configure the approved SAW repository using " +
            "$script:SawPowerShellGuidance."
        )
    }

    $probeErrors = [System.Collections.Generic.List[string]]::new()
    foreach ($candidate in $candidates) {
        $repositoryUri = $null
        if (
            -not [uri]::TryCreate(
                [string]$candidate.Uri,
                [System.UriKind]::Absolute,
                [ref]$repositoryUri
            ) -or
            $repositoryUri.Scheme -ne [System.Uri]::UriSchemeHttps
        ) {
            $probeErrors.Add(
                "$($candidate.Name) ($($candidate.Provider)) does not use a valid " +
                'HTTPS source URI.'
            )
            continue
        }

        if (-not $candidate.Trusted -and -not $PermitUntrusted) {
            $probeErrors.Add(
                "$($candidate.Name) ($($candidate.Provider)) is registered but not " +
                'trusted. Confirm it is SAW-approved, then use ' +
                '-AllowUntrustedRepository.'
            )
            continue
        }

        try {
            foreach ($requirement in $Requirements.GetEnumerator()) {
                $resource = Find-RequiredModule `
                    -RepositoryInfo $candidate `
                    -Name $requirement.Key `
                    -MinimumVersion $requirement.Value
                if ($null -eq $resource) {
                    throw [System.InvalidOperationException]::new(
                        "$($requirement.Key) $($requirement.Value) or later was not found."
                    )
                }
            }
            return $candidate
        }
        catch {
            $probeErrors.Add(
                "$($candidate.Name) ($($candidate.Provider)): $($_.Exception.Message)"
            )
        }
    }

    throw [System.InvalidOperationException]::new(
        "No eligible registered repository provides the required SyncSAW modules. " +
        "$($probeErrors -join ' ') Follow $script:SawPowerShellGuidance, then retry."
    )
}

function Get-LatestInstalledModule {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    return Get-Module -Name $Name -ListAvailable |
        Sort-Object Version -Descending |
        Select-Object -First 1
}

function Install-RequiredModule {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][object]$RepositoryInfo,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][version]$MinimumVersion,
        [Parameter(Mandatory)][bool]$PermitUntrusted
    )

    $installed = Get-LatestInstalledModule -Name $Name
    if ($null -ne $installed -and [version]$installed.Version -ge $MinimumVersion) {
        Write-Host "$Name $($installed.Version) already satisfies the requirement."
        return
    }

    if (-not $PSCmdlet.ShouldProcess(
            "$Name $MinimumVersion or later from $($RepositoryInfo.Name)",
            'Install for CurrentUser')) {
        return
    }

    Write-Host (
        "Installing $Name $MinimumVersion or later from " +
        "$($RepositoryInfo.Name) for CurrentUser..."
    )
    if ($RepositoryInfo.Provider -eq 'PSResourceGet') {
        $installParameters = @{
            Name           = $Name
            Version        = "[$MinimumVersion,)"
            Repository     = $RepositoryInfo.Name
            Scope          = 'CurrentUser'
            AcceptLicense  = $true
            ErrorAction    = 'Stop'
        }
        if (-not $RepositoryInfo.Trusted -and $PermitUntrusted) {
            $installParameters.TrustRepository = $true
        }
        Install-PSResource @installParameters
    }
    else {
        Install-Module `
            -Name $Name `
            -MinimumVersion $MinimumVersion `
            -Repository $RepositoryInfo.Name `
            -Scope CurrentUser `
            -AllowClobber `
            -AcceptLicense `
            -Force `
            -ErrorAction Stop
    }
}

function Assert-RequiredCommands {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ModuleName,
        [Parameter(Mandatory)][string[]]$Commands
    )

    $missing = @($Commands | Where-Object {
        $null -eq (Get-Command -Name $_ -Module $ModuleName -ErrorAction SilentlyContinue)
    })
    if ($missing.Count -gt 0) {
        throw [System.InvalidOperationException]::new(
            "$ModuleName imported, but required commands are missing: " +
            ($missing -join ', ')
        )
    }
}

if (
    $PSVersionTable.PSEdition -ne 'Core' -or
    $PSVersionTable.PSVersion -lt [version]'7.0'
) {
    throw [System.PlatformNotSupportedException]::new(
        'PowerShell 7 is required on SAW. Install it manually from Software ' +
        'Center, then run this installer with pwsh. Do not install PowerShell ' +
        'from WinGet, an MSI download, or the Microsoft Store.'
    )
}

$requirements = [ordered]@{
    'Az.Accounts' = $MinimumAzAccountsVersion
    'Az.Storage'  = $MinimumAzStorageVersion
}
$requiredCommands = @{
    'Az.Accounts' = @(
        'Connect-AzAccount',
        'Enable-AzContextAutosave',
        'Get-AzAccessToken',
        'Get-AzContext',
        'Set-AzContext'
    )
    'Az.Storage' = @(
        'Get-AzStorageBlob',
        'Get-AzStorageBlobContent',
        'Get-AzStorageContainer',
        'New-AzStorageContainer',
        'New-AzStorageContext',
        'Remove-AzStorageBlob',
        'Set-AzStorageBlobContent'
    )
}

Write-Host "PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
$repositories = @(Get-RegisteredModuleRepositories)
if ($repositories.Count -eq 0) {
    throw [System.InvalidOperationException]::new(
        "No supported PowerShell module repository is registered. Follow " +
        "$script:SawPowerShellGuidance, then retry."
    )
}

Write-Host 'Registered module repositories:'
$repositories |
    Sort-Object Name, Provider |
    Format-Table Name, Provider, Trusted, Uri -AutoSize |
    Out-Host

$selectedRepository = Select-ModuleRepository `
    -Repositories $repositories `
    -RequestedName $Repository `
    -Requirements $requirements `
    -PermitUntrusted ([bool]$AllowUntrustedRepository)
Write-Host (
    "Selected repository: $($selectedRepository.Name) " +
    "($($selectedRepository.Provider))"
)

foreach ($requirement in $requirements.GetEnumerator()) {
    Install-RequiredModule `
        -RepositoryInfo $selectedRepository `
        -Name $requirement.Key `
        -MinimumVersion $requirement.Value `
        -PermitUntrusted ([bool]$AllowUntrustedRepository) `
        -WhatIf:$WhatIfPreference
}

if ($WhatIfPreference) {
    Write-Host 'WhatIf completed; module import validation was skipped.'
    return
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($requirement in $requirements.GetEnumerator()) {
    Remove-Module -Name $requirement.Key -Force -ErrorAction SilentlyContinue
    Import-Module `
        -Name $requirement.Key `
        -MinimumVersion $requirement.Value `
        -Force `
        -ErrorAction Stop
    Assert-RequiredCommands `
        -ModuleName $requirement.Key `
        -Commands $requiredCommands[$requirement.Key]
    $installed = Get-LatestInstalledModule -Name $requirement.Key
    if ($null -eq $installed -or [version]$installed.Version -lt $requirement.Value) {
        throw [System.InvalidOperationException]::new(
            "$($requirement.Key) did not meet its minimum version after installation."
        )
    }
    $results.Add([pscustomobject]@{
        Module  = $requirement.Key
        Version = [string]$installed.Version
        Path    = [string]$installed.ModuleBase
    })
}

Write-Host ''
Write-Host 'SyncSAW SAW dependency readiness: PASS' -ForegroundColor Green
$results | Format-Table Module, Version, Path -AutoSize | Out-Host
Write-Host 'Review Sync-SAW.config.json, then run Sync-SAW.ps1.'
