<#
.SYNOPSIS
Synchronizes a local SAW folder with an Azure Blob container using Azure PowerShell.

.DESCRIPTION
Sync-SAW uses Az.Accounts for Microsoft Entra sign-in and Az.Storage for all Blob
operations. It does not require a separate transfer executable.

The script uploads local files that are new or changed, then downloads cloud-only
files without overwriting existing local files. Explicit deletion requests from
the management client remove only confirmed paths.

Settings are loaded from Sync-SAW.config.json beside this script by default.
Explicit command-line parameters override matching configuration values.

.PARAMETER ConfigPath
Optional JSON configuration path.

.PARAMETER LocalFolder
Existing local source folder.

.PARAMETER StorageAccount
Azure Storage account name. Normalized to lowercase.

.PARAMETER Container
Blob container name. Normalized to lowercase.

.PARAMETER AuthenticationMode
AzurePowerShell uses the connected Microsoft Entra account. Sas uses the account-
or container-scoped SAS stored in the configuration file.

.PARAMETER IntervalSeconds
Delay between continuous cycles. Defaults to 10 seconds.

.PARAMETER Continuous
Keeps running until Ctrl+C.

.PARAMETER PauseSync
Keeps a continuous process running without Blob operations.

.PARAMETER PublishSyncFlags
Publishes internal markers used by the management application to show which
current Blob versions have reached SAW.

.PARAMETER TenantId
Microsoft Entra tenant used for interactive authentication.

.PARAMETER SubscriptionId
Azure subscription selected after sign-in.

.PARAMETER LogDirectory
Directory for daily transcript logs. Defaults to the script directory.

.EXAMPLE
.\Sync-SAW.ps1

Loads the adjacent JSON configuration and starts the configured job.

.EXAMPLE
.\Sync-SAW.ps1 -LocalFolder 'D:\SAW' -StorageAccount 'contosodata' -Container 'files' -Continuous

Continuously uploads local changes and downloads cloud-only files.

.NOTES
Requires PowerShell 7, Az.Accounts, and Az.Storage. No storage account keys,
passwords, client secrets, or executable-specific token adapters are used.
#>

#requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter()]
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'Sync-SAW.config.json'),

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
$script:MarkerPrefix = '.syncsaw/saw-flags/'
$script:DeletionMarkerPrefix = '.syncsaw/deletions/'
$script:SasTokenForRedaction = $null

function Import-SyncSawConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][bool]$PathWasExplicit
    )

    $resolvedPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        if ($PathWasExplicit) {
            throw [System.IO.FileNotFoundException]::new(
                "Configuration file was not found: $resolvedPath"
            )
        }
        return @{}
    }

    try {
        $configuration = Get-Content -LiteralPath $resolvedPath -Raw -Encoding UTF8 |
            ConvertFrom-Json -AsHashtable
    }
    catch {
        throw [System.IO.InvalidDataException]::new(
            "Configuration file is not valid JSON: $resolvedPath",
            $_.Exception
        )
    }

    if ($null -eq $configuration) {
        throw [System.IO.InvalidDataException]::new(
            'Configuration file must contain a JSON object.'
        )
    }

    $allowedNames = @(
        'LocalFolder',
        'StorageAccount',
        'Container',
        'AuthenticationMode',
        'SasToken',
        'IntervalSeconds',
        'Continuous',
        'PauseSync',
        'PublishSyncFlags',
        # Accepted only to migrate existing configurations that have this set to false.
        'DeletionMode',
        'TenantId',
        'SubscriptionId',
        'LogDirectory'
    )
    foreach ($name in $configuration.Keys) {
        if ($name -notin $allowedNames) {
            throw [System.IO.InvalidDataException]::new(
                "Unsupported configuration property '$name'. Allowed properties: $($allowedNames -join ', ')."
            )
        }
    }
    if ($configuration.ContainsKey('DeletionMode')) {
        if ($configuration.DeletionMode -isnot [bool]) {
            throw [System.IO.InvalidDataException]::new(
                'DeletionMode in the configuration must be true or false.'
            )
        }
        if ([bool]$configuration.DeletionMode) {
            throw [System.IO.InvalidDataException]::new(
                'DeletionMode has been removed. Delete selected files explicitly in the management client.'
            )
        }
        [void]$configuration.Remove('DeletionMode')
    }

    return $configuration
}

function ConvertTo-StorageAccountName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $normalized = $Name.Trim().ToLowerInvariant()
    if ($normalized -notmatch '^[a-z0-9]{3,24}$') {
        throw [System.ArgumentException]::new(
            'StorageAccount must contain 3-24 lowercase letters or digits.'
        )
    }
    return $normalized
}

function ConvertTo-BlobContainerName {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $normalized = $Name.Trim().ToLowerInvariant()
    if (
        $normalized.Length -lt 3 -or
        $normalized.Length -gt 63 -or
        $normalized -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$'
    ) {
        throw [System.ArgumentException]::new(
            'Container must contain 3-63 lowercase letters, digits, or single hyphens.'
        )
    }
    return $normalized
}

