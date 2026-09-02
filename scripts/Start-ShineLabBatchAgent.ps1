<#
.SYNOPSIS
    控制电脑侧批次代理：消费 MES 写入的样品任务批次，调用已冻结的导入脚本导入 ShineLab，写回回执。

.DESCRIPTION
    运行位置：离子色谱控制电脑（ShineLab 所在机器）。MES 中控 WPF 不直接操作 ShineLab，
    而是把 CSV + 清单写到交接目录，由本代理串行消费。

    单批次流程：
      1. 扫描 InboxDir 中的 *.manifest.ready，按创建时间取最早一个（严格串行，不并发）。
      2. 校验清单：schemaVersion、CSV 存在、SHA256 与清单一致、行数与 expectedRows 一致。
      3. 幂等：以 batchId|csvSha256|targetSequence 为键查账本，已处理过的批次直接复用原回执。
      4. 调用 Invoke-ShineLabCsvImport.ps1（版本 2026-08-24.13，不修改）执行导入。
      5. 若清单要求 verifyByExport，调用 Export-ShineLabSequence.ps1 做只读导出比对。
      6. 写回 {batchId}.receipt.json（先 .tmp 再原子改名）。

    安全边界（硬编码，清单无法放宽）：
      - 绝不点击“运行”：本脚本不含任何启动分析的调用，回执 runTriggered 恒为 false。
      - 清单里 allowRun 为 true 时直接判 Failed 并拒绝该批次。
      - 仅当导出比对为 Match 时才判 Verified；无法判断一律 Unknown，且不自动重试。
      - 不覆盖、不删除 ShineLab 现有序列文件，导出只写受控新临时文件。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$InboxDir,

    [string]$ShineLabPath,

    [string]$LedgerPath,

    [string]$EvidenceRoot,

    [int]$PollSeconds = 5,

    [int]$TimeoutSeconds = 60,

    [Nullable[int]]$GridPointX,

    [Nullable[int]]$GridPointY,

    [Nullable[int]]$ImportMenuPointX,

    [Nullable[int]]$ImportMenuPointY,

    [Nullable[int]]$ExportMenuPointX,

    [Nullable[int]]$ExportMenuPointY,

    [switch]$ExecuteImport,

    # 纯离线校验清单和 CSV；不访问 ShineLab，不写账本/回执，不移动 ready 文件。
    [switch]$ValidateOnly,

    # 现场非消耗预检：校验清单后只打开并观察右键菜单，不选择导入项；
    # 不写账本/回执，不移动 ready 文件。必须与 -RunOnce 一起使用。
    [switch]$PreflightOnly,

    [switch]$RunOnce,

    [switch]$UseKeyboardMenuFallback,

    [switch]$UseKeyboardFileDialogFallback,

    # 常驻模式：心跳间隔。无待处理批次时按此间隔打印一行，证明进程还活着。
    [int]$HeartbeatMinutes = 10,

    # 常驻模式：控制台输出同时落盘到此目录，按天滚动。默认 EvidenceRoot\agent-log。
    [string]$DaemonLogDir,

    # 常驻模式：优雅停机哨兵。在 InboxDir 建此文件即在当前批次结束后退出。
    [string]$StopFileName = 'agent.stop',

    # 常驻模式：导入前要求键鼠已空闲这么多秒。RPA 靠模拟点击工作，
    # 现场有人正在用这台机器时启动导入会打偏。0 表示不检查。
    [int]$RequireIdleSeconds = 0,

    # 常驻模式健康文件；先写临时文件再原子替换，供 MES/监控读取。
    [string]$HealthFile,

    # 生产模式可要求清单使用共享密钥 HMAC-SHA256 签名。密钥只从本机文件读取，
    # 不放在命令行参数或清单中。
    [string]$SigningKeyFile,
    [switch]$RequireSignedCommands,
    [int]$MaxClockSkewSeconds = 300,

    # ShineLab 主窗口标题的匹配模式。默认与导入内核 Invoke-ShineLabCsvImport.ps1
    # 完全一致：现场装机名字有 ShineDataAcquire / ShineDataAcquire-Normal / ShineLab
    # 等多种写法，按进程名精确匹配会把开着的 ShineLab 判成没开（现场实测：闸门报
    # 未找到进程，而同一时刻导入内核找得到主窗口）。
    # 判据必须和内核同源：闸门比内核严，就会拦下内核本来能干的活。
    [string]$ShineLabWindowPattern = 'ShineDataAcquisition|ShineDataAcquire|ShineLab'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modeCount = @(@($ExecuteImport, $ValidateOnly, $PreflightOnly) | Where-Object { [bool]$_ }).Count
if ($modeCount -ne 1) {
    throw '必须且只能选择一种模式：-ExecuteImport、-ValidateOnly 或 -PreflightOnly。'
}
if (($ValidateOnly -or $PreflightOnly) -and -not $RunOnce) {
    throw '-ValidateOnly 和 -PreflightOnly 必须与 -RunOnce 一起使用，避免常驻循环反复检查同一批次。'
}

$script:AgentVersion = 'batch-agent-2026-08-27.4'
$script:ShineCsvHeader = '序号,选择,样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,数据名称,色谱方法'
$script:ScriptDir = Split-Path -Parent $PSCommandPath
$script:ImportScript = Join-Path $script:ScriptDir 'Invoke-ShineLabCsvImport.ps1'
$script:ExportScript = Join-Path $script:ScriptDir 'Export-ShineLabSequence.ps1'

$script:DaemonLogPath = $null
$script:SigningKey = $null

function Write-Stage {
    param([string]$Message)

    $line = "[ShineLab Agent] {0}" -f $Message
    Write-Host $line

    # 常驻模式下控制台会被关掉或滚掉，日志必须落盘，否则夜里卡住无从追查。
    # 落盘失败绝不能拖垮代理本体，所以整段吞掉异常。
    if ($script:DaemonLogPath) {
        try {
            $stamped = "{0:yyyy-MM-dd HH:mm:ss} {1}" -f (Get-Date), $line
            [System.IO.File]::AppendAllText($script:DaemonLogPath, ($stamped + "`r`n"), (Get-Utf8NoBom))
        }
        catch { }
    }
}

