<#
.SYNOPSIS
Downloads and runs versioned test-cluster packages without an Entra identity.

.DESCRIPTION
Sync.ps1 runs under 64-bit Windows PowerShell 5.1 as a local administrator. It
checks a preconfigured HTTPS SAS URL for cluster_package.zip every 10 seconds.
When Azure Blob Storage reports a newer package, the script downloads it,
extracts it into a versioned task execution directory, adopts the refreshed
read-only user delegation SAS from cluster_package.config, and runs run.ps1
synchronously when that file exists.

The package check is suspended for the full lifetime of run.ps1. The script
does not require Az modules, Azure CLI, AzCopy, storage keys, or an Entra login.

.EXAMPLE
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File .\Sync.ps1

.EXAMPLE
.\Sync.ps1 -ConfigPath C:\ProgramData\SyncSAW\Sync.config.json -Once
#>

#requires -Version 5.1

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'Sync.config.json'),

    [Parameter()]
    [string]$PackageUri,

    [Parameter()]
    [string]$TaskExecutionRoot,

    [Parameter()]
    [ValidateRange(1, 86400)]
    [int]$IntervalSeconds = 10,

    [Parameter()]
    [switch]$Once
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RunnerBoundParameters = @{} + $PSBoundParameters
$script:PackageBlobName = 'cluster_package.zip'
$script:PackageConfigName = 'cluster_package.config'
$script:StateFileName = '.syncsaw-package-state.json'
$script:RuntimeConfigFileName = '.syncsaw-runtime.config'
$script:LogFileName = 'syncsaw-package-runner.log'
$script:MaximumPackageBytes = 2GB
$script:MaximumExtractedBytes = 4GB
$script:MaximumArchiveEntries = 10000

function ConvertTo-SyncRunnerHashtable {
    [CmdletBinding()]
    param([AllowNull()][object]$InputObject)

    if ($null -eq $InputObject) {
        return $null
    }
    if ($InputObject -is [System.Collections.IDictionary]) {
        $result = @{}
        foreach ($key in $InputObject.Keys) {
            $result[[string]$key] = ConvertTo-SyncRunnerHashtable $InputObject[$key]
        }
        return $result
    }
    if ($InputObject -is [pscustomobject]) {
        $result = @{}
        foreach ($property in $InputObject.PSObject.Properties) {
            $result[$property.Name] = ConvertTo-SyncRunnerHashtable $property.Value
        }
        return $result
    }
    if ($InputObject -is [System.Collections.IEnumerable] -and
        $InputObject -isnot [string]) {
        return @($InputObject | ForEach-Object {
            ConvertTo-SyncRunnerHashtable $_
        })
    }
    return $InputObject
}

function Protect-SyncRunnerLogText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Text)

    $redacted = [regex]::Replace(
        $Text,
        '(?i)([?&]sig=)[^&\s"''\r\n]+',
        '$1<redacted>'
    )
    return [regex]::Replace(
        $redacted,
        '(?i)(https://[^\s?"'']+)\?[^\s"'']+',
        '$1?<redacted>'
    )
}