function ConvertTo-RequiredGuid {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Value,
        [Parameter(Mandatory)][string]$Name
    )

    $parsed = [guid]::Empty
    if (-not [guid]::TryParse($Value.Trim(), [ref]$parsed) -or $parsed -eq [guid]::Empty) {
        throw [System.ArgumentException]::new("$Name must be a non-empty GUID.")
    }
    return $parsed.ToString('D')
}

function ConvertTo-SasToken {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Value,
        [Parameter(Mandatory)][string]$ExpectedAccount,
        [Parameter(Mandatory)][string]$ExpectedContainer
    )

    $candidate = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw [System.ArgumentException]::new(
            "SasToken is required when AuthenticationMode is 'Sas'."
        )
    }

    $absoluteUri = $null
    if ([uri]::TryCreate($candidate, [UriKind]::Absolute, [ref]$absoluteUri)) {
        $expectedHost = "$ExpectedAccount.blob.core.windows.net"
        $path = $absoluteUri.AbsolutePath.TrimEnd('/')
        if (
            $absoluteUri.Scheme -ne 'https' -or
            $absoluteUri.Host -ine $expectedHost -or
            $path -notin @('', "/$ExpectedContainer")
        ) {
            throw [System.ArgumentException]::new(
                'A SAS URL must use HTTPS and match the configured account and container.'
            )
        }
        $candidate = $absoluteUri.Query.TrimStart('?')
    }
    else {
        $candidate = $candidate.TrimStart('?')
    }

    if (
        [string]::IsNullOrWhiteSpace($candidate) -or
        $candidate -match '[\s#]' -or
        $candidate -notmatch '(?:^|&)sig=[^&]+' -or
        $candidate -notmatch '(?:^|&)sv=[^&]+'
    ) {
        throw [System.ArgumentException]::new(
            'SasToken must be a valid SAS query string or matching HTTPS SAS URL.'
        )
    }
    return "?$candidate"
}

function Resolve-LocalFolder {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    $resolved = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw [System.IO.DirectoryNotFoundException]::new(
            "Local source folder does not exist: $resolved"
        )
    }
    return (Get-Item -LiteralPath $resolved -Force).FullName
}

function Import-LatestModule {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $module = Get-Module -Name $Name -ListAvailable |
        Sort-Object Version -Descending |
        Select-Object -First 1
    if ($null -eq $module) {
        throw [System.IO.FileNotFoundException]::new(
            "$Name was not found. In PowerShell 7 run: Install-Module $Name -Scope CurrentUser"
        )
    }
    Import-Module -Name $module.Path -Force -ErrorAction Stop
    Write-Host "$Name $($module.Version) loaded."
}

function Test-SawAzureContext {
    [CmdletBinding()]
    param(
        [AllowNull()][object]$Context,
        [Parameter(Mandatory)][string]$Tenant,
        [AllowNull()][string]$Subscription
    )

    if ($null -eq $Context -or $null -eq $Context.Tenant) {
        return $false
    }
    if ($Context.Tenant.Id.ToString() -ine $Tenant) {
        return $false
    }
    return [string]::IsNullOrWhiteSpace($Subscription) -or (
        $null -ne $Context.Subscription -and
        $Context.Subscription.Id.ToString() -ieq $Subscription
    )
}

function New-SawStorageContext {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Account,
        [Parameter(Mandatory)][string]$AuthMode,
        [AllowNull()][string]$ConfiguredSas,
        [AllowNull()][string]$Tenant,
        [AllowNull()][string]$Subscription,
        [switch]$ForceInteractiveLogin
    )

    if ($AuthMode -eq 'Sas') {
        $sas = ConvertTo-SasToken `
            -Value $ConfiguredSas `
            -ExpectedAccount $Account `
            -ExpectedContainer $script:NormalizedContainer
        $script:SasTokenForRedaction = $sas
        Write-Host 'Using the configured account- or container-scoped SAS.'
        return New-AzStorageContext `
            -StorageAccountName $Account `
            -SasToken $sas
    }

    [void](Enable-AzContextAutosave -Scope CurrentUser -ErrorAction Stop)
    $context = if ($ForceInteractiveLogin) {
        $null
    }
    else {
        Get-AzContext -ErrorAction SilentlyContinue
    }
    if (
        -not $ForceInteractiveLogin -and
        -not (Test-SawAzureContext `
            -Context $context `
            -Tenant $Tenant `
            -Subscription $Subscription)
    ) {
        $context = Get-AzContext -ListAvailable -ErrorAction SilentlyContinue |
            Where-Object {
                Test-SawAzureContext `
                    -Context $_ `
                    -Tenant $Tenant `
                    -Subscription $Subscription
            } |
            Select-Object -First 1
        if ($null -ne $context) {
            [void](Set-AzContext `
                -Context $context `
                -Scope Process `
                -ErrorAction Stop)
        }
    }

    $cachedCredentialWorks = $false
    if (
        -not $ForceInteractiveLogin -and
        (Test-SawAzureContext `
            -Context $context `
            -Tenant $Tenant `
            -Subscription $Subscription)
    ) {
        try {
            [void](Get-AzAccessToken `
                -ResourceUrl 'https://storage.azure.com/' `
                -TenantId $Tenant `
                -DefaultProfile $context `
                -ErrorAction Stop)
            $cachedCredentialWorks = $true
            Write-Host "Using cached Microsoft Entra credential for $($context.Account.Id)."
        }
        catch {
            Write-Host (
                "The cached Microsoft Entra credential could not be refreshed: " +
                $_.Exception.Message
            )
        }
    }

    if (-not $cachedCredentialWorks) {
        $loginParameters = @{
            Tenant      = $Tenant
            ErrorAction = 'Stop'
        }
        if (-not [string]::IsNullOrWhiteSpace($Subscription)) {
            $loginParameters.Subscription = $Subscription
        }
        if ($ForceInteractiveLogin) {
            $loginParameters.Force = $true
            Write-Host 'The Microsoft Entra credential expired or requires interaction.'
        }

        Write-Host "Opening Microsoft Entra sign-in for tenant $Tenant..."
        [void](Connect-AzAccount @loginParameters)
        if (-not [string]::IsNullOrWhiteSpace($Subscription)) {
            [void](Set-AzContext `
                -Tenant $Tenant `
                -Subscription $Subscription `
                -ErrorAction Stop)
        }
        $context = Get-AzContext -ErrorAction Stop
        [void](Get-AzAccessToken `
            -ResourceUrl 'https://storage.azure.com/' `
            -TenantId $Tenant `
            -DefaultProfile $context `
            -ErrorAction Stop)
    }

    if ($context.Tenant.Id.ToString() -ine $Tenant) {
        throw [System.InvalidOperationException]::new(
            "The active Azure context tenant '$($context.Tenant.Id)' does not match '$Tenant'."
        )
    }
    if (
        -not [string]::IsNullOrWhiteSpace($Subscription) -and
        $context.Subscription.Id.ToString() -ine $Subscription
    ) {
        throw [System.InvalidOperationException]::new(
            "The active Azure subscription '$($context.Subscription.Id)' does not match '$Subscription'."
        )
    }

    return New-AzStorageContext -StorageAccountName $Account -UseConnectedAccount
}

