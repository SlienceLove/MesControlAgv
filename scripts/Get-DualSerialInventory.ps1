param(
    [string]$Output = 'C:\MES-RPA\inbox\dual-serial-inventory.json'
)

$ErrorActionPreference = 'Stop'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
$isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isElevated) {
    throw 'Run this script from an elevated Administrator PowerShell.'
}

function Get-UsbIdentity {
    param([string]$InstanceId)

    $vid = $null
    $productId = $null
    $serialNumber = $null
    $vidMarker = 'VID_'
    $pidMarker = 'PID_'
    $vidIndex = $InstanceId.IndexOf($vidMarker, [StringComparison]::OrdinalIgnoreCase)
    $pidIndex = $InstanceId.IndexOf($pidMarker, [StringComparison]::OrdinalIgnoreCase)

    if ($vidIndex -ge 0 -and $InstanceId.Length -ge ($vidIndex + 8)) {
        $vid = $InstanceId.Substring($vidIndex + 4, 4).ToUpperInvariant()
    }
    if ($pidIndex -ge 0 -and $InstanceId.Length -ge ($pidIndex + 8)) {
        $productId = $InstanceId.Substring($pidIndex + 4, 4).ToUpperInvariant()
    }

    $segments = @($InstanceId.Split([char]92))
    if ($segments.Count -ge 3) {
        $serialNumber = $segments[$segments.Count - 1]
        $ampersand = $serialNumber.IndexOf([char]38)
        if ($ampersand -ge 0) {
            $serialNumber = $serialNumber.Substring(0, $ampersand)
        }
    }

    [pscustomobject]@{
        Vid = $vid
        Pid = $productId
        SerialNumber = $serialNumber
    }
}

$shinePattern = 'ShineLab|ShineDataAcquisition|ShineDataAcquire|ShineControl'
$shine = @(Get-Process -ErrorAction SilentlyContinue |
    Where-Object { $_.ProcessName -match $shinePattern } |
    Select-Object Id,ProcessName,MainWindowTitle,Path)

$cimPorts = @(Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue |
    Select-Object DeviceID,Name,Description,PNPDeviceID,ProviderType,Status,MaxBaudRate)

$pnpPorts = @()
if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
    $pnpPorts = @(Get-PnpDevice -Class Ports -ErrorAction SilentlyContinue |
        Where-Object { $_.FriendlyName -like '*(COM*)' } |
        ForEach-Object {
            $usb = Get-UsbIdentity -InstanceId $_.InstanceId
            $open = $_.FriendlyName.LastIndexOf([char]40)
            $close = $_.FriendlyName.LastIndexOf([char]41)
            $com = $null
            if ($open -ge 0 -and $close -gt $open) {
                $com = $_.FriendlyName.Substring($open + 1, $close - $open - 1)
            }
            [pscustomobject]@{
                Status = $_.Status
                Class = $_.Class
                FriendlyName = $_.FriendlyName
                ComPort = $com
                InstanceId = $_.InstanceId
                Vid = $usb.Vid
                Pid = $usb.Pid
                SerialNumber = $usb.SerialNumber
                ProblemCode = $_.ProblemCode
            }
        })
}

$serialComm = @()
$serialCommPath = 'HKLM:\HARDWARE\DEVICEMAP\SERIALCOMM'
if (Test-Path -LiteralPath $serialCommPath) {
    $serialComm = @((Get-ItemProperty -LiteralPath $serialCommPath).PSObject.Properties |
        Where-Object { -not $_.Name.StartsWith('PS') } |
        ForEach-Object {
            [pscustomobject]@{ RegistryName = $_.Name; ComPort = [string]$_.Value }
        })
}

$toolCandidates = @()
if ($env:ProgramFiles) {
    $toolCandidates += Join-Path $env:ProgramFiles 'USBPcap\USBPcapCMD.exe'
    $toolCandidates += Join-Path $env:ProgramFiles 'Wireshark\Wireshark.exe'
    $toolCandidates += Join-Path $env:ProgramFiles 'Wireshark\tshark.exe'
}
if (${env:ProgramFiles(x86)}) {
    $toolCandidates += Join-Path ${env:ProgramFiles(x86)} 'USBPcap\USBPcapCMD.exe'
    $toolCandidates += Join-Path ${env:ProgramFiles(x86)} 'Wireshark\Wireshark.exe'
    $toolCandidates += Join-Path ${env:ProgramFiles(x86)} 'Wireshark\tshark.exe'
}
$captureTools = @($toolCandidates | Select-Object -Unique | ForEach-Object {
    [pscustomobject]@{ Path = $_; Installed = (Test-Path -LiteralPath $_ -PathType Leaf) }
})

$report = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    computerName = $env:COMPUTERNAME
    elevated = $isElevated
    safety = 'Inventory only. No COM port was opened and no instrument frame was sent.'
    shineLabProcesses = $shine
    cimSerialPorts = $cimPorts
    pnpPorts = $pnpPorts
    serialCommRegistry = $serialComm
    captureTools = $captureTools
}

$parent = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $Output -Encoding UTF8

Write-Host '=== ShineLab processes (keep ShineLab running) ==='
$shine | Format-Table Id,ProcessName,MainWindowTitle -AutoSize
Write-Host '=== COM / USB identity ==='
$pnpPorts | Format-Table Status,FriendlyName,ComPort,Vid,Pid,SerialNumber -AutoSize
Write-Host '=== USBPcap / Wireshark ==='
$captureTools | Format-Table Installed,Path -AutoSize
Write-Host "Inventory complete: $Output"
Write-Host 'No COM port was opened and no instrument frame was sent.'
