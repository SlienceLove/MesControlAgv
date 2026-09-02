<#
.SYNOPSIS
    只读比对：将 ShineLab 导出的样品任务 CSV 与期望 CSV 做结构化比较。

.DESCRIPTION
    该脚本不打开、不操作 ShineLab，只读取两个 CSV 文件并输出结构化 JSON 结果。
    它是导出验证链路的确定性内核，可离线运行和单元测试。

    比较规则：
    - 忽略可重新编号或自动生成的展示字段（默认 序号、选择、数据名称）。
      数据名称必须忽略：2026-08-26 现场证实 ShineLab 会用自己的文件名模板
      （如 %i%#）无条件覆盖该列，送什么进去都不保留。
    - 只校验任务数量和业务关键字段。
    - 按文件行顺序逐行比较（序号被忽略，因此不依赖序号本身）。
    - -MatchMode 决定对齐口径：
      Full（默认）        导出必须与期望逐行全等，行数也必须相同。用于空序列导入。
      AppendTail          追加语义：只把导出的「末 N 行」与期望的 N 行对齐比较，
                          N = 期望行数。前面的历史行是既有数据，不算差异。
                          2026-08-26 batch-07 就是被这一点误判成 Failed 的：
                          导出 10 行（7 行历史 + 3 行新增），Full 口径拿
                          MES-B07-A1 去比历史的「基线」行，产出 18 处假差异。
                          该模式只能用于旧证据人工复核，不能证明历史前缀未改变。
      AppendDelta         生产追加验证：必须提供导入前快照，要求导出历史前缀保持不变、
                          总行数恰好增加 N，并逐字段校验新增尾段。代理只使用此模式判定
                          Verified。
    - 单元格比较前做 Trim。
    - 默认按字符串语义比较，避免丢失 "07"、"13" 等标识列的前导零。
      样品等级是校准曲线等级编号（标识符），必须保持字符串严格比。
    - 仅 -NumericFields 列出的列按数值语义比较。2026-08-26 现场导出证实：
      同一个文件里 样品等级 "07" 原样保留、循环次数 01 被归一化成 1，
      说明 ShineLab 区分「标识列」和「数量列」。循环次数属于数量列，
      1 与 01 必须判等，否则会产生无意义的 Mismatch。
      两边都能解析成数值时才走数值比较，否则自动回落到字符串比较。
    - -InheritableFields 仅供旧证据人工复核 ShineLab 的「空值继承」：期望为空、导出非空、且导出值
      与紧邻上一行相同时，判为 InheritedValue，不计入差异，但在 JSON 里单独列出。
      2026-08-26 batch-07 证实：送空的 样品等级/清除校正 被回填成上一行的
      "07" / 清除校正。这是 ShineLab 的既有行为，不是导入失败。
      仅当上一行存在且值完全相同才算继承；否则仍报 ValueMismatch。默认关闭，且
      AppendDelta 始终严格比较，不允许靠继承进入 Match。

    输出 JSON 顶层 status 只有 Match / Mismatch 两种；调用方据此决定批次是否可判 Verified。
    该脚本永远不会触发导入，也不会点击“运行”。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ActualCsvPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ExpectedCsvPath,

    [string[]]$IgnoreFields = @('序号', '选择', '数据名称'),

    [string[]]$KeyFields,

    # 数量列：按数值语义比较，1 与 01 判等。标识列（如 样品等级）不得列入。
    # 默认只放已被现场导出证实会被归一化的 循环次数；其他列需要时显式传入。
    [string[]]$NumericFields = @('循环次数'),

    # 对齐口径。追加导入必须用 AppendTail，否则历史行会产生假差异。
    [ValidateSet('Full', 'AppendTail', 'AppendDelta')]
    [string]$MatchMode = 'Full',

    # AppendDelta 必填：导入前只读导出的序列快照。
    [string]$BaselineCsvPath,

    # 仅用于旧证据人工复核；生产验证默认严格，必须显式传入才启用。
    [string[]]$InheritableFields = @(),

    [string]$OutputJsonPath,

    [switch]$AsObject
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:ShineCanonicalColumns = @(
    '序号', '选择', '样品名称', '样品类型', '样品等级', '处理方法', '清除校正',
    '循环次数', '进样体积', '进样单位', '空白', '数据名称', '色谱方法'
)

function Get-CanonicalHeader {
    ($script:ShineCanonicalColumns -join ',')
}

