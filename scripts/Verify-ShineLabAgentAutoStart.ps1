<#
.SYNOPSIS
    在控制电脑上验证“登录自启”这条链路真的通了，且不导入任何批次。

.DESCRIPTION
    运行位置：离子色谱控制电脑，管理员 PowerShell。

    为什么需要这个脚本而不是直接重启计划任务：
    上一次失败的表现是“任务注册成功、状态 Ready、上次运行结果 1”——任务计划只
    告诉你退出码，不告诉你为什么。这里把判断依据集中起来：先读健康文件拿到
    退出原因，再以空跑模式亲自拉起一轮，确认代理这次会“挂住等待”而不是退出。

    为什么用空跑（-DryRun）：
    实跑一旦遇到待处理批次就会真的导入，而导入不可逆、认领即烧批次号。
    先证明自启链路通，再切实跑，代价差别很大。

    安全边界：不点击“运行”，不写串口/寄存器，不认领批次号。
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'ShineLab-MES-BatchAgent',

    [string]$InboxDir = 'C:\MES-RPA\inbox',

    # 空跑观察时长。默认 90 秒足够看到启动横幅 + 至少一轮轮询。
    [int]$ObserveSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Write-Host '[自启验证] 版本：verify-autostart-2026-08-26.1'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '[自启验证] 需要管理员 PowerShell：非管理员会话发出的右键会被 ShineLab 吞掉。'
    exit 2
}

$scriptDir = Split-Path -Parent $PSCommandPath
$launcher = Join-Path $scriptDir 'Start-ShineLabAgentResident.ps1'
if (-not (Test-Path -LiteralPath $launcher)) {
    Write-Host "[自启验证] 未找到常驻启动器：$launcher"
    exit 2
}

Write-Host ''
Write-Host '=== 1. 确认共享上的代理已是修复版 ==='
$agent = Join-Path $scriptDir 'Start-ShineLabBatchAgent.ps1'
$text = [System.IO.File]::ReadAllText($agent, (New-Object System.Text.UTF8Encoding($true, $true)))
$hasFix = $text -match 'Test-ShineLabProcessMissing -Problem \$startupProblem'
$gateIntact = $text -match '\$readinessProblem = Get-ExecutionReadinessProblem'
Write-Host "  ShineLab 未启动降级为可等待：$hasFix"
Write-Host "  导入前检查仍在：$gateIntact"
if (-not $hasFix -or -not $gateIntact) {
    Write-Host '  [自启验证] 代理不是预期版本，先重新部署再验证。'
    exit 3
}

Write-Host ''
Write-Host '=== 2. 计划任务当前状态 ==='
$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $task) {
    Write-Host "  未注册任务：$TaskName（先跑 Register-ShineLabAgentTask.ps1）"
}
else {
    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    Write-Host "  状态：$($task.State)"
    Write-Host "  上次运行：$($info.LastRunTime)  上次结果：$($info.LastTaskResult)"
    if ($task.State -eq 'Running') {
        Write-Host '  [自启验证] 任务正在运行，先停掉再空跑，避免两个代理抢 ShineLab 焦点。'
        Stop-ScheduledTask -TaskName $TaskName
        Start-Sleep -Seconds 3
        Write-Host "  已停止，现在状态：$((Get-ScheduledTask -TaskName $TaskName).State)"
    }
}

Write-Host ''
Write-Host '=== 3. 上一次失败留下的原因（健康文件） ==='
$health = Join-Path $InboxDir 'evidence\agent-health.json'
if (Test-Path -LiteralPath $health) {
    $j = Get-Content -LiteralPath $health -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "  status=$($j.status)"
    Write-Host "  reason=$($j.reason)"
    Write-Host "  interactive=$($j.interactive) elevated=$($j.elevated) shineLabRunning=$($j.shineLabRunning)"
}
else {
    Write-Host '  健康文件还不存在。'
}

Write-Host ''
Write-Host "=== 4. 空跑 $ObserveSeconds 秒：只校验批次写回执，不导入 ==="
Write-Host '  期望看到：启动横幅之后进入轮询；若 ShineLab 没开，写 Blocked 心跳并继续等待，而不是退出。'

# 用 Start-Process -PassThru 而不是 Start-Job：作业里再套一层 powershell.exe，
# Stop-Job 只杀作业宿主，孙进程会活下来继续轮询（实测残留 pid=4564 一直在写心跳，
# 而它处于空跑模式，MES 下发的批次会被它先吃掉并写回执）。启动器用 & 调用代理，
# 二者同进程，所以这里拿到的 pid 就是代理本体。
$outFile = Join-Path $env:TEMP 'shinelab-verify-dryrun.out'
$errFile = Join-Path $env:TEMP 'shinelab-verify-dryrun.err'
$proc = Start-Process -FilePath 'powershell.exe' `
    -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $launcher,
        '-InboxDir', $InboxDir, '-DryRun') `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile `
    -WindowStyle Hidden -PassThru

$deadline = (Get-Date).AddSeconds($ObserveSeconds)
while ((Get-Date) -lt $deadline -and -not $proc.HasExited) {
    Start-Sleep -Seconds 5
}

$stillAlive = -not $proc.HasExited
Write-Host ""
Write-Host "  观察结束，进程 pid=$($proc.Id) 已退出：$($proc.HasExited)"
Write-Host "  仍在运行（这就是期望结果）：$stillAlive"

# 收尾用设计好的停机哨兵，不用 Stop-Process：代理只在批次边界退出，
# 不会把导入撕在半路。哨兵必须删掉，否则正式计划任务一起来就立刻退出。
if ($stillAlive) {
    $sentinel = Join-Path $InboxDir 'agent.stop'
    Write-Host '  空跑收尾：建立 agent.stop，等代理自行退出。'
    Set-Content -LiteralPath $sentinel -Value 'verify dry-run teardown' -Encoding ASCII
    $waited = 0
    while (-not $proc.HasExited -and $waited -lt 60) { Start-Sleep -Seconds 3; $waited += 3 }
    if (-not $proc.HasExited) {
        Write-Host '  哨兵未能收住，改为直接结束进程（空跑不涉及导入，可安全终止）。'
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
    }
    Remove-Item -LiteralPath $sentinel -Force -ErrorAction SilentlyContinue
    Write-Host "  哨兵已清除：$(-not (Test-Path -LiteralPath $sentinel))"
}

$output = ''
foreach ($f in @($outFile, $errFile)) {
    if (Test-Path -LiteralPath $f) {
        $output += (Get-Content -LiteralPath $f -Raw -ErrorAction SilentlyContinue)
        Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '=== 5. 磁盘日志尾部（判断依据） ==='
$logDir = Join-Path $InboxDir 'evidence\agent-log'
if (Test-Path -LiteralPath $logDir) {
    $log = Get-ChildItem -LiteralPath $logDir -Filter 'agent-*.log' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($log) {
        Write-Host "  日志：$($log.FullName)"
        Get-Content -LiteralPath $log.FullName -Tail 15 -Encoding UTF8 |
            ForEach-Object { Write-Host "    $_" }
    }
}

Write-Host ''
Write-Host '=== 结论 ==='
if ($stillAlive) {
    Write-Host '  空跑保持常驻：自启链路已修好。'
    Write-Host '  下一步（实跑，会真导入）：'
    Write-Host "    Start-ScheduledTask -TaskName $TaskName"
    Write-Host '  停机：在交接目录建立 agent.stop 文件。'
    exit 0
}
else {
    Write-Host '  空跑提前退出了，修复未生效或另有原因。以下是捕获到的输出：'
    Write-Host $output
    exit 4
}
