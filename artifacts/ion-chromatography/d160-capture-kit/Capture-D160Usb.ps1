param(
    [int]$DurationSeconds = 60,
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'results')
)

$ErrorActionPreference = 'Stop'
if ($DurationSeconds -lt 10) { throw 'DurationSeconds must be at least 10.' }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator rights are required. Double-click Run-D160Capture.cmd instead.'
}

$cmdCandidates = @(
    (Join-Path ${env:ProgramFiles} 'USBPcap\USBPcapCMD.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'USBPcap\USBPcapCMD.exe'),
    (Join-Path $PSScriptRoot 'USBPcapCMD.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
if (-not $cmdCandidates) {
    throw 'USBPcapCMD.exe was not found. Install USBPcapSetup-1.5.4.0.exe first.'
}
$usbpcap = [string]($cmdCandidates | Select-Object -First 1)

$shine = Get-Process -Name 'ShineLab','ShineControl-Normal','ShineDataAcquire-Normal' -ErrorAction SilentlyContinue
if (-not $shine) {
    throw 'Start ShineLab and leave it running before starting this capture.'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $OutputRoot "d160-capture-$stamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue |
    Select-Object DeviceID,Name,PNPDeviceID,Description,Caption,Status |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $runDir 'serial-ports.json') -Encoding UTF8

if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
    Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
        Where-Object { $_.InstanceId -match '^USB\\' -or $_.Class -match 'USB' } |
        Select-Object Status,Class,FriendlyName,InstanceId,ProblemCode |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runDir 'usb-devices.json') -Encoding UTF8
}

$interfacesText = (& $usbpcap --extcap-interfaces 2>$null | Out-String)
$interfaces = [regex]::Matches($interfacesText, '\\\\\.\\USBPcap\d+') |
    ForEach-Object Value |
    Select-Object -Unique
if (-not $interfaces) {
    throw 'No USBPcap root-hub interfaces were found. Reboot after driver installation if required.'
}

$interfaceList = @($interfaces | ForEach-Object { [string]$_ })
$interfaceList | Set-Content -LiteralPath (Join-Path $runDir 'usbpcap-interfaces.txt') -Encoding ASCII

$started = [DateTimeOffset]::UtcNow
$processes = [System.Collections.Generic.List[object]]::new()
foreach ($device in $interfaceList) {
    $label = ($device -replace '[^A-Za-z0-9]+', '_').Trim('_')
    $pcap = Join-Path $runDir "$label.pcap"
    $arguments = "-d `"$device`" -A --inject-descriptors -o `"$pcap`""
    $process = Start-Process -FilePath $usbpcap -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $processes.Add([ordered]@{ device = $device; pcap = $pcap; pid = $process.Id })
}

$processes | ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $runDir 'capture-processes.json') -Encoding UTF8

Write-Host "Capture started for $DurationSeconds seconds on $($interfaceList.Count) USBPcap root hub(s)."
Write-Host 'During this window, use ShineLab only for opening communication and one read-only status refresh.'
Write-Host 'Do not load methods, inject, start, stop, reset, or change instrument settings.'
Start-Sleep -Seconds $DurationSeconds

foreach ($entry in $processes) {
    Get-Process -Id ([int]$entry.pid) -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Seconds 2

$ended = [DateTimeOffset]::UtcNow
$binaryEncoding = [Text.Encoding]::GetEncoding(28591)
$requestBytes = [byte[]](0x01,0x04,0x19,0x00,0x00,0x14,0xF7,0x59)
$requestText = $binaryEncoding.GetString($requestBytes)
$pcapChecks = @($processes | ForEach-Object {
    $item = Get-Item -LiteralPath $_.pcap -ErrorAction SilentlyContinue
    $containsRequest = $false
    if ($item -and $item.Length -gt 24) {
        $captureText = $binaryEncoding.GetString([IO.File]::ReadAllBytes($item.FullName))
        $containsRequest = $captureText.IndexOf($requestText, [StringComparison]::Ordinal) -ge 0
    }
    [ordered]@{
        device = $_.device
        file = $_.pcap
        length = if ($item) { $item.Length } else { 0 }
        containsKnownReadRequest = $containsRequest
    }
})
$knownRequestCaptured = @($pcapChecks | Where-Object containsKnownReadRequest).Count -gt 0

[ordered]@{
    startedAtUtc = $started.ToString('o')
    endedAtUtc = $ended.ToString('o')
    durationSeconds = $DurationSeconds
    usbpcapCommand = $usbpcap
    shineLabProcesses = @($shine | Select-Object Id,ProcessName,StartTime)
    knownReadRequestHex = '01 04 19 00 00 14 F7 59'
    knownReadRequestCaptured = $knownRequestCaptured
    pcapChecks = $pcapChecks
    note = 'USBPcap capture covers selected root hubs; analyze USB bulk directions before interpreting serial frames.'
} | ConvertTo-Json -Depth 7 |
    Set-Content -LiteralPath (Join-Path $runDir 'capture-manifest.json') -Encoding UTF8

Get-ChildItem -LiteralPath $runDir -File |
    Get-FileHash -Algorithm SHA256 |
    Select-Object Algorithm,Hash,Path |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $runDir 'SHA256.json') -Encoding UTF8

$zip = Join-Path $OutputRoot "d160-capture-result-$stamp.zip"
Compress-Archive -Path (Join-Path $runDir '*') -DestinationPath $zip -Force
Write-Host "Result package: $zip"
if (-not $knownRequestCaptured) {
    Write-Error 'The known D160+ read request was not found. Keep ShineLab connected and run the capture again before returning the USB drive.'
    exit 2
}
