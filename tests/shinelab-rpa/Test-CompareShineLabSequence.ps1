<#
.SYNOPSIS
    Compare-ShineLabSequence.ps1 的离线自测。

.DESCRIPTION
    使用 fixtures 目录下的固定 CSV，验证结构化比对逻辑：
    - actual-match：仅 序号/选择/数据名称 不同，业务字段一致 -> Match
    - actual-field-mismatch：多个业务字段不同 -> Mismatch，列出差异
    - actual-rowcount：多出一行（重复追加）-> Mismatch，ExtraRow
    - expected 自比对 -> Match
    该测试不打开 ShineLab，全程只读文件。失败时以非零码退出。
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$here = Split-Path -Parent $MyInvocation.MyCommand.Definition
$compare = Join-Path $here '..\..\scripts\Compare-ShineLabSequence.ps1'
$fixtures = Join-Path $here 'fixtures'
$expected = Join-Path $fixtures 'expected.csv'

$failures = New-Object System.Collections.Generic.List[string]

function Invoke-Compare {
    param(
        [string]$Actual,
        [string]$MatchMode = 'Full',
        [string]$Baseline,
        [string[]]$InheritableFields = @()
    )
    $arguments = @{
        ActualCsvPath = $Actual
        ExpectedCsvPath = $expected
        MatchMode = $MatchMode
        InheritableFields = $InheritableFields
        AsObject = $true
    }
    if ($Baseline) { $arguments['BaselineCsvPath'] = $Baseline }
    & $compare @arguments
}

function Assert-Equal {
    param([string]$Name, $Expected, $Actual)
    if ($Expected -ne $Actual) {
        $script:failures.Add(("[FAIL] {0}: 期望 <{1}>，实际 <{2}>" -f $Name, $Expected, $Actual))
        Write-Host ("[FAIL] {0}: 期望 <{1}>，实际 <{2}>" -f $Name, $Expected, $Actual)
    }
    else {
        Write-Host ("[PASS] {0} = {1}" -f $Name, $Actual)
    }
}

# 1. 自比对：Match
$self = Invoke-Compare -Actual $expected
Assert-Equal 'self.status' 'Match' $self.status
Assert-Equal 'self.differenceCount' 0 $self.differenceCount

# 2. 仅忽略字段不同：Match
$match = Invoke-Compare -Actual (Join-Path $fixtures 'actual-match.csv')
Assert-Equal 'match.status' 'Match' $match.status
Assert-Equal 'match.differenceCount' 0 $match.differenceCount

# 3. 业务字段不同：Mismatch，含 ValueMismatch
$field = Invoke-Compare -Actual (Join-Path $fixtures 'actual-field-mismatch.csv')
Assert-Equal 'field.status' 'Mismatch' $field.status
Assert-Equal 'field.rowCountMatch' $true $field.rowCountMatch
$valueMismatches = @($field.differences | Where-Object { $_.kind -eq 'ValueMismatch' })
if ($valueMismatches.Count -lt 1) {
    $failures.Add('[FAIL] field: 期望至少 1 处 ValueMismatch')
    Write-Host '[FAIL] field: 期望至少 1 处 ValueMismatch'
}
else {
    Write-Host ("[PASS] field.ValueMismatch 数量 = {0}" -f $valueMismatches.Count)
}
# 差异不应包含被忽略字段
$leakedIgnored = @($field.differences | Where-Object { @('序号', '选择', '数据名称') -contains $_.field })
Assert-Equal 'field.noIgnoredLeak' 0 $leakedIgnored.Count

# 4. 行数不同（重复追加）：Mismatch，含 ExtraRow
$rowcount = Invoke-Compare -Actual (Join-Path $fixtures 'actual-rowcount.csv')
Assert-Equal 'rowcount.status' 'Mismatch' $rowcount.status
Assert-Equal 'rowcount.rowCountMatch' $false $rowcount.rowCountMatch
$extraRows = @($rowcount.differences | Where-Object { $_.kind -eq 'ExtraRow' })
Assert-Equal 'rowcount.extraRow' 1 $extraRows.Count

# 5. 循环次数 01 vs 1：数量列按数值语义判等 -> Match
#    2026-08-26 现场导出证实 ShineLab 会把 循环次数 01 归一化成 1，这不是缺陷。
$cycle = Invoke-Compare -Actual (Join-Path $fixtures 'actual-numeric-cycle.csv')
Assert-Equal 'cycle.status' 'Match' $cycle.status
Assert-Equal 'cycle.differenceCount' 0 $cycle.differenceCount
Assert-Equal 'cycle.numericFields' '循环次数' ($cycle.numericFields -join ',')

