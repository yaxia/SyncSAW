<#
.SYNOPSIS
Downloads and runs versioned test-cluster packages without an Entra identity.

.DESCRIPTION
Sync.ps1 runs under 64-bit Windows PowerShell 5.1 as a local administrator. It
checks a preconfigured HTTPS SAS URL for cluster_package.zip every 10 seconds.
When Azure Blob Storage reports a newer package, the script downloads it,
extracts it into a versioned task execution directory, adopts the refreshed
read-only user delegation SAS from cluster_package.config, and runs bootstrap.ps1
synchronously when that file exists.

The package check is suspended for the full lifetime of bootstrap.ps1. The script
does not require Az modules, Azure CLI, AzCopy, storage keys, or an Entra login.

.EXAMPLE
powershell.exe -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File .\Sync.ps1

.EXAMPLE
.\Sync.ps1 -ConfigPath .\Sync.config.json -Once
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
$script:RunnerScriptRoot = $PSScriptRoot
$script:PackageBlobName = 'cluster_package.zip'
$script:PackageConfigName = 'cluster_package.config'
$script:PackageBootstrapName = 'bootstrap.ps1'
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
        $separator = $part.IndexOf('=')
        if ($separator -gt 0) {
            $name = [uri]::UnescapeDataString($part.Substring(0, $separator))
            $value = [uri]::UnescapeDataString($part.Substring($separator + 1))
            $values[$name] = $value
        }
    }
    return $values
}

function Assert-ClusterPackageEndpoint {
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
    return $uri.AbsoluteUri
}

