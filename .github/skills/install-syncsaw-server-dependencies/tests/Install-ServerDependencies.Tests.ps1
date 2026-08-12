Describe 'Install-ServerDependencies' {
    BeforeAll {
        $scriptPath = Join-Path `
            $PSScriptRoot `
            '..\scripts\Install-ServerDependencies.ps1'
    }

    BeforeEach {
        $testRoot = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        [void](New-Item -ItemType Directory -Path $testRoot)
        $dotnetPath = Join-Path $testRoot 'dotnet.cmd'
        $azureCliPath = Join-Path $testRoot 'az.cmd'
        $azCopyPath = Join-Path $testRoot 'azcopy.cmd'
        $wingetPath = Join-Path $testRoot 'winget.cmd'
        $markerPath = Join-Path $testRoot 'ready.flag'
        $argumentsPath = Join-Path $testRoot 'winget-arguments.txt'

        Set-Content -LiteralPath $dotnetPath -Encoding Ascii -Value @"
@echo off
if not exist "$markerPath" exit /b 1
echo Microsoft.WindowsDesktop.App 8.0.30 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]
"@
        Set-Content -LiteralPath $azureCliPath -Encoding Ascii -Value @"
@echo off
if not exist "$markerPath" exit /b 1
echo {"azure-cli":"2.89.0"}
"@
        Set-Content -LiteralPath $azCopyPath -Encoding Ascii -Value @"
@echo off
if not exist "$markerPath" exit /b 1
echo azcopy version 10.32.6
"@
        Set-Content -LiteralPath $wingetPath -Encoding Ascii -Value @"
@echo off
echo %*>>"$argumentsPath"
if "%1"=="source" (
  echo {"Arg":"https://cdn.winget.microsoft.com/cache","Name":"winget","TrustLevel":["Trusted"]}
  exit /b 0
)
if "%1"=="install" (
  type nul >"$markerPath"
  exit /b 0
)
exit /b 1
"@
    }

    It 'reports readiness without invoking WinGet when all prerequisites satisfy minimums' {
        Set-Content -LiteralPath $markerPath -Value 'ready'

        & $scriptPath `
            -DotNetPath $dotnetPath `
            -AzureCliPath $azureCliPath `
            -AzCopyPath $azCopyPath `
            -WingetPath $wingetPath

        Test-Path -LiteralPath $argumentsPath | Should -BeFalse
    }

    It 'installs all missing prerequisites using exact official package IDs' {
        & $scriptPath `
            -DotNetPath $dotnetPath `
            -AzureCliPath $azureCliPath `
            -AzCopyPath $azCopyPath `
            -WingetPath $wingetPath

        $arguments = Get-Content -LiteralPath $argumentsPath
        @($arguments | Where-Object { $_ -like 'install *' }).Count | Should -Be 3
        $arguments | Should -Contain (
            'install --id Microsoft.DotNet.DesktopRuntime.8 --exact --source ' +
            'winget --silent --accept-package-agreements --accept-source-agreements ' +
            '--disable-interactivity'
        )
        $arguments | Should -Contain (
            'install --id Microsoft.AzureCLI --exact --source winget --silent ' +
            '--accept-package-agreements --accept-source-agreements ' +
            '--disable-interactivity'
        )
        $arguments | Should -Contain (
            'install --id Microsoft.Azure.AZCopy.10 --exact --source winget --silent ' +
            '--accept-package-agreements --accept-source-agreements ' +
            '--disable-interactivity'
        )
    }

    It 'does not install packages in WhatIf mode' {
        & $scriptPath `
            -DotNetPath $dotnetPath `
            -AzureCliPath $azureCliPath `
            -AzCopyPath $azCopyPath `
            -WingetPath $wingetPath `
            -WhatIf

        $arguments = Get-Content -LiteralPath $argumentsPath
        @($arguments | Where-Object { $_ -like 'install *' }).Count | Should -Be 0
        Test-Path -LiteralPath $markerPath | Should -BeFalse
    }

    It 'rejects a WinGet source that is not the official Microsoft HTTPS source' {
        Set-Content -LiteralPath $wingetPath -Encoding Ascii -Value @"
@echo off
if "%1"=="source" (
  echo {"Arg":"https://packages.example.test/cache","Name":"winget","TrustLevel":["Trusted"]}
  exit /b 0
)
exit /b 1
"@

        {
            & $scriptPath `
                -DotNetPath $dotnetPath `
                -AzureCliPath $azureCliPath `
                -AzCopyPath $azCopyPath `
                -WingetPath $wingetPath
        } | Should -Throw '*not Microsoft*expected HTTPS source*'
    }

    It 'fails clearly when WinGet is unavailable and installation is required' {
        {
            & $scriptPath `
                -DotNetPath $dotnetPath `
                -AzureCliPath $azureCliPath `
                -AzCopyPath $azCopyPath `
                -WingetPath (Join-Path $testRoot 'missing-winget.exe')
        } | Should -Throw '*WinGet is required*Microsoft App Installer*'
    }
}