function ConvertTo-NormalizedCell {
    param($Value)
    if ($null -eq $Value) { return '' }
    return ([string]$Value).Trim()
}

function Test-CellEqual {
    <#
        单元格判等。数量列按数值语义比较（1 与 01 判等），其余列按字符串比较。
        两边必须都能解析成数值才走数值路径；任一侧不是纯数值（空、区间、带单位等）
        就回落到字符串比较，避免把「空」和「0」误判成相等。
    #>
    param(
        [string]$Expected,
        [string]$Actual,
        [switch]$Numeric
    )

    # 沿用原有的大小写不敏感字符串比较，本次只新增数值语义分支。
    if ($Expected -eq $Actual) { return $true }
    if (-not $Numeric) { return $false }

    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $styles = [System.Globalization.NumberStyles]::Float
    $expectedNumber = [decimal]0
    $actualNumber = [decimal]0
    if (-not [decimal]::TryParse($Expected, $styles, $culture, [ref]$expectedNumber)) { return $false }
    if (-not [decimal]::TryParse($Actual, $styles, $culture, [ref]$actualNumber)) { return $false }

    return ($expectedNumber -eq $actualNumber)
}

function Read-ShineSequenceCsv {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Role
    )

    $resolved = (Resolve-Path -LiteralPath $Path -ErrorAction Stop).ProviderPath
    # 严格 UTF-8 读取；ShineLab 导出为 UTF-8。
    $utf8 = [System.Text.UTF8Encoding]::new($false, $true)
    $content = [System.IO.File]::ReadAllText($resolved, $utf8)

    $lines = @($content -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -eq 0) {
        throw "$Role CSV 没有任何内容：$resolved"
    }

    $headerTokens = @(($lines[0].TrimEnd(',')) -split ',')
    $canonical = $script:ShineCanonicalColumns
    $headerMatches = $headerTokens.Count -eq $canonical.Count
    if ($headerMatches) {
        for ($i = 0; $i -lt $canonical.Count; $i++) {
            if ($headerTokens[$i].Trim() -ne $canonical[$i]) { $headerMatches = $false; break }
        }
    }
    if (-not $headerMatches) {
        throw ("$Role CSV 表头不符合 ShineLab ExportData.csv 模板。`n期望：{0}`n实际：{1}" -f (Get-CanonicalHeader), ($headerTokens -join ','))
    }

    # 用规范列名显式解析，忽略行尾多余的空列。
    $rows = @(ConvertFrom-Csv -InputObject $lines -Header $canonical)
    # 第一行是表头，ConvertFrom-Csv 会把它当成数据行，需要丢弃。
    if ($rows.Count -gt 0) {
        $rows = @($rows | Select-Object -Skip 1)
    }

    $hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash

    [pscustomobject]@{
        Path     = $resolved
        Sha256   = $hash
        Rows     = $rows
        RowCount = $rows.Count
    }
}

function Get-ComparedFields {
    param([string[]]$IgnoreFields, [string[]]$KeyFields)

    if ($KeyFields -and $KeyFields.Count -gt 0) {
        $unknown = @($KeyFields | Where-Object { $script:ShineCanonicalColumns -notcontains $_ })
        if ($unknown.Count -gt 0) {
            throw ("KeyFields 含有未知列：{0}" -f ($unknown -join ', '))
        }
        return @($KeyFields)
    }
    return @($script:ShineCanonicalColumns | Where-Object { $IgnoreFields -notcontains $_ })
}