# 6. 样品等级 "07" vs "7"：标识列必须保持字符串严格比 -> Mismatch
#    样品等级是校准曲线等级编号，前导零有意义，绝不能按数值判等。
$level = Invoke-Compare -Actual (Join-Path $fixtures 'actual-level-numeric.csv')
Assert-Equal 'level.status' 'Mismatch' $level.status
$levelDiffs = @($level.differences | Where-Object { $_.field -eq '样品等级' })
Assert-Equal 'level.diffCount' 1 $levelDiffs.Count
if ($levelDiffs.Count -eq 1) {
    Assert-Equal 'level.comparison' 'String' $levelDiffs[0].comparison
    Assert-Equal 'level.expected' '07' $levelDiffs[0].expected
    Assert-Equal 'level.actual' '7' $levelDiffs[0].actual
}

# 7. 追加导入后导出含历史行：Full 口径应判 Mismatch（旧行为），AppendTail 口径应判 Match。
#    2026-08-26 batch-07 就是这样导入成功却被判 Failed 的。
$appendFile = Join-Path $fixtures 'actual-append-tail.csv'
$appendFull = Invoke-Compare -Actual $appendFile
Assert-Equal 'appendFull.status' 'Mismatch' $appendFull.status
Assert-Equal 'appendFull.rowCountMatch' $false $appendFull.rowCountMatch

$appendTail = Invoke-Compare -Actual $appendFile -MatchMode 'AppendTail'
Assert-Equal 'appendTail.status' 'Match' $appendTail.status
Assert-Equal 'appendTail.differenceCount' 0 $appendTail.differenceCount
Assert-Equal 'appendTail.rowCountMatch' $true $appendTail.rowCountMatch
Assert-Equal 'appendTail.matchMode' 'AppendTail' $appendTail.matchMode
Assert-Equal 'appendTail.tailOffset' 2 $appendTail.tailOffset

# 8. 生产追加验证必须同时证明：历史前缀未变、总行数精确增加 N、尾段与期望一致。
$baselineFile = Join-Path $fixtures 'baseline-append.csv'
$appendDelta = Invoke-Compare -Actual $appendFile -MatchMode 'AppendDelta' -Baseline $baselineFile
Assert-Equal 'appendDelta.status' 'Match' $appendDelta.status
Assert-Equal 'appendDelta.baselineUnchanged' $true $appendDelta.baselineUnchanged
Assert-Equal 'appendDelta.appendedRowCount' 7 $appendDelta.appendedRowCount
Assert-Equal 'appendDelta.tailOffset' 2 $appendDelta.tailOffset

$emptyBaselineDelta = Invoke-Compare -Actual $expected -MatchMode 'AppendDelta' `
    -Baseline (Join-Path $fixtures 'baseline-empty.csv')
Assert-Equal 'emptyBaselineDelta.status' 'Match' $emptyBaselineDelta.status
Assert-Equal 'emptyBaselineDelta.baselineUnchanged' $true $emptyBaselineDelta.baselineUnchanged
Assert-Equal 'emptyBaselineDelta.appendedRowCount' 7 $emptyBaselineDelta.appendedRowCount

# AppendTail 会只看最后 N 行而放过中间多出的任务；AppendDelta 必须拒绝。
$appendExtraFile = Join-Path $fixtures 'actual-append-extra.csv'
$appendExtraTail = Invoke-Compare -Actual $appendExtraFile -MatchMode 'AppendTail'
Assert-Equal 'appendExtraTail.status' 'Match' $appendExtraTail.status
$appendExtraDelta = Invoke-Compare -Actual $appendExtraFile -MatchMode 'AppendDelta' -Baseline $baselineFile
Assert-Equal 'appendExtraDelta.status' 'Mismatch' $appendExtraDelta.status
Assert-Equal 'appendExtraDelta.rowCountMatch' $false $appendExtraDelta.rowCountMatch
Assert-Equal 'appendExtraDelta.appendedRowCount' 8 $appendExtraDelta.appendedRowCount

# 即使新增尾段正确，历史前缀被修改也必须阻止 Verified。
$prefixMutated = Invoke-Compare -Actual (Join-Path $fixtures 'actual-append-prefix-mutated.csv') `
    -MatchMode 'AppendDelta' -Baseline $baselineFile
Assert-Equal 'prefixMutated.status' 'Mismatch' $prefixMutated.status
Assert-Equal 'prefixMutated.baselineUnchanged' $false $prefixMutated.baselineUnchanged
$baselineDiffs = @($prefixMutated.differences | Where-Object { $_.kind -eq 'BaselineValueMismatch' })
Assert-Equal 'prefixMutated.baselineDiffCount' 1 $baselineDiffs.Count

