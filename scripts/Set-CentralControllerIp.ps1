param(
    [string]$InterfaceAlias = '以太网',
    [string]$TargetAddress = '192.168.1.11',
    [int]$PrefixLength = 24,
    [string]$PreviousAddress = '192.168.1.106'
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must run from an elevated Administrator PowerShell.'
}

$adapter = Get-NetAdapter -Name $InterfaceAlias -ErrorAction Stop
if ($adapter.Status -ne 'Up') {
    throw "Network adapter '$InterfaceAlias' is not Up."
}

$existingTarget = Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 -IPAddress $TargetAddress -ErrorAction SilentlyContinue
if (-not $existingTarget) {
    New-NetIPAddress -InterfaceAlias $InterfaceAlias -IPAddress $TargetAddress -PrefixLength $PrefixLength | Out-Null
    Start-Sleep -Seconds 2
}

$target = Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 -IPAddress $TargetAddress -ErrorAction Stop
if ($target.AddressState -eq 'Duplicate') {
    Remove-NetIPAddress -InterfaceAlias $InterfaceAlias -IPAddress $TargetAddress -Confirm:$false
    throw "Target address $TargetAddress is already in use. The new address was removed and the previous address was preserved."
}

if ($PreviousAddress -ne $TargetAddress) {
    Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 -IPAddress $PreviousAddress -ErrorAction SilentlyContinue |
        Remove-NetIPAddress -Confirm:$false
}

Get-NetIPAddress -InterfaceAlias $InterfaceAlias -AddressFamily IPv4 |
    Select-Object InterfaceAlias,IPAddress,PrefixLength,PrefixOrigin,AddressState |
    Format-Table -AutoSize