function Set-DaemonLogPath {
    param([Parameter(Mandatory = $true)][string]$Directory)

    $null = New-Item -ItemType Directory -Path $Directory -Force
    $name = 'agent-{0:yyyyMMdd}.log' -f (Get-Date)
    $script:DaemonLogPath = Join-Path (Resolve-Path -LiteralPath $Directory).ProviderPath $name

    # 日志带中文，必须写 BOM：PowerShell 5.1 和记事本读无 BOM 的中文会按 GBK
    # 解码，现场翻日志就是一片乱码。BOM 只能出现在文件头，所以只在新建时写，
    # 之后 Write-Stage 一律纯追加。
    if (-not (Test-Path -LiteralPath $script:DaemonLogPath)) {
        try {
            [System.IO.File]::WriteAllBytes(
                $script:DaemonLogPath,
                (New-Object System.Text.UTF8Encoding($true)).GetPreamble())
        }
        catch { }
    }
}

# 唯一的 ShineLab 查找入口：判据与导入内核 Invoke-ShineLabCsvImport.ps1 的
# Get-MainWindowProcess 一致 —— 有主窗口 + 标题匹配。健康文件和启动闸门都必须
# 走这里，两处各写一遍 Get-Process 就是上一版 bug 的成因。
# 要求 MainWindowHandle -ne 0：RPA 靠窗口干活，有进程但没主窗口（最小化到托盘
# 或正在启动）时点击同样打不中，此时判成"没开"才是对的。
function Get-ShineLabWindowProcess {
    return Get-Process | Where-Object {
        $_.MainWindowHandle -ne 0 -and
        ($_.MainWindowTitle -match $ShineLabWindowPattern)
    } | Select-Object -First 1
}

function Write-Health {
    param(
        [Parameter(Mandatory = $true)][string]$Status,
        [string]$Reason,
        [int]$ProcessedCount = 0,
        [int]$PendingCount = 0
    )

    if ([string]::IsNullOrWhiteSpace($HealthFile)) { return }

    try {
        $process = Get-ShineLabWindowProcess
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        $health = [ordered]@{
            schemaVersion = '1.0'
            agentVersion = $script:AgentVersion
            status = $Status
            reason = $Reason
            pid = $PID
            user = $identity.Name
            sessionName = $env:SESSIONNAME
            interactive = [Environment]::UserInteractive
            elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
            shineLabPattern = $ShineLabWindowPattern
            shineLabRunning = $null -ne $process
            # 记下实际命中的进程名和窗口标题：上一版只记了"我要找什么"，没记
            # "我找到了什么"，排查时无法区分是没开还是判据不对。
            shineLabProcess = if ($null -ne $process) { $process.ProcessName } else { $null }
            shineLabWindowTitle = if ($null -ne $process) { $process.MainWindowTitle } else { $null }
            inboxDir = $InboxDir
            lastHeartbeatUtc = [DateTime]::UtcNow.ToString('o')
            processedCount = $ProcessedCount
            pendingCount = $PendingCount
        }
        Write-JsonAtomic -Path $HealthFile -Value ([pscustomobject]$health)
    }
    catch {
        Write-Stage "写入健康文件失败：$($_.Exception.Message)"
    }
}

function Get-ExecutionReadinessProblem {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not [Environment]::UserInteractive) { return '当前会话不是交互式桌面。' }
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        return '当前会话未提升为管理员。'
    }
    if ($null -eq (Get-ShineLabWindowProcess)) { return (Get-ShineLabMissingProblem) }
    return $null
}

# ShineLab 未启动的判定文案集中在一处：常驻循环要靠它区分“可等待”和“致命”，
# 两边各写一遍字符串迟早会漂移。
function Get-ShineLabMissingProblem {
    return "未找到 ShineLab 主窗口（标题匹配：$ShineLabWindowPattern）。"
}

function Test-ShineLabProcessMissing {
    param([string]$Problem)

    return ($Problem -eq (Get-ShineLabMissingProblem))
}

# 现场有人在用鼠标键盘时不要抢焦点。GetLastInputInfo 返回全会话最后一次输入的
# tick，减去当前 tick 即空闲毫秒数。Add-Type 只做一次。
function Get-IdleSeconds {
    if (-not ('MesIdle.Win32' -as [type])) {
        Add-Type -Namespace 'MesIdle' -Name 'Win32' -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
[DllImport("user32.dll")]
public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
[DllImport("kernel32.dll")]
public static extern uint GetTickCount();
public static double IdleSeconds()
{
    LASTINPUTINFO lii = new LASTINPUTINFO();
    lii.cbSize = (uint)Marshal.SizeOf(lii);
    if (!GetLastInputInfo(ref lii)) { return -1; }
    return (GetTickCount() - lii.dwTime) / 1000.0;
}
'@
    }
    return [MesIdle.Win32]::IdleSeconds()
}

function Get-Utf8NoBom {
    return [System.Text.UTF8Encoding]::new($false)
}

function Get-ManifestSignaturePayload {
    param([Parameter(Mandatory = $true)]$Manifest)

    $issued = [DateTimeOffset]::Parse([string]$Manifest.issuedAtUtc).ToUnixTimeMilliseconds()
    $expires = [DateTimeOffset]::Parse([string]$Manifest.expiresAtUtc).ToUnixTimeMilliseconds()
    return @(
        [string]$Manifest.schemaVersion
        ([string]$Manifest.batchId).Trim()
        ([string]$Manifest.csvSha256).Trim().ToLowerInvariant()
        ([string]$Manifest.csvFileName).Trim()
        ([string]$Manifest.targetSequence).Trim()
        ([string]([int]$Manifest.expectedRows))
        ([bool]$Manifest.allowAppend).ToString().ToLowerInvariant()
        ([bool]$Manifest.verifyByExport).ToString().ToLowerInvariant()
        ([bool]$Manifest.allowRun).ToString().ToLowerInvariant()
        ([string]$issued)
        ([string]$expires)
        ([string]$Manifest.nonce).Trim()
    ) -join "`n"
}

