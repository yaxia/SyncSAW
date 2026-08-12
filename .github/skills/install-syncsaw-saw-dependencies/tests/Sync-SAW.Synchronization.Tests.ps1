Describe 'Sync-SAW cloud update detection' {
    BeforeAll {
        $syncScriptPath = Join-Path $PSScriptRoot '..\..\..\..\scripts\Sync-SAW.ps1'
        $tokens = $null
        $parseErrors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile(
            (Resolve-Path $syncScriptPath),
            [ref]$tokens,
            [ref]$parseErrors)
        if ($parseErrors.Count -gt 0) {
            throw 'Sync-SAW.ps1 could not be parsed for tests.'
        }

        foreach ($functionName in @(
            'Get-SyncSawSha256Hex',
            'New-RecordDictionary',
            'Test-RemoteDownloadRequired',
            'Get-SawMarkerPath',
            'Publish-SawSyncFlags'
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

        $script:MarkerPrefix = '.syncsaw/saw-flags/'
        Set-Item -Path 'Function:\global:Invoke-SawStorageOperation' -Value {
            param($Description, $Operation, [switch]$IgnoreNotFound)
            $global:syncSawStorageDescriptions.Add($Description)
        }
        Set-Item -Path 'Function:\global:Get-RemoteBlobRecords' -Value {
            return @($global:syncSawLatestRemoteFiles)
        }
    }

    AfterAll {
        Remove-Item -Path 'Function:\global:Invoke-SawStorageOperation' -ErrorAction SilentlyContinue
        Remove-Item -Path 'Function:\global:Get-RemoteBlobRecords' -ErrorAction SilentlyContinue
        Remove-Variable syncSawStorageDescriptions -Scope Global -ErrorAction SilentlyContinue
        Remove-Variable syncSawLatestRemoteFiles -Scope Global -ErrorAction SilentlyContinue
    }

    It 'downloads a same-size cloud update even within the former two-second tolerance' {
        $localTime = [DateTimeOffset]::Parse('2026-08-12T05:00:00.0000000Z')
        $local = [pscustomobject]@{
            Length           = 40KB
            LastWriteTimeUtc = $localTime
        }
        $remote = [pscustomobject]@{
            Length       = 40KB
            LastModified = $localTime.AddMilliseconds(250)
        }

        Test-RemoteDownloadRequired -Remote $remote -Local $local | Should -BeTrue
    }

    It 'does not download again after size and exact timestamp match' {
        $timestamp = [DateTimeOffset]::Parse('2026-08-12T05:00:00.1234567Z')
        $local = [pscustomobject]@{
            Length           = 40KB
            LastWriteTimeUtc = $timestamp
        }
        $remote = [pscustomobject]@{
            Length       = 40KB
            LastModified = $timestamp
        }

        Test-RemoteDownloadRequired -Remote $remote -Local $local | Should -BeFalse
    }

    It 'does not republish a marker while the local copy is stale' {
        $path = 'DiskIOTest/Sync-SAW.ps1'
        $localTime = [DateTimeOffset]::Parse('2026-08-12T05:00:00Z')
        $markerPath = Get-SawMarkerPath -RelativePath $path
        $local = [pscustomobject]@{
            RelativePath     = $path
            Length           = 40960
            LastWriteTimeUtc = $localTime
        }
        $source = [pscustomobject]@{
            Name         = $path
            Length       = 40960
            LastModified = $localTime.AddSeconds(1)
        }
        $marker = [pscustomobject]@{
            Name         = $markerPath
            Length       = 0
            LastModified = $source.LastModified.AddSeconds(1)
        }

        $global:syncSawStorageDescriptions =
            [System.Collections.Generic.List[string]]::new()
        $global:syncSawLatestRemoteFiles = @($source)

        Publish-SawSyncFlags `
            -LocalFiles @($local) `
            -RemoteFiles @($source, $marker) `
            -ContainerName 'syncsaw' `
            -Context ([pscustomobject]@{})

        @($global:syncSawStorageDescriptions | Where-Object {
            $_ -like 'Publishing SAW marker*'
        }).Count | Should -Be 0
        @($global:syncSawStorageDescriptions | Where-Object {
            $_ -eq "Deleting stale SAW marker '$markerPath'"
        }).Count | Should -Be 1
    }

    It 'removes a marker when the source changes during publication' {
        $path = 'DiskIOTest/Sync-SAW.ps1'
        $localTime = [DateTimeOffset]::Parse('2026-08-12T05:00:00Z')
        $markerPath = Get-SawMarkerPath -RelativePath $path
        $local = [pscustomobject]@{
            RelativePath     = $path
            Length           = 40960
            LastWriteTimeUtc = $localTime
        }
        $initialSource = [pscustomobject]@{
            Name         = $path
            Length       = 40960
            LastModified = $localTime
        }
        $updatedSource = [pscustomobject]@{
            Name         = $path
            Length       = 40960
            LastModified = $localTime.AddMilliseconds(500)
        }
        $newMarker = [pscustomobject]@{
            Name         = $markerPath
            Length       = 0
            LastModified = $updatedSource.LastModified.AddMilliseconds(100)
        }

        $global:syncSawStorageDescriptions =
            [System.Collections.Generic.List[string]]::new()
        $global:syncSawLatestRemoteFiles = @($updatedSource, $newMarker)

        Publish-SawSyncFlags `
            -LocalFiles @($local) `
            -RemoteFiles @($initialSource) `
            -ContainerName 'syncsaw' `
            -Context ([pscustomobject]@{})

        @($global:syncSawStorageDescriptions | Where-Object {
            $_ -eq "Publishing SAW marker '$markerPath'"
        }).Count | Should -Be 1
        @($global:syncSawStorageDescriptions | Where-Object {
            $_ -eq "Deleting stale SAW marker '$markerPath'"
        }).Count | Should -Be 1
    }
}