function Get-SawErrorText {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$ErrorRecord)

    $details = [System.Collections.Generic.List[string]]::new()
    [void]$details.Add([string]$ErrorRecord)
    foreach ($propertyName in @('FullyQualifiedErrorId', 'Exception')) {
        $property = $ErrorRecord.PSObject.Properties[$propertyName]
        if ($null -ne $property -and $null -ne $property.Value) {
            [void]$details.Add([string]$property.Value)
        }
    }
    $errorDetails = $ErrorRecord.PSObject.Properties['ErrorDetails']
    if ($null -ne $errorDetails -and $null -ne $errorDetails.Value) {
        $message = $errorDetails.Value.PSObject.Properties['Message']
        if ($null -ne $message -and $null -ne $message.Value) {
            [void]$details.Add([string]$message.Value)
        }
    }
    return $details -join [Environment]::NewLine
}

function Test-SawNotFoundError {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$ErrorRecord)

    return (Get-SawErrorText -ErrorRecord $ErrorRecord) -match (
        '(?i)(?:\b404\b|BlobNotFound|ContainerNotFound|ResourceNotFound|' +
        'The specified (?:blob|container) does not exist|NotFound)'
    )
}

function Test-SawTransientStorageError {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$ErrorRecord)

    return (Get-SawErrorText -ErrorRecord $ErrorRecord) -match (
        '(?i)(?:\b408\b|\b429\b|\b500\b|\b502\b|\b503\b|\b504\b|' +
        'OperationTimedOut|ServerBusy|InternalError|temporar(?:y|ily)|' +
        'timed?\s*out|connection (?:reset|closed|aborted)|' +
        'name resolution|network.*unavailable)'
    )
}

function Test-SawAuthenticationError {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$ErrorRecord)

    return (Get-SawErrorText -ErrorRecord $ErrorRecord) -match (
        '(?i)(?:\b401\b|AuthenticationFailed|AuthenticationRequired|' +
        'ExpiredAuthenticationToken|InvalidAuthenticationToken|TokenExpired|' +
        'AADSTS(?:50058|50076|50078|50079|50173|70043|700082|700084)|' +
        'InteractionRequired|MsalUiRequiredException|interaction_required|' +
        'user interaction is required|refresh token.{0,80}expired|' +
        'access token.{0,80}expired|token (?:has )?expired|' +
        'credentials? (?:has|have) expired|failed to acquire token|' +
        'could not acquire token|token refresh failed|' +
        'server failed to authenticate the request|' +
        'reauthenticat(?:e|ion))'
    )
}

function Invoke-SawStorageOperation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Description,
        [Parameter(Mandatory)][scriptblock]$Operation,
        [switch]$IgnoreNotFound,
        [ValidateRange(1, 10)][int]$MaximumAttempts = 4
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            return & $Operation
        }
        catch {
            if ($IgnoreNotFound -and (Test-SawNotFoundError -ErrorRecord $_)) {
                Write-Host "$Description skipped because the Blob no longer exists."
                return $null
            }
            if (
                -not (Test-SawTransientStorageError -ErrorRecord $_) -or
                $attempt -eq $MaximumAttempts
            ) {
                throw
            }

            $delaySeconds = [Math]::Min(
                [Math]::Pow(2, $attempt - 1),
                8
            )
            Write-Warning (
                "$Description failed with a transient storage error " +
                "(attempt $attempt of $MaximumAttempts): $($_.Exception.Message) " +
                "Retrying in $delaySeconds second(s)."
            )
            Start-Sleep -Seconds $delaySeconds
        }
    }
}