function Compare-ShineSequence {
    param(
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Actual,
        [Parameter(Mandatory = $true)][string[]]$ComparedFields,
        $Baseline,
        [string[]]$NumericFields = @(),
        [string]$MatchMode = 'Full',
        [string[]]$InheritableFields = @()
    )

    $differences = New-Object System.Collections.Generic.List[object]
    $inherited = New-Object System.Collections.Generic.List[object]
    $baselineUnchanged = $null

    # AppendTail：只看导出的末 N 行（N = 期望行数）。历史行既不比较也不算差异，
    # 但导出行数少于期望行数说明追加没落全，那是真缺陷，仍要报 MissingRow。
    $offset = 0
    if ($MatchMode -eq 'AppendDelta') {
        if ($null -eq $Baseline) {
            throw 'AppendDelta 必须提供导入前快照 BaselineCsvPath。'
        }
        $offset = $Baseline.RowCount
        $rowCountMatch = $Actual.RowCount -eq ($Baseline.RowCount + $Expected.RowCount)
        $baselineUnchanged = $true

        # 历史前缀必须逐字段保持不变。展示字段已由 ComparedFields 过滤。
        for ($baselineIndex = 0; $baselineIndex -lt $Baseline.RowCount; $baselineIndex++) {
            $baselineRow = $Baseline.Rows[$baselineIndex]
            $actualBaselineRow = if ($baselineIndex -lt $Actual.RowCount) { $Actual.Rows[$baselineIndex] } else { $null }
            if ($null -eq $actualBaselineRow) {
                $differences.Add([pscustomobject]@{
                        rowIndex = $baselineIndex + 1; actualRowIndex = $null
                        field = '*'; kind = 'BaselineMissingRow'
                        expected = '导入前历史行'; actual = $null
                    })
                $baselineUnchanged = $false
                continue
            }

            foreach ($field in $ComparedFields) {
                $baselineCell = ConvertTo-NormalizedCell -Value $baselineRow.$field
                $actualCell = ConvertTo-NormalizedCell -Value $actualBaselineRow.$field
                $isNumeric = ($NumericFields -contains $field)
                if (Test-CellEqual -Expected $baselineCell -Actual $actualCell -Numeric:$isNumeric) { continue }

                $differences.Add([pscustomobject]@{
                        rowIndex = $baselineIndex + 1; actualRowIndex = $baselineIndex + 1
                        field = $field; kind = 'BaselineValueMismatch'
                        comparison = $(if ($isNumeric) { 'Numeric' } else { 'String' })
                        expected = $baselineCell; actual = $actualCell
                    })
                $baselineUnchanged = $false
            }
        }
    }
    elseif ($MatchMode -eq 'AppendTail') {
        $rowCountMatch = $Actual.RowCount -ge $Expected.RowCount
        if ($rowCountMatch) { $offset = $Actual.RowCount - $Expected.RowCount }
    }
    else {
        $rowCountMatch = $Expected.RowCount -eq $Actual.RowCount
    }

    $maxCount = if ($MatchMode -in @('AppendTail', 'AppendDelta')) {
        [Math]::Max($Expected.RowCount, [Math]::Max(0, $Actual.RowCount - $offset))
    }
    else {
        [Math]::Max($Expected.RowCount, $Actual.RowCount)
    }

    for ($rowIndex = 0; $rowIndex -lt $maxCount; $rowIndex++) {
        $actualIndex = $rowIndex + $offset
        $expectedRow = if ($rowIndex -lt $Expected.RowCount) { $Expected.Rows[$rowIndex] } else { $null }
        $actualRow = if ($actualIndex -lt $Actual.RowCount) { $Actual.Rows[$actualIndex] } else { $null }

        if ($null -eq $expectedRow) {
            $differences.Add([pscustomobject]@{
                    rowIndex = $rowIndex + 1; actualRowIndex = $actualIndex + 1
                    field = '*'; kind = 'ExtraRow'
                    expected = $null; actual = '导出多出该行'
                })
            continue
        }
        if ($null -eq $actualRow) {
            $differences.Add([pscustomobject]@{
                    rowIndex = $rowIndex + 1; actualRowIndex = $null
                    field = '*'; kind = 'MissingRow'
                    expected = '期望存在该行'; actual = $null
                })
            continue
        }

        foreach ($field in $ComparedFields) {
            $expectedCell = ConvertTo-NormalizedCell -Value $expectedRow.$field
            $actualCell = ConvertTo-NormalizedCell -Value $actualRow.$field
            $isNumeric = ($NumericFields -contains $field)
            $comparisonKind = if ($isNumeric) { 'Numeric' } else { 'String' }
            if (Test-CellEqual -Expected $expectedCell -Actual $actualCell -Numeric:$isNumeric) { continue }

            # 空值继承：期望为空、导出非空、且与紧邻上一行导出值相同 -> 记账但不算差异。
            # 上一行取导出文件里的物理前一行，AppendTail 下首行的上一行就是最后一条历史行。
            if (($MatchMode -ne 'AppendDelta') -and
                ($InheritableFields -contains $field) -and
                [string]::IsNullOrEmpty($expectedCell) -and
                -not [string]::IsNullOrEmpty($actualCell) -and
                $actualIndex -gt 0) {
                $prevCell = ConvertTo-NormalizedCell -Value $Actual.Rows[$actualIndex - 1].$field
                if ($prevCell -eq $actualCell) {
                    $inherited.Add([pscustomobject]@{
                            rowIndex = $rowIndex + 1; actualRowIndex = $actualIndex + 1
                            field = $field; kind = 'InheritedValue'
                            expected = $expectedCell; actual = $actualCell
                            inheritedFrom = $actualIndex
                        })
                    continue
                }
            }

            $differences.Add([pscustomobject]@{
                    rowIndex = $rowIndex + 1; actualRowIndex = $actualIndex + 1
                    field = $field; kind = 'ValueMismatch'
                    comparison = $comparisonKind
                    expected = $expectedCell; actual = $actualCell
                })
        }
    }

    $status = if ($rowCountMatch -and $differences.Count -eq 0) { 'Match' } else { 'Mismatch' }
    # 注意：Windows PowerShell 5.1 严格模式下，@() 包裹 List[object] 放入 pscustomobject 字面量会抛
    # ArgumentException（参数类型不匹配）。用 ToArray() 转成普通数组后再返回。
    return [pscustomobject]@{
        Status        = $status
        RowCountMatch = $rowCountMatch
        Differences   = $differences.ToArray()
        TailOffset    = $offset
        Inherited     = $inherited.ToArray()
        BaselineUnchanged = $baselineUnchanged
    }
}

