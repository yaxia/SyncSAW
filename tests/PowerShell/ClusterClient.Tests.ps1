Describe 'Windows PowerShell cluster client compatibility' {
    BeforeAll {
        $repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
        $implementationPath = Join-Path $repoRoot 'scripts\Sync-SAW.ps1'
        $clusterPath = Join-Path $repoRoot 'scripts\Sync.ps1'
        $installerPath = Join-Path $repoRoot 'scripts\Install-ClusterDependencies.ps1'
    }

    It 'parses every cluster script with Windows PowerShell 5.1' {
        $parserPath = Join-Path $TestDrive 'Test-Parse.ps1'
        @'
param([Parameter(Mandatory)][string]$RepoRoot)

$failed = $false
foreach ($relativePath in @(
    'scripts\Sync-SAW.ps1',
    'scripts\Sync.ps1',
    'scripts\Install-ClusterDependencies.ps1'
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

    It 'keeps the cluster entry point parameter-compatible with the shared implementation' {
        $clusterCommand = Get-Command $clusterPath
        $implementationCommand = Get-Command $implementationPath
        $commonParameters = [System.Management.Automation.PSCmdlet]::CommonParameters
        $clusterParameters = @($clusterCommand.Parameters.Keys | Where-Object {
            $_ -notin $commonParameters
        } | Sort-Object)
        $implementationParameters = @($implementationCommand.Parameters.Keys | Where-Object {
            $_ -notin $commonParameters
        } | Sort-Object)

        $clusterParameters | Should -Be $implementationParameters
    }

    It 'avoids PowerShell 7-only runtime APIs in the shared implementation' {
        $content = Get-Content -LiteralPath $implementationPath -Raw

        $content | Should -Not -Match 'ConvertFrom-Json\s+-AsHashtable'
        $content | Should -Not -Match '\[System\.IO\.Path\]::GetRelativePath'
        $content | Should -Not -Match '\[System\.Security\.Cryptography\.SHA256\]::HashData'
        $content | Should -Not -Match '\[System\.Convert\]::ToHexString'
        $content | Should -Not -Match 'Start-Transcript[^\r\n]+-UseMinimalHeader'
    }

    It 'runs a paused configured cycle successfully in Windows PowerShell 5.1' {
        $localFolder = Join-Path $TestDrive 'cluster-data'
        $logFolder = Join-Path $TestDrive 'logs'
        [void](New-Item -ItemType Directory -Path $localFolder, $logFolder -Force)
        $configuration = @{
            LocalFolder       = $localFolder
            StorageAccount    = 'clusterstorage'
            Container         = 'cluster-files'
            AuthenticationMode = 'AzurePowerShell'
            IntervalSeconds   = 10
            Continuous        = $false
            PauseSync         = $true
            PublishSyncFlags  = $true
            LogDirectory      = $logFolder
            TenantId          = '72f988bf-86f1-41af-91ab-2d7cd011db47'
            SubscriptionId    = 'a0d901ba-9956-4f7d-830c-2d7974c36666'
        }
        $configPath = Join-Path $TestDrive 'Sync.config.json'
        $configuration | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8

        $process = Start-Process `
            -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -ArgumentList @(
                '-NoLogo',
                '-NoProfile',
                '-NonInteractive',
                '-ExecutionPolicy',
                'Bypass',
                '-File',
                $clusterPath,
                '-ConfigPath',
                $configPath
            ) `
            -Wait `
            -PassThru

        $process.ExitCode | Should -Be 0
        Get-ChildItem -LiteralPath $logFolder -Filter 'sync-saw-*.log' |
            Should -HaveCount 1
    }
}
