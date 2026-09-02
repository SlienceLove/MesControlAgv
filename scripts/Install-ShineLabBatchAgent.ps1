<#[
.SYNOPSIS
    将 ShineLab 批次代理安装为控制电脑上的交互式登录任务。

.DESCRIPTION
    UI 自动化必须运行在已登录的交互式桌面，因此这里使用 Windows Task Scheduler 的
    AtLogOn + InteractiveToken，而不是 Windows Service。任务只启动固定的 Agent 脚本，
    不提供任意 PowerShell 或任意程序执行能力。

    安装/更新任务需要在控制电脑上以管理员 PowerShell 执行一次。之后 MES 只通过交接
    协议投递批次，Agent 在登录后自动常驻消费。

    卸载：
      .\Install-ShineLabBatchAgent.ps1 -Uninstall -TaskName MES-ShineLabBatchAgent
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$TaskName = 'MES-ShineLabBatchAgent',
    [string]$AgentScriptPath = (Join-Path $PSScriptRoot 'Start-ShineLabBatchAgent.ps1'),
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$InboxDir,
    [string]$EvidenceRoot,
    [string]$LedgerPath,
    [string]$HealthFile,
    [string]$ShineLabPath,
    [string]$SigningKeyFile,
    [switch]$RequireSignedCommands,
    [int]$PollSeconds = 5,
    [int]$TimeoutSeconds = 60,
    [int]$RequireIdleSeconds = 30,
    [string]$RunAsUser = "$env:USERDOMAIN\$env:USERNAME",
    [switch]$Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Uninstall) {
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        Write-Host "任务不存在：$TaskName"
        exit 0
    }
    if ($PSCmdlet.ShouldProcess($TaskName, '删除 ShineLab Agent 登录任务')) {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "已删除任务：$TaskName"
    }
    exit 0
}

foreach ($path in @($AgentScriptPath, $InboxDir)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "路径不存在：$path" }
}
$AgentScriptPath = (Resolve-Path -LiteralPath $AgentScriptPath).ProviderPath
$InboxDir = (Resolve-Path -LiteralPath $InboxDir).ProviderPath
if (-not [string]::IsNullOrWhiteSpace($EvidenceRoot)) { $EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot) }
if (-not [string]::IsNullOrWhiteSpace($LedgerPath)) { $LedgerPath = [IO.Path]::GetFullPath($LedgerPath) }
if (-not [string]::IsNullOrWhiteSpace($HealthFile)) { $HealthFile = [IO.Path]::GetFullPath($HealthFile) }
if (-not [string]::IsNullOrWhiteSpace($SigningKeyFile)) {
    if (-not (Test-Path -LiteralPath $SigningKeyFile)) { throw "SigningKeyFile 不存在：$SigningKeyFile" }
    $SigningKeyFile = (Resolve-Path -LiteralPath $SigningKeyFile).ProviderPath
}
if ($RequireSignedCommands -and [string]::IsNullOrWhiteSpace($SigningKeyFile)) {
    throw '启用 -RequireSignedCommands 时必须提供 -SigningKeyFile。'
}

$argumentParts = [System.Collections.Generic.List[string]]::new()
$argumentParts.Add('-NoProfile')
$argumentParts.Add('-ExecutionPolicy')
$argumentParts.Add('Bypass')
$argumentParts.Add('-File')
$argumentParts.Add(('"{0}"' -f $AgentScriptPath))
$argumentParts.Add('-InboxDir')
$argumentParts.Add(('"{0}"' -f $InboxDir))
$argumentParts.Add('-ExecuteImport')
$argumentParts.Add('-UseKeyboardMenuFallback')
$argumentParts.Add('-UseKeyboardFileDialogFallback')
$argumentParts.Add('-PollSeconds')
$argumentParts.Add([string]$PollSeconds)
$argumentParts.Add('-TimeoutSeconds')
$argumentParts.Add([string]$TimeoutSeconds)
$argumentParts.Add('-RequireIdleSeconds')
$argumentParts.Add([string]$RequireIdleSeconds)
if (-not [string]::IsNullOrWhiteSpace($EvidenceRoot)) { $argumentParts.Add('-EvidenceRoot'); $argumentParts.Add(('"{0}"' -f $EvidenceRoot)) }
if (-not [string]::IsNullOrWhiteSpace($LedgerPath)) { $argumentParts.Add('-LedgerPath'); $argumentParts.Add(('"{0}"' -f $LedgerPath)) }
if (-not [string]::IsNullOrWhiteSpace($HealthFile)) { $argumentParts.Add('-HealthFile'); $argumentParts.Add(('"{0}"' -f $HealthFile)) }
if (-not [string]::IsNullOrWhiteSpace($ShineLabPath)) { $argumentParts.Add('-ShineLabPath'); $argumentParts.Add(('"{0}"' -f $ShineLabPath)) }
if (-not [string]::IsNullOrWhiteSpace($SigningKeyFile)) { $argumentParts.Add('-SigningKeyFile'); $argumentParts.Add(('"{0}"' -f $SigningKeyFile)) }
if ($RequireSignedCommands) { $argumentParts.Add('-RequireSignedCommands') }

$action = New-ScheduledTaskAction -Execute 'PowerShell.exe' -Argument ($argumentParts -join ' ')
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $RunAsUser
$principal = New-ScheduledTaskPrincipal -UserId $RunAsUser -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Days 3650) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable

if ($PSCmdlet.ShouldProcess($TaskName, '注册 ShineLab Agent 登录任务')) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'MES ShineLab 受控批次代理；仅允许白名单导入/只读验证，不触发运行。' -Force | Out-Null
    Write-Host "已安装任务：$TaskName"
    Write-Host "运行账户：$RunAsUser"
    Write-Host "交接目录：$InboxDir"
    Write-Host '触发方式：该账户登录时自动启动；需要交互式桌面和管理员权限。'
}
