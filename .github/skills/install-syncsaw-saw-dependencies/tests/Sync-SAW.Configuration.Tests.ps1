Describe 'Sync-SAW configuration compatibility' {
    BeforeAll {
        $syncScriptPath = Join-Path $PSScriptRoot '..\..\..\..\scripts\Sync-SAW.ps1'
        $tokens = $null
        $parseErrors = $null
        $script:syncSawConfigurationAst = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $syncScriptPath),
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw 'Sync-SAW.ps1 could not be parsed for tests.'
        }

        $definition = $script:syncSawConfigurationAst.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq 'Import-SyncSawConfiguration'
        }, $true) | Select-Object -First 1
        if ($null -eq $definition) {
            throw "Function 'Import-SyncSawConfiguration' was not found in Sync-SAW.ps1."
        }

        $bodyText = $definition.Body.Extent.Text
        $body = [scriptblock]::Create($bodyText.Substring(1, $bodyText.Length - 2))
        Set-Item -Path 'Function:\Import-SyncSawConfiguration' -Value $body
    }

    It 'does not expose the removed DeletionMode command-line parameter' {
        $parameterNames = @(
            $script:syncSawConfigurationAst.ParamBlock.Parameters |
                ForEach-Object { $_.Name.VariablePath.UserPath }
        )

        $parameterNames | Should -Not -Contain 'DeletionMode'
    }

    It 'silently migrates an existing false DeletionMode configuration value' {
        $path = Join-Path $TestDrive 'legacy-false.json'
        Set-Content -LiteralPath $path -Value '{"DeletionMode":false,"Continuous":true}'

        $configuration = Import-SyncSawConfiguration -Path $path -PathWasExplicit $true

        $configuration.ContainsKey('DeletionMode') | Should -BeFalse
        $configuration.Continuous | Should -BeTrue
    }

    It 'rejects an existing true DeletionMode configuration value' {
        $path = Join-Path $TestDrive 'legacy-true.json'
        Set-Content -LiteralPath $path -Value '{"DeletionMode":true}'

        {
            Import-SyncSawConfiguration -Path $path -PathWasExplicit $true
        } | Should -Throw '*DeletionMode has been removed*'
    }
}
