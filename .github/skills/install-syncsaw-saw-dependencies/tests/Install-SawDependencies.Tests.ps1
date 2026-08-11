Describe 'Install-SawDependencies' {
    BeforeAll {
        $scriptPath = Join-Path $PSScriptRoot '..\scripts\Install-SawDependencies.ps1'
    }

    BeforeEach {
        $global:syncSawTestInstalledVersions = @{}

        Mock Get-Command {
            param($Name)

            return @($Name | ForEach-Object {
                [pscustomobject]@{ Name = [string]$_ }
            })
        }
        Mock Get-PSResourceRepository {
            [pscustomobject]@{
                Name     = 'TrustedRepo'
                Uri      = [uri]'https://packages.example.test/v3/index.json'
                Trusted  = $true
                Priority = 10
            }
        }
        Mock Get-PSRepository { return @() }
        Mock Find-PSResource {
            param($Name)

            [pscustomobject]@{
                Name    = $Name
                Version = if ($Name -eq 'Az.Accounts') {
                    [version]'5.5.0'
                } else {
                    [version]'9.4.0'
                }
            }
        }
        Mock Find-Module {
            param($Name)

            [pscustomobject]@{
                Name    = $Name
                Version = if ($Name -eq 'Az.Accounts') {
                    [version]'5.5.0'
                } else {
                    [version]'9.4.0'
                }
            }
        }
        Mock Get-Module {
            param($Name, $ListAvailable)

            if ($global:syncSawTestInstalledVersions.ContainsKey($Name)) {
                return [pscustomobject]@{
                    Name    = $Name
                    Version = $global:syncSawTestInstalledVersions[$Name]
                    Path    = "C:\Modules\$Name"
                    ModuleBase = "C:\Modules\$Name"
                }
            }

            return [pscustomobject]@{
                Name    = $Name
                Version = if ($Name -eq 'Az.Accounts') {
                    [version]'5.5.0'
                } else {
                    [version]'9.4.0'
                }
                Path    = "C:\Modules\$Name"
                ModuleBase = "C:\Modules\$Name"
            }
        }
        Mock Install-PSResource {
            param($Name, $Version, $Repository, $TrustRepository)
        }
        Mock Install-Module {
            param($Name, $MinimumVersion, $Repository)
        }
        Mock Import-Module {}
    }

    AfterEach {
        Remove-Variable syncSawTestInstalledVersions -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable syncSawTestModuleCalls -Scope Global -ErrorAction SilentlyContinue
    }

    It 'does not reinstall modules that satisfy the minimum versions' {
        & $scriptPath

        Should -Invoke Install-PSResource -Times 0 -Exactly
        Should -Invoke Install-Module -Times 0 -Exactly
        Should -Invoke Import-Module -Times 2 -Exactly
    }

    It 'installs missing modules through PSResourceGet' {
        $global:syncSawTestModuleCalls = @{}
        Mock Get-Module {
            param($Name, $ListAvailable)

            $moduleName = [string](@($Name)[0])
            $callCount = if ($global:syncSawTestModuleCalls.ContainsKey($moduleName)) {
                $global:syncSawTestModuleCalls[$moduleName]
            } else {
                0
            }
            $global:syncSawTestModuleCalls[$moduleName] = $callCount + 1
            if ($global:syncSawTestModuleCalls[$moduleName] -eq 1) {
                return @()
            }

            return [pscustomobject]@{
                Name    = $moduleName
                Version = if ($moduleName -eq 'Az.Accounts') {
                    [version]'5.5.0'
                } else {
                    [version]'9.4.0'
                }
                Path    = "C:\Modules\$moduleName"
                ModuleBase = "C:\Modules\$moduleName"
            }
        }

        & $scriptPath

        Should -Invoke Install-PSResource -Times 2 -Exactly
        Should -Invoke Install-Module -Times 0 -Exactly
        Should -Invoke Install-PSResource -Times 2 -Exactly -ParameterFilter {
            $Repository -eq 'TrustedRepo'
        }
    }

    It 'falls back to PowerShellGet when it is the available provider' {
        Mock Get-PSResourceRepository { return @() }
        Mock Get-PSRepository {
            [pscustomobject]@{
                Name               = 'LegacyRepo'
                SourceLocation     = 'https://packages.example.test/v2'
                InstallationPolicy = 'Trusted'
            }
        }
        $global:syncSawTestModuleCalls = @{}
        Mock Get-Module {
            param($Name, $ListAvailable)

            $moduleName = [string](@($Name)[0])
            $callCount = if ($global:syncSawTestModuleCalls.ContainsKey($moduleName)) {
                $global:syncSawTestModuleCalls[$moduleName]
            } else {
                0
            }
            $global:syncSawTestModuleCalls[$moduleName] = $callCount + 1
            if ($global:syncSawTestModuleCalls[$moduleName] -eq 1) {
                return @()
            }

            return [pscustomobject]@{
                Name    = $moduleName
                Version = if ($moduleName -eq 'Az.Accounts') {
                    [version]'5.5.0'
                } else {
                    [version]'9.4.0'
                }
                Path    = "C:\Modules\$moduleName"
                ModuleBase = "C:\Modules\$moduleName"
            }
        }

        & $scriptPath

        Should -Invoke Install-Module -Times 2 -Exactly
        Should -Invoke Install-PSResource -Times 0 -Exactly
        Should -Invoke Install-Module -Times 2 -Exactly -ParameterFilter {
            $Repository -eq 'LegacyRepo'
        }
    }

    It 'rejects execution when no module repository is registered' {
        Mock Get-PSResourceRepository { return @() }
        Mock Get-PSRepository { return @() }

        { & $scriptPath } | Should -Throw '*No supported PowerShell module repository is registered*'
        Should -Invoke Install-PSResource -Times 0 -Exactly
        Should -Invoke Install-Module -Times 0 -Exactly
    }

    It 'rejects an untrusted repository unless permission is explicit' {
        Mock Get-PSResourceRepository {
            [pscustomobject]@{
                Name     = 'UntrustedRepo'
                Uri      = [uri]'https://packages.example.test/v3/index.json'
                Trusted  = $false
                Priority = 10
            }
        }

        { & $scriptPath } | Should -Throw '*-AllowUntrustedRepository*'
        Should -Invoke Find-PSResource -Times 0 -Exactly
        Should -Invoke Install-PSResource -Times 0 -Exactly
    }

    It 'uses operation-scoped trust after an untrusted repository is approved' {
        Mock Get-PSResourceRepository {
            [pscustomobject]@{
                Name     = 'ApprovedRepo'
                Uri      = [uri]'https://packages.example.test/v3/index.json'
                Trusted  = $false
                Priority = 10
            }
        }
        $global:syncSawTestModuleCalls = @{}
        Mock Get-Module {
            param($Name, $ListAvailable)

            $moduleName = [string](@($Name)[0])
            $callCount = if ($global:syncSawTestModuleCalls.ContainsKey($moduleName)) {
                $global:syncSawTestModuleCalls[$moduleName]
            } else {
                0
            }
            $global:syncSawTestModuleCalls[$moduleName] = $callCount + 1
            if ($global:syncSawTestModuleCalls[$moduleName] -eq 1) {
                return @()
            }

            return [pscustomobject]@{
                Name       = $moduleName
                Version    = if ($moduleName -eq 'Az.Accounts') {
                    [version]'5.5.0'
                } else {
                    [version]'9.4.0'
                }
                Path       = "C:\Modules\$moduleName"
                ModuleBase = "C:\Modules\$moduleName"
            }
        }

        & $scriptPath -AllowUntrustedRepository

        Should -Invoke Find-PSResource -Times 2 -Exactly
        Should -Invoke Install-PSResource -Times 2 -Exactly -ParameterFilter {
            $TrustRepository
        }
    }

    It 'rejects a registered repository that does not use HTTPS' {
        Mock Get-PSResourceRepository {
            [pscustomobject]@{
                Name     = 'InsecureRepo'
                Uri      = [uri]'http://packages.example.test/v3/index.json'
                Trusted  = $true
                Priority = 10
            }
        }

        { & $scriptPath } | Should -Throw '*valid HTTPS source URI*'
        Should -Invoke Find-PSResource -Times 0 -Exactly
        Should -Invoke Install-PSResource -Times 0 -Exactly
    }

    It 'uses the explicitly selected repository' {
        Mock Get-PSResourceRepository {
            @(
                [pscustomobject]@{
                    Name     = 'FirstRepo'
                    Uri      = [uri]'https://first.example.test/v3/index.json'
                    Trusted  = $true
                    Priority = 10
                },
                [pscustomobject]@{
                    Name     = 'SelectedRepo'
                    Uri      = [uri]'https://selected.example.test/v3/index.json'
                    Trusted  = $true
                    Priority = 20
                }
            )
        }
        $global:syncSawTestModuleCalls = @{}
        Mock Get-Module {
            param($Name, $ListAvailable)

            $moduleName = [string](@($Name)[0])
            $callCount = if ($global:syncSawTestModuleCalls.ContainsKey($moduleName)) {
                $global:syncSawTestModuleCalls[$moduleName]
            } else {
                0
            }
            $global:syncSawTestModuleCalls[$moduleName] = $callCount + 1
            if ($global:syncSawTestModuleCalls[$moduleName] -eq 1) {
                return @()
            }

            return [pscustomobject]@{
                Name    = $moduleName
                Version = if ($moduleName -eq 'Az.Accounts') {
                    [version]'5.5.0'
                } else {
                    [version]'9.4.0'
                }
                Path    = "C:\Modules\$moduleName"
                ModuleBase = "C:\Modules\$moduleName"
            }
        }

        & $scriptPath -Repository SelectedRepo

        Should -Invoke Install-PSResource -Times 2 -Exactly -ParameterFilter {
            $Repository -eq 'SelectedRepo'
        }
    }

    It 'does not install or import in WhatIf mode' {
        Mock Get-Module { return @() }

        & $scriptPath -WhatIf

        Should -Invoke Install-PSResource -Times 0 -Exactly
        Should -Invoke Install-Module -Times 0 -Exactly
        Should -Invoke Import-Module -Times 0 -Exactly
    }
}
