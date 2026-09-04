[CmdletBinding()]
param(
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$templatePath = Join-Path $repoRoot 'artifacts\physical-acceptance\phase2-std-20260904-093754\wpf-workflows.json'
$importScript = Join-Path $PSScriptRoot 'Import-WorkflowGraph.ps1'
$httpScript = Join-Path $PSScriptRoot 'Invoke-MesJsonUtf8.ps1'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot ('artifacts\phase3b-workflow-tooling-test-{0}' -f [Guid]::NewGuid().ToString('N'))
}
$outputAbsolute = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputAbsolute) {
    throw "Refusing to overwrite existing test output '$outputAbsolute'."
}
New-Item -ItemType Directory -Path $outputAbsolute -Force | Out-Null

if (-not (Test-Path -LiteralPath $templatePath -PathType Leaf)) {
    throw "The saved UTF-8 graph fixture was not found at '$templatePath'."
}

# Invoke the importer in a child Windows PowerShell process with an explicit
# execution-policy bypass. This mirrors the field operator's PS5.1 boundary,
# while keeping the test itself independent of the caller's policy.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $importScript `
    -TemplatePath $templatePath `
    -OutputDirectory $outputAbsolute `
    -Actor 'offline-tooling-test' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Import-WorkflowGraph.ps1 failed with exit code $LASTEXITCODE."
}

$definitionPath = Join-Path $outputAbsolute 'workflow-definition-request.json'
$summaryPath = Join-Path $outputAbsolute 'workflow-import-summary.json'
$definitionRaw = [IO.File]::ReadAllText(
    $definitionPath,
    (New-Object System.Text.UTF8Encoding($false, $true)))
$definition = $definitionRaw | ConvertFrom-Json
$summary = Get-Content -Raw -Encoding UTF8 $summaryPath | ConvertFrom-Json
$nodes = @($definition.nodes)
$edges = @($definition.edges)
$definitionBytes = [IO.File]::ReadAllBytes($definitionPath)

if ($nodes.Count -ne 9 -or $edges.Count -ne 8) {
    throw "Imported definition has $($nodes.Count) nodes and $($edges.Count) edges; expected 9/8."
}
$startParameters = @($nodes[0].parameters)
if ($startParameters.Count -ne 0) {
    throw 'An empty graph-node parameter collection was serialized as a phantom parameter.'
}
if (@($nodes[0].ports).Count -eq 0 -or @($nodes[1].nextNodeIds).Count -eq 0) {
    throw 'Imported graph arrays (ports/nextNodeIds) were not preserved.'
}
$uniqueNodeIds = @($nodes | ForEach-Object { $_.id } | Select-Object -Unique)
if ($uniqueNodeIds.Count -ne $nodes.Count) {
    throw 'Imported node ids are not unique.'
}
$uniqueEdgeIds = @($edges | ForEach-Object { $_.id } | Select-Object -Unique)
if ($uniqueEdgeIds.Count -ne $edges.Count) {
    throw 'Imported edge ids are not unique.'
}
$hasFirstChineseName = $definitionRaw -match '\u53D6\u6599\u76D8'
$hasSecondChineseName = $definitionRaw -match '\u56DE\u6536\u6599\u76D8'
$hasRequiredChineseNames = $hasFirstChineseName -and $hasSecondChineseName
if (-not $hasRequiredChineseNames) {
    throw 'The imported UTF-8 definition did not preserve Chinese program names.'
}
if ($definitionRaw -match '(?i)/Date\(') {
    throw 'The imported definition contains a legacy .NET date wrapper.'
}
if ($definitionBytes.Length -ge 3 -and
    $definitionBytes[0] -eq 0xEF -and
    $definitionBytes[1] -eq 0xBB -and
    $definitionBytes[2] -eq 0xBF) {
    throw 'The imported definition unexpectedly contains a UTF-8 BOM.'
}
if ([bool]$summary.writesToDevicesAttempted -or [bool]$summary.publicationAttempted) {
    throw 'Offline import unexpectedly attempted publication or device writes.'
}

$httpSource = Get-Content -Raw -Encoding UTF8 $httpScript
foreach ($required in @('HttpClient', 'ByteArrayContent', 'Encoding]::UTF8', 'SendAsync')) {
    if ($httpSource -notmatch [regex]::Escape($required)) {
        throw "UTF-8 HTTP helper is missing required marker '$required'."
    }
}
if ($httpSource -match 'Invoke-WebRequest|Invoke-RestMethod') {
    throw 'UTF-8 HTTP helper must not delegate to PowerShell web cmdlets.'
}

$evidence = [ordered]@{
    schema = 'mes.field-workflow-tooling-test/1.0'
    verifiedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    templatePath = '[REDACTED]'
    outputDirectory = '[REDACTED]'
    nodes = $nodes.Count
    edges = $edges.Count
    chinesePreserved = $true
    uniqueIds = $true
    legacyDateWrapper = $false
    publicationAttempted = [bool]$summary.publicationAttempted
    deviceWritesAttempted = [bool]$summary.writesToDevicesAttempted
    automaticRetry = $false
}
[IO.File]::WriteAllText(
    (Join-Path $outputAbsolute 'tooling-test-evidence.json'),
    ($evidence | ConvertTo-Json -Depth 12),
    [Text.UTF8Encoding]::new($false))

Write-Output ($evidence | ConvertTo-Json -Depth 12)
