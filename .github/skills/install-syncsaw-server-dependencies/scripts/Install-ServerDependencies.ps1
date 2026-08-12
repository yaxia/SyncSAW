<#
.SYNOPSIS
Validates and installs prerequisites for the SyncSAW WPF management client.

.DESCRIPTION
The installer validates 64-bit Windows, Microsoft Windows Desktop Runtime 8,
Azure CLI 2.61 or later, and AzCopy v10. Missing or outdated prerequisites are
installed by exact package ID from Microsoft's official WinGet source. All
installed tools are probed again before readiness is reported.

.PARAMETER MinimumAzureCliVersion
Minimum Azure CLI version. Defaults to 2.61.0.

.PARAMETER MinimumAzCopyVersion
Minimum AzCopy version. Defaults to 10.0.0.

.PARAMETER DotNetPath
Optional explicit path to dotnet.exe.

.PARAMETER AzureCliPath
Optional explicit path to az.cmd or az.exe.

.PARAMETER AzCopyPath
Optional explicit path to azcopy.exe.

.PARAMETER WingetPath
Optional explicit path to winget.exe.

.EXAMPLE
.\Install-ServerDependencies.ps1

.EXAMPLE
.\Install-ServerDependencies.ps1 -WhatIf
#>

#requires -Version 5.1

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter()]
    [ValidateNotNull()]
    [version]$MinimumAzureCliVersion = '2.61.0',

    [Parameter()]
    [ValidateNotNull()]
    [version]$MinimumAzCopyVersion = '10.0.0',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$DotNetPath,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$AzureCliPath,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$AzCopyPath,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$WingetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:WingetSourceName = 'winget'
$script:WingetSourceHost = 'cdn.winget.microsoft.com'

function Invoke-ExternalCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter()][string[]]$Arguments = @()
    )

    $output = @(& $FilePath @Arguments 2>&1 | ForEach-Object { $_.ToString() })
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        $detail = ($output -join [Environment]::NewLine).Trim()
        throw [System.InvalidOperationException]::new(
            "'$FilePath' exited with code $exitCode." +
            $(if ([string]::IsNullOrWhiteSpace($detail)) { '' } else { " $detail" })
        )
    }

    return ($output -join [Environment]::NewLine).Trim()
}

function Resolve-Executable {
    [CmdletBinding()]
    param(
        [Parameter()][AllowNull()][string]$ExplicitPath,
        [Parameter(Mandatory)][string]$CommandName,
        [Parameter()][string[]]$Candidates = @()
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = [System.IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw [System.IO.FileNotFoundException]::new(
                "The configured $CommandName path does not exist: $resolved"
            )
        }
        return $resolved
    }

    foreach ($candidate in $Candidates | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_)
    }) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    $command = Get-Command $CommandName -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if ($null -ne $command) {
        $path = if (-not [string]::IsNullOrWhiteSpace($command.Source)) {
            $command.Source
        } else {
            $command.Path
        }
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            return [System.IO.Path]::GetFullPath($path)
        }
    }

    throw [System.IO.FileNotFoundException]::new(
        "$CommandName was not found."
    )
}

function Get-ProgramFilesCandidates {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RelativePath)

    return @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    ) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique |
        ForEach-Object { Join-Path $_ $RelativePath }
}