function Get-SignatureBytes {
    param([Parameter(Mandatory = $true)]$Manifest)

    $payload = Get-ManifestSignaturePayload -Manifest $Manifest
    $hmac = [System.Security.Cryptography.HMACSHA256]::new($script:SigningKey)
    try {
        return $hmac.ComputeHash((Get-Utf8NoBom).GetBytes($payload))
    }
    finally {
        $hmac.Dispose()
    }
}

function Test-ManifestSignature {
    param([Parameter(Mandatory = $true)]$Manifest)

    if (-not $RequireSignedCommands) { return $null }
    if ($null -eq $script:SigningKey) { return '已启用签名校验，但未加载 SigningKeyFile。' }
    if ([string]$Manifest.signatureAlgorithm -ne 'HMAC-SHA256') {
        return '清单缺少受支持的 signatureAlgorithm。'
    }
    foreach ($field in @('issuedAtUtc', 'expiresAtUtc', 'nonce', 'signature')) {
        if ([string]::IsNullOrWhiteSpace([string]$Manifest.$field)) {
            return "清单缺少签名字段：$field。"
        }
    }

    try {
        $issued = [DateTimeOffset]::Parse([string]$Manifest.issuedAtUtc).ToUniversalTime()
        $expires = [DateTimeOffset]::Parse([string]$Manifest.expiresAtUtc).ToUniversalTime()
    }
    catch {
        return '签名时间字段格式无效。'
    }

    $now = [DateTimeOffset]::UtcNow
    if ($issued -gt $now.AddSeconds($MaxClockSkewSeconds)) { return '清单签发时间晚于本机时钟。' }
    if ($expires -lt $now.AddSeconds(-$MaxClockSkewSeconds)) { return '清单已过期。' }
    if ($expires -le $issued) { return '清单有效期无效。' }

    try { $provided = [Convert]::FromBase64String([string]$Manifest.signature) }
    catch { return '清单 signature 不是有效的 Base64。' }
    $expected = Get-SignatureBytes -Manifest $Manifest
    if ($provided.Length -ne $expected.Length) { return '清单签名不匹配。' }

    $different = 0
    for ($i = 0; $i -lt $expected.Length; $i++) { $different = $different -bor ($provided[$i] -bxor $expected[$i]) }
    if ($different -ne 0) { return '清单签名不匹配。' }
    return $null
}

# 冻结的导入内核只在校验模式走 exit 0；实跑路径跑到文件末尾自然结束，
# 不设置 $LASTEXITCODE。StrictMode 下直接读未赋值的变量会抛异常，
# 因此这里按“没有退出码就是没有失败信号”处理，返回 0。
function Get-LastExitCode {
    $code = Get-Variable -Name 'LASTEXITCODE' -Scope Global -ValueOnly -ErrorAction SilentlyContinue
    if ($null -eq $code) { return 0 }
    return [int]$code
}

# 原子写：同目录 .tmp 落盘后 Move 覆盖，避免 MES 读到半截 JSON。
function Write-JsonAtomic {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 8
    $tempPath = "$Path.tmp"
    [System.IO.File]::WriteAllText($tempPath, $json, (Get-Utf8NoBom))
    Move-Item -LiteralPath $tempPath -Destination $Path -Force
}

function Get-FileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Read-Ledger {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return @{}
    }

    $raw = [System.IO.File]::ReadAllText($Path, (Get-Utf8NoBom))
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return @{}
    }

    $table = @{}
    $parsed = $raw | ConvertFrom-Json
    foreach ($property in $parsed.PSObject.Properties) {
        $table[$property.Name] = $property.Value
    }
    return $table
}

function Save-Ledger {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][hashtable]$Ledger
    )

    # PS 5.1 下 hashtable 直接 ConvertTo-Json 可用，但键顺序不稳定；仅作账本用途，可接受。
    Write-JsonAtomic -Path $Path -Value ([pscustomobject]$Ledger)
}
function New-Receipt {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$Status,
        $StartedAtUtc,
        [string]$Error,
        [Nullable[int]]$ObservedRows,
        [string]$ComparisonStatus,
        [Nullable[int]]$DifferenceCount,
        $Evidence
    )

    $receipt = [ordered]@{
        schemaVersion   = '1.0'
        batchId         = [string]$Manifest.batchId
        csvSha256       = [string]$Manifest.csvSha256
        targetSequence  = [string]$Manifest.targetSequence
        status          = $Status
        expectedRows    = [int]$Manifest.expectedRows
        observedRows    = $ObservedRows
        comparisonStatus = $ComparisonStatus
        differenceCount = $DifferenceCount
        startedAtUtc    = $StartedAtUtc
        completedAtUtc  = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        agentVersion    = $script:AgentVersion
        error           = $Error
        evidence        = $Evidence
        # 安全基线：代理从不点击“运行”。
        runTriggered    = $false
    }

    return [pscustomobject]$receipt
}