function Initialize-BlobContainer {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][object]$Context
    )

    try {
        [void](Invoke-SawStorageOperation `
            -Description "Checking Blob container '$Name'" `
            -Operation {
                Get-AzStorageContainer `
                    -Name $Name `
                    -Context $Context `
                    -ErrorAction Stop
            })
        return
    }
    catch {
        $detail = $_.Exception.ToString()
        if ($detail -notmatch '(?i)ContainerNotFound|does not exist|(?:^|\D)404(?:\D|$)') {
            throw
        }
    }

    Write-Host "Blob container '$Name' does not exist. Creating it..."
    [void](Invoke-SawStorageOperation `
        -Description "Creating Blob container '$Name'" `
        -Operation {
            New-AzStorageContainer `
                -Name $Name `
                -Context $Context `
                -ErrorAction Stop
        })
}

function Get-LocalFileRecords {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root)

    $records = foreach ($file in Get-ChildItem -LiteralPath $Root -File -Recurse -Force) {
        $relative = [System.IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
        if (
            $relative.Equals('.syncsaw', [StringComparison]::OrdinalIgnoreCase) -or
            $relative.StartsWith('.syncsaw/', [StringComparison]::OrdinalIgnoreCase)
        ) {
            continue
        }

        [pscustomobject]@{
            RelativePath     = $relative
            FullName         = $file.FullName
            Length           = [long]$file.Length
            LastWriteTimeUtc = [DateTimeOffset]$file.LastWriteTimeUtc
        }
    }
    return @($records)
}

function Get-BlobRecord {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Blob)

    $properties = @($Blob)
    foreach ($propertyName in @('BlobProperties', 'Properties')) {
        $property = $Blob.PSObject.Properties[$propertyName]
        if ($null -ne $property -and $null -ne $property.Value) {
            $properties += $property.Value
        }
    }
    $cloudBlobProperty = $Blob.PSObject.Properties['ICloudBlob']
    if ($null -ne $cloudBlobProperty -and $null -ne $cloudBlobProperty.Value) {
        $properties += $cloudBlobProperty.Value
        $cloudProperties = $cloudBlobProperty.Value.PSObject.Properties['Properties']
        if ($null -ne $cloudProperties -and $null -ne $cloudProperties.Value) {
            $properties += $cloudProperties.Value
        }
    }

    $lastModified = $null
    $length = $null
    foreach ($candidate in $properties) {
        if ($null -eq $lastModified) {
            $property = $candidate.PSObject.Properties['LastModified']
            if ($null -ne $property -and $null -ne $property.Value) {
                $lastModified = [DateTimeOffset]$property.Value
            }
        }
        if ($null -eq $length) {
            foreach ($propertyName in @('Length', 'ContentLength')) {
                $property = $candidate.PSObject.Properties[$propertyName]
                if ($null -ne $property -and $null -ne $property.Value) {
                    $length = [long]$property.Value
                    break
                }
            }
        }
    }

    if ($null -eq $lastModified -or $null -eq $length) {
        throw [System.IO.InvalidDataException]::new(
            "Az.Storage returned incomplete properties for Blob '$($Blob.Name)'."
        )
    }

    return [pscustomobject]@{
        Name         = [string]$Blob.Name
        Length       = $length
        LastModified = $lastModified
    }
}

