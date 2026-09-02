<#
.SYNOPSIS
    常驻代理启动器：把现场验证过的参数固化成一条命令，操作员不需要记任何开关。

.DESCRIPTION
    运行位置：离子色谱控制电脑，需管理员 PowerShell（或由登录时计划任务拉起）。

    为什么要这个启动器而不是直接调 Start-ShineLabBatchAgent.ps1：
    常驻代理的命令行很长，而其中两个开关一旦漏掉，ShineLab 自绘菜单不会被点击，
    每个批次都会超时；而批次号在导入前就已被账本认领，漏一个开关就烧掉一个批次号。
    把它们固化在这里，人就不可能漏。

    固化的参数及依据：
      -UseKeyboardMenuFallback     ShineLab 自绘弹出菜单，UI Automation 看不见，
                                   必须靠键盘 End 选中最后一项（已验证顺序：导出CSV、从CSV导入）。
      -UseKeyboardFileDialogFallback  原生文件对话框 class=#32770 title=打开。
      -GridPointX/Y 910,683        1920x1080 下命中网格 class=XTPReport 的坐标。
      -RequireIdleSeconds 90       RPA 靠模拟点击工作；现场有人正在用这台机器时
                                   启动导入会打偏，所以等键鼠空闲再动手。

    安全边界：不点击“运行”，不写串口/寄存器；MES 侧不获得远程管理员桌面控制能力。
#>
[CmdletBinding()]
param(
    [string]$InboxDir = 'C:\MES-RPA\inbox',

    # 空跑：不加 -ExecuteImport，只校验批次并写回执，不真的导入。
    # 首次部署建议先这样跑一轮，确认目录、权限、日志都通了。
    [switch]$DryRun,

    # 覆盖屏幕坐标。仅当分辨率不是 1920x1080 时才需要，
    # 用 Get-ShineLabGridPoint.ps1 重新取点。
    [Nullable[int]]$GridPointX,
    [Nullable[int]]$GridPointY,

    [int]$RequireIdleSeconds = 90,

    [int]$PollSeconds = 5,

    [int]$HeartbeatMinutes = 10
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:LauncherVersion = 'resident-launcher-2026-08-26.1'

Write-Host "[常驻启动器] 版本：$script:LauncherVersion"

$scriptDir = Split-Path -Parent $PSCommandPath
$agent = Join-Path $scriptDir 'Start-ShineLabBatchAgent.ps1'
if (-not (Test-Path -LiteralPath $agent)) {
    Write-Host "[常驻启动器] 未找到批次代理：$agent"
    exit 2
}

# 管理员检查：ShineDataAcquire.exe 以 highestAvailable 运行，普通完整性级别会话
# 发出的右键会被系统吞掉（现场表现为 采样点 0 / 范围 none）。这不是可选项。
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host '[常驻启动器] 需要管理员 PowerShell：非管理员会话发出的右键会被 ShineLab 吞掉。'
    exit 2
}

if (-not (Test-Path -LiteralPath $InboxDir)) {
    Write-Host "[常驻启动器] 交接目录不存在：$InboxDir"
    exit 2
}

# 上一轮的停机哨兵若没清掉，代理会立刻退出，看起来像“启动了但没反应”。
$stale = Join-Path $InboxDir 'agent.stop'
if (Test-Path -LiteralPath $stale) {
    Write-Host '[常驻启动器] 清理上一轮遗留的停机哨兵 agent.stop。'
    Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue
}

$agentArgs = @{
    InboxDir                      = $InboxDir
    PollSeconds                   = $PollSeconds
    HeartbeatMinutes              = $HeartbeatMinutes
    RequireIdleSeconds            = $RequireIdleSeconds
    UseKeyboardMenuFallback       = $true
    UseKeyboardFileDialogFallback = $true
    GridPointX                    = if ($null -ne $GridPointX) { $GridPointX } else { 910 }
    GridPointY                    = if ($null -ne $GridPointY) { $GridPointY } else { 683 }
}

if ($DryRun) {
    Write-Host '[常驻启动器] 空跑模式：不导入，只校验批次并写回执。'
}
else {
    $agentArgs['ExecuteImport'] = $true
    Write-Host '[常驻启动器] 实跑模式：MES 下发批次后将无人值守真实导入。'
}

Write-Host "[常驻启动器] 交接目录：$InboxDir"
Write-Host ("[常驻启动器] 网格坐标：{0},{1}" -f $agentArgs['GridPointX'], $agentArgs['GridPointY'])
Write-Host "[常驻启动器] 空闲门槛：$RequireIdleSeconds 秒"
Write-Host '[常驻启动器] 停机方式：在交接目录建立 agent.stop 文件。'
Write-Host '[常驻启动器] 移交批次代理，以下为代理输出。'

& $agent @agentArgs
exit $LASTEXITCODE