function Test-Manifest {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$CsvPath
    )

    $problems = [System.Collections.Generic.List[string]]::new()

    if ([string]$Manifest.schemaVersion -ne '1.0') {
        $problems.Add("不支持的 schemaVersion：$($Manifest.schemaVersion)")
    }
    foreach ($field in @('batchId', 'csvSha256', 'csvFileName', 'targetSequence')) {
        if ([string]::IsNullOrWhiteSpace([string]$Manifest.$field)) {
            $problems.Add("清单字段缺失：$field")
        }
    }
    if ([int]$Manifest.expectedRows -le 0) {
        $problems.Add("expectedRows 必须为正数，实际：$($Manifest.expectedRows)")
    }
    # 硬边界：MES 侧即便误发 allowRun，代理也拒绝执行该批次。
    if ([bool]$Manifest.allowRun) {
        $problems.Add('清单请求 allowRun=true，违反安全基线，已拒绝该批次。')
    }
    if (-not [bool]$Manifest.allowAppend) {
        $problems.Add('清单未确认 allowAppend；ShineLab 导入为追加语义，必须由操作人员显式确认。')
    }

    $signatureProblem = Test-ManifestSignature -Manifest $Manifest
    if ($null -ne $signatureProblem) { $problems.Add($signatureProblem) }

    if (-not (Test-Path -LiteralPath $CsvPath)) {
        $problems.Add("CSV 不存在：$CsvPath")
        return $problems
    }

    $actualSha = Get-FileSha256 -Path $CsvPath
    if ($actualSha -ne ([string]$Manifest.csvSha256).ToLowerInvariant()) {
        $problems.Add("CSV 哈希与清单不一致。清单：$($Manifest.csvSha256)；实际：$actualSha")
    }

    $content = [System.IO.File]::ReadAllText($CsvPath, [System.Text.UTF8Encoding]::new($false, $true))
    $lines = @($content -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 })
    if ($lines.Count -eq 0) {
        $problems.Add('CSV 为空。')
        return $problems
    }
    if ($lines[0].TrimEnd(',') -ne $script:ShineCsvHeader) {
        $problems.Add('CSV 表头不符合 ShineLab ExportData.csv 模板。')
    }
    $rowCount = $lines.Count - 1
    if ($rowCount -ne [int]$Manifest.expectedRows) {
        $problems.Add("CSV 行数与 expectedRows 不一致。清单：$($Manifest.expectedRows)；实际：$rowCount")
    }

    # ShineLab 会把这些空字段沿用上一行。若允许空值进入现场，导入后才发现语义变化已经不可回滚；
    # 因此在任何 UI 操作前 fail closed，要求 MES 明确写出实际业务值。
    if ($lines[0].TrimEnd(',') -eq $script:ShineCsvHeader) {
        $columns = @($script:ShineCsvHeader -split ',')
        $rows = @(ConvertFrom-Csv -InputObject $lines -Header $columns | Select-Object -Skip 1)
        $explicitFields = @('样品等级', '处理方法', '清除校正')
        for ($rowIndex = 0; $rowIndex -lt $rows.Count; $rowIndex++) {
            foreach ($field in $explicitFields) {
                if ([string]::IsNullOrWhiteSpace([string]$rows[$rowIndex].$field)) {
                    $problems.Add("CSV 第 $($rowIndex + 2) 行字段[$field]为空；ShineLab 可能继承上一行，必须显式填写。")
                }
            }
        }
    }

    return $problems
}
function Invoke-BatchImport {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][string]$CsvPath,
        [Parameter(Mandatory = $true)][string]$EvidenceDir,
        [switch]$ProbeContextMenu
    )

    $importLogPath = Join-Path $EvidenceDir 'import.log'
    $importArgs = @{
        CsvPath        = $CsvPath
        ExpectedRows   = [int]$Manifest.expectedRows
        TimeoutSeconds = $TimeoutSeconds
    }
    if ($ShineLabPath) { $importArgs['ShineLabPath'] = $ShineLabPath }
    if ($null -ne $GridPointX) { $importArgs['GridPointX'] = [int]$GridPointX }
    if ($null -ne $GridPointY) { $importArgs['GridPointY'] = [int]$GridPointY }
    if ($null -ne $ImportMenuPointX) { $importArgs['ImportMenuPointX'] = [int]$ImportMenuPointX }
    if ($null -ne $ImportMenuPointY) { $importArgs['ImportMenuPointY'] = [int]$ImportMenuPointY }
    if ($ExecuteImport) { $importArgs['ExecuteImport'] = $true }
    if ($ProbeContextMenu) { $importArgs['ProbeContextMenu'] = $true }
    # 追加语义已在 Test-Manifest 中确认，透传给冻结脚本。
    $importArgs['AllowAppend'] = $true
    if ($UseKeyboardMenuFallback) { $importArgs['UseKeyboardMenuFallback'] = $true }
    if ($UseKeyboardFileDialogFallback) { $importArgs['UseKeyboardFileDialogFallback'] = $true }

    Write-Stage "调用导入脚本：$script:ImportScript"
    Set-Variable -Name LASTEXITCODE -Scope Global -Value 0
    $importOutput = & $script:ImportScript @importArgs 2>&1
    # 日志必须先落盘：导入不可回滚，之后任何异常都不能让现场丢掉唯一的动作证据。
    [System.IO.File]::WriteAllText($importLogPath, ($importOutput -join [Environment]::NewLine), (Get-Utf8NoBom))
    $importExitCode = Get-LastExitCode

    return [pscustomobject]@{
        ExitCode = $importExitCode
        LogPath  = $importLogPath
        Output   = $importOutput
    }
}

function Invoke-BatchSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$CsvPath,
        [Parameter(Mandatory = $true)][int]$ExpectedRows,
        [Parameter(Mandatory = $true)][string]$EvidenceDir
    )

    $snapshotDir = Join-Path $EvidenceDir 'before-import'
    $null = New-Item -ItemType Directory -Path $snapshotDir -Force
    $exportArgs = @{
        ExpectedCsvPath = $CsvPath
        ExpectedRows    = $ExpectedRows
        EvidenceDir     = $snapshotDir
        TimeoutSeconds  = $TimeoutSeconds
        SnapshotOnly    = $true
    }
    if ($ShineLabPath) { $exportArgs['ShineLabPath'] = $ShineLabPath }
    if ($null -ne $GridPointX) { $exportArgs['GridPointX'] = [int]$GridPointX }
    if ($null -ne $GridPointY) { $exportArgs['GridPointY'] = [int]$GridPointY }
    if ($null -ne $ExportMenuPointX) { $exportArgs['ExportMenuPointX'] = [int]$ExportMenuPointX }
    if ($null -ne $ExportMenuPointY) { $exportArgs['ExportMenuPointY'] = [int]$ExportMenuPointY }
    if ($UseKeyboardMenuFallback) { $exportArgs['UseKeyboardMenuFallback'] = $true }
    if ($UseKeyboardFileDialogFallback) { $exportArgs['UseKeyboardFileDialogFallback'] = $true }

    Write-Stage '导入前建立只读序列快照。快照成功前不会占用幂等账本，也不会导入。'
    $snapshotLogPath = Join-Path $snapshotDir 'snapshot.log'
    Set-Variable -Name LASTEXITCODE -Scope Global -Value 0
    $snapshotOutput = & $script:ExportScript @exportArgs 2>&1
    [System.IO.File]::WriteAllText($snapshotLogPath, ($snapshotOutput -join [Environment]::NewLine), (Get-Utf8NoBom))
    $snapshotExitCode = Get-LastExitCode
    $snapshotFile = Get-ChildItem -LiteralPath $snapshotDir -Filter 'shinelab-export-*.csv' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1

    if ($snapshotExitCode -ne 0 -or $null -eq $snapshotFile) {
        throw "导入前只读快照失败，未执行导入。详见 $snapshotLogPath"
    }

    return [pscustomobject]@{
        Path = $snapshotFile.FullName
        LogPath = $snapshotLogPath
    }
}