function Get-RemoteBlobRecords {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][object]$Context
    )

    $blobs = @(Invoke-SawStorageOperation `
        -Description "Listing Blob container '$ContainerName'" `
        -Operation {
            Get-AzStorageBlob `
                -Container $ContainerName `
                -Context $Context `
                -ErrorAction Stop
        })
    $records = foreach ($blob in $blobs) {
        if ($null -eq $blob) {
            continue
        }
        Get-BlobRecord -Blob $blob
    }
    return @($records)
}

function New-RecordDictionary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$Records,
        [Parameter(Mandatory)][string]$Property
    )

    $dictionary = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    foreach ($record in @($Records)) {
        if ($null -eq $record) {
            continue
        }
        $dictionary[[string]$record.$Property] = $record
    }
    return $dictionary
}

function Resolve-SafeLocalPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RelativePath
    )

    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $candidate = [System.IO.Path]::GetFullPath(
        [System.IO.Path]::Combine(
            $fullRoot,
            $RelativePath.Replace('/', [System.IO.Path]::DirectorySeparatorChar)
        )
    )
    $prefix = $fullRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw [System.IO.InvalidDataException]::new(
            "Blob path escapes the local folder: $RelativePath"
        )
    }
    return $candidate
}

function Test-LocalUploadRequired {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Local,
        [AllowNull()][object]$Remote
    )

    return $null -eq $Remote
}

function Test-RemoteDownloadRequired {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Remote,
        [AllowNull()][object]$Local
    )

    if ($null -eq $Local) {
        return $true
    }
    return $Local.Length -ne $Remote.Length -or
        (
            $Remote.LastModified - $Local.LastWriteTimeUtc
        ).Duration() -gt [TimeSpan]::FromSeconds(2)
}

function Test-SawInternalBlob {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BlobPath)

    return $BlobPath.StartsWith($script:MarkerPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        $BlobPath.StartsWith(
            $script:DeletionMarkerPrefix,
            [StringComparison]::OrdinalIgnoreCase
        )
}

function Get-SawDeletionMarkerPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/').TrimStart('/')
    $hash = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($normalized)
    )
    return $script:DeletionMarkerPrefix +
        [System.Convert]::ToHexString($hash).ToLowerInvariant() +
        '.delete'
}

function ConvertFrom-SawDeletionRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content,
        [Parameter(Mandatory)][string]$RequestName
    )

    $trimmed = $Content.Trim()
    if ([string]::IsNullOrWhiteSpace($trimmed)) {
        throw [System.IO.InvalidDataException]::new(
            "SAW deletion request is empty: $RequestName"
        )
    }

    if ($trimmed.StartsWith('"', [StringComparison]::Ordinal)) {
        try {
            $target = $trimmed | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw [System.IO.InvalidDataException]::new(
                "SAW deletion request is not a valid JSON string: $RequestName",
                $_.Exception
            )
        }
    }
    else {
        # Older management clients stored the relative path as plain UTF-8 text.
        $target = $trimmed
    }

    if ($target -isnot [string] -or $target -match '[\x00-\x1f\x7f]') {
        throw [System.IO.InvalidDataException]::new(
            "Invalid SAW deletion request content: $RequestName"
        )
    }
    return $target
}

function Invoke-CloudDeletionRequests {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$RemoteFiles,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][object]$Context
    )

    $remoteByPath = New-RecordDictionary -Records $RemoteFiles -Property 'Name'
    $applied = 0
    foreach ($request in @($RemoteFiles)) {
        if (
            $null -eq $request -or
            -not $request.Name.StartsWith(
                $script:DeletionMarkerPrefix,
                [StringComparison]::OrdinalIgnoreCase
            )
        ) {
            continue
        }

        $temporaryRequest = [System.IO.Path]::GetTempFileName()
        try {
            $requestRetrieved = Invoke-SawStorageOperation `
                -Description "Downloading deletion request '$($request.Name)'" `
                -IgnoreNotFound `
                -Operation {
                    [void](Get-AzStorageBlobContent `
                        -Container $ContainerName `
                        -Blob $request.Name `
                        -Destination $temporaryRequest `
                        -Context $Context `
                        -Force `
                        -ErrorAction Stop)
                    return $true
                }
            if ($requestRetrieved -ne $true) {
                continue
            }
            $requestTarget = ConvertFrom-SawDeletionRequest `
                -Content ([System.IO.File]::ReadAllText($temporaryRequest)) `
                -RequestName $request.Name
            $relativePath = $requestTarget.Replace('\', '/').TrimStart('/')
            if (
                [string]::IsNullOrWhiteSpace($relativePath) -or
                -not $request.Name.Equals(
                    (Get-SawDeletionMarkerPath -RelativePath $relativePath),
                    [StringComparison]::OrdinalIgnoreCase
                )
            ) {
                throw [System.IO.InvalidDataException]::new(
                    "Invalid SAW deletion request: $($request.Name)"
                )
            }

            $localPath = Resolve-SafeLocalPath -Root $Root -RelativePath $relativePath
            Write-Host "Applying cloud deletion locally for $relativePath"
            if (Test-Path -LiteralPath $localPath -PathType Leaf) {
                Remove-Item -LiteralPath $localPath -Force -ErrorAction Stop
            }

            foreach ($blobPath in @(
                $relativePath,
                (Get-SawMarkerPath -RelativePath $relativePath),
                $request.Name
            )) {
                if (-not $remoteByPath.ContainsKey($blobPath)) {
                    continue
                }
                [void](Invoke-SawStorageOperation `
                    -Description "Deleting Blob '$blobPath'" `
                    -IgnoreNotFound `
                    -Operation {
                        Remove-AzStorageBlob `
                            -Container $ContainerName `
                            -Blob $blobPath `
                            -Context $Context `
                            -Force `
                            -ErrorAction Stop
                    })
            }
            $applied++
        }
        finally {
            Remove-Item -LiteralPath $temporaryRequest -Force -ErrorAction SilentlyContinue
        }
    }
    return $applied
}

