Describe 'Windows PowerShell 5.1 cluster package runner' {
    BeforeAll {
        $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
        $runnerPath = Join-Path $repoRoot 'scripts\Sync.ps1'
        $sawPath = Join-Path $repoRoot 'scripts\Sync-SAW.ps1'
        $installerPath = Join-Path $repoRoot 'scripts\Install-WindowsPowerShellDependencies.ps1'
        . $runnerPath
    }

    It 'parses every supported script with Windows PowerShell 5.1' {
        $parserPath = Join-Path $TestDrive 'Test-Parse.ps1'
        @'
param([Parameter(Mandatory)][string]$RepoRoot)

$failed = $false
foreach ($relativePath in @(
    'scripts\Sync-SAW.ps1',
    'scripts\Sync.ps1',
    'scripts\Install-WindowsPowerShellDependencies.ps1'
)) {
    $path = Join-Path $RepoRoot $relativePath
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        $path,
        [ref]$tokens,
        [ref]$errors
    ) | Out-Null
    if ($errors.Count -gt 0) {
        $failed = $true
        $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }
    }
}
if ($failed) { exit 1 }
'@ | Set-Content -LiteralPath $parserPath -Encoding UTF8

        $process = Start-Process `
            -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -ArgumentList @(
                '-NoLogo',
                '-NoProfile',
                '-NonInteractive',
                '-File',
                $parserPath,
                '-RepoRoot',
                $repoRoot
            ) `
            -Wait `
            -PassThru

        $process.ExitCode | Should -Be 0
    }

    It 'does not depend on Entra, Az modules, Azure CLI, or AzCopy' {
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            $runnerPath,
            [ref]$null,
            [ref]$null
        )
        $commands = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst]
        }, $true) | ForEach-Object { $_.GetCommandName() })

        $commands | Should -Not -Contain 'Connect-AzAccount'
        $commands | Should -Not -Contain 'Get-AzStorageBlob'
        $commands | Should -Not -Contain 'az'
        $commands | Should -Not -Contain 'azcopy'
    }

    It 'accepts only an HTTPS Azure Blob SAS for cluster_package.zip' {
        $valid =
            'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
            '?sp=r&se=2099-01-01T00%3A00%3A00Z&sr=b&sig=test'

        Assert-ClusterPackageUri -Value $valid | Should -Be $valid
        {
            Assert-ClusterPackageUri -Value (
                'https://account123.blob.core.windows.net/packages/other.zip' +
                '?sp=r&se=2099-01-01T00%3A00%3A00Z&sr=b&sig=test'
            )
        } | Should -Throw
        {
            Assert-ClusterPackageUri -Value (
                'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
                '?sp=l&se=2099-01-01T00%3A00%3A00Z&sr=b&sig=test'
            )
        } | Should -Throw
    }

    It 'requires a strictly newer Blob timestamp and treats a matching ETag as current' {
        $state = @{
            ETag = '"etag-1"'
            LastModifiedUtc = '2026-08-27T01:00:00Z'
        }

        Test-RemotePackageUpdateRequired `
            -Remote ([pscustomobject]@{
                ETag = '"etag-1"'
                LastModifiedUtc = [DateTimeOffset]'2026-08-27T02:00:00Z'
            }) `
            -State $state | Should -BeFalse
        Test-RemotePackageUpdateRequired `
            -Remote ([pscustomobject]@{
                ETag = '"etag-2"'
                LastModifiedUtc = [DateTimeOffset]'2026-08-27T01:00:00Z'
            }) `
            -State $state | Should -BeFalse
        Test-RemotePackageUpdateRequired `
            -Remote ([pscustomobject]@{
                ETag = '"etag-2"'
                LastModifiedUtc = [DateTimeOffset]'2026-08-27T01:00:01Z'
            }) `
            -State $state | Should -BeTrue
    }

    It 'extracts safe entries and blocks archive traversal' {
        Add-Type -AssemblyName System.IO.Compression
        $safeArchive = Join-Path $TestDrive 'safe.zip'
        $safeOutput = Join-Path $TestDrive 'safe-output'
        $stream = [IO.File]::Open(
            $safeArchive,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite
        )
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create
        )
        $entry = $archive.CreateEntry('folder/run.ps1')
        $writer = [IO.StreamWriter]::new($entry.Open())
        $writer.Write('exit 0')
        $writer.Dispose()
        $archive.Dispose()
        $stream.Dispose()

        Expand-ClusterPackageSafely -ArchivePath $safeArchive -Destination $safeOutput
        Test-Path -LiteralPath (Join-Path $safeOutput 'folder\run.ps1') |
            Should -BeTrue

        $unsafeArchive = Join-Path $TestDrive 'unsafe.zip'
        $stream = [IO.File]::Open(
            $unsafeArchive,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::ReadWrite
        )
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create
        )
        [void]$archive.CreateEntry('../escape.ps1')
        $archive.Dispose()
        $stream.Dispose()

        {
            Expand-ClusterPackageSafely `
                -ArchivePath $unsafeArchive `
                -Destination (Join-Path $TestDrive 'unsafe-output')
        } | Should -Throw '*unsafe entry*'
    }

    It 'accepts rollover only for the same Blob endpoint' {
        $packageDirectory = New-Item -ItemType Directory `
            -Path (Join-Path $TestDrive 'package') -Force
        $current =
            'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
            '?sp=r&se=2099-01-01T00%3A00%3A00Z&sr=b&sig=old'
        $next =
            'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
            '?sp=r&se=2099-02-01T00%3A00%3A00Z&sr=b&sig=new'
        @{
            SchemaVersion = 1
            PackageUri = $next
        } | ConvertTo-Json | Set-Content `
            -LiteralPath (Join-Path $packageDirectory 'cluster_package.config') `
            -Encoding UTF8

        Get-RefreshedPackageUri `
            -PackageDirectory $packageDirectory `
            -CurrentPackageUri $current | Should -Be $next

        @{
            SchemaVersion = 1
            PackageUri = $next.Replace('account123', 'otheraccount')
        } | ConvertTo-Json | Set-Content `
            -LiteralPath (Join-Path $packageDirectory 'cluster_package.config') `
            -Encoding UTF8
        {
            Get-RefreshedPackageUri `
                -PackageDirectory $packageDirectory `
                -CurrentPackageUri $current
        } | Should -Throw '*change the configured Blob endpoint*'
    }
}