function Invoke-BatchVerify {
    param(
        [Parameter(Mandatory = $true)][string]$CsvPath,
        [Parameter(Mandatory = $true)][int]$ExpectedRows,
        [Parameter(Mandatory = $true)][string]$EvidenceDir,
        [string]$BaselineCsvPath,
        [bool]$AllowAppend = $false
    )

    # 生产追加验证必须用导入前快照证明历史前缀未变，并要求精确新增 N 行。
    $matchMode = if ($AllowAppend) { 'AppendDelta' } else { 'Full' }
    if ($AllowAppend -and [string]::IsNullOrWhiteSpace($BaselineCsvPath)) {
        throw '追加验证缺少导入前快照，不能判定 Verified。'
    }

    $exportArgs = @{
        ExpectedCsvPath = $CsvPath
        ExpectedRows    = $ExpectedRows
        EvidenceDir     = $EvidenceDir
        TimeoutSeconds  = $TimeoutSeconds
        MatchMode       = $matchMode
    }
    if ($AllowAppend) { $exportArgs['BaselineCsvPath'] = $BaselineCsvPath }
    if ($ShineLabPath) { $exportArgs['ShineLabPath'] = $ShineLabPath }
    if ($null -ne $GridPointX) { $exportArgs['GridPointX'] = [int]$GridPointX }
    if ($null -ne $GridPointY) { $exportArgs['GridPointY'] = [int]$GridPointY }
    if ($null -ne $ExportMenuPointX) { $exportArgs['ExportMenuPointX'] = [int]$ExportMenuPointX }
    if ($null -ne $ExportMenuPointY) { $exportArgs['ExportMenuPointY'] = [int]$ExportMenuPointY }
    if ($UseKeyboardMenuFallback) { $exportArgs['UseKeyboardMenuFallback'] = $true }
    if ($UseKeyboardFileDialogFallback) { $exportArgs['UseKeyboardFileDialogFallback'] = $true }

    Write-Stage "调用只读导出比对：$script:ExportScript（对齐口径 $matchMode）"
    $exportLogPath = Join-Path $EvidenceDir 'export.log'
    Set-Variable -Name LASTEXITCODE -Scope Global -Value 0
    $exportOutput = & $script:ExportScript @exportArgs 2>&1
    [System.IO.File]::WriteAllText($exportLogPath, ($exportOutput -join [Environment]::NewLine), (Get-Utf8NoBom))
    $exportExitCode = Get-LastExitCode

    # 导出脚本把比对 JSON 写在 EvidenceDir 下，取最新一个读取判定。
    $comparePath = $null
    $compareFile = Get-ChildItem -LiteralPath $EvidenceDir -Filter 'shinelab-export-compare-*.json' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -ne $compareFile) { $comparePath = $compareFile.FullName }

    $exportedPath = $null
    $exportedFile = Get-ChildItem -LiteralPath $EvidenceDir -Filter 'shinelab-export-*.csv' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($null -ne $exportedFile) { $exportedPath = $exportedFile.FullName }

    $comparison = $null
    if ($null -ne $comparePath) {
        $comparison = [System.IO.File]::ReadAllText($comparePath, (Get-Utf8NoBom)) | ConvertFrom-Json
    }

    return [pscustomobject]@{
        ExitCode     = $exportExitCode
        ComparePath  = $comparePath
        ExportedPath = $exportedPath
        LogPath      = $exportLogPath
        Comparison   = $comparison
    }
}
function Get-EvidenceDirectory {
    param([Parameter(Mandatory = $true)][string]$BatchId)

    $dir = Join-Path $EvidenceRoot $BatchId
    $null = New-Item -ItemType Directory -Path $dir -Force
    return $dir
}

function Invoke-BatchCheck {
    param(
        [Parameter(Mandatory = $true)][System.IO.FileInfo]$ManifestFile,
        [switch]$ProbeContextMenu
    )

    $manifestRaw = [System.IO.File]::ReadAllText($ManifestFile.FullName, (Get-Utf8NoBom))
    $manifest = $manifestRaw | ConvertFrom-Json
    $batchId = [string]$manifest.batchId
    if ([string]::IsNullOrWhiteSpace($batchId)) {
        throw "清单缺少 batchId：$($ManifestFile.FullName)"
    }

    $csvPath = Join-Path $InboxDir ([string]$manifest.csvFileName)
    $problems = @(Test-Manifest -Manifest $manifest -CsvPath $csvPath)
    if ($problems.Count -gt 0) {
        throw "非消耗检查失败：$($problems -join '; ')"
    }

    $key = "{0}|{1}|{2}" -f $batchId, $manifest.csvSha256, $manifest.targetSequence
    $ledger = Read-Ledger -Path $LedgerPath
    if ($ledger.ContainsKey($key)) {
        throw "批次已存在于幂等账本，禁止再次预检或导入：$batchId"
    }

    if ($ProbeContextMenu) {
        $stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss')
        $preflightDir = Join-Path $EvidenceRoot ("preflight-{0}-{1}" -f $batchId, $stamp)
        $null = New-Item -ItemType Directory -Path $preflightDir -Force
        $probe = Invoke-BatchImport -Manifest $manifest -CsvPath $csvPath -EvidenceDir $preflightDir -ProbeContextMenu
        if ($probe.ExitCode -ne 0) {
            throw "现场菜单预检失败；未导入、未占账本。详见 $($probe.LogPath)"
        }
        Write-Stage "现场菜单预检通过：$batchId。未选择菜单、未导入、未写账本/回执，ready 文件保持不变。"
    }
    else {
        Write-Stage "离线清单/CSV 校验通过：$batchId。未访问 ShineLab、未写账本/回执，ready 文件保持不变。"
    }

    return [pscustomobject]@{
        BatchId = $batchId
        CsvPath = $csvPath
        ProbeContextMenu = [bool]$ProbeContextMenu
        Consumed = $false
    }
}

