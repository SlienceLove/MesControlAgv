param(
    [string]$RootFile = (Join-Path $env:PUBLIC 'SHA18iA-current-capture.txt')
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated Administrator PowerShell.'
}

if (-not (Test-Path -LiteralPath $RootFile -PathType Leaf)) {
    throw "Capture marker not found: $RootFile"
}

$root = (Get-Content -LiteralPath $RootFile -Raw).Trim()
$processFile = Join-Path $root 'capture-processes.json'
$items = @(Get-Content -LiteralPath $processFile -Raw | ConvertFrom-Json)
foreach ($item in $items) {
    Stop-Process -Id ([int]$item.Pid) -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue |
    Select-Object DeviceID,Name,PNPDeviceID,Description,Status |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $root 'serial-ports.json') -Encoding UTF8

Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.InstanceId.StartsWith('USB\') -or $_.Class -eq 'USB' } |
    Select-Object Status,Class,FriendlyName,InstanceId,ProblemCode |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $root 'usb-devices.json') -Encoding UTF8

Get-ChildItem -LiteralPath $root -File |
    Get-FileHash -Algorithm SHA256 |
    Select-Object Algorithm,Hash,Path |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $root 'SHA256.json') -Encoding UTF8

$zip = "$root.zip"
Compress-Archive -Path (Join-Path $root '*') -DestinationPath $zip -Force
Write-Host "Capture packaged: $zip"

$deliveryDirectory = 'C:\MES-RPA\inbox'
if (Test-Path -LiteralPath $deliveryDirectory -PathType Container) {
    $deliveryZip = Join-Path $deliveryDirectory (Split-Path -Leaf $zip)
    Copy-Item -LiteralPath $zip -Destination $deliveryZip -Force
    Write-Host "Copied to shared inbox: $deliveryZip"
}
else {
    Write-Warning "Shared inbox was not found: $deliveryDirectory"
}