function Assert-UserDelegationSasQuery {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Query,
        [Parameter(Mandatory)][string]$Resource,
        [Parameter(Mandatory)][string]$Permissions
    )

    $requiredDelegationFields = @(
        'sig', 'se', 'sp', 'spr', 'sr', 'skoid', 'sktid',
        'skt', 'ske', 'sks', 'skv'
    )
    if (@($requiredDelegationFields | Where-Object {
                -not $Query.ContainsKey($_) -or
                [string]::IsNullOrWhiteSpace([string]$Query[$_])
            }).Count -gt 0 -or
        -not ([string]$Query.sp).Equals(
            $Permissions,
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        -not ([string]$Query.spr).Equals(
            'https',
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        -not ([string]$Query.sr).Equals(
            $Resource,
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        -not ([string]$Query.sks).Equals(
            'b',
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw [System.ArgumentException]::new(
            "SAS must use exact permissions '$Permissions', HTTPS, resource '$Resource', and user delegation."
        )
    }

    $expires = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse(
            [string]$Query.se,
            [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::AssumeUniversal -bor
                [Globalization.DateTimeStyles]::AdjustToUniversal,
            [ref]$expires
        ) -or $expires -le [DateTimeOffset]::UtcNow) {
        throw [System.ArgumentException]::new(
            'PackageUri contains an expired or invalid SAS expiry.'
        )
    }
}

function Assert-ClusterPackageUri {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Value)

    $absoluteUri = Assert-ClusterPackageEndpoint -Value $Value
    $uri = [uri]$absoluteUri
    Assert-UserDelegationSasQuery `
        -Query (Get-SasQueryValues -Uri $uri) `
        -Resource 'b' `
        -Permissions 'r'
    return $uri.AbsoluteUri
}

function Assert-ClusterResultsUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$PackageUri
    )

    $candidate = $Value.Trim()
    $uri = $null
    if (-not [uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or
        -not $uri.Host.EndsWith(
            '.blob.core.windows.net',
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        -not [string]::IsNullOrEmpty($uri.Fragment) -or
        -not [string]::IsNullOrEmpty($uri.UserInfo) -or
        -not $uri.IsDefaultPort) {
        throw [System.ArgumentException]::new(
            'ResultsBlobUri must be an HTTPS Azure Blob SAS URL.'
        )
    }

    $package = [uri]$PackageUri
    $segments = @($uri.AbsolutePath.Trim('/').Split('/'))
    if (-not $uri.Host.Equals(
            $package.Host,
            [StringComparison]::OrdinalIgnoreCase
        ) -or
        $segments.Count -lt 3 -or
        $segments[1] -cne 'cluster-results' -or
        -not $segments[-1].EndsWith('.zip', [StringComparison]::OrdinalIgnoreCase)) {
        throw [System.ArgumentException]::new(
            'ResultsBlobUri must target a cluster-results/*.zip Blob in the package storage account.'
        )
    }

    Assert-UserDelegationSasQuery `
        -Query (Get-SasQueryValues -Uri $uri) `
        -Resource 'b' `
        -Permissions 'c'
    return $uri.AbsoluteUri
}

function Test-SameClusterPackageEndpoint {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$First,
        [Parameter(Mandatory)][string]$Second
    )

    $firstUri = [uri]$First
    $secondUri = [uri]$Second
    return $firstUri.Host.Equals(
        $secondUri.Host,
        [StringComparison]::OrdinalIgnoreCase
    ) -and $firstUri.AbsolutePath -ceq $secondUri.AbsolutePath
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

    $configuration.PackageUri = Assert-ClusterPackageEndpoint `
        -Value ([string]$configuration.PackageUri)
    if (-not $configuration.ContainsKey('TaskExecutionRoot') -or
        [string]::IsNullOrWhiteSpace([string]$configuration.TaskExecutionRoot)) {
        $configuration.TaskExecutionRoot = $script:RunnerScriptRoot
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

function Assert-NoReparsePointInPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($fullPath)
    $current = $root
    foreach ($segment in $fullPath.Substring($root.Length).Split('\')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) {
            break
        }
        $item = Get-Item -LiteralPath $current -Force
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw [System.Security.SecurityException]::new(
                "Runner directories cannot contain a reparse point: '$current'."
            )
        }
    }
}

function Initialize-SecureDirectory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    if ($fullPath.StartsWith('\\')) {
        throw [System.Security.SecurityException]::new(
            'Runner directories must be on a local fixed drive.'
        )
    }
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($fullPath))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) {
        throw [System.Security.SecurityException]::new(
            'Runner directories must be on a local fixed drive.'
        )
    }

    Assert-NoReparsePointInPath -Path $fullPath
    [void][IO.Directory]::CreateDirectory($fullPath)
    Assert-NoReparsePointInPath -Path $fullPath
    $directory = Get-Item -LiteralPath $fullPath -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw [System.Security.SecurityException]::new(
            "Secure runner directory cannot be a reparse point: '$fullPath'."
        )
    }

    $administrators = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::BuiltinAdministratorsSid,
        $null
    )
    $system = [Security.Principal.SecurityIdentifier]::new(
        [Security.Principal.WellKnownSidType]::LocalSystemSid,
        $null
    )
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true, $false)
    $security.SetOwner($administrators)
    [void]$security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $administrators,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            $propagation,
            $allow
        )
    )
    [void]$security.AddAccessRule(
        [Security.AccessControl.FileSystemAccessRule]::new(
            $system,
            [Security.AccessControl.FileSystemRights]::FullControl,
            $inheritance,
            $propagation,
            $allow
        )
    )
    Set-Acl -LiteralPath $fullPath -AclObject $security
    Assert-NoReparsePointInPath -Path $fullPath
    return $fullPath
}

function Initialize-SecureExecutionRoot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $trustedParent = [IO.Path]::GetFullPath($script:RunnerScriptRoot)
    $requested = [IO.Path]::GetFullPath($Path)
    $trustedPrefix = $trustedParent.TrimEnd('\') + '\'
    if (-not $requested.Equals(
            $trustedParent,
            [StringComparison]::OrdinalIgnoreCase
        ) -and
        -not $requested.StartsWith(
            $trustedPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )) {
        throw [System.Security.SecurityException]::new(
            "TaskExecutionRoot must be '$trustedParent' or one of its subdirectories."
        )
    }

    $current = Initialize-SecureDirectory -Path $trustedParent
    $relative = $requested.Substring($trustedParent.Length).TrimStart(
        [char[]]@('\')
    )
    foreach ($segment in $relative.Split('\')) {
        if ([string]::IsNullOrWhiteSpace($segment)) {
            continue
        }
        $current = Initialize-SecureDirectory -Path (Join-Path $current $segment)
    }
    return $current
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
        if ([string]::IsNullOrWhiteSpace([string]$response.Headers['ETag'])) {
            throw [System.IO.InvalidDataException]::new(
                'Remote package metadata did not include an ETag.'
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
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$ExpectedETag
    )

    $request = [Net.HttpWebRequest]::Create($Uri)
    $request.Method = 'GET'
    $request.AllowAutoRedirect = $false
    $request.Timeout = 30000
    $request.ReadWriteTimeout = 30000
    $request.Headers['If-Match'] = $ExpectedETag
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
        $responseETag = [string]$response.Headers['ETag']
        if ([string]::IsNullOrWhiteSpace($responseETag) -or
            $responseETag -cne $ExpectedETag) {
            throw [System.IO.InvalidDataException]::new(
                'The downloaded package ETag does not match the metadata check.'
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
        return [pscustomobject]@{
            ETag = $responseETag
            LastModifiedUtc = [DateTimeOffset]$response.LastModified.ToUniversalTime()
            ContentLength = [long]$response.ContentLength
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
        [int]$configuration.SchemaVersion -ne 3 -or
        -not $configuration.ContainsKey('PackageUri') -or
        -not $configuration.ContainsKey('ResultsBlobUri')) {
        throw [IO.InvalidDataException]::new(
            "Package '$($script:PackageConfigName)' has an unsupported schema."
        )
    }

    $refreshed = Assert-ClusterPackageUri -Value ([string]$configuration.PackageUri)
    if (-not (Test-SameClusterPackageEndpoint `
            -First $CurrentPackageUri `
            -Second $refreshed)) {
        throw [IO.InvalidDataException]::new(
            'Package attempted to change the configured Blob endpoint.'
        )
    }
    [void](Assert-ClusterResultsUri `
        -Value ([string]$configuration.ResultsBlobUri) `
        -PackageUri $refreshed)
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
    $packagesRoot = Initialize-SecureDirectory `
        -Path (Join-Path $ExecutionRoot 'packages')
    try {
        $downloadedMetadata = Receive-ClusterPackage `
            -Uri $PackageUri `
            -Destination $downloadPath `
            -ExpectedETag ([string]$RemoteMetadata.ETag)
        Expand-ClusterPackageSafely `
            -ArchivePath $downloadPath `
            -Destination $stagingPath
        $refreshedUri = Get-RefreshedPackageUri `
            -PackageDirectory $stagingPath `
            -CurrentPackageUri $PackageUri
        $stagedBootstrapScript = Join-Path $stagingPath $script:PackageBootstrapName
        if (Test-Path -LiteralPath $stagedBootstrapScript -PathType Leaf) {
            [void](Assert-BootstrapScriptSafety -Path $stagedBootstrapScript)
        }
        $versionName = '{0}-{1}' -f `
            ([DateTimeOffset]$downloadedMetadata.LastModifiedUtc).ToString('yyyyMMddHHmmss'),
            [guid]::NewGuid().ToString('N').Substring(0, 8)
        $packageDirectory = Join-Path $packagesRoot $versionName
        [IO.Directory]::Move($stagingPath, $packageDirectory)
        [void](Initialize-SecureDirectory -Path $packageDirectory)

        Save-SyncRunnerJson `
            -Path (Join-Path $ExecutionRoot $script:RuntimeConfigFileName) `
            -Value @{
                SchemaVersion = 1
                PackageUri = $refreshedUri
                IntervalSeconds = $PollingInterval
            }
        return [pscustomobject]@{
            PackageDirectory = $packageDirectory
            PackageUri = $refreshedUri
            Metadata = $downloadedMetadata
        }
    }
    finally {
        Remove-Item -LiteralPath $downloadPath -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $stagingPath -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-ClusterPackageBootstrap {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$PackageDirectory)

    $bootstrapScript = Join-Path $PackageDirectory $script:PackageBootstrapName
    if (-not (Test-Path -LiteralPath $bootstrapScript -PathType Leaf)) {
        return $null
    }
    [void](Assert-BootstrapScriptSafety -Path $bootstrapScript)
    $argumentList = '-NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "{0}"' -f `
        $bootstrapScript.Replace('"', '""')
    return Start-Process `
        -FilePath (Join-Path $PSHOME 'powershell.exe') `
        -ArgumentList $argumentList `
        -WorkingDirectory $PackageDirectory `
        -Wait `
        -PassThru
}

function Assert-BootstrapScriptSafety {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [string]$Path
    )

    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw [IO.FileNotFoundException]::new(
            "The bootstrap script was not found: '$fullPath'.",
            $fullPath
        )
    }

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile(
        $fullPath,
        [ref]$tokens,
        [ref]$parseErrors
    )
    if ($parseErrors.Count -gt 0) {
        $messages = @($parseErrors | ForEach-Object { $_.Message }) -join '; '
        throw [IO.InvalidDataException]::new(
            "bootstrap.ps1 contains PowerShell parse errors: $messages"
        )
    }

    $blockedCommands = @(
        'Remove-Item',
        'Remove-ItemProperty',
        'Clear-Content',
        'Set-Content',
        'Add-Content',
        'Set-Item',
        'Set-ItemProperty',
        'Set-Acl',
        'Out-File',
        'Copy-Item',
        'Move-Item',
        'Rename-Item',
        'Compress-Archive',
        'Restart-Computer',
        'Stop-Computer',
        'Restart-Service',
        'Stop-Service',
        'Set-Service',
        'New-Service',
        'Remove-Service',
        'Restart-AzVM',
        'Stop-AzVM',
        'Remove-AzStorageBlob',
        'Remove-AzStorageContainer',
        'Stop-Process',
        'Invoke-Expression',
        'Invoke-Command',
        'New-PSSession',
        'Enter-PSSession',
        'Start-Job',
        'Start-Process',
        'az',
        'az.cmd',
        'az.exe',
        'azcopy',
        'azcopy.exe',
        'cmd',
        'cmd.exe',
        'powershell',
        'powershell.exe',
        'pwsh',
        'pwsh.exe',
        'shutdown',
        'shutdown.exe',
        'reboot',
        'reboot.exe',
        'taskkill',
        'taskkill.exe',
        'wmic',
        'wmic.exe',
        'sc',
        'sc.exe',
        'net',
        'net.exe',
        'schtasks',
        'schtasks.exe',
        'robocopy',
        'robocopy.exe',
        'xcopy',
        'xcopy.exe',
        'diskpart',
        'diskpart.exe',
        'fsutil',
        'fsutil.exe',
        'rm',
        'ri',
        'del',
        'erase',
        'rd',
        'rmdir',
        'clc',
        'sc',
        'ac',
        'si',
        'sp',
        'cp',
        'copy',
        'cpi',
        'mv',
        'move',
        'mi',
        'ren',
        'rni',
        'kill',
        'spps',
        'spsv'
    )
    $blockedMembers = @(
        'Delete',
        'DeleteFile',
        'DeleteDirectory',
        'Move',
        'MoveFile',
        'MoveDirectory',
        'Replace',
        'Copy',
        'WriteAllText',
        'WriteAllBytes',
        'WriteAllLines',
        'AppendAllText',
        'AppendAllLines',
        'OpenWrite',
        'AppendText',
        'CreateText',
        'SetAccessControl',
        'Kill'
    )
    $violations = New-Object 'System.Collections.Generic.List[string]'

    $commands = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.CommandAst]
    }, $true))
    foreach ($command in $commands) {
        $commandName = $command.GetCommandName()
        if ([string]::IsNullOrWhiteSpace($commandName)) {
            $violations.Add(
                "Dynamic command invocation is not allowed at line $($command.Extent.StartLineNumber)."
            )
            continue
        }

        $leafName = [IO.Path]::GetFileName($commandName)
        if ($blockedCommands -contains $leafName) {
            $violations.Add(
                "Command '$commandName' is not allowed at line $($command.Extent.StartLineNumber)."
            )
        }
        if ($leafName -ieq 'New-Item' -or $leafName -ieq 'ni') {
            $force = @($command.CommandElements | Where-Object {
                $_ -is [System.Management.Automation.Language.CommandParameterAst] -and
                $_.ParameterName -ieq 'Force'
            })
            if ($force.Count -gt 0) {
                $violations.Add(
                    "New-Item -Force can modify existing paths and is not allowed at line " +
                    "$($command.Extent.StartLineNumber)."
                )
            }
        }
        if ($command.Extent.Text -match
            '(?i)(?:-Method\s+|--request\s+|-X\s+)[''"]?DELETE(?:[''"]|\s|$)') {
            $violations.Add(
                "HTTP DELETE is not allowed at line $($command.Extent.StartLineNumber)."
            )
        }
    }

    $members = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.InvokeMemberExpressionAst]
    }, $true))
    foreach ($member in $members) {
        if ($member.Extent.Text -match
            '(?i)\[(?:System\.)?IO\.FileMode\]::(?:Create|OpenOrCreate|Truncate|Append)\b') {
            $violations.Add(
                "File open mode can change an existing file at line " +
                "$($member.Extent.StartLineNumber)."
            )
        }
        $memberName = if (
            $member.Member -is
                [System.Management.Automation.Language.StringConstantExpressionAst]
        ) {
            [string]$member.Member.Value
        }
        else {
            $null
        }
        if ([string]::IsNullOrWhiteSpace($memberName)) {
            $violations.Add(
                "Dynamic method invocation is not allowed at line $($member.Extent.StartLineNumber)."
            )
        }
        elseif ($blockedMembers -contains $memberName) {
            $violations.Add(
                "Method '$memberName' is not allowed at line $($member.Extent.StartLineNumber)."
            )
        }
        elseif (
            $memberName -ieq 'Create' -and
            $member.Expression.Extent.Text -match
                '(?i)^\[(?:System\.)?IO\.(?:File|FileInfo)\]$'
        ) {
            $violations.Add(
                "File creation without explicit create-new semantics is not allowed at line " +
                "$($member.Extent.StartLineNumber)."
            )
        }
    }

    $redirections = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.FileRedirectionAst]
    }, $true))
    foreach ($redirection in $redirections) {
        $violations.Add(
            "File redirection is not allowed at line $($redirection.Extent.StartLineNumber)."
        )
    }

    $deleteAssignments = @($ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Extent.Text -match '(?i)\.Method\s*=\s*[''"]DELETE[''"]'
    }, $true))
    foreach ($assignment in $deleteAssignments) {
        $violations.Add(
            "HTTP DELETE is not allowed at line $($assignment.Extent.StartLineNumber)."
        )
    }

    if ($violations.Count -gt 0) {
        throw [Security.SecurityException]::new(
            "bootstrap.ps1 failed the additive-only safety harness:`r`n- " +
            ($violations -join "`r`n- ")
        )
    }

    return $fullPath
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
    $root = Initialize-SecureExecutionRoot `
        -Path ([string]$configuration.TaskExecutionRoot)

    $runtimeConfigPath = Join-Path $root $script:RuntimeConfigFileName
    $statePath = Join-Path $root $script:StateFileName
    $bootstrapPackageUri = [string]$configuration.PackageUri
    $runtimeWasReset = $false
    $runtimeWasRejected = $false
    if (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) {
        $runtime = Read-SyncRunnerJson -Path $runtimeConfigPath
        if ($runtime.ContainsKey('PackageUri')) {
            $runtimeEndpoint = $null
            try {
                $runtimeEndpoint = Assert-ClusterPackageEndpoint `
                    -Value ([string]$runtime.PackageUri)
            }
            catch {
                Remove-Item -LiteralPath $runtimeConfigPath -Force
                $runtimeWasRejected = $true
            }
            if ($null -ne $runtimeEndpoint -and
                -not (Test-SameClusterPackageEndpoint `
                    -First $bootstrapPackageUri `
                    -Second $runtimeEndpoint)) {
                Remove-Item -LiteralPath $runtimeConfigPath -Force
                Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
                $runtimeWasReset = $true
            }
            elseif ($null -ne $runtimeEndpoint -and
                -not $script:RunnerBoundParameters.ContainsKey('PackageUri')) {
                try {
                    $configuration.PackageUri = Assert-ClusterPackageUri `
                        -Value ([string]$runtime.PackageUri)
                }
                catch {
                    Remove-Item -LiteralPath $runtimeConfigPath -Force
                    $runtimeWasRejected = $true
                }
            }
        }
    }
    $configuration.PackageUri = Assert-ClusterPackageUri `
        -Value ([string]$configuration.PackageUri)

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
        if ($runtimeWasReset) {
            Write-SyncRunnerLog -Root $root `
                -Message 'Discarded runtime SAS and package state for a changed bootstrap endpoint.'
        }
        elseif ($runtimeWasRejected) {
            Write-SyncRunnerLog -Root $root `
                -Message 'Discarded an invalid or expired runtime SAS and used the bootstrap SAS.'
        }
        while ($true) {
            try {
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
                    $bootstrapProcess = Invoke-ClusterPackageBootstrap `
                        -PackageDirectory $installed.PackageDirectory
                    if ($null -ne $bootstrapProcess) {
                        Write-SyncRunnerLog -Root $root `
                            -Message "bootstrap.ps1 exited with code $($bootstrapProcess.ExitCode)."
                    }
                    else {
                        Write-SyncRunnerLog -Root $root `
                            -Message 'Package has no bootstrap.ps1; execution was skipped.'
                    }
                    Save-SyncRunnerJson `
                        -Path $statePath `
                        -Value @{
                            SchemaVersion = 1
                            ETag = [string]$installed.Metadata.ETag
                            LastModifiedUtc = (
                                [DateTimeOffset]$installed.Metadata.LastModifiedUtc
                            ).ToUniversalTime().ToString('O')
                            PackageDirectory = $installed.PackageDirectory
                            BootstrapExitCode = if ($null -eq $bootstrapProcess) {
                                $null
                            }
                            else {
                                [int]$bootstrapProcess.ExitCode
                            }
                            CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
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
