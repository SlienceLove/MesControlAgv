[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$OutputPath,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$NoRestore,
    [switch]$DisableBuildServers
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$checks = [System.Collections.Generic.List[object]]::new()
$testSummary = [ordered]@{
    executed = $false
    passed = 0
    failed = 0
    skipped = 0
    total = 0
}
$testExitCode = $null
$knownAllowedSkipCount = 0

function Add-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Detail
    )

    $checks.Add([pscustomobject]@{
        name = $Name
        passed = $Passed
        detail = $Detail
    })
}

function Redact-OfflineText {
    param([AllowNull()][string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    $redacted = $Value
    $redacted = [regex]::Replace(
        $redacted,
        '(?i)\b(password|pwd|token|secret|authorization|api[-_]?key)\b\s*[:=]\s*(?:"[^"]*"|''[^'']*''|[^\s,;]+)',
        '$1=[REDACTED]')
    $redacted = [regex]::Replace($redacted, '(?i)https?://[^\s"'']+', '[URL]')
    $redacted = [regex]::Replace($redacted, '(?i)\b(host|hostname|address|endpoint)\b\s*[:=]\s*(?:"[^"]*"|''[^'']*''|[^\s,;]+)', '$1=[REDACTED]')
    $redacted = [regex]::Replace($redacted, '"[A-Za-z]:\\[^"\r\n]+"', '"[PATH]"')
    $redacted = [regex]::Replace($redacted, '\b[A-Za-z]:\\[^\r\n,;\s]+', '[PATH]')
    $redacted = [regex]::Replace($redacted, '\\\\[^\r\n,;\s]+', '[PATH]')
    $redacted = [regex]::Replace($redacted, '\b(?:\d{1,3}\.){3}\d{1,3}\b', '[IP]')
    $redacted = [regex]::Replace($redacted, '(?i)\bCOM\d+\b', '[PORT]')
    return $redacted
}

function Get-TestSummary {
    param([object[]]$NativeOutput)

    $summary = [ordered]@{
        executed = $true
        passed = 0
        failed = 0
        skipped = 0
        total = 0
    }
    $text = (@($NativeOutput | ForEach-Object { $_.ToString() }) -join "`n")
    $pattern = '(?:失败|Failed):\s*(\d+).*?(?:通过|Passed):\s*(\d+).*?(?:跳过|Skipped):\s*(\d+).*?(?:总计|Total):\s*(\d+)'
    foreach ($match in [regex]::Matches($text, $pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        $summary.failed += [int]$match.Groups[1].Value
        $summary.passed += [int]$match.Groups[2].Value
        $summary.skipped += [int]$match.Groups[3].Value
        $summary.total += [int]$match.Groups[4].Value
    }
    if ($summary.total -eq 0) {
        # Nested Windows PowerShell can decode dotnet's localized labels with a
        # different code page. The summary line still has four colon-separated
        # numeric fields, so use that shape as a locale-independent fallback.
        $fallbackPattern = '(?m)^[^\r\n]*:\s*(\d+)\s*[,，]\s*[^:\r\n]*:\s*(\d+)\s*[,，]\s*[^:\r\n]*:\s*(\d+)\s*[,，]\s*[^:\r\n]*:\s*(\d+)[^\r\n]*$'
        foreach ($match in [regex]::Matches($text, $fallbackPattern)) {
            $summary.failed += [int]$match.Groups[1].Value
            $summary.passed += [int]$match.Groups[2].Value
            $summary.skipped += [int]$match.Groups[3].Value
            $summary.total += [int]$match.Groups[4].Value
        }
    }
    return [pscustomobject]$summary
}

function Get-TrxSummary {
    param([string]$TrxPath)

    $document = [xml](Get-Content -Raw -LiteralPath $TrxPath -Encoding UTF8)
    $counters = $document.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw 'dotnet test TRX did not contain ResultSummary.Counters.'
    }
    $read = {
        param([string]$Name)
        $attribute = $counters.GetAttribute($Name)
        if ([string]::IsNullOrWhiteSpace($attribute)) { return 0 }
        return [int]$attribute
    }
    $passed = & $read 'passed'
    $failed = (& $read 'failed') + (& $read 'error')
    $total = & $read 'total'
    return [pscustomobject]@{
        executed = $true
        passed = $passed
        failed = $failed
        skipped = [Math]::Max(0, $total - $passed - $failed)
        total = $total
    }
}

function Get-GitSummary {
    try {
        $revision = (& git -C $repoRoot rev-parse --short HEAD 2>$null | Select-Object -First 1).ToString().Trim()
        $status = @(git -C $repoRoot status --porcelain 2>$null)
        return [pscustomobject]@{
            revision = if ([string]::IsNullOrWhiteSpace($revision)) { '[UNKNOWN]' } else { $revision }
            workingTree = if ($status.Count -eq 0) { 'clean' } else { 'dirty' }
        }
    }
    catch {
        return [pscustomobject]@{ revision = '[UNKNOWN]'; workingTree = 'unknown' }
    }
}

function Read-RepoFile {
    param([string]$RelativePath)
    $path = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required repository file is missing: $RelativePath"
    }
    return Get-Content -Raw -LiteralPath $path -Encoding UTF8
}

try {
    $auditSource = Read-RepoFile 'src\MesControlAgv.Wpf\Services\OfflineDiagnosticAudit.cs'
    $inspectorSource = Read-RepoFile 'src\MesControlAgv.Wpf\Services\StartupConfigurationInspector.cs'
    $fieldPreflightSource = Read-RepoFile 'src\MesControlAgv.Wpf\Services\FieldPreflightChecklist.cs'
    $snapshotLifecycleSource = Read-RepoFile 'src\MesControlAgv.Wpf\Services\OfflineDiagnosticSnapshotLifecycle.cs'
    $workflowSource = Read-RepoFile '.github\workflows\offline-release-gate.yml'
    $archiveValidatorSource = Read-RepoFile 'scripts\assert-offline-release-report.ps1'
    $fieldHttpSource = Read-RepoFile 'scripts\Invoke-MesJsonUtf8.ps1'
    $workflowImportSource = Read-RepoFile 'scripts\Import-WorkflowGraph.ps1'
    $workflowToolingTestSource = Read-RepoFile 'scripts\Test-FieldWorkflowTooling.ps1'
    $physicalMonitorSource = Read-RepoFile 'scripts\Monitor-PhysicalWorkflow.ps1'
    $physicalMonitorTestSource = Read-RepoFile 'scripts\Test-PhysicalWorkflowMonitor.ps1'
    $auboCorrelationSource = Read-RepoFile 'src\MesControlAgv.Mes\Services\AuboArmWorkflowCorrelationValidator.cs'
    $auboWorkerSource = Read-RepoFile 'src\MesControlAgv.Mes\Services\WorkflowAuboProgramWorker.cs'
    $auboContractSource = Read-RepoFile 'src\MesControlAgv.Contracts\Devices\AuboArmContracts.cs'
    $wpfStartupBindingSource = Read-RepoFile 'src\MesControlAgv.Wpf\Services\WorkflowRunStartupBinding.cs'
    $wpfAppSource = Read-RepoFile 'src\MesControlAgv.Wpf\App.xaml.cs'
    $e2eSource = Read-RepoFile 'tests\MesControlAgv.E2E.Tests\CompoundTaskIntegrationTests.cs'
    Add-Check 'diagnostic-export-schema' ($auditSource -match 'ExportSchema\s*=\s*"mes\.offline-diagnostics/1\.0"') 'Current export schema is declared.'
    Add-Check 'diagnostic-rule-schema' ($auditSource -match 'RuleSchema\s*=\s*"mes\.startup-diagnostics/1\.0"') 'Current startup rule schema is declared.'
    Add-Check 'field-preflight-schema' ($auditSource -match 'FieldPreflightSchema\s*=\s*"mes\.field-preflight/1\.0"') 'Field preflight schema is declared.'
    Add-Check 'diagnostic-diff-schema' ($auditSource -match 'DiffSchema\s*=\s*"mes\.offline-diagnostic-diff/1\.0"') 'Configuration diff schema is declared.'
    Add-Check 'field-preflight-exported' ($auditSource -match 'fieldPreflight\s*=\s*new') 'Field preflight inputs are included in diagnostic exports.'
    Add-Check 'diagnostic-diff-exported' ($auditSource -match 'configurationDiff\s*=') 'Configuration diff is included when a baseline is supplied.'
    Add-Check 'diagnostic-diff-no-go' ($auditSource -match 'configurationDiff\.CanDetermineGo') 'Configuration diff exports preserve the no-GO contract.'
    Add-Check 'field-preflight-no-go' ($fieldPreflightSource -match 'CanDetermineGo\s*=>\s*false') 'Field preflight checklist cannot determine a field GO decision.'
    Add-Check 'startup-inspector-version' ($inspectorSource -match 'DiagnosticRuleVersion\s*=\s*OfflineDiagnosticVersions\.RuleSchema') 'Startup reports the active rule version.'
    Add-Check 'ci-workflow-uses-gate' ($workflowSource -match 'verify-offline-release\.ps1' -and $workflowSource -match 'assert-offline-release-report\.ps1') 'CI invokes the offline gate and archive validator.'
    Add-Check 'ci-uploads-json-only' ($workflowSource -match 'upload-artifact@v4' -and $workflowSource -match 'runner\.temp.*mes-offline-release-gate\.json' -and $workflowSource -notmatch '(?i)\.trx|\.log') 'CI artifact path is limited to the redacted JSON report.'
    Add-Check 'archive-validator-enforces-boundary' ($archiveValidatorSource -match 'canDetermineGo' -and $archiveValidatorSource -match '\[REDACTED\]' -and $archiveValidatorSource -match 'field-preflight') 'Archive validator checks schema, redaction, and the no-GO field.'
    Add-Check 'field-http-uses-explicit-utf8' (
        $fieldHttpSource -match 'HttpClient' -and
        $fieldHttpSource -match 'ByteArrayContent' -and
        $fieldHttpSource -match 'Encoding\]::UTF8' -and
        $fieldHttpSource -match 'SendAsync' -and
        $fieldHttpSource -notmatch 'Invoke-WebRequest|Invoke-RestMethod' -and
        $fieldHttpSource -match 'Date\\\(') 'Field JSON HTTP helper sends one explicit UTF-8 request and rejects legacy dates.'
    Add-Check 'workflow-import-tool-is-fail-closed' (
        $workflowImportSource -match 'ConvertFrom-Json' -and
        $workflowImportSource -match 'workflow-definition-request' -and
        $workflowImportSource -match 'automaticRetry\s*=\s*\$false' -and
        $workflowImportSource -match 'writesToDevicesAttempted\s*=\s*\$false' -and
        $workflowImportSource -notmatch 'Invoke-WebRequest\s+-Body') 'Workflow graph importer preserves UTF-8 and does not retry or write devices.'
    Add-Check 'workflow-tooling-replay-test' (
        $workflowToolingTestSource -match 'phase2-std-20260904-093754' -and
        $workflowToolingTestSource -match 'nodes\.Count\s*-ne\s*9' -and
        $workflowToolingTestSource -match 'publicationAttempted') 'Offline workflow tooling replay covers the saved nine-node field graph.'
    Add-Check 'physical-monitor-collection-normalization' (
        $physicalMonitorSource -match 'Expand-JsonCollection' -and
        $physicalMonitorSource -match "'value'.*'items'.*'data'" -and
        $physicalMonitorSource -match 'ReplayDirectory' -and
        $physicalMonitorSource -notmatch '(?i)\b(?:POST|PUT|DELETE)\b|Invoke-WebRequest\s+-Method\s+(?:POST|PUT|DELETE)') 'Physical monitor normalizes JSON collections and remains read-only.'
    Add-Check 'physical-monitor-replay-test' (
        $physicalMonitorTestSource -match 'fixtures\\physical-workflow-monitor' -and
        $physicalMonitorTestSource -match 'wrappersLeaked' -and
        $physicalMonitorTestSource -match 'deviceWritesAttempted') 'Physical monitor has an offline real-array replay test.'
    Add-Check 'aubo-workflow-correlation-contract' (
        $auboContractSource -match 'AuboArmOperationCorrelation' -and
        $auboContractSource -match 'workflowRunId' -and
        $auboContractSource -match 'workflowNodeExecutionId' -and
        $auboContractSource -match 'deviceOperationId' -and
        $auboContractSource -match 'correlationWarningCode') 'AUBO write contracts carry durable workflow identity and warning metadata.'
    Add-Check 'aubo-worker-reuses-durable-operation' (
        $auboWorkerSource -match 'operationId\s*=\s*operation\.OperationId' -and
        $auboWorkerSource -match 'CreateArmCorrelation' -and
        $auboWorkerSource -notmatch 'LoadProgramAsync\([\s\S]{0,500}Guid\.NewGuid\(\)') 'AUBO workflow worker reuses the claimed operation id and never invents a write id.'
    Add-Check 'aubo-correlation-boundary' (
        $auboCorrelationSource -match 'AUBO_WORKFLOW_CORRELATION_INVALID' -and
        $auboCorrelationSource -match 'AUBO_UNCORRELATED_WRITE' -and
        $auboCorrelationSource -match 'ListDeviceOperationsAsync') 'MES validates correlated writes and marks unassociated manual writes.'
    Add-Check 'wpf-explicit-run-binding' (
        $wpfStartupBindingSource -match 'workflow-execution-id' -and
        $wpfStartupBindingSource -match 'workflow-request-id' -and
        $wpfAppSource -match 'StartMainWindowAsync' -and
        $wpfAppSource -notmatch '(?i)GetLatest|latestRun|lastRun') 'WPF accepts only explicit execution/request startup identities.'
    Add-Check 'release-summary-no-go' ($fieldPreflightSource -match 'CanDetermineGo\s*=>\s*false') 'The release summary must preserve the no-GO contract.'
    Add-Check 'snapshot-lifecycle-read-only' ($snapshotLifecycleSource -match 'class OfflineDiagnosticSnapshotLifecycle' -and $snapshotLifecycleSource -match 'Inspect\s*\(' -and $snapshotLifecycleSource -notmatch '(?i)File\.Delete|Remove-Item|Directory\.Delete') 'Snapshot lifecycle only previews retention and never deletes files.'
    $knownAllowedSkipCount = ([regex]::Matches($e2eSource, '\[Fact\(Skip\s*=\s*"Requires full AGV simulator setup"\)\]')).Count
    Add-Check 'known-e2e-skip-policy' ($knownAllowedSkipCount -eq 5) "The only allowed skipped tests are the five documented full-simulator E2E cases (found $knownAllowedSkipCount)."

    $fixtureDirectory = Join-Path $repoRoot 'tests\MesControlAgv.Wpf.Tests\fixtures\startup-diagnostics'
    $fixtures = @(Get-ChildItem -LiteralPath $fixtureDirectory -Filter '*.json' -File)
    Add-Check 'startup-diagnostic-fixtures' ($fixtures.Count -ge 5) "Found $($fixtures.Count) startup diagnostic fixtures."
    $sensitivePattern = '(?i)(password|pwd|token|secret|authorization|api[-_]?key)\s*[:=]|https?://(?!(localhost|127\.0\.0\.1)(?:[:/]))|(?<!\d)(?!(?:127\.0\.0\.1|0\.0\.0\.0)(?!\d))(?:\d{1,3}\.){3}\d{1,3}(?!\d)'
    foreach ($fixture in $fixtures) {
        $content = Get-Content -Raw -LiteralPath $fixture.FullName -Encoding UTF8
        Add-Check "fixture-$($fixture.Name)" (-not ($content -match $sensitivePattern)) 'No credentials or non-loopback addresses found.'
    }

    $physicalHandoff = Read-RepoFile 'docs\physical-acceptance\AGV-ARM-VISION-MASTER-CONTROL-HANDOFF-2026-08-28.md'
    $physicalRecord = Read-RepoFile 'docs\physical-acceptance\FIELD-ACCEPTANCE-RECORD.md'
    Add-Check 'physical-handoff-no-go' ($physicalHandoff -match '(?i)NO-GO') 'Physical handoff still declares the NO-GO boundary.'
    Add-Check 'physical-record-no-go' ($physicalRecord -match '(?i)NO-GO') 'Physical acceptance record still declares the NO-GO boundary.'

    if ($SkipTests) {
        $testSummary = [pscustomobject]@{
            executed = $false
            passed = 0
            failed = 0
            skipped = 0
            total = 0
        }
        Add-Check 'solution-tests' $true 'Skipped by -SkipTests.'
    }
    else {
        $resultsDirectory = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-offline-release-$([Guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null
        try {
            # Keep the default gate behavior intact, while allowing an already
            # built Release tree to be verified without touching binaries that
            # may be held by a local service. The explicit argument list also
            # avoids Windows PowerShell's positional-array binding surprises.
            $testArguments = [System.Collections.Generic.List[string]]::new()
            $testArguments.Add((Join-Path $repoRoot 'MesControlAgv.sln'))
            $testArguments.Add('-m:1')
            $testArguments.Add('--nologo')
            $testArguments.Add('--configuration')
            $testArguments.Add($Configuration)
            $testArguments.Add('--results-directory')
            $testArguments.Add($resultsDirectory)
            $testArguments.Add('--logger')
            $testArguments.Add('trx')
            if ($NoBuild) { $testArguments.Add('--no-build') }
            if ($NoRestore) { $testArguments.Add('--no-restore') }
            if ($DisableBuildServers) { $testArguments.Add('--disable-build-servers') }
            $testOutput = @(& dotnet test @($testArguments.ToArray()) 2>&1)
            $testExitCode = $LASTEXITCODE
            $trxFiles = @(Get-ChildItem -LiteralPath $resultsDirectory -Filter '*.trx' -File -Recurse)
            if ($trxFiles.Count -gt 0) {
                $summaries = @($trxFiles | ForEach-Object { Get-TrxSummary $_.FullName })
                $testSummary = [pscustomobject]@{
                    executed = $true
                    passed = ($summaries | Measure-Object -Property passed -Sum).Sum
                    failed = ($summaries | Measure-Object -Property failed -Sum).Sum
                    skipped = ($summaries | Measure-Object -Property skipped -Sum).Sum
                    total = ($summaries | Measure-Object -Property total -Sum).Sum
                }
            }
            else {
                $testSummary = Get-TestSummary $testOutput
            }
            $testDetail = Redact-OfflineText (($testOutput | Select-Object -Last 1).ToString())
        }
        finally {
            if (Test-Path -LiteralPath $resultsDirectory -PathType Container) { Remove-Item -LiteralPath $resultsDirectory -Recurse -Force }
        }
        Add-Check 'solution-tests' ($testExitCode -eq 0) "dotnet test exit code $testExitCode. $testDetail"
    }

    if ([string]::IsNullOrWhiteSpace($OutputPath)) {
        $OutputPath = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-offline-release-$([DateTime]::Now.ToString('yyyyMMdd-HHmmss')).json"
    }
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $OutputPath
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    $gitSummary = Get-GitSummary
    $failedChecks = @($checks | Where-Object { -not $_.passed })
    $gatePassed = $failedChecks.Count -eq 0
    $unexpectedSkipped = [Math]::Max(0, [int]$testSummary.skipped - $knownAllowedSkipCount)
    $releaseEligible = $gatePassed -and -not $SkipTests -and $testSummary.executed -and $testSummary.failed -eq 0 -and $testSummary.total -gt 0 -and $unexpectedSkipped -eq 0
    $testSummaryReport = [ordered]@{
        executed = [bool]$testSummary.executed
        passed = [int]$testSummary.passed
        failed = [int]$testSummary.failed
        skipped = [int]$testSummary.skipped
        total = [int]$testSummary.total
        allowedSkipped = $knownAllowedSkipCount
        unexpectedSkipped = $unexpectedSkipped
    }
    $report = [ordered]@{
        schemaVersion = 'mes.offline-release-gate/1.0'
        generatedAt = [DateTimeOffset]::Now
        repository = '[REDACTED]'
        revision = $gitSummary.revision
        workingTree = $gitSummary.workingTree
        releaseEligible = $releaseEligible
        gatePassed = $gatePassed
        diagnosticSchemas = [ordered]@{
            export = 'mes.offline-diagnostics/1.0'
            startupRules = 'mes.startup-diagnostics/1.0'
            fieldPreflight = 'mes.field-preflight/1.0'
            diff = 'mes.offline-diagnostic-diff/1.0'
        }
        fieldPreflight = [ordered]@{
            schemaVersion = 'mes.field-preflight/1.0'
            canDetermineGo = $false
            decision = 'field-go-not-determined-offline'
        }
        fixtureCount = $fixtures.Count
        fixtureNames = @($fixtures.Name)
        tests = $testSummaryReport
        checks = @($checks)
        passed = $gatePassed
    } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($OutputPath, $report, [Text.UTF8Encoding]::new($false))
    if (-not $gatePassed) {
        throw "Offline release gate failed: $($failedChecks.name -join ', ')"
    }
    if ($releaseEligible) {
        Write-Host "Offline release gate passed. Report: $OutputPath"
    }
    else {
        Write-Host "Offline static checks passed (tests skipped). Report: $OutputPath"
    }
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        Write-Error $_
    }
    throw
}
