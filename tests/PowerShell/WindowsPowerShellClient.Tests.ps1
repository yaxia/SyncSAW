Describe 'Windows PowerShell 5.1 cluster package runner' {
    BeforeAll {
        $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
        $runnerPath = Join-Path $repoRoot 'scripts\Sync.ps1'
        $sawPath = Join-Path $repoRoot 'scripts\Sync-SAW.ps1'
        $runExamplePath = Join-Path $repoRoot 'scripts\run.example.ps1'
        $installerPath = Join-Path $repoRoot 'scripts\Install-WindowsPowerShellDependencies.ps1'
        $delegationSas =
            '?sp=r&spr=https&se=2099-01-01T00%3A00%3A00Z&sr=b' +
            '&skoid=00000000-0000-0000-0000-000000000001' +
            '&sktid=00000000-0000-0000-0000-000000000002' +
            '&skt=2098-12-31T00%3A00%3A00Z&ske=2099-01-01T00%3A00%3A00Z' +
            '&sks=b&skv=2026-04-06&sig=test'
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
    'scripts\run.example.ps1',
    'scripts\Test-RunScriptSafety.ps1',
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

    It 'parses SAS query values correctly in Windows PowerShell 5.1' {
        $probePath = Join-Path $TestDrive 'Test-SasParsing.ps1'
        @"
`$ErrorActionPreference = 'Stop'
. '$($runnerPath.Replace("'", "''"))'
`$uri = [uri]('https://account123.blob.core.windows.net/packages/' +
    'cluster_package.zip$delegationSas')
`$values = Get-SasQueryValues -Uri `$uri
if (`$values.sp -ne 'r' -or `$values.sig -ne 'test') { exit 2 }
[void](Assert-ClusterPackageUri -Value `$uri.AbsoluteUri)
"@ | Set-Content -LiteralPath $probePath -Encoding UTF8

        $process = Start-Process `
            -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -ArgumentList @(
                '-NoLogo',
                '-NoProfile',
                '-NonInteractive',
                '-File',
                $probePath
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

    It 'accepts the additive-only run.ps1 example' {
        Assert-RunScriptSafety -Path $runExamplePath |
            Should -Be ([IO.Path]::GetFullPath($runExamplePath))
    }

    It 'rejects destructive, overwrite, restart, cloud, and dynamic operations' {
        $unsafeScripts = @(
            'Remove-Item -LiteralPath .\result.txt',
            'Set-Content -LiteralPath .\result.txt -Value changed',
            'cp .\source.txt .\result.txt',
            'New-Item -ItemType File -Path .\result.txt -Force',
            'ni -ItemType File -Path .\result.txt -Force',
            'Restart-Computer -Force',
            'az storage blob delete --account-name test --container-name test --name test',
            '[IO.File]::Delete(''.\result.txt'')',
            '[IO.File]::Open(''.\result.txt'', [IO.FileMode]::Create)',
            'Invoke-WebRequest -Uri https://example.test -Method Delete',
            'Write-Output unsafe > .\result.txt',
            '$command = ''tool.exe''; & $command'
        )

        for ($index = 0; $index -lt $unsafeScripts.Count; $index++) {
            $path = Join-Path $TestDrive "unsafe-$index.ps1"
            $unsafeScripts[$index] |
                Set-Content -LiteralPath $path -Encoding UTF8

            { Assert-RunScriptSafety -Path $path } |
                Should -Throw '*failed the additive-only safety harness*'
        }
    }

    It 'accepts only an HTTPS Azure Blob SAS for cluster_package.zip' {
        $valid =
            'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
            $delegationSas

        Assert-ClusterPackageUri -Value $valid | Should -Be $valid
        {
            Assert-ClusterPackageUri -Value (
                'https://account123.blob.core.windows.net/packages/other.zip' +
                $delegationSas
            )
        } | Should -Throw
        {
            Assert-ClusterPackageUri -Value (
                'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
                $delegationSas.Replace('sp=r', 'sp=l')
            )
        } | Should -Throw
        foreach ($unsafeQuery in @(
                $delegationSas.Replace('sp=r', 'sp=rw'),
                $delegationSas.Replace('sr=b', 'sr=c'),
                $delegationSas.Replace('&spr=https', '')
            )) {
            {
                Assert-ClusterPackageUri -Value (
                    'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
                    $unsafeQuery
                )
            } | Should -Throw '*exact permissions*'
        }
    }

    It 'defaults task execution to the secured ProgramData hierarchy' {
        $configurationPath = Join-Path $TestDrive 'minimal.config.json'
        @{
            PackageUri = (
                'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
                $delegationSas
            )
        } | ConvertTo-Json | Set-Content -LiteralPath $configurationPath -Encoding UTF8

        $configuration = Resolve-SyncRunnerConfiguration `
            -Path $configurationPath `
            -Overrides @{}
        $expectedRoot = Join-Path (
            Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'SyncSAW'
        ) 'Tasks'

        $configuration.TaskExecutionRoot | Should -Be $expectedRoot
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
            'https://account123.blob.core.windows.net/sync-packages/cluster_package.zip' +
            $delegationSas.Replace('sig=test', 'sig=old')
        $next =
            'https://account123.blob.core.windows.net/sync-packages/cluster_package.zip' +
            $delegationSas.Replace('sig=test', 'sig=new')
        $results =
            'https://account123.blob.core.windows.net/sync/cluster-results/result.zip' +
            $delegationSas.Replace('sp=r', 'sp=c')
        @{
            SchemaVersion = 2
            PackageUri = $next
            ResultsBlobUri = $results
        } | ConvertTo-Json | Set-Content `
            -LiteralPath (Join-Path $packageDirectory 'cluster_package.config') `
            -Encoding UTF8

        Get-RefreshedPackageUri `
            -PackageDirectory $packageDirectory `
            -CurrentPackageUri $current | Should -Be $next

        @{
            SchemaVersion = 2
            PackageUri = $next.Replace('account123', 'otheraccount')
            ResultsBlobUri = $results
        } | ConvertTo-Json | Set-Content `
            -LiteralPath (Join-Path $packageDirectory 'cluster_package.config') `
            -Encoding UTF8
        {
            Get-RefreshedPackageUri `
                -PackageDirectory $packageDirectory `
                -CurrentPackageUri $current
        } | Should -Throw '*change the configured Blob endpoint*'
    }

    It 'accepts only a create-only SAS for one result ZIP Blob' {
        $package =
            'https://account123.blob.core.windows.net/sync-packages/cluster_package.zip' +
            $delegationSas
        $results =
            'https://account123.blob.core.windows.net/sync/cluster-results/result.zip' +
            $delegationSas.Replace('sp=r', 'sp=c')

        Assert-ClusterResultsUri -Value $results -PackageUri $package |
            Should -Be $results
        {
            Assert-ClusterResultsUri `
                -Value $results.Replace('sp=c', 'sp=rc') `
                -PackageUri $package
        } | Should -Throw '*exact permissions*'
        {
            Assert-ClusterResultsUri `
                -Value $results.Replace('sr=b', 'sr=c') `
                -PackageUri $package
        } | Should -Throw '*exact permissions*'
        {
            Assert-ClusterResultsUri `
                -Value $results.Replace('/cluster-results/', '/other/') `
                -PackageUri $package
        } | Should -Throw '*cluster-results*'
    }

    It 'compares bootstrap and runtime endpoints without considering SAS values' {
        $first =
            'https://account123.blob.core.windows.net/packages/cluster_package.zip' +
            $delegationSas
        $renewed = $first.Replace('sig=test', 'sig=renewed')

        Test-SameClusterPackageEndpoint -First $first -Second $renewed |
            Should -BeTrue
        Test-SameClusterPackageEndpoint `
            -First $first `
            -Second $renewed.Replace('/packages/', '/different/') |
            Should -BeFalse
    }

    It 'uses the metadata ETag as an If-Match download condition and saves state after execution' {
        $content = Get-Content -LiteralPath $runnerPath -Raw

        (Get-Command Receive-ClusterPackage).Parameters.Keys |
            Should -Contain 'ExpectedETag'
        $ifMatchAssignment = [regex]::Escape(
            "`$request.Headers['If-Match'] = `$ExpectedETag"
        )
        $content | Should -Match $ifMatchAssignment
        $stagedHarnessIndex = $content.IndexOf(
            '[void](Assert-RunScriptSafety -Path $stagedRunScript)'
        )
        $installMoveIndex = $content.IndexOf('[IO.Directory]::Move($stagingPath')
        $runIndex = $content.IndexOf('$process = Invoke-ClusterPackageRun')
        $harnessIndex = $content.IndexOf('[void](Assert-RunScriptSafety -Path $runScript)')
        $startIndex = $content.IndexOf('return Start-Process', $harnessIndex)
        $completedStateIndex = $content.IndexOf('RunExitCode = if')
        $stagedHarnessIndex | Should -BeGreaterThan -1
        $installMoveIndex | Should -BeGreaterThan $stagedHarnessIndex
        $runIndex | Should -BeGreaterThan -1
        $harnessIndex | Should -BeGreaterThan -1
        $startIndex | Should -BeGreaterThan $harnessIndex
        $completedStateIndex | Should -BeGreaterThan $runIndex
        $content | Should -Match 'Initialize-SecureDirectory'
        $content | Should -Match 'Assert-NoReparsePointInPath'
        $content | Should -Match 'SetAccessRuleProtection\(\$true, \$false\)'
    }

    It 'prevents SAW deletion requests from targeting reserved deployment blobs' {
        $content = Get-Content -LiteralPath $sawPath -Raw
        $reservedDeletionGuard = [regex]::Escape(
            '(Test-SawInternalBlob -BlobPath $relativePath)'
        )

        $content | Should -Match $reservedDeletionGuard
    }
}