$comparedFields = Get-ComparedFields -IgnoreFields $IgnoreFields -KeyFields $KeyFields
$expected = Read-ShineSequenceCsv -Path $ExpectedCsvPath -Role '期望'
$actual = Read-ShineSequenceCsv -Path $ActualCsvPath -Role '导出'
$baseline = $null
if ($MatchMode -eq 'AppendDelta') {
    if ([string]::IsNullOrWhiteSpace($BaselineCsvPath)) {
        throw 'AppendDelta 必须指定 -BaselineCsvPath。'
    }
    $baseline = Read-ShineSequenceCsv -Path $BaselineCsvPath -Role '导入前快照'
}
$activeNumericFields = @($NumericFields | Where-Object { $comparedFields -contains $_ })
$activeInheritableFields = @($InheritableFields | Where-Object { $comparedFields -contains $_ })
$comparison = Compare-ShineSequence -Expected $expected -Actual $actual -Baseline $baseline -ComparedFields $comparedFields -NumericFields $activeNumericFields -MatchMode $MatchMode -InheritableFields $activeInheritableFields

$result = [pscustomobject]@{
    schemaVersion  = '1.0'
    generatedAt    = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    status         = $comparison.Status
    rowCountMatch  = $comparison.RowCountMatch
    matchMode      = $MatchMode
    # AppendTail 下跳过的历史行数；口径可自证，现场回执不必再靠人推断。
    tailOffset     = $comparison.TailOffset
    baseline       = if ($null -eq $baseline) { $null } else { [pscustomobject]@{ path = $baseline.Path; sha256 = $baseline.Sha256; rowCount = $baseline.RowCount } }
    baselineUnchanged = $comparison.BaselineUnchanged
    appendedRowCount = if ($null -eq $baseline) { $null } else { $actual.RowCount - $baseline.RowCount }
    expected       = [pscustomobject]@{ path = $expected.Path; sha256 = $expected.Sha256; rowCount = $expected.RowCount }
    actual         = [pscustomobject]@{ path = $actual.Path; sha256 = $actual.Sha256; rowCount = $actual.RowCount }
    comparedFields = @($comparedFields)
    numericFields  = @($activeNumericFields)
    inheritableFields = @($activeInheritableFields)
    ignoredFields  = @($script:ShineCanonicalColumns | Where-Object { $comparedFields -notcontains $_ })
    differenceCount = $comparison.Differences.Count
    differences    = @($comparison.Differences)
    # 空值继承：不算差异，但必须在回执里可见，否则「送空却回来有值」会被静默吞掉。
    inheritedCount = $comparison.Inherited.Count
    inherited      = @($comparison.Inherited)
}

if ($OutputJsonPath) {
    $json = $result | ConvertTo-Json -Depth 6
    $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
    [System.IO.File]::WriteAllText((New-Item -ItemType File -Path $OutputJsonPath -Force).FullName, $json, $utf8NoBom)
    Write-Host ("[ShineLab Compare] 结果已写入：{0}" -f $OutputJsonPath)
}

if ($AsObject) {
    $result
}
else {
    $result | ConvertTo-Json -Depth 6
}
