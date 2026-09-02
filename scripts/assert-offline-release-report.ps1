[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) { throw $Message }
}

$fullPath = [IO.Path]::GetFullPath($Path)
Assert-Condition (Test-Path -LiteralPath $fullPath -PathType Leaf) 'Offline release report does not exist.'

$raw = Get-Content -Raw -LiteralPath $fullPath -Encoding UTF8
$report = $raw | ConvertFrom-Json

Assert-Condition ($report.schemaVersion -eq 'mes.offline-release-gate/1.0') 'Unexpected offline release report schema.'
Assert-Condition ($report.repository -eq '[REDACTED]') 'Repository identity is not redacted.'
Assert-Condition ([bool]$report.gatePassed) 'Offline release gate did not pass.'
Assert-Condition ([bool]$report.releaseEligible) 'Report is not release eligible.'
Assert-Condition ([bool]$report.tests.executed) 'Solution tests were not executed.'
Assert-Condition ([int]$report.tests.failed -eq 0) 'Solution tests contain failures.'
Assert-Condition ([int]$report.tests.total -gt 0) 'Solution test count is empty.'
Assert-Condition ([int]$report.tests.unexpectedSkipped -eq 0) 'Unexpected skipped tests are present.'

$fieldPreflight = $report.diagnosticSchemas.fieldPreflight
Assert-Condition ($fieldPreflight -eq 'mes.field-preflight/1.0') 'Field preflight schema is missing or unsupported.'
Assert-Condition ($report.diagnosticSchemas.diff -eq 'mes.offline-diagnostic-diff/1.0') 'Configuration diff schema is missing or unsupported.'

# A CI archive is a summary contract, never a transport for a raw test log or
# a field release decision. Keep this check deliberately textual so it also
# catches unexpected future properties before upload.
Assert-Condition ($raw -notmatch '(?i)"(rawLogs?|rawOutput|trx|testLog|repositoryPath)"\s*:') 'Raw test/log properties are not allowed in the archive.'
Assert-Condition ($raw -notmatch '(?i)(?:password|pwd|token|secret|authorization|api[-_]?key)\s*[:=]\s*(?!"?\[REDACTED\])') 'A credential-like value is present in the archive.'
Assert-Condition ($raw -notmatch '(?i)\b[A-Za-z]:\\|\\\\[^\r\n,;\s]+') 'A local or UNC path is present in the archive.'
Assert-Condition ($raw -notmatch '(?<!\d)(?!(?:127\.0\.0\.1|0\.0\.0\.0)(?!\d))(?:\d{1,3}\.){3}\d{1,3}(?!\d)') 'A non-loopback IP address is present in the archive.'

Assert-Condition ($null -ne $report.fieldPreflight) 'Field preflight summary is missing from the archive.'
Assert-Condition ($report.fieldPreflight.schemaVersion -eq 'mes.field-preflight/1.0') 'Field preflight summary has an unsupported schema.'
Assert-Condition (-not [bool]$report.fieldPreflight.canDetermineGo) 'Field preflight archive must never determine field GO.'

if ($null -ne $report.configurationDiff) {
    Assert-Condition ($report.configurationDiff.schemaVersion -eq 'mes.offline-diagnostic-diff/1.0') 'Configuration diff has an unsupported schema.'
    Assert-Condition (-not [bool]$report.configurationDiff.canDetermineGo) 'Configuration diff must never determine field GO.'
}

Write-Host "Offline release report boundary passed: $([IO.Path]::GetFileName($fullPath))"