function Write-SyncRunnerLog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $Root -Force)
    }
    $entry = '{0} {1}' -f [DateTimeOffset]::Now.ToString('O'),
        (Protect-SyncRunnerLogText -Text $Message)
    Add-Content -LiteralPath (Join-Path $Root $script:LogFileName) `
        -Value $entry -Encoding UTF8
}

function Read-SyncRunnerJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    try {
        $parsed = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop |
            ConvertFrom-Json -ErrorAction Stop
        return ConvertTo-SyncRunnerHashtable $parsed
    }
    catch {
        throw [System.IO.InvalidDataException]::new(
            "Configuration '$Path' is not valid JSON.",
            $_.Exception
        )
    }
}

function Get-SasQueryValues {
    [CmdletBinding()]
    param([Parameter(Mandatory)][uri]$Uri)

    $values = @{}
    foreach ($part in $Uri.Query.TrimStart('?').Split('&')) {
        if ([string]::IsNullOrWhiteSpace($part)) {
            continue
        }
        $pair = $part.Split(@('='), 2)
        if ($pair.Count -eq 2) {
            $name = [uri]::UnescapeDataString($pair[0])
            $value = [uri]::UnescapeDataString($pair[1])
            $values[$name] = $value
        }
    }
    return $values
}

function Assert-ClusterPackageUri {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Value)

    $candidate = $Value.Trim()
    $uri = $null
    if (-not [uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or
        -not $uri.Host.EndsWith(
            '.blob.core.windows.net',
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        $uri.Host.Split('.')[0] -notmatch '^[a-z0-9]{3,24}$' -or
        $uri.AbsolutePath.TrimEnd('/').Split('/')[-1] -cne $script:PackageBlobName -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not $uri.IsDefaultPort) {
        throw [System.ArgumentException]::new(
            "PackageUri must be an HTTPS Azure Blob SAS URL ending in '$($script:PackageBlobName)'."
        )
    }

    $query = Get-SasQueryValues -Uri $uri
    if (-not $query.ContainsKey('sig') -or
        -not $query.ContainsKey('se') -or
        -not $query.ContainsKey('sp') -or
        ([string]$query.sp).IndexOf('r', [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw [System.ArgumentException]::new(
            'PackageUri must contain an unexpired SAS with read permission.'
        )
    }

    $expires = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$query.se,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$expires
        ) -or $expires -le [DateTimeOffset]::UtcNow) {
        throw [System.ArgumentException]::new(
            'PackageUri contains an expired or invalid SAS expiry.'
        )
    }
    return $uri.AbsoluteUri
}

function Resolve-SyncRunnerConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][hashtable]$Overrides
    )

    $allowed = @('PackageUri', 'TaskExecutionRoot', 'IntervalSeconds')
    $configuration = @{}
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $configuration = Read-SyncRunnerJson -Path $Path
        foreach ($key in @($configuration.Keys)) {
            if ($key -notin $allowed) {
                throw [System.ArgumentException]::new(
                    "Unknown property '$key' in '$Path'."
                )
            }
        }
    }

    foreach ($key in $allowed) {
        if ($Overrides.ContainsKey($key)) {
            $configuration[$key] = $Overrides[$key]
        }
    }
    if (-not $configuration.ContainsKey('PackageUri') -or
        [string]::IsNullOrWhiteSpace([string]$configuration.PackageUri)) {
        throw [System.ArgumentException]::new(
            "Set PackageUri in '$Path' or pass -PackageUri."
        )
    }

    $configuration.PackageUri = Assert-ClusterPackageUri `
        -Value ([string]$configuration.PackageUri)
    if (-not $configuration.ContainsKey('TaskExecutionRoot') -or
        [string]::IsNullOrWhiteSpace([string]$configuration.TaskExecutionRoot)) {
        $configuration.TaskExecutionRoot = Join-Path $PSScriptRoot 'Tasks'
    }
    $configuration.TaskExecutionRoot = [IO.Path]::GetFullPath(
        [Environment]::ExpandEnvironmentVariables(
            [string]$configuration.TaskExecutionRoot
        )
    )
    if (-not $configuration.ContainsKey('IntervalSeconds')) {
        $configuration.IntervalSeconds = 10
    }
    $parsedInterval = 0
    if (-not [int]::TryParse(
            [string]$configuration.IntervalSeconds,
            [ref]$parsedInterval
        ) -or $parsedInterval -lt 1 -or $parsedInterval -gt 86400) {
        throw [System.ArgumentOutOfRangeException]::new(
            'IntervalSeconds',
            'IntervalSeconds must be between 1 and 86400.'
        )
    }
    $configuration.IntervalSeconds = $parsedInterval
    return $configuration
}

