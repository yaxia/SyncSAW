Describe 'Sync-SAW SAS container validation' {
    BeforeAll {
        $scriptPath = Join-Path $PSScriptRoot '..\..\scripts\Sync-SAW.ps1'
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $scriptPath),
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw 'Sync-SAW.ps1 could not be parsed for tests.'
        }

        foreach ($functionName in @(
            'Get-SawErrorText',
            'Test-SawNotFoundError',
            'Initialize-BlobContainer'
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

        Set-Item -Path 'Function:\Invoke-SawStorageOperation' -Value {
            param($Description, $Operation)
            & $Operation
        }
        $script:testStorageContext = New-AzStorageContext `
            -StorageAccountName 'validationaccount' `
            -Anonymous
    }

    AfterAll {
        Remove-Item -Path 'Function:\Invoke-SawStorageOperation' -ErrorAction SilentlyContinue
    }

    BeforeEach {
        Mock Get-AzStorageBlob { return @() }
        Mock Get-AzStorageContainer { return [pscustomobject]@{ Name = 'syncsaw' } }
        Mock New-AzStorageContainer {}
    }

    It 'checks an existing container SAS by listing blobs inside the container' {
        Initialize-BlobContainer `
            -Name 'syncsaw' `
            -Context $script:testStorageContext `
            -AuthenticationMode Sas

        Should -Invoke Get-AzStorageBlob -Times 1 -Exactly -ParameterFilter {
            $Container -eq 'syncsaw' -and $MaxCount -eq 1
        }
        Should -Invoke Get-AzStorageContainer -Times 0 -Exactly
        Should -Invoke New-AzStorageContainer -Times 0 -Exactly
    }

    It 'retains account-level validation for Entra authentication' {
        Initialize-BlobContainer `
            -Name 'syncsaw' `
            -Context $script:testStorageContext `
            -AuthenticationMode AzurePowerShell

        Should -Invoke Get-AzStorageContainer -Times 1 -Exactly -ParameterFilter {
            $Name -eq 'syncsaw'
        }
        Should -Invoke Get-AzStorageBlob -Times 0 -Exactly
    }
}