function Get-VersionedAzCopyCandidates {
    [CmdletBinding()]
    param()

    $results = [System.Collections.Generic.List[string]]::new()
    $roots = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    ) | Where-Object {
        -not [string]::IsNullOrWhiteSpace($_) -and
        (Test-Path -LiteralPath $_ -PathType Container)
    } | Select-Object -Unique

    foreach ($root in $roots) {
        foreach ($directory in @(Get-ChildItem `
                -LiteralPath $root `
                -Directory `
                -Filter 'azcopy_windows_*' `
                -ErrorAction SilentlyContinue |
                Sort-Object Name -Descending)) {
            $results.Add((Join-Path $directory.FullName 'azcopy.exe'))
        }
    }

    $localAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($localAppData)) {
        $results.Add((Join-Path $localAppData 'Microsoft\WinGet\Links\azcopy.exe'))
        $packageRoot = Join-Path $localAppData 'Microsoft\WinGet\Packages'
        if (Test-Path -LiteralPath $packageRoot -PathType Container) {
            foreach ($file in @(Get-ChildItem `
                    -LiteralPath $packageRoot `
                    -Filter 'azcopy.exe' `
                    -File `
                    -Recurse `
                    -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.FullName -like '*Microsoft.Azure.AZCopy.10*'
                    })) {
                $results.Add($file.FullName)
            }
        }
    }

    return @($results)
}

function New-PrerequisiteState {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][bool]$Ready,
        [Parameter()][AllowNull()][version]$Version,
        [Parameter()][AllowNull()][string]$Path,
        [Parameter(Mandatory)][string]$Detail
    )

    return [pscustomobject]@{
        Name      = $Name
        PackageId = $PackageId
        Ready     = $Ready
        Version   = $Version
        Path      = $Path
        Detail    = $Detail
    }
}

function Get-DesktopRuntimeState {
    [CmdletBinding()]
    param([Parameter()][AllowNull()][string]$ConfiguredPath)

    $packageId = 'Microsoft.DotNet.DesktopRuntime.8'
    try {
        $candidates = @(Get-ProgramFilesCandidates -RelativePath 'dotnet\dotnet.exe')
        $executable = Resolve-Executable `
            -ExplicitPath $ConfiguredPath `
            -CommandName 'dotnet' `
            -Candidates $candidates
        $output = Invoke-ExternalCommand `
            -FilePath $executable `
            -Arguments @('--list-runtimes')
        $versions = @(
            foreach ($line in $output -split '\r?\n') {
                if ($line -match '^Microsoft\.WindowsDesktop\.App\s+(\d+\.\d+\.\d+)') {
                    [version]$Matches[1]
                }
            }
        )
        $version = $versions |
            Where-Object { $_.Major -eq 8 } |
            Sort-Object -Descending |
            Select-Object -First 1
        $ready = $null -ne $version
        return New-PrerequisiteState `
            -Name '.NET Windows Desktop Runtime 8' `
            -PackageId $packageId `
            -Ready $ready `
            -Version $version `
            -Path $executable `
            -Detail $(if ($ready) {
                'Ready'
            } else {
                'Microsoft.WindowsDesktop.App 8.x was not found.'
            })
    }
    catch {
        return New-PrerequisiteState `
            -Name '.NET Windows Desktop Runtime 8' `
            -PackageId $packageId `
            -Ready $false `
            -Version $null `
            -Path $null `
            -Detail $_.Exception.Message
    }
}

function Get-AzureCliState {
    [CmdletBinding()]
    param(
        [Parameter()][AllowNull()][string]$ConfiguredPath,
        [Parameter(Mandatory)][version]$MinimumVersion
    )

    $packageId = 'Microsoft.AzureCLI'
    try {
        $candidates = @(
            Get-ProgramFilesCandidates `
                -RelativePath 'Microsoft SDKs\Azure\CLI2\wbin\az.cmd'
        )
        $executable = Resolve-Executable `
            -ExplicitPath $ConfiguredPath `
            -CommandName 'az' `
            -Candidates $candidates
        $output = Invoke-ExternalCommand `
            -FilePath $executable `
            -Arguments @('version', '--output', 'json', '--only-show-errors')
        $jsonStart = $output.IndexOf('{')
        if ($jsonStart -lt 0) {
            throw [System.FormatException]::new(
                'Azure CLI did not return JSON version output.'
            )
        }
        $data = $output.Substring($jsonStart) | ConvertFrom-Json -ErrorAction Stop
        $version = [version]$data.'azure-cli'
        $ready = $version -ge $MinimumVersion
        return New-PrerequisiteState `
            -Name 'Azure CLI' `
            -PackageId $packageId `
            -Ready $ready `
            -Version $version `
            -Path $executable `
            -Detail $(if ($ready) {
                'Ready'
            } else {
                "Version $version is older than $MinimumVersion."
            })
    }
    catch {
        return New-PrerequisiteState `
            -Name 'Azure CLI' `
            -PackageId $packageId `
            -Ready $false `
            -Version $null `
            -Path $null `
            -Detail $_.Exception.Message
    }
}

function Get-AzCopyState {
    [CmdletBinding()]
    param(
        [Parameter()][AllowNull()][string]$ConfiguredPath,
        [Parameter(Mandatory)][version]$MinimumVersion
    )

    $packageId = 'Microsoft.Azure.AZCopy.10'
    try {
        $candidates = @(
            $env:AZCOPY_PATH,
            (Get-ProgramFilesCandidates -RelativePath 'AzCopy\azcopy.exe'),
            (Get-VersionedAzCopyCandidates)
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $executable = Resolve-Executable `
            -ExplicitPath $ConfiguredPath `
            -CommandName 'azcopy' `
            -Candidates $candidates
        $output = Invoke-ExternalCommand `
            -FilePath $executable `
            -Arguments @('--version')
        if ($output -notmatch '(?i)azcopy\s+version\s+(\d+\.\d+\.\d+)') {
            throw [System.FormatException]::new(
                'AzCopy did not return recognizable version output.'
            )
        }
        $version = [version]$Matches[1]
        $ready = $version -ge $MinimumVersion -and $version.Major -eq 10
        return New-PrerequisiteState `
            -Name 'AzCopy v10' `
            -PackageId $packageId `
            -Ready $ready `
            -Version $version `
            -Path $executable `
            -Detail $(if ($ready) {
                'Ready'
            } else {
                "Version $version does not satisfy AzCopy v10 $MinimumVersion or later."
            })
    }
    catch {
        return New-PrerequisiteState `
            -Name 'AzCopy v10' `
            -PackageId $packageId `
            -Ready $false `
            -Version $null `
            -Path $null `
            -Detail $_.Exception.Message
    }
}

function Get-PrerequisiteStates {
    [CmdletBinding()]
    param()

    return @(
        Get-DesktopRuntimeState -ConfiguredPath $DotNetPath
        Get-AzureCliState `
            -ConfiguredPath $AzureCliPath `
            -MinimumVersion $MinimumAzureCliVersion
        Get-AzCopyState `
            -ConfiguredPath $AzCopyPath `
            -MinimumVersion $MinimumAzCopyVersion
    )
}

function Resolve-WinGet {
    [CmdletBinding()]
    param([Parameter()][AllowNull()][string]$ConfiguredPath)

    try {
        return Resolve-Executable `
            -ExplicitPath $ConfiguredPath `
            -CommandName 'winget'
    }
    catch {
        throw [System.InvalidOperationException]::new(
            'WinGet is required to install missing prerequisites. Install or repair ' +
            'Microsoft App Installer, then retry.',
            $_.Exception
        )
    }
}

function Assert-OfficialWinGetSource {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Executable)

    $output = Invoke-ExternalCommand `
        -FilePath $Executable `
        -Arguments @(
            'source',
            'export',
            '--name',
            $script:WingetSourceName,
            '--disable-interactivity'
        )
    $source = $output | ConvertFrom-Json -ErrorAction Stop
    $uri = $null
    if (
        $source.Name -ne $script:WingetSourceName -or
        -not [uri]::TryCreate(
            [string]$source.Arg,
            [System.UriKind]::Absolute,
            [ref]$uri
        ) -or
        $uri.Scheme -ne [System.Uri]::UriSchemeHttps -or
        $uri.Host -ne $script:WingetSourceHost
    ) {
        throw [System.InvalidOperationException]::new(
            "The registered '$($script:WingetSourceName)' source is not Microsoft's " +
            "expected HTTPS source at $($script:WingetSourceHost)."
        )
    }
}

function Install-WinGetPackage {
    [CmdletBinding(SupportsShouldProcess)]
    param(
        [Parameter(Mandatory)][string]$Executable,
        [Parameter(Mandatory)][string]$PackageId,
        [Parameter(Mandatory)][string]$DisplayName
    )

    if (-not $PSCmdlet.ShouldProcess($DisplayName, "Install or upgrade $PackageId")) {
        return
    }

    Write-Host "Installing or upgrading $DisplayName ($PackageId)..."
    $output = Invoke-ExternalCommand `
        -FilePath $Executable `
        -Arguments @(
            'install',
            '--id',
            $PackageId,
            '--exact',
            '--source',
            $script:WingetSourceName,
            '--silent',
            '--accept-package-agreements',
            '--accept-source-agreements',
            '--disable-interactivity'
        )
    if (-not [string]::IsNullOrWhiteSpace($output)) {
        Write-Host $output
    }
}

function Update-ProcessPath {
    [CmdletBinding()]
    param()

    $machinePath = [Environment]::GetEnvironmentVariable(
        'PATH',
        [EnvironmentVariableTarget]::Machine)
    $userPath = [Environment]::GetEnvironmentVariable(
        'PATH',
        [EnvironmentVariableTarget]::User)
    $pathParts = @($machinePath, $userPath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        ForEach-Object { $_.TrimEnd(';') }
    $env:PATH = $pathParts -join ';'
}

if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)) {
    throw [System.PlatformNotSupportedException]::new(
        'The SyncSAW management client requires Windows.'
    )
}
if (-not [Environment]::Is64BitOperatingSystem) {
    throw [System.PlatformNotSupportedException]::new(
        'The packaged SyncSAW management client requires 64-bit Windows.'
    )
}
if ([Environment]::OSVersion.Version.Major -lt 10) {
    throw [System.PlatformNotSupportedException]::new(
        'SyncSAW requires Windows 10 or later.'
    )
}

Write-Host "Windows $([Environment]::OSVersion.Version) (64-bit)"
$states = @(Get-PrerequisiteStates)
Write-Host 'Current prerequisite state:'
$states |
    Select-Object Name, Ready, Version, Path, Detail |
    Format-Table -AutoSize |
    Out-Host

$pending = @($states | Where-Object { -not $_.Ready })
if ($pending.Count -gt 0) {
    $winget = Resolve-WinGet -ConfiguredPath $WingetPath
    Assert-OfficialWinGetSource -Executable $winget
    Write-Host "Validated WinGet source: https://$script:WingetSourceHost/"

    foreach ($state in $pending) {
        Install-WinGetPackage `
            -Executable $winget `
            -PackageId $state.PackageId `
            -DisplayName $state.Name `
            -WhatIf:$WhatIfPreference
    }
}

if ($WhatIfPreference) {
    Write-Host 'WhatIf completed; post-install validation was skipped.'
    return
}

Update-ProcessPath
$finalStates = @(Get-PrerequisiteStates)
$failures = @($finalStates | Where-Object { -not $_.Ready })
if ($failures.Count -gt 0) {
    throw [System.InvalidOperationException]::new(
        'SyncSAW server dependency validation failed: ' +
        (($failures | ForEach-Object {
            "$($_.Name): $($_.Detail)"
        }) -join ' ')
    )
}

Write-Host ''
Write-Host 'SyncSAW management server dependency readiness: PASS' -ForegroundColor Green
$finalStates |
    Select-Object Name, Version, Path |
    Format-Table -AutoSize |
    Out-Host
Write-Host 'Start a new SyncSAW.App.exe process before configuring Microsoft Entra sign-in.'
