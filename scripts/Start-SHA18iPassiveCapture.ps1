param(
    [string]$Root = (Join-Path $env:PUBLIC ("SHA18iA-capture-{0}" -f (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated Administrator PowerShell.'
}

$usbpcap = @(
    (Join-Path $env:ProgramFiles 'USBPcap\USBPcapCMD.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'USBPcap\USBPcapCMD.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
if (-not $usbpcap) { throw 'USBPcapCMD.exe was not found.' }

New-Item -ItemType Directory -Force -Path $Root | Out-Null
$interfacesText = (& $usbpcap --extcap-interfaces 2>$null | Out-String)
$interfaces = @([regex]::Matches($interfacesText, '\\\\\.\\USBPcap\d+') |
    ForEach-Object { $_.Value } | Select-Object -Unique)
if (-not $interfaces) { throw 'No USBPcap root-hub interface was found.' }

$processes = foreach ($device in $interfaces) {
    $label = ($device -replace '[^A-Za-z0-9]+', '_').Trim('_')
    $pcap = Join-Path $Root "$label.pcap"
    $arguments = "-d `"$device`" -A --inject-descriptors -o `"$pcap`""
    $process = Start-Process -FilePath $usbpcap -ArgumentList $arguments -PassThru -WindowStyle Hidden
    [pscustomobject]@{ Device = $device; Pcap = $pcap; Pid = $process.Id }
}

$processes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Root 'capture-processes.json') -Encoding UTF8
$Root | Set-Content -LiteralPath (Join-Path $env:PUBLIC 'SHA18iA-current-capture.txt') -Encoding ASCII

Write-Host "Passive capture started: $Root"
Write-Host 'Keep ShineLab running. Perform one action at a time with about 5 seconds between actions.'
Write-Host 'First actions only: connect/auto-detect, status refresh, initialize/home.'
Write-Host 'When finished, close this window only after running STOP-3-SHA18I-CAPTURE.cmd.'
