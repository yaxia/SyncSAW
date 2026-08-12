Describe 'Sync-SAW authentication recovery' {
    BeforeAll {
        $syncScriptPath = Join-Path $PSScriptRoot '..\..\..\..\scripts\Sync-SAW.ps1'
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $syncScriptPath),
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw "Sync-SAW.ps1 could not be parsed for tests."
        }

        AfterEach {
            Remove-Variable syncSawAuthTestContext -Scope Global -ErrorAction SilentlyContinue
        }

        foreach ($functionName in @(
            'Get-SawErrorText',
            'Test-SawAzureContext',
            'New-SawStorageContext',
            'Test-SawAuthenticationError'
        )) {
            $definition = $ast.FindAll({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                $node.Name -eq $functionName
            }, $true) | Select-Object -First 1
            if ($null -eq $definition) {
                throw "Function '$functionName' was not found in Sync-SAW.ps1."
            }

            $bodyText = $definition.Body.Extent.Text
            $body = [scriptblock]::Create(
                $bodyText.Substring(1, $bodyText.Length - 2))
            Set-Item -Path "Function:\$functionName" -Value $body
        }
    }

    It 'recognizes expired and interaction-required credential failures' {
        $messages = @(
            'HTTP 401 AuthenticationFailed: Server failed to authenticate the request.',
            'AADSTS50173: The provided grant has expired due to it being revoked.',
            'AADSTS700082: The refresh token has expired due to inactivity.',
            'MsalUiRequiredException: InteractionRequired',
            'interaction_required: user interaction is required',
            'The refresh token has expired due to sign-in frequency policy.'
        )

        foreach ($message in $messages) {
            $record = [System.Management.Automation.ErrorRecord]::new(
                [System.Exception]::new($message),
                'AuthenticationTest',
                [System.Management.Automation.ErrorCategory]::AuthenticationError,
                $null)

            Test-SawAuthenticationError -ErrorRecord $record | Should -BeTrue
        }
    }

    It 'does not treat an RBAC authorization failure as an expired credential' {
        $record = [System.Management.Automation.ErrorRecord]::new(
            [System.Exception]::new(
                'HTTP 403 AuthorizationPermissionMismatch: This request is not authorized.'),
            'AuthorizationTest',
            [System.Management.Automation.ErrorCategory]::PermissionDenied,
            $null)

        Test-SawAuthenticationError -ErrorRecord $record | Should -BeFalse
    }

    It 'forces interactive sign-in instead of testing stale cached contexts' {
        $tenant = '72f988bf-86f1-41af-91ab-2d7cd011db47'
        $subscription = 'a0d901ba-9956-4f7d-830c-2d7974c36666'
        $global:syncSawAuthTestContext = [pscustomobject]@{
            Tenant       = [pscustomobject]@{ Id = $tenant }
            Subscription = [pscustomobject]@{ Id = $subscription }
            Account      = [pscustomobject]@{ Id = 'user@example.test' }
        }

        Mock Enable-AzContextAutosave {}
        Mock Get-AzContext { return $global:syncSawAuthTestContext }
        Mock Get-AzAccessToken { return [pscustomobject]@{ Token = 'token' } }
        Mock Connect-AzAccount {}
        Mock Set-AzContext {}
        Mock New-AzStorageContext {
            return [pscustomobject]@{ Refreshed = $true }
        }

        $result = New-SawStorageContext `
            -Account 'account123' `
            -AuthMode 'AzurePowerShell' `
            -Tenant $tenant `
            -Subscription $subscription `
            -ForceInteractiveLogin

        $result.Refreshed | Should -BeTrue
        Should -Invoke Connect-AzAccount -Times 1 -Exactly -ParameterFilter {
            $Force -eq $true -and
            $Tenant -eq $tenant -and
            $Subscription -eq $subscription
        }
        Should -Invoke Get-AzAccessToken -Times 1 -Exactly
        Should -Invoke New-AzStorageContext -Times 1 -Exactly -ParameterFilter {
            $StorageAccountName -eq 'account123' -and $UseConnectedAccount
        }
    }

    It 'continues to reuse a valid cached context during normal startup' {
        $tenant = '72f988bf-86f1-41af-91ab-2d7cd011db47'
        $subscription = 'a0d901ba-9956-4f7d-830c-2d7974c36666'
        $global:syncSawAuthTestContext = [pscustomobject]@{
            Tenant       = [pscustomobject]@{ Id = $tenant }
            Subscription = [pscustomobject]@{ Id = $subscription }
            Account      = [pscustomobject]@{ Id = 'user@example.test' }
        }

        Mock Enable-AzContextAutosave {}
        Mock Get-AzContext { return $global:syncSawAuthTestContext }
        Mock Get-AzAccessToken { return [pscustomobject]@{ Token = 'token' } }
        Mock Connect-AzAccount {}
        Mock Set-AzContext {}
        Mock New-AzStorageContext {
            return [pscustomobject]@{ Cached = $true }
        }

        $result = New-SawStorageContext `
            -Account 'account123' `
            -AuthMode 'AzurePowerShell' `
            -Tenant $tenant `
            -Subscription $subscription

        $result.Cached | Should -BeTrue
        Should -Invoke Connect-AzAccount -Times 0 -Exactly
        Should -Invoke Get-AzAccessToken -Times 1 -Exactly
    }
}