function Invoke-NormalSynchronization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][object]$Context,
        [Parameter(Mandatory)][bool]$PublishFlags
    )

    $remoteFiles = @(
        Get-RemoteBlobRecords -ContainerName $ContainerName -Context $Context
    )
    $cloudDeletionsApplied = Invoke-CloudDeletionRequests `
        -Root $Root `
        -RemoteFiles $remoteFiles `
        -ContainerName $ContainerName `
        -Context $Context
    if ($cloudDeletionsApplied -gt 0) {
        $remoteFiles = @(
            Get-RemoteBlobRecords -ContainerName $ContainerName -Context $Context
        )
    }
    $localFiles = @(Get-LocalFileRecords -Root $Root)
    $remoteByPath = New-RecordDictionary -Records $remoteFiles -Property 'Name'

    $uploaded = 0
    foreach ($local in $localFiles) {
        $remote = $null
        [void]$remoteByPath.TryGetValue($local.RelativePath, [ref]$remote)
        if (-not (Test-LocalUploadRequired -Local $local -Remote $remote)) {
            continue
        }

        Write-Host "Uploading $($local.RelativePath)"
        [void](Invoke-SawStorageOperation `
            -Description "Uploading Blob '$($local.RelativePath)'" `
            -Operation {
                Set-AzStorageBlobContent `
                    -File $local.FullName `
                    -Container $ContainerName `
                    -Blob $local.RelativePath `
                    -Context $Context `
                    -Force `
                    -ErrorAction Stop
            })
        $uploaded++
    }

    $localByPath = New-RecordDictionary -Records $localFiles -Property 'RelativePath'
    $downloaded = 0
    foreach ($remote in $remoteFiles) {
        if (Test-SawInternalBlob -BlobPath $remote.Name) {
            continue
        }
        $local = $null
        [void]$localByPath.TryGetValue($remote.Name, [ref]$local)
        if (-not (Test-RemoteDownloadRequired -Remote $remote -Local $local)) {
            continue
        }

        $destination = Resolve-SafeLocalPath -Root $Root -RelativePath $remote.Name
        [void][System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($destination)
        )
        $downloadDescription = if ($null -eq $local) {
            'cloud-only file'
        }
        else {
            'authoritative cloud version'
        }
        Write-Host "Downloading $downloadDescription $($remote.Name)"
        $blobRetrieved = Invoke-SawStorageOperation `
            -Description "Downloading Blob '$($remote.Name)'" `
            -IgnoreNotFound `
            -Operation {
                [void](Get-AzStorageBlobContent `
                    -Container $ContainerName `
                    -Blob $remote.Name `
                    -Destination $destination `
                    -Context $Context `
                    -Force `
                    -ErrorAction Stop)
                return $true
            }
        if ($blobRetrieved -ne $true) {
            continue
        }
        [System.IO.File]::SetLastWriteTimeUtc(
            $destination,
            $remote.LastModified.UtcDateTime
        )
        $downloaded++
    }

    if ($PublishFlags) {
        $currentLocalFiles = @(Get-LocalFileRecords -Root $Root)
        $currentRemoteFiles = @(
            Get-RemoteBlobRecords `
                -ContainerName $ContainerName `
                -Context $Context
        )
        Publish-SawSyncFlags `
            -LocalFiles $currentLocalFiles `
            -RemoteFiles $currentRemoteFiles `
            -ContainerName $ContainerName `
            -Context $Context
    }

    Write-Host (
        "Synchronization completed: $uploaded uploaded, $downloaded downloaded, " +
        "$cloudDeletionsApplied cloud deletion(s) applied locally."
    )
}

function Get-SawMarkerPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/').TrimStart('/')
    $hash = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($normalized)
    )
    return $script:MarkerPrefix +
        [System.Convert]::ToHexString($hash).ToLowerInvariant() +
        '.flag'
}

function Publish-SawSyncFlags {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$LocalFiles,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyCollection()][object[]]$RemoteFiles,
        [Parameter(Mandatory)][string]$ContainerName,
        [Parameter(Mandatory)][object]$Context
    )

    $remoteByPath = New-RecordDictionary -Records $RemoteFiles -Property 'Name'
    $desiredMarkers = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    $temporaryFlag = [System.IO.Path]::GetTempFileName()

    try {
        [System.IO.File]::WriteAllBytes($temporaryFlag, [byte[]]::new(0))
        foreach ($local in @($LocalFiles)) {
            if ($null -eq $local) {
                continue
            }
            $markerPath = Get-SawMarkerPath -RelativePath $local.RelativePath
            [void]$desiredMarkers.Add($markerPath)

            $source = $null
            $marker = $null
            [void]$remoteByPath.TryGetValue($local.RelativePath, [ref]$source)
            [void]$remoteByPath.TryGetValue($markerPath, [ref]$marker)
            if (
                $null -ne $source -and
                $null -ne $marker -and
                $marker.LastModified -ge $source.LastModified
            ) {
                continue
            }

            [void](Invoke-SawStorageOperation `
                -Description "Publishing SAW marker '$markerPath'" `
                -Operation {
                    Set-AzStorageBlobContent `
                        -File $temporaryFlag `
                        -Container $ContainerName `
                        -Blob $markerPath `
                        -Context $Context `
                        -Force `
                        -ErrorAction Stop
                })
        }

        foreach ($remote in @($RemoteFiles)) {
            if ($null -eq $remote) {
                continue
            }
            if (
                $remote.Name.StartsWith($script:MarkerPrefix, [StringComparison]::OrdinalIgnoreCase) -and
                -not $desiredMarkers.Contains($remote.Name)
            ) {
                $markerToDelete = $remote.Name
                [void](Invoke-SawStorageOperation `
                    -Description "Deleting stale SAW marker '$markerToDelete'" `
                    -IgnoreNotFound `
                    -Operation {
                        Remove-AzStorageBlob `
                            -Container $ContainerName `
                            -Blob $markerToDelete `
                            -Context $Context `
                            -Force `
                            -ErrorAction Stop
                    })
            }
        }
    }
    finally {
        Remove-Item -LiteralPath $temporaryFlag -Force -ErrorAction SilentlyContinue
    }
}

function Get-SynchronizationMutexName {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Folder,
        [Parameter(Mandatory)][string]$Account,
        [Parameter(Mandatory)][string]$ContainerName
    )

    $identity = "$($Folder.ToLowerInvariant())|$Account|$ContainerName"
    $hash = [System.Security.Cryptography.SHA256]::HashData(
        [System.Text.Encoding]::UTF8.GetBytes($identity)
    )
    return 'Local\SyncSAW_' + [System.Convert]::ToHexString($hash).Substring(0, 32)
}

function Protect-SawTranscript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [AllowNull()][string]$SasToken
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return
    }

    $content = [System.IO.File]::ReadAllText($Path)
    if (-not [string]::IsNullOrWhiteSpace($SasToken)) {
        $content = $content.Replace($SasToken, '?<redacted>')
        $content = $content.Replace($SasToken.TrimStart('?'), '<redacted>')
    }
    $content = [regex]::Replace(
        $content,
        '([?&]sig=)[^&\s"''>]+',
        '$1<redacted>',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    $content = [regex]::Replace(
        $content,
        '(https://[^\s?"''>]+)\?[^\s"''>]+',
        '$1?<redacted>',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase
    )
    [System.IO.File]::WriteAllText($Path, $content)
}

$mutex = $null
$mutexAcquired = $false
$transcriptStarted = $false
$transcriptPath = $null
$finalExitCode = 0

try {
    $configuration = Import-SyncSawConfiguration `
        -Path $ConfigPath `
        -PathWasExplicit $PSBoundParameters.ContainsKey('ConfigPath')

    foreach ($name in @(
        'LocalFolder',
        'StorageAccount',
        'Container',
        'AuthenticationMode',
        'TenantId',
        'SubscriptionId',
        'LogDirectory'
    )) {
        if (-not $PSBoundParameters.ContainsKey($name) -and $configuration.ContainsKey($name)) {
            Set-Variable -Name $name -Value ([string]$configuration[$name])
        }
    }

    if (-not $PSBoundParameters.ContainsKey('IntervalSeconds') -and $configuration.ContainsKey('IntervalSeconds')) {
        $configuredInterval = 0
        if (
            -not [int]::TryParse([string]$configuration.IntervalSeconds, [ref]$configuredInterval) -or
            $configuredInterval -lt 1 -or
            $configuredInterval -gt 86400
        ) {
            throw [System.IO.InvalidDataException]::new(
                'IntervalSeconds must be an integer from 1 through 86400.'
            )
        }
        $IntervalSeconds = $configuredInterval
    }

    foreach ($name in @('Continuous', 'PauseSync', 'PublishSyncFlags')) {
        if (-not $PSBoundParameters.ContainsKey($name) -and $configuration.ContainsKey($name)) {
            if ($configuration[$name] -isnot [bool]) {
                throw [System.IO.InvalidDataException]::new(
                    "$name in the configuration must be true or false."
                )
            }
            Set-Variable -Name $name -Value ([bool]$configuration[$name])
        }
    }

    $configuredSas = if ($configuration.ContainsKey('SasToken')) {
        if ($configuration.SasToken -isnot [string]) {
            throw [System.IO.InvalidDataException]::new(
                'SasToken in the configuration must be a string.'
            )
        }
        [string]$configuration.SasToken
    }
    else {
        $null
    }

    foreach ($requiredName in @('LocalFolder', 'StorageAccount', 'Container')) {
        if ([string]::IsNullOrWhiteSpace([string](Get-Variable -Name $requiredName -ValueOnly))) {
            throw [System.ArgumentException]::new(
                "$requiredName is required in '$ConfigPath' or as -$requiredName."
            )
        }
    }

    $normalizedAccount = ConvertTo-StorageAccountName -Name $StorageAccount
    $script:NormalizedContainer = ConvertTo-BlobContainerName -Name $Container
    $normalizedTenant = if ($AuthenticationMode -eq 'AzurePowerShell') {
        ConvertTo-RequiredGuid -Value $TenantId -Name 'TenantId'
    }
    else {
        $null
    }
    $normalizedSubscription = if (
        $AuthenticationMode -eq 'AzurePowerShell' -and
        -not [string]::IsNullOrWhiteSpace($SubscriptionId)
    ) {
        ConvertTo-RequiredGuid -Value $SubscriptionId -Name 'SubscriptionId'
    }
    else {
        $null
    }
    $resolvedFolder = Resolve-LocalFolder -Path $LocalFolder

    $resolvedLogDirectory = if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
        $PSScriptRoot
    }
    else {
        $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($LogDirectory.Trim())
    }
    [void][System.IO.Directory]::CreateDirectory($resolvedLogDirectory)
    $transcriptPath = Join-Path `
        $resolvedLogDirectory `
        "sync-saw-$([DateTimeOffset]::Now.ToString('yyyyMMdd')).log"
    [void](Start-Transcript -LiteralPath $transcriptPath -Append -UseMinimalHeader)
    $transcriptStarted = $true

    $mutexName = Get-SynchronizationMutexName `
        -Folder $resolvedFolder `
        -Account $normalizedAccount `
        -ContainerName $script:NormalizedContainer
    $mutex = [System.Threading.Mutex]::new($false, $mutexName)
    try {
        $mutexAcquired = $mutex.WaitOne(0)
    }
    catch [System.Threading.AbandonedMutexException] {
        $mutexAcquired = $true
    }
    if (-not $mutexAcquired) {
        throw [System.InvalidOperationException]::new(
            'Another Sync-SAW process is already using this local folder and container.'
        )
    }

    $storageContext = $null
    if (-not [bool]$PauseSync) {
        Import-LatestModule -Name 'Az.Accounts'
        Import-LatestModule -Name 'Az.Storage'
        $storageContext = New-SawStorageContext `
            -Account $normalizedAccount `
            -AuthMode $AuthenticationMode `
            -ConfiguredSas $configuredSas `
            -Tenant $normalizedTenant `
            -Subscription $normalizedSubscription
        Initialize-BlobContainer `
            -Name $script:NormalizedContainer `
            -Context $storageContext
    }

    Write-Host "Local folder: $resolvedFolder"
    Write-Host "Storage account: $normalizedAccount"
    Write-Host "Blob container: $($script:NormalizedContainer)"
    Write-Host "Operation log: $transcriptPath"

    do {
        Write-Host ''
        Write-Host "Cycle started at $([DateTimeOffset]::Now.ToString('u'))"
        $reauthenticatedThisCycle = $false
        while ($true) {
            try {
                if ([bool]$PauseSync) {
                    Write-Host 'Synchronization is paused by configuration.'
                }
                else {
                    Invoke-NormalSynchronization `
                        -Root $resolvedFolder `
                        -ContainerName $script:NormalizedContainer `
                        -Context $storageContext `
                        -PublishFlags ([bool]$PublishSyncFlags)
                }
                break
            }
            catch [System.Management.Automation.PipelineStoppedException] {
                throw
            }
            catch {
                $authenticationExpired =
                    $AuthenticationMode -eq 'AzurePowerShell' -and
                    (Test-SawAuthenticationError -ErrorRecord $_)
                if (
                    [bool]$Continuous -and
                    $authenticationExpired -and
                    -not $reauthenticatedThisCycle
                ) {
                    Write-Warning (
                        'Microsoft Entra authentication expired. SyncSAW will remain ' +
                        'running and open sign-in again.'
                    )
                    try {
                        $storageContext = New-SawStorageContext `
                            -Account $normalizedAccount `
                            -AuthMode $AuthenticationMode `
                            -ConfiguredSas $configuredSas `
                            -Tenant $normalizedTenant `
                            -Subscription $normalizedSubscription `
                            -ForceInteractiveLogin
                        $reauthenticatedThisCycle = $true
                        Write-Host (
                            'Microsoft Entra sign-in succeeded. Retrying the current ' +
                            'synchronization cycle.'
                        )
                        continue
                    }
                    catch [System.Management.Automation.PipelineStoppedException] {
                        throw
                    }
                    catch {
                        Write-Warning (
                            'Microsoft Entra sign-in did not complete. SyncSAW will ' +
                            "continue running and retry after $IntervalSeconds second(s): " +
                            $_.Exception.Message
                        )
                        break
                    }
                }
                if (
                    [bool]$Continuous -and
                    $authenticationExpired -and
                    $reauthenticatedThisCycle
                ) {
                    Write-Warning (
                        'Authentication still failed after sign-in. SyncSAW will ' +
                        "continue running and retry after $IntervalSeconds second(s): " +
                        $_.Exception.Message
                    )
                    break
                }

                $recoverable = (Test-SawNotFoundError -ErrorRecord $_) -or
                    (Test-SawTransientStorageError -ErrorRecord $_)
                if (-not [bool]$Continuous -or -not $recoverable) {
                    throw
                }
                Write-Warning (
                    "Synchronization cycle encountered a recoverable storage error and " +
                    "will continue: $($_.Exception.Message)"
                )
                break
            }
        }

        if ([bool]$Continuous) {
            Write-Host "Waiting $IntervalSeconds second(s). Press Ctrl+C to stop."
            Start-Sleep -Seconds $IntervalSeconds
        }
    } while ([bool]$Continuous)
}
catch [System.Management.Automation.PipelineStoppedException] {
    $finalExitCode = 130
}
catch {
    [Console]::Error.WriteLine("Sync-SAW failed: $($_.Exception.Message)")
    $finalExitCode = 1
}
finally {
    if ($mutexAcquired -and $null -ne $mutex) {
        try {
            $mutex.ReleaseMutex()
        }
        catch [System.ApplicationException] {
            # The host already released the mutex.
        }
    }
    if ($null -ne $mutex) {
        $mutex.Dispose()
    }
    if ($transcriptStarted) {
        try {
            [void](Stop-Transcript)
        }
        catch [System.InvalidOperationException] {
            # The host already stopped the transcript.
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($transcriptPath)) {
        try {
            Protect-SawTranscript `
                -Path $transcriptPath `
                -SasToken $script:SasTokenForRedaction
        }
        catch {
            [Console]::Error.WriteLine(
                "Sync-SAW could not sanitize its transcript: $($_.Exception.Message)"
            )
            $finalExitCode = 1
        }
    }
}

exit $finalExitCode
