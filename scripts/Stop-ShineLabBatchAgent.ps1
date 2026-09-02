[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string]$InboxDir,
    [string]$TaskName = 'MES-ShineLabBatchAgent'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $InboxDir)) { throw "交接目录不存在：$InboxDir" }
$sentinel = Join-Path (Resolve-Path -LiteralPath $InboxDir).ProviderPath 'agent.stop'
if ($PSCmdlet.ShouldProcess($sentinel, '请求 Agent 在当前批次结束后停机')) {
    New-Item -ItemType File -Path $sentinel -Force | Out-Null
    Write-Host "已写入停机哨兵：$sentinel"
}