# 9. AppendTail 不能变成「什么都放过」：末 N 行里的业务字段差异必须照报，
#    且要给出导出中的真实行号（actualRowIndex）便于现场核对。
$appendBad = Invoke-Compare -Actual (Join-Path $fixtures 'actual-append-tail-bad.csv') -MatchMode 'AppendTail'
Assert-Equal 'appendBad.status' 'Mismatch' $appendBad.status
$badDiffs = @($appendBad.differences | Where-Object { $_.field -eq '样品等级' })
Assert-Equal 'appendBad.diffCount' 1 $badDiffs.Count
if ($badDiffs.Count -eq 1) {
    Assert-Equal 'appendBad.expected' '07' $badDiffs[0].expected
    Assert-Equal 'appendBad.actual' '99' $badDiffs[0].actual
    Assert-Equal 'appendBad.rowIndex' 7 $badDiffs[0].rowIndex
    Assert-Equal 'appendBad.actualRowIndex' 9 $badDiffs[0].actualRowIndex
}

# 10. 导出行数少于期望行数：追加没落全，是真缺陷，AppendTail 也必须判 Mismatch。
$appendShort = Invoke-Compare -Actual (Join-Path $fixtures 'actual-append-short.csv') -MatchMode 'AppendTail'
Assert-Equal 'appendShort.status' 'Mismatch' $appendShort.status
Assert-Equal 'appendShort.rowCountMatch' $false $appendShort.rowCountMatch
$missingRows = @($appendShort.differences | Where-Object { $_.kind -eq 'MissingRow' })
if ($missingRows.Count -lt 1) {
    $failures.Add('[FAIL] appendShort: 期望至少 1 处 MissingRow')
    Write-Host '[FAIL] appendShort: 期望至少 1 处 MissingRow'
}
else {
    Write-Host ("[PASS] appendShort.MissingRow 数量 = {0}" -f $missingRows.Count)
}

# 11. 默认口径必须仍是 Full，自比对不受本次改动影响。
Assert-Equal 'self.matchMode' 'Full' $self.matchMode
Assert-Equal 'self.tailOffset' 0 $self.tailOffset

# 12. 空值继承默认必须严格失败；仅人工复核旧证据时显式开启后才可作为 Match 观察。
#    2026-08-26 batch-07 现场证实 ShineLab 会用上一行的 样品等级/清除校正 回填空单元格。
$inheritExpected = Join-Path $fixtures 'expected-inherit.csv'
$inheritStrict = & $compare -ActualCsvPath (Join-Path $fixtures 'actual-inherit.csv') `
    -ExpectedCsvPath $inheritExpected -MatchMode AppendTail -AsObject
Assert-Equal 'inheritStrict.status' 'Mismatch' $inheritStrict.status
Assert-Equal 'inheritStrict.inheritedCount' 0 $inheritStrict.inheritedCount

$inherit = & $compare -ActualCsvPath (Join-Path $fixtures 'actual-inherit.csv') `
    -ExpectedCsvPath $inheritExpected -MatchMode AppendTail `
    -InheritableFields @('样品等级', '清除校正', '处理方法') -AsObject
Assert-Equal 'inherit.status' 'Match' $inherit.status
Assert-Equal 'inherit.tailOffset' 7 $inherit.tailOffset
Assert-Equal 'inherit.differenceCount' 0 $inherit.differenceCount
Assert-Equal 'inherit.inheritedCount' 5 $inherit.inheritedCount
$inheritFields = @($inherit.inherited | ForEach-Object { $_.field } | Sort-Object -Unique)
Assert-Equal 'inherit.fields' '清除校正,样品等级' ($inheritFields -join ',')

# 13. 继承链断裂：回填值与上一行不一致 -> 必须报 Mismatch，不能被继承规则吞掉
$break = & $compare -ActualCsvPath (Join-Path $fixtures 'actual-inherit-break.csv') `
    -ExpectedCsvPath $inheritExpected -MatchMode AppendTail `
    -InheritableFields @('样品等级', '清除校正', '处理方法') -AsObject
Assert-Equal 'break.status' 'Mismatch' $break.status
$breakLevel = @($break.differences | Where-Object { $_.field -eq '样品等级' })
Assert-Equal 'break.levelDiffCount' 1 $breakLevel.Count
if ($breakLevel.Count -eq 1) {
    Assert-Equal 'break.actualRowIndex' 8 $breakLevel[0].actualRowIndex
    Assert-Equal 'break.actual' '99' $breakLevel[0].actual
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host ("测试失败：{0} 项" -f $failures.Count)
    exit 1
}
Write-Host '全部离线比对测试通过。'
exit 0