function Assert-SyncRunnerAdministrator {
    [CmdletBinding()]
    param()

    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator
        )) {
        throw [System.Security.SecurityException]::new(
            'Sync.ps1 must run from an elevated 64-bit Windows PowerShell 5.1 session.'
        )
    }
    if (-not [Environment]::Is64BitProcess) {
        throw [System.PlatformNotSupportedException]::new(
            'Use 64-bit Windows PowerShell from System32, not SysWOW64.'
        )
    }
}

function Get-RemotePackageMetadata {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Uri)

    $request = [Net.HttpWebRequest]::Create($Uri)
    $request.Method = 'HEAD'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $response = $null
    try {
        $response = [Net.HttpWebResponse]$request.GetResponse()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) {
            throw [Net.WebException]::new(
                "Blob metadata request returned HTTP $([int]$response.StatusCode)."
            )
        }
        if ([long]$response.ContentLength -gt $script:MaximumPackageBytes) {
            throw [System.IO.InvalidDataException]::new(
                "Remote package exceeds the $($script:MaximumPackageBytes)-byte limit."
            )
        }
        return [pscustomobject]@{
            ETag = [string]$response.Headers['ETag']
            LastModifiedUtc = [DateTimeOffset]$response.LastModified.ToUniversalTime()
            ContentLength = [long]$response.ContentLength
        }
    }
    finally {
        if ($null -ne $response) {
            $response.Dispose()
        }
    }
}

function Test-RemotePackageUpdateRequired {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Remote,
        [AllowNull()][hashtable]$State
    )

    if ($null -eq $State -or -not $State.ContainsKey('LastModifiedUtc')) {
        return $true
    }
    if ($State.ContainsKey('ETag') -and
        -not [string]::IsNullOrWhiteSpace([string]$Remote.ETag) -and
        [string]$State.ETag -eq [string]$Remote.ETag) {
        return $false
    }

    $current = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$State.LastModifiedUtc,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$current
        )) {
        throw [System.IO.InvalidDataException]::new(
            'The package state has an invalid LastModifiedUtc value.'
        )
    }
    return ([DateTimeOffset]$Remote.LastModifiedUtc) -gt $current
}

function Save-SyncRunnerJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][object]$Value
    )

    $temporaryPath = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $Value | ConvertTo-Json -Depth 5 |
            Set-Content -LiteralPath $temporaryPath -Encoding UTF8
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            [IO.File]::Replace($temporaryPath, $Path, $null)
        }
        else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
    }
}

function Copy-SyncRunnerStream {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][IO.Stream]$Source,
        [Parameter(Mandatory)][IO.Stream]$Destination,
        [Parameter(Mandatory)][long]$MaximumBytes
    )

    $buffer = New-Object byte[] 1048576
    [long]$total = 0
    while (($read = $Source.Read($buffer, 0, $buffer.Length)) -gt 0) {
        $total += $read
        if ($total -gt $MaximumBytes) {
            throw [IO.InvalidDataException]::new(
                "Stream exceeds the $MaximumBytes-byte safety limit."
            )
        }
        $Destination.Write($buffer, 0, $read)
    }
    return $total
}

