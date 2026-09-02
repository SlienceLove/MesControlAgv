<#
.SYNOPSIS
    Start-ShineLabBatchAgent.ps1 非消耗离线校验回归测试。

.DESCRIPTION
    只使用临时目录和固定 CSV，不启动或操作 ShineLab。验证 -ValidateOnly 不写账本/回执、
    不移动 ready 文件，并在可继承字段为空时 fail closed。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$agent = Join-Path $here '..\..\scripts\Start-ShineLabBatchAgent.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ("shinelab-agent-validation-{0}" -f [Guid]::NewGuid().ToString('N'))
$failures = [System.Collections.Generic.List[string]]::new()

function Assert-Equal {
    param([string]$Name, $Expected, $Actual)
    if ($Expected -ne $Actual) {
        $script:failures.Add("[FAIL] $Name：期望 <$Expected>，实际 <$Actual>")
        Write-Host "[FAIL] $Name：期望 <$Expected>，实际 <$Actual>"
    }
    else {
        Write-Host "[PASS] $Name = $Actual"
    }
}

function New-TestBatch {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)][string]$BatchId,
        [Parameter(Mandatory = $true)][string]$DataRow
    )

    $null = New-Item -ItemType Directory -Path $Directory -Force
    $header = '序号,选择,样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,数据名称,色谱方法,'
    $csv = $header + "`r`n" + $DataRow + "`r`n"
    $csvName = "$BatchId.csv"
    $csvPath = Join-Path $Directory $csvName
    [IO.File]::WriteAllText($csvPath, $csv, [Text.UTF8Encoding]::new($false))
    $hash = (Get-FileHash -LiteralPath $csvPath -Algorithm SHA256).Hash
    $manifest = [ordered]@{
        schemaVersion = '1.0'
        batchId = $BatchId
        csvSha256 = $hash
        csvFileName = $csvName
        targetSequence = 'offline-validation'
        expectedRows = 1
        createdAtUtc = [DateTime]::UtcNow.ToString('o')
        createdBy = 'offline-test'
        allowAppend = $true
        verifyByExport = $true
        allowRun = $false
    }
    $readyPath = Join-Path $Directory "$BatchId.manifest.ready"
    [IO.File]::WriteAllText($readyPath, ($manifest | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    return $readyPath
}

function Invoke-Validation {
    param([Parameter(Mandatory = $true)][string]$Inbox)
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $agent `
        -InboxDir $Inbox -EvidenceRoot (Join-Path $Inbox 'evidence') -RunOnce -ValidateOnly 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) }
}

try {
    $validInbox = Join-Path $root 'valid'
    $validReady = New-TestBatch -Directory $validInbox -BatchId 'batch-valid' `
        -DataRow '1,,水样-01,未知样品,"1",阴离子标准法,否,1,25,μL,否,,阴离子常规,'
    $valid = Invoke-Validation -Inbox $validInbox
    Assert-Equal 'valid.exitCode' 0 $valid.ExitCode
    Assert-Equal 'valid.readyPreserved' $true (Test-Path -LiteralPath $validReady)
    Assert-Equal 'valid.noLedger' $false (Test-Path -LiteralPath (Join-Path $validInbox 'evidence\batch-ledger.json'))
    Assert-Equal 'valid.noReceipt' 0 @(Get-ChildItem -LiteralPath $validInbox -Filter '*.receipt.json').Count

    $invalidInbox = Join-Path $root 'invalid'
    $invalidReady = New-TestBatch -Directory $invalidInbox -BatchId 'batch-invalid' `
        -DataRow '1,,水样-02,未知样品,,,否,1,25,μL,否,,阴离子常规,'
    $invalid = Invoke-Validation -Inbox $invalidInbox
    Assert-Equal 'invalid.exitCode' 1 $invalid.ExitCode
    Assert-Equal 'invalid.readyPreserved' $true (Test-Path -LiteralPath $invalidReady)
    Assert-Equal 'invalid.noRejectedOrError' 0 @(Get-ChildItem -LiteralPath $invalidInbox -Filter '*.manifest.*' |
        Where-Object { $_.Extension -in @('.rejected', '.error', '.done') }).Count
    Assert-Equal 'invalid.noLedger' $false (Test-Path -LiteralPath (Join-Path $invalidInbox 'evidence\batch-ledger.json'))
    Assert-Equal 'invalid.noReceipt' 0 @(Get-ChildItem -LiteralPath $invalidInbox -Filter '*.receipt.json').Count
    if (-not (($invalid.Output -join "`n") -match '必须显式填写')) {
        $failures.Add('[FAIL] invalid.message：输出未包含显式字段拒绝原因')
    }
    else {
        Write-Host '[PASS] invalid.message 包含显式字段拒绝原因'
    }
}
finally {
    if (Test-Path -LiteralPath $root) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "测试失败：$($failures.Count) 项"
    exit 1
}

Write-Host '全部代理离线校验测试通过。'
exit 0
