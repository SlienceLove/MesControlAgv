[CmdletBinding()]
param(
    [ValidateRange(3, 30)]
    [int]$CountdownSeconds = 8
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName Microsoft.VisualBasic

$process = Get-Process | Where-Object {
    $_.MainWindowHandle -ne 0 -and
    ($_.MainWindowTitle -match 'ShineDataAcquisition|ShineDataAcquire|ShineLab')
} | Select-Object -First 1

if ($null -eq $process) {
    throw 'ShineLab main window was not found. Open Analysis Control and the sequence task grid first.'
}

Write-Host '[ShineLab RPA] Activating ShineLab.'
Write-Host "[ShineLab RPA] Within $CountdownSeconds seconds, move the pointer to a task-grid point where a manual right-click opens the CSV menu. Keep the pointer still."
Start-Sleep -Milliseconds 800

[Microsoft.VisualBasic.Interaction]::AppActivate($process.Id)
Start-Sleep -Seconds $CountdownSeconds

$point = [System.Windows.Forms.Cursor]::Position
$arguments = '-GridPointX {0} -GridPointY {1}' -f $point.X, $point.Y
Set-Clipboard -Value $arguments

Write-Host "[ShineLab RPA] Captured point: $($point.X),$($point.Y)"
Write-Host "[ShineLab RPA] Copied arguments to clipboard: $arguments"
Write-Host '[ShineLab RPA] Manually right-click this point and confirm the Export CSV / Import CSV menu before running the import.'