function Receive-ClusterPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Destination
    )

    $request = [Net.HttpWebRequest]::Create($Uri)
    $request.Method = 'GET'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $response = $null
    $output = $null
    try {
        $response = [Net.HttpWebResponse]$request.GetResponse()
        if ($response.StatusCode -ne [Net.HttpStatusCode]::OK) {
            throw [Net.WebException]::new(
                "Blob download returned HTTP $([int]$response.StatusCode)."
            )
        }
        if ([long]$response.ContentLength -gt $script:MaximumPackageBytes) {
            throw [System.IO.InvalidDataException]::new(
                "Remote package exceeds the $($script:MaximumPackageBytes)-byte limit."
            )
        }
        $output = [IO.File]::Open(
            $Destination,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None
        )
        $input = $response.GetResponseStream()
        try {
            [void](Copy-SyncRunnerStream `
                -Source $input `
                -Destination $output `
                -MaximumBytes $script:MaximumPackageBytes)
        }
        finally {
            $input.Dispose()
        }
    }
    finally {
        if ($null -ne $output) {
            $output.Dispose()
        }
        if ($null -ne $response) {
            $response.Dispose()
        }
    }
}

function Expand-ClusterPackageSafely {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression
    [void](New-Item -ItemType Directory -Path $Destination -Force)
    $destinationRoot = [IO.Path]::GetFullPath($Destination)
    $destinationPrefix = $destinationRoot.TrimEnd('\') + '\'
    $stream = [IO.File]::OpenRead($ArchivePath)
    $archive = $null
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Read,
            $false
        )
        if ($archive.Entries.Count -gt $script:MaximumArchiveEntries) {
            throw [IO.InvalidDataException]::new(
                "Package contains more than $($script:MaximumArchiveEntries) entries."
            )
        }

        [long]$totalLength = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $segments = @($name.Split('/') | Where-Object { $_ -ne '' })
            if ([string]::IsNullOrWhiteSpace($name) -or
                $name.StartsWith('/') -or
                $name.Contains(':') -or
                $segments -contains '.' -or
                $segments -contains '..') {
                throw [IO.InvalidDataException]::new(
                    "Package contains an unsafe entry: '$name'."
                )
            }
            if ($entry.FullName.EndsWith('/') -or
                $entry.FullName.EndsWith('\')) {
                continue
            }

            $totalLength += [long]$entry.Length
            if ($totalLength -gt $script:MaximumExtractedBytes) {
                throw [IO.InvalidDataException]::new(
                    "Expanded package exceeds the $($script:MaximumExtractedBytes)-byte limit."
                )
            }
            $target = [IO.Path]::GetFullPath(
                [IO.Path]::Combine(
                    $destinationRoot,
                    $name.Replace('/', [IO.Path]::DirectorySeparatorChar)
                )
            )
            if (-not $target.StartsWith(
                    $destinationPrefix,
                    [StringComparison]::OrdinalIgnoreCase
                )) {
                throw [IO.InvalidDataException]::new(
                    "Package entry escapes the task directory: '$name'."
                )
            }
            [void](New-Item -ItemType Directory `
                -Path ([IO.Path]::GetDirectoryName($target)) -Force)
            $entryStream = $entry.Open()
            $targetStream = $null
            try {
                $targetStream = [IO.File]::Open(
                    $target,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None
                )
                [void](Copy-SyncRunnerStream `
                    -Source $entryStream `
                    -Destination $targetStream `
                    -MaximumBytes ([long]$entry.Length))
            }
            finally {
                if ($null -ne $targetStream) {
                    $targetStream.Dispose()
                }
                $entryStream.Dispose()
            }
            [IO.File]::SetLastWriteTimeUtc($target, $entry.LastWriteTime.UtcDateTime)
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        $stream.Dispose()
    }
}

function Get-RefreshedPackageUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PackageDirectory,
        [Parameter(Mandatory)][string]$CurrentPackageUri
    )

    $path = Join-Path $PackageDirectory $script:PackageConfigName
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw [IO.InvalidDataException]::new(
            "Package does not contain required '$($script:PackageConfigName)'."
        )
    }
    $configuration = Read-SyncRunnerJson -Path $path
    if (-not $configuration.ContainsKey('SchemaVersion') -or
        [int]$configuration.SchemaVersion -ne 1 -or
        -not $configuration.ContainsKey('PackageUri')) {
        throw [IO.InvalidDataException]::new(
            "Package '$($script:PackageConfigName)' has an unsupported schema."
        )
    }

    $refreshed = Assert-ClusterPackageUri -Value ([string]$configuration.PackageUri)
    $current = [uri]$CurrentPackageUri
    $next = [uri]$refreshed
    if (-not $current.Host.Equals($next.Host, [StringComparison]::OrdinalIgnoreCase) -or
        $current.AbsolutePath -cne $next.AbsolutePath) {
        throw [IO.InvalidDataException]::new(
            'Package attempted to change the configured Blob endpoint.'
        )
    }
    return $refreshed
}

function Install-ClusterPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$PackageUri,
        [Parameter(Mandatory)][string]$ExecutionRoot,
        [Parameter(Mandatory)][object]$RemoteMetadata,
        [Parameter(Mandatory)][int]$PollingInterval
    )

    $downloadPath = Join-Path $ExecutionRoot (
        '.download-{0}.zip' -f [guid]::NewGuid().ToString('N')
    )
    $stagingPath = Join-Path $ExecutionRoot (
        '.staging-{0}' -f [guid]::NewGuid().ToString('N')
    )
    $packagesRoot = Join-Path $ExecutionRoot 'packages'
    [void](New-Item -ItemType Directory -Path $packagesRoot -Force)
    try {
        Receive-ClusterPackage -Uri $PackageUri -Destination $downloadPath
        Expand-ClusterPackageSafely `
            -ArchivePath $downloadPath `
            -Destination $stagingPath
        $refreshedUri = Get-RefreshedPackageUri `
            -PackageDirectory $stagingPath `
            -CurrentPackageUri $PackageUri
        $versionName = '{0}-{1}' -f `
            ([DateTimeOffset]$RemoteMetadata.LastModifiedUtc).ToString('yyyyMMddHHmmss'),
            [guid]::NewGuid().ToString('N').Substring(0, 8)
        $packageDirectory = Join-Path $packagesRoot $versionName
        [IO.Directory]::Move($stagingPath, $packageDirectory)

        Save-SyncRunnerJson `
            -Path (Join-Path $ExecutionRoot $script:RuntimeConfigFileName) `
            -Value @{
                SchemaVersion = 1
                PackageUri = $refreshedUri
                IntervalSeconds = $PollingInterval
            }
        Save-SyncRunnerJson `
            -Path (Join-Path $ExecutionRoot $script:StateFileName) `
            -Value @{
                SchemaVersion = 1
                ETag = [string]$RemoteMetadata.ETag
                LastModifiedUtc = (
                    [DateTimeOffset]$RemoteMetadata.LastModifiedUtc
                ).ToUniversalTime().ToString('O')
                PackageDirectory = $packageDirectory
            }
        return [pscustomobject]@{
            PackageDirectory = $packageDirectory
            PackageUri = $refreshedUri
        }
    }
    finally {
        Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-ClusterPackageRun {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackageDirectory)

    $runScript = Join-Path $PackageDirectory 'run.ps1'
    if (-not (Test-Path -LiteralPath $runScript -PathType Leaf)) {
        return $null
    }
    $argumentList = '-NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "{0}"' -f `
        $runScript.Replace('"', '""')
    return Start-Process `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList $argumentList `
        -WorkingDirectory $PackageDirectory `
        -Wait `
        -PassThru
}

function Remove-OldClusterPackages {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ExecutionRoot,
        [Parameter(Mandatory)][string]$CurrentPackageDirectory
    )

    $packagesRoot = Join-Path $ExecutionRoot 'packages'
    if (-not (Test-Path -LiteralPath $packagesRoot -PathType Container)) {
        return
    }
    Get-ChildItem -LiteralPath $packagesRoot -Directory |
        Where-Object { $_.FullName -ne $CurrentPackageDirectory } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -Skip 2 |
        Remove-Item -Recurse -Force -ErrorAction Stop
}

function Invoke-SyncRunner {
    [CmdletBinding()]
    param()

    Assert-SyncRunnerAdministrator
    [Net.ServicePointManager]::SecurityProtocol = `
        [Net.ServicePointManager]::SecurityProtocol -bor `
        [Net.SecurityProtocolType]::Tls12

    $overrides = @{}
    foreach ($name in @('PackageUri', 'TaskExecutionRoot', 'IntervalSeconds')) {
        if ($PSBoundParameters.ContainsKey($name)) {
            $overrides[$name] = $PSBoundParameters[$name]
        }
        elseif ($script:RunnerBoundParameters.ContainsKey($name)) {
            $overrides[$name] = $script:RunnerBoundParameters[$name]
        }
    }
    $configuration = Resolve-SyncRunnerConfiguration `
        -Path ([IO.Path]::GetFullPath($ConfigPath)) `
        -Overrides $overrides
    $root = [string]$configuration.TaskExecutionRoot
    [void](New-Item -ItemType Directory -Path $root -Force)

    $runtimeConfigPath = Join-Path $root $script:RuntimeConfigFileName
    if (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) {
        $runtime = Read-SyncRunnerJson -Path $runtimeConfigPath
        if ($runtime.ContainsKey('PackageUri')) {
            $configuration.PackageUri = Assert-ClusterPackageUri `
                -Value ([string]$runtime.PackageUri)
        }
    }

    $mutexNameBytes = [Text.Encoding]::UTF8.GetBytes(
        [IO.Path]::GetFullPath($ConfigPath).ToLowerInvariant()
    )
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $mutexHash = [BitConverter]::ToString(
            $sha.ComputeHash($mutexNameBytes)
        ).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
    $mutex = [Threading.Mutex]::new(
        $false,
        "Global\SyncSAW.ClusterPackage.$mutexHash"
    )
    $hasMutex = $false
    try {
        try {
            $hasMutex = $mutex.WaitOne(0, $false)
        }
        catch [Threading.AbandonedMutexException] {
            $hasMutex = $true
        }
        if (-not $hasMutex) {
            throw [InvalidOperationException]::new(
                'Another Sync.ps1 package runner is already active for this configuration.'
            )
        }

        Write-SyncRunnerLog -Root $root -Message 'Package runner started.'
        while ($true) {
            try {
                $statePath = Join-Path $root $script:StateFileName
                $state = if (Test-Path -LiteralPath $statePath -PathType Leaf) {
                    Read-SyncRunnerJson -Path $statePath
                }
                else {
                    $null
                }
                $remote = Get-RemotePackageMetadata `
                    -Uri ([string]$configuration.PackageUri)
                if (Test-RemotePackageUpdateRequired -Remote $remote -State $state) {
                    Write-SyncRunnerLog -Root $root `
                        -Message "New package detected at $($remote.LastModifiedUtc.ToString('O'))."
                    $installed = Install-ClusterPackage `
                        -PackageUri ([string]$configuration.PackageUri) `
                        -ExecutionRoot $root `
                        -RemoteMetadata $remote `
                        -PollingInterval ([int]$configuration.IntervalSeconds)
                    $configuration.PackageUri = $installed.PackageUri
                    Write-SyncRunnerLog -Root $root `
                        -Message "Installed package '$($installed.PackageDirectory)'."
                    $process = Invoke-ClusterPackageRun `
                        -PackageDirectory $installed.PackageDirectory
                    if ($null -ne $process) {
                        Write-SyncRunnerLog -Root $root `
                            -Message "run.ps1 exited with code $($process.ExitCode)."
                    }
                    else {
                        Write-SyncRunnerLog -Root $root `
                            -Message 'Package has no run.ps1; execution was skipped.'
                    }
                    Remove-OldClusterPackages `
                        -ExecutionRoot $root `
                        -CurrentPackageDirectory $installed.PackageDirectory
                }
            }
            catch {
                Write-SyncRunnerLog -Root $root `
                    -Message "Package cycle failed: $($_.Exception.Message)"
                if ($Once) {
                    throw
                }
            }

            if ($Once) {
                break
            }
            Start-Sleep -Seconds ([int]$configuration.IntervalSeconds)
        }
    }
    finally {
        if ($hasMutex) {
            $mutex.ReleaseMutex()
        }
        $mutex.Dispose()
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-SyncRunner
}
