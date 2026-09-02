<#
.SYNOPSIS
    把常驻代理注册成“登录时自动启动”的计划任务，操作员开机后不需要再输入任何指令。

.DESCRIPTION
    运行位置：离子色谱控制电脑，需管理员 PowerShell。只注册任务，不立刻启动导入。

    为什么用“仅在用户登录时运行”而不是 Windows 服务：
    RPA 靠模拟鼠标键盘操作 ShineLab 界面，必须有一个已登录的交互桌面。服务会话
    （Session 0）没有可见桌面，右键和文件对话框都不可靠。

    为什么要“以最高权限运行”：
    ShineDataAcquire.exe 以 highestAvailable 运行，普通完整性级别会话发出的右键会
    被系统吞掉（现场表现为 采样点 0 / 范围 none）。

    安全边界：任务只在本机交互桌面运行，MES 侧不获得远程管理员桌面控制能力；
    代理本体依旧不点击“运行”，不写串口/寄存器。
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'ShineLab-MES-BatchAgent',

    [string]$InboxDir = 'C:\MES-RPA\inbox',

    # 默认注册当前登录用户。跨用户注册时传对方账号，如 'DOMAIN\operator'。
    [string]$UserId,

    # 登录后延迟启动，等 ShineLab 和网络共享就绪。ISO 8601 duration。
    [string]$StartupDelay = 'PT2M',

    # 只打印将要注册的内容，不实际写入任务计划。
    [switch]$WhatIfOnly,

    # 注册完立刻启动一次，省得为了验证去重启。
    [switch]$StartNow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $PSCommandPath
$launcher = Join-Path $scriptDir 'Start-ShineLabAgentResident.ps1'

Write-Host '[注册计划任务] 版本：register-agent-task-2026-08-26.1'

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "未找到常驻启动器：$launcher"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ([string]::IsNullOrWhiteSpace($UserId)) {
    $UserId = $identity.Name
}

$launcherFull = (Resolve-Path -LiteralPath $launcher).ProviderPath
$psExe = Join-Path $PSHOME 'powershell.exe'

# -File 而不是 -Command：路径里有空格或 UNC 时 -Command 的引号规则很容易出错。
# -NonInteractive 不加，因为 RPA 需要真实交互桌面；-WindowStyle Hidden 也不加，
# 现场需要能看到代理在跑。
$taskArgs = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -InboxDir "{1}"' -f $launcherFull, $InboxDir

Write-Host "[注册计划任务] 任务名称：$TaskName"
Write-Host "[注册计划任务] 运行账号：$UserId（仅在此用户登录时运行，以最高权限）"
Write-Host "[注册计划任务] 启动程序：$psExe"
Write-Host "[注册计划任务] 启动参数：$taskArgs"
Write-Host "[注册计划任务] 登录后延迟：$StartupDelay"

if ($WhatIfOnly) {
    Write-Host '[注册计划任务] -WhatIfOnly：以上内容未写入任务计划。'
    if (-not $isAdmin) {
        Write-Host '[注册计划任务] 当前不是管理员会话；真正注册时必须用管理员 PowerShell。'
    }
    return
}

# 管理员校验放在 -WhatIfOnly 之后：预览不写任何东西，普通窗口也该能看。
# 真正注册需要 RunLevel Highest，非管理员会话必须挡下。
if (-not $isAdmin) {
    Write-Host '[注册计划任务] 注册“以最高权限运行”的计划任务需要管理员 PowerShell。'
    exit 2
}

$action = New-ScheduledTaskAction -Execute $psExe -Argument $taskArgs -WorkingDirectory $scriptDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $UserId
$trigger.Delay = $StartupDelay

# Interactive + Highest：交互桌面 + 提升权限，两者缺一不可，理由见文件头说明。
$taskPrincipal = New-ScheduledTaskPrincipal -UserId $UserId -LogonType Interactive -RunLevel Highest

# 代理是常驻进程，必须关掉执行时限，否则默认 3 天后会被任务计划杀掉。
# 同一时刻只允许一个实例：ShineLab 是单实例前台应用，并发会互抢焦点。
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 5)

$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host '[注册计划任务] 同名任务已存在，就地更新。'
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

$null = Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $taskPrincipal `
    -Settings $settings `
    -Description '消费 MES 下发的 ShineLab 样品任务批次（常驻轮询，不点击运行）。'

$check = Get-ScheduledTask -TaskName $TaskName
Write-Host "[注册计划任务] 注册完成，当前状态：$($check.State)"

if ($StartNow) {
    Start-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 3
    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    Write-Host "[注册计划任务] 已手动启动一次，上次运行结果：$($info.LastTaskResult)"
}

Write-Host '[注册计划任务] 停机方法：在交接目录建立 agent.stop 文件，代理会在当前批次结束后退出。'
Write-Host "[注册计划任务] 取消自启：Unregister-ScheduledTask -TaskName $TaskName -Confirm:`$false"