function Invoke-Batch {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$ManifestFile)

    $startedAtUtc = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    $manifestRaw = [System.IO.File]::ReadAllText($ManifestFile.FullName, (Get-Utf8NoBom))
    $manifest = $manifestRaw | ConvertFrom-Json
    $batchId = [string]$manifest.batchId
    if ([string]::IsNullOrWhiteSpace($batchId)) {
        throw "清单缺少 batchId：$($ManifestFile.FullName)"
    }

    Write-Stage "开始处理批次 $batchId（目标序列：$($manifest.targetSequence)）"
    $receiptPath = Join-Path $InboxDir ("{0}.receipt.json" -f $batchId)
    $csvPath = Join-Path $InboxDir ([string]$manifest.csvFileName)
    $evidenceDir = Get-EvidenceDirectory -BatchId $batchId

    $ledger = Read-Ledger -Path $LedgerPath
    $key = "{0}|{1}|{2}" -f $batchId, $manifest.csvSha256, $manifest.targetSequence

    # 幂等：同一批次已处理过就不再重复导入（ShineLab 是追加语义，重复导入会翻倍）。
    if ($ledger.ContainsKey($key)) {
        $previous = $ledger[$key]
        Write-Stage "批次已处理过，跳过导入，复用原判定：$($previous.status)"
        $receipt = New-Receipt -Manifest $manifest -Status ([string]$previous.status) -StartedAtUtc $startedAtUtc `
            -Error '幂等命中：该批次此前已处理，未重复导入。'
        Write-JsonAtomic -Path $receiptPath -Value $receipt
        Move-Item -LiteralPath $ManifestFile.FullName -Destination "$($ManifestFile.FullName).done" -Force
        return $receipt
    }

    # @() 必需：函数返回 List[string] 会被 PowerShell 展开，
    # 0 条问题时变成 $null，StrictMode 下 $null.Count 直接抛异常。
    $problems = @(Test-Manifest -Manifest $manifest -CsvPath $csvPath)
    if ($problems.Count -gt 0) {
        $message = $problems -join '; '
        Write-Stage "批次校验失败：$message"
        $receipt = New-Receipt -Manifest $manifest -Status 'Failed' -StartedAtUtc $startedAtUtc -Error $message
        Write-JsonAtomic -Path $receiptPath -Value $receipt
        Move-Item -LiteralPath $ManifestFile.FullName -Destination "$($ManifestFile.FullName).rejected" -Force
        return $receipt
    }

    $evidence = [ordered]@{
        importLogPath = $null
        baselineCsvPath = $null
        baselineSnapshotLogPath = $null
        exportedCsvPath = $null
        comparisonJsonPath = $null
        screenshotPaths = @()
    }
    $baselineCsvPath = $null

    # 追加导入不可回滚：先用只读导出冻结历史前缀。快照成功前不占账本、不发导入。
    if ($ExecuteImport -and [bool]$manifest.allowAppend -and [bool]$manifest.verifyByExport) {
        try {
            $snapshot = Invoke-BatchSnapshot -CsvPath $csvPath -ExpectedRows ([int]$manifest.expectedRows) -EvidenceDir $evidenceDir
            $baselineCsvPath = $snapshot.Path
            $evidence['baselineCsvPath'] = $snapshot.Path
            $evidence['baselineSnapshotLogPath'] = $snapshot.LogPath
        }
        catch {
            $message = "导入前快照失败，未执行导入、未占用幂等账本：$($_.Exception.Message)"
            Write-Stage $message
            $receipt = New-Receipt -Manifest $manifest -Status 'Failed' -StartedAtUtc $startedAtUtc `
                -Error $message -Evidence ([pscustomobject]$evidence)
            Write-JsonAtomic -Path $receiptPath -Value $receipt
            Move-Item -LiteralPath $ManifestFile.FullName -Destination "$($ManifestFile.FullName).rejected" -Force
            return $receipt
        }
    }

    # 先占账本：导入动作一旦发出就不可回滚，崩溃后也不能重放。
    $ledger[$key] = [pscustomobject]@{ status = 'Importing'; startedAtUtc = $startedAtUtc; agentVersion = $script:AgentVersion }
    Save-Ledger -Path $LedgerPath -Ledger $ledger

    $status = 'Unknown'
    $errorMessage = $null
    $observedRows = $null
    $comparisonStatus = $null
    $differenceCount = $null

    try {
        $import = Invoke-BatchImport -Manifest $manifest -CsvPath $csvPath -EvidenceDir $evidenceDir
        $evidence['importLogPath'] = $import.LogPath

        if ($import.ExitCode -ne 0) {
            $status = 'Failed'
            $errorMessage = "导入脚本退出码 $($import.ExitCode)。详见 $($import.LogPath)"
        }
        elseif (-not $ExecuteImport) {
            # 演练模式：脚本只做定位与校验，没有真正提交导入。
            $status = 'Ready'
            $errorMessage = '演练模式（未指定 -ExecuteImport），未实际提交导入。'
        }
        elseif (-not [bool]$manifest.verifyByExport) {
            $status = 'Submitted'
            $errorMessage = '清单未要求导出比对，无法判定 Verified。'
        }
        else {
            $verify = Invoke-BatchVerify -CsvPath $csvPath -ExpectedRows ([int]$manifest.expectedRows) `
                -EvidenceDir $evidenceDir -BaselineCsvPath $baselineCsvPath -AllowAppend ([bool]$manifest.allowAppend)
            $evidence['exportedCsvPath'] = $verify.ExportedPath
            $evidence['comparisonJsonPath'] = $verify.ComparePath

            if ($null -eq $verify.Comparison) {
                $status = 'Unknown'
                $errorMessage = "导入已提交，但导出比对未产出结果，无法判定。详见 $($verify.LogPath)"
            }
            else {
                $comparisonStatus = [string]$verify.Comparison.status
                $differenceCount = [int]$verify.Comparison.differenceCount
                $observedRows = [int]$verify.Comparison.actual.rowCount
                $isDeterministicDelta =
                    [string]$verify.Comparison.matchMode -eq 'AppendDelta' -and
                    [bool]$verify.Comparison.baselineUnchanged -and
                    [int]$verify.Comparison.appendedRowCount -eq [int]$manifest.expectedRows
                if ($comparisonStatus -eq 'Match' -and $isDeterministicDelta) {
                    $status = 'Verified'
                }
                else {
                    $status = 'Failed'
                    $errorMessage = "导出增量比对未满足确定性门禁：差异 $differenceCount 处。详见 $($verify.ComparePath)"
                }
            }
        }
    }
    catch {
        # 导入可能已部分生效，因此判 Unknown 而不是 Failed，交人工处置，不自动重试。
        $status = 'Unknown'
        $errorMessage = "代理异常：$($_.Exception.Message)"
        Write-Stage $errorMessage
    }

    $screenshots = @(Get-ChildItem -LiteralPath $evidenceDir -Filter '*.png' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc | Select-Object -ExpandProperty FullName)
    $evidence['screenshotPaths'] = $screenshots

    $receipt = New-Receipt -Manifest $manifest -Status $status -StartedAtUtc $startedAtUtc -Error $errorMessage `
        -ObservedRows $observedRows -ComparisonStatus $comparisonStatus -DifferenceCount $differenceCount `
        -Evidence ([pscustomobject]$evidence)

    $ledger[$key] = [pscustomobject]@{ status = $status; startedAtUtc = $startedAtUtc; completedAtUtc = $receipt.completedAtUtc; agentVersion = $script:AgentVersion }
    Save-Ledger -Path $LedgerPath -Ledger $ledger
    Write-JsonAtomic -Path $receiptPath -Value $receipt
    Move-Item -LiteralPath $ManifestFile.FullName -Destination "$($ManifestFile.FullName).done" -Force

    Write-Stage "批次 $batchId 处理完成：$status"
    return $receipt
}
if (-not (Test-Path -LiteralPath $InboxDir)) {
    throw "交接目录不存在：$InboxDir"
}
$InboxDir = (Resolve-Path -LiteralPath $InboxDir).ProviderPath

if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $InboxDir 'evidence'
}
$null = New-Item -ItemType Directory -Path $EvidenceRoot -Force
$EvidenceRoot = (Resolve-Path -LiteralPath $EvidenceRoot).ProviderPath

if ([string]::IsNullOrWhiteSpace($HealthFile)) {
    $HealthFile = Join-Path $EvidenceRoot 'agent-health.json'
}
else {
    $healthParent = Split-Path -Parent $HealthFile
    if (-not [string]::IsNullOrWhiteSpace($healthParent)) {
        $null = New-Item -ItemType Directory -Path $healthParent -Force
    }
    $HealthFile = [IO.Path]::GetFullPath($HealthFile)
}

if ([string]::IsNullOrWhiteSpace($LedgerPath)) {
    $LedgerPath = Join-Path $EvidenceRoot 'batch-ledger.json'
}

if ($RequireSignedCommands) {
    if ([string]::IsNullOrWhiteSpace($SigningKeyFile) -or -not (Test-Path -LiteralPath $SigningKeyFile)) {
        throw '已启用 -RequireSignedCommands，但 SigningKeyFile 不存在。'
    }
    $keyText = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $SigningKeyFile).ProviderPath, (Get-Utf8NoBom)).Trim()
    try {
        $script:SigningKey = [Convert]::FromBase64String($keyText)
    }
    catch {
        throw 'SigningKeyFile 必须包含 Base64 编码的随机密钥。'
    }
    if ($script:SigningKey.Length -lt 32) {
        throw 'SigningKeyFile 中的 HMAC 密钥至少需要 32 字节。'
    }
}

if (-not (Test-Path -LiteralPath $script:ImportScript)) {
    throw "未找到导入脚本：$script:ImportScript"
}
if (-not (Test-Path -LiteralPath $script:ExportScript)) {
    throw "未找到导出比对脚本：$script:ExportScript"
}

if ([string]::IsNullOrWhiteSpace($DaemonLogDir)) {
    $DaemonLogDir = Join-Path $EvidenceRoot 'agent-log'
}
if (-not $RunOnce) {
    Set-DaemonLogPath -Directory $DaemonLogDir
}

$stopFilePath = Join-Path $InboxDir $StopFileName

Write-Stage "代理版本：$script:AgentVersion"
Write-Stage "交接目录：$InboxDir"
Write-Stage "证据目录：$EvidenceRoot"
Write-Stage "幂等账本：$LedgerPath"
if ($ValidateOnly) {
    Write-Stage '运行模式：离线非消耗校验；不访问 ShineLab，不写账本/回执，不移动 ready 文件。'
}
elseif ($PreflightOnly) {
    Write-Stage '运行模式：现场非消耗菜单预检；不选择菜单、不导入、不写账本/回执，不移动 ready 文件。'
}

if ($RunOnce) {
    Write-Stage '运行模式：单次（-RunOnce），处理完当前批次即退出。'
}
else {
    Write-Stage "运行模式：常驻轮询，间隔 $PollSeconds 秒。"
    Write-Stage "心跳间隔：$HeartbeatMinutes 分钟"
    Write-Stage "运行日志：$script:DaemonLogPath"
    Write-Stage "停机哨兵：$stopFilePath（建立该文件即在当前批次结束后退出）"
    if ($RequireIdleSeconds -gt 0) {
        Write-Stage "空闲门槛：导入前要求键鼠空闲 $RequireIdleSeconds 秒"
    }

    # 常驻模式下没人盯着屏幕，缺开关会让每个批次都卡在自绘菜单上并烧掉批次号
    # （batch-06 就是这么丢的）。这里直接拒绝启动，而不是跑起来再一个个失败。
    if ($ExecuteImport -and -not ($UseKeyboardMenuFallback -and $UseKeyboardFileDialogFallback)) {
        throw '常驻模式必须同时指定 -UseKeyboardMenuFallback 和 -UseKeyboardFileDialogFallback，否则 ShineLab 自绘菜单不会被点击，每个批次都会超时并烧掉批次号。'
    }
}

Write-Stage '安全基线：本代理不会点击“运行”，启动分析必须现场人工确认。'
Write-Health -Status 'Starting' -Reason '代理正在启动。'

# 启动自检分两类，不能一视同仁：
#   会话本身不对（非交互 / 未提升）—— 永远不会自己变好，直接退出。
#   ShineLab 没开 —— 是暂时状态。常驻代理必须等它，不能退出：登录自启只延迟
#   2 分钟，ShineLab 往往还没起来，若在此处 throw，自启就永远失败（现场实测
#   任务计划“上次运行结果 1”、agent-health.json reason=未找到 ShineLab 进程）。
# 注意：ShineLab 检查只是从“启动即致命”降级为“逐轮等待”，导入前那道检查不能
# 去掉，否则会对着没开的 ShineLab 发点击并白烧一个批次号。
if ($ExecuteImport -or $PreflightOnly) {
    $startupProblem = Get-ExecutionReadinessProblem
    if ($null -ne $startupProblem) {
        Write-Health -Status 'Blocked' -Reason $startupProblem
        if ($RunOnce -or -not (Test-ShineLabProcessMissing -Problem $startupProblem)) {
            throw $startupProblem
        }
        Write-Stage "$startupProblem 常驻模式将持续等待，ShineLab 启动后自动恢复。"
    }
}

$lastHeartbeat = Get-Date
$processedCount = 0
$checkFailed = $false

do {
    if (-not $RunOnce -and (Test-Path -LiteralPath $stopFilePath)) {
        Write-Stage "检测到停机哨兵 $StopFileName，代理准备退出。"
        Remove-Item -LiteralPath $stopFilePath -Force -ErrorAction SilentlyContinue
        break
    }

    # 轮询本身不能因为共享目录瞬断而终止常驻进程。
    try {
        $pending = @(Get-ChildItem -LiteralPath $InboxDir -Filter '*.manifest.ready' -ErrorAction Stop |
            Sort-Object CreationTimeUtc)
    }
    catch {
        if ($RunOnce) { throw }
        Write-Stage "扫描交接目录失败（将继续重试）：$($_.Exception.Message)"
        Start-Sleep -Seconds $PollSeconds
        continue
    }

    if ($pending.Count -eq 0) {
        Write-Health -Status 'Ready' -Reason '等待批次。' -ProcessedCount $processedCount -PendingCount 0
        if ($RunOnce) {
            Write-Stage '没有待处理批次。'
            break
        }

        if (((Get-Date) - $lastHeartbeat).TotalMinutes -ge $HeartbeatMinutes) {
            Write-Stage "心跳：等待批次中，已处理 $processedCount 个批次。"
            $lastHeartbeat = Get-Date
        }

        Start-Sleep -Seconds $PollSeconds
        continue
    }

    # 串行处理：ShineLab 是单实例前台应用，并发导入会互相抢焦点。
    Write-Health -Status 'Ready' -Reason '发现待处理批次。' -ProcessedCount $processedCount -PendingCount $pending.Count
    foreach ($manifestFile in $pending) {
        if (-not $RunOnce -and $RequireIdleSeconds -gt 0 -and $ExecuteImport) {
            # 等到现场的人停手为止。批次号在导入调用之前才登记，
            # 所以在这里等待不会烧掉任何批次号。
            $waited = 0
            while ($true) {
                $idle = Get-IdleSeconds
                if ($idle -lt 0 -or $idle -ge $RequireIdleSeconds) { break }
                if ($waited % 60 -eq 0) {
                    Write-Stage ("检测到键鼠活动（空闲 {0:N0} 秒 < {1} 秒），推迟导入：{2}" -f $idle, $RequireIdleSeconds, $manifestFile.Name)
                }
                Start-Sleep -Seconds 5
                $waited += 5
                if (Test-Path -LiteralPath $stopFilePath) { break }
            }
            if (Test-Path -LiteralPath $stopFilePath) { break }
        }

        try {
            if ($ExecuteImport -or $PreflightOnly) {
                $readinessProblem = Get-ExecutionReadinessProblem
                if ($null -ne $readinessProblem) {
                    Write-Health -Status 'Blocked' -Reason $readinessProblem -ProcessedCount $processedCount -PendingCount $pending.Count
                    Write-Stage "暂不处理批次，执行条件不满足：$readinessProblem"
                    Start-Sleep -Seconds $PollSeconds
                    continue
                }
            }
            if ($ValidateOnly -or $PreflightOnly) {
                Write-Health -Status 'Checking' -Reason "非消耗检查 $($manifestFile.Name)" -ProcessedCount $processedCount -PendingCount $pending.Count
                $null = Invoke-BatchCheck -ManifestFile $manifestFile -ProbeContextMenu:$PreflightOnly
                $processedCount++
                Write-Health -Status 'Ready' -Reason '非消耗检查完成；批次仍保持 ready。' -ProcessedCount $processedCount -PendingCount $pending.Count
                break
            }

            Write-Health -Status 'Importing' -Reason "处理 $($manifestFile.Name)" -ProcessedCount $processedCount -PendingCount $pending.Count
            $null = Invoke-Batch -ManifestFile $manifestFile
            $processedCount++
            Write-Health -Status 'Ready' -Reason '批次处理完成。' -ProcessedCount $processedCount -PendingCount ($pending.Count - 1)
        }
        catch {
            Write-Health -Status 'Unknown' -Reason $_.Exception.Message -ProcessedCount $processedCount -PendingCount $pending.Count
            Write-Stage "批次处理抛出未捕获异常：$($_.Exception.Message)"
            if ($ValidateOnly -or $PreflightOnly) {
                # 非消耗检查失败也必须保留 ready 文件，修正输入或环境后可再次检查。
                $checkFailed = $true
                break
            }
            $failedName = "$($manifestFile.FullName).error"
            Move-Item -LiteralPath $manifestFile.FullName -Destination $failedName -Force
            $processedCount++
        }

        $lastHeartbeat = Get-Date
    }

    if ($RunOnce) { break }
    Start-Sleep -Seconds $PollSeconds
} while ($true)

Write-Stage "代理已退出。本次共处理或检查 $processedCount 个批次。"
if ($checkFailed) { exit 1 }
exit 0
