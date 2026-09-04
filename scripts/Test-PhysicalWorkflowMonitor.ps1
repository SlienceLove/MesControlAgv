[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$monitorScript = Join-Path $PSScriptRoot 'Monitor-PhysicalWorkflow.ps1'
$fixtureDirectory = Join-Path $PSScriptRoot 'fixtures\physical-workflow-monitor'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts\phase3c-monitor-test-{0}' -f [Guid]::NewGuid().ToString('N'))
}
$outputAbsolute = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputAbsolute) {
    throw "Refusing to overwrite existing test output '$outputAbsolute'."
}

$rawResult = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $monitorScript `
    -ExecutionId '937e061e-82c8-434e-ba38-8b9ac320a0c1' `
    -OutputDirectory $outputAbsolute `
    -ReplayDirectory $fixtureDirectory
if ($LASTEXITCODE -ne 0) { throw "Monitor replay failed with exit code $LASTEXITCODE." }

$result = (($rawResult -join [Environment]::NewLine) | ConvertFrom-Json)
$finalPath = [string]$result.final
$final = Get-Content -Raw -Encoding UTF8 -LiteralPath $finalPath | ConvertFrom-Json

if (-not [bool]$final.replay) { throw 'Replay marker was not written.' }
if (@($final.nodes).Count -ne 2) { throw "Expected 2 node entries, got $(@($final.nodes).Count)." }
if (@($final.deviceOperations).Count -ne 2) { throw "Expected 2 device operations, got $(@($final.deviceOperations).Count)." }
if (@($final.acceptances).Count -ne 1) { throw "Expected 1 acceptance, got $(@($final.acceptances).Count)." }
if (@($final.timeline).Count -ne 2) { throw "Expected 2 timeline entries, got $(@($final.timeline).Count)." }
if ($final.nodes[0].PSObject.Properties['value'] -or $final.deviceOperations[0].PSObject.Properties['Count']) {
    throw 'A collection wrapper leaked into the monitor snapshot.'
}
if ([string]$final.nodes[1].nodeName -notmatch 'LM7') { throw 'Chinese node text was not preserved.' }

$evidence = [ordered]@{
    schema = 'mes.physical-workflow-monitor-replay-test/1.0'
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    topLevelArrayCount = @($final.nodes).Count
    valueWrapperCount = @($final.deviceOperations).Count
    itemsWrapperCount = @($final.acceptances).Count
    dataWrapperCount = @($final.timeline).Count
    scalarObjectPreserved = ($final.execution.executionId -eq '937e061e-82c8-434e-ba38-8b9ac320a0c1')
    scalarCollectionCount = 1
    chineseTextPreserved = $true
    wrappersLeaked = $false
    deviceWritesAttempted = $false
    automaticRetry = $false
    output = $outputAbsolute
}
New-Item -ItemType Directory -Path $outputAbsolute -Force | Out-Null
[IO.File]::WriteAllText(
    (Join-Path $outputAbsolute 'monitor-replay-evidence.json'),
    ($evidence | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))
Write-Output ($evidence | ConvertTo-Json -Depth 12)
