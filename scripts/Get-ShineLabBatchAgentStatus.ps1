[CmdletBinding()]
param(
    [string]$HealthFile,
    [string]$TaskName = 'MES-ShineLabBatchAgent'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($HealthFile)) {
    throw '请提供控制电脑本地的 -HealthFile，例如 C:\MES-RPA\inbox\evidence\agent-health.json。'
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
$health = $null
if (Test-Path -LiteralPath $HealthFile) {
    $health = Get-Content -LiteralPath $HealthFile -Raw -Encoding utf8 | ConvertFrom-Json
}

[pscustomobject]@{
    taskName = $TaskName
    taskState = if ($null -eq $task) { 'NotInstalled' } else { [string]$task.State }
    healthFile = [IO.Path]::GetFullPath($HealthFile)
    agentStatus = if ($null -eq $health) { 'NoHealthFile' } else { [string]$health.status }
    agentVersion = if ($null -eq $health) { $null } else { [string]$health.agentVersion }
    reason = if ($null -eq $health) { $null } else { [string]$health.reason }
    lastHeartbeatUtc = if ($null -eq $health) { $null } else { [string]$health.lastHeartbeatUtc }
    shineLabRunning = if ($null -eq $health) { $false } else { [bool]$health.shineLabRunning }
    elevated = if ($null -eq $health) { $false } else { [bool]$health.elevated }
    interactive = if ($null -eq $health) { $false } else { [bool]$health.interactive }
} | Format-List
