param(
    [int]$SampleCount = 5,
    [int]$MaxDurationSeconds = 300,
    [string]$DisplayUnit = 'MPa',
    [string]$OutputRoot = (Join-Path $PSScriptRoot 'd160-pressure-correlation-results')
)

$ErrorActionPreference = 'Stop'
if ($SampleCount -lt 3 -or $SampleCount -gt 20) { throw 'SampleCount must be between 3 and 20.' }
if ($MaxDurationSeconds -lt 60) { throw 'MaxDurationSeconds must be at least 60.' }

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Administrator rights are required. Double-click Run-D160PressureCorrelation.cmd instead.'
}

$cmdCandidates = @(
    (Join-Path ${env:ProgramFiles} 'USBPcap\USBPcapCMD.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'USBPcap\USBPcapCMD.exe'),
    (Join-Path $PSScriptRoot 'USBPcapCMD.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) }
if (-not $cmdCandidates) { throw 'USBPcapCMD.exe was not found. Install USBPcap first.' }
$usbpcap = [string]($cmdCandidates | Select-Object -First 1)

$shine = Get-Process -Name 'ShineLab','ShineControl-Normal','ShineDataAcquire-Normal' -ErrorAction SilentlyContinue
if (-not $shine) { throw 'Start ShineLab and leave it running before starting this capture.' }

if ([string]::IsNullOrWhiteSpace($DisplayUnit)) {
    $DisplayUnit = Read-Host 'Enter the pressure unit shown by ShineLab (for example MPa, bar, or kPa)'
}
if ([string]::IsNullOrWhiteSpace($DisplayUnit)) { throw 'DisplayUnit is required.' }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $OutputRoot "d160-pressure-correlation-$stamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$annotations = [Collections.Generic.List[object]]::new()
$processes = [Collections.Generic.List[object]]::new()
$startedAtUtc = [DateTimeOffset]::UtcNow
$failure = $null

function ConvertTo-OptionalDecimal([string]$Text) {
    [decimal]$value = 0
    if ([decimal]::TryParse($Text, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { return $value }
    if ([decimal]::TryParse($Text, [ref]$value)) { return $value }
    return $null
}

function Save-Manifest([DateTimeOffset]$EndedAtUtc = [DateTimeOffset]::MinValue) {
    $manifest = [ordered]@{
        startedAtUtc = $startedAtUtc.ToString('o')
        endedAtUtc = if ($EndedAtUtc -eq [DateTimeOffset]::MinValue) { $null } else { $EndedAtUtc.ToString('o') }
        maxDurationSeconds = $MaxDurationSeconds
        requestedSampleCount = $SampleCount
        displayUnit = $DisplayUnit
        endpoint = 'usb://D160+-passive-capture'
        mode = 'read-only-pressure-correlation'
        shineLabProcesses = @($shine | Select-Object Id,ProcessName,StartTime)
        annotations = $annotations
        failure = $failure
        note = 'USBPcap only observes USB traffic. This script never opens COM4 and never injects serial data.'
    }
    $manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $runDir 'pressure-correlation-manifest.json') -Encoding UTF8
}

try {
    Get-CimInstance Win32_SerialPort -ErrorAction SilentlyContinue |
        Select-Object DeviceID,Name,PNPDeviceID,Description,Caption,Status |
        ConvertTo-Json -Depth 5 |
        Set-Content -LiteralPath (Join-Path $runDir 'serial-ports.json') -Encoding UTF8

    $interfacesText = (& $usbpcap --extcap-interfaces 2>$null | Out-String)
    $interfacesText | Set-Content -LiteralPath (Join-Path $runDir 'usbpcap-interface-enumeration.txt') -Encoding UTF8
    $interfaces = [regex]::Matches($interfacesText, 'USBPcap\d+') |
        ForEach-Object { "\\.\$($_.Value)" } |
        Select-Object -Unique
    if (-not $interfaces) { throw 'No USBPcap root-hub interfaces were found.' }
    @($interfaces) | Set-Content -LiteralPath (Join-Path $runDir 'usbpcap-interfaces.txt') -Encoding ASCII

    foreach ($device in @($interfaces)) {
        $label = ($device -replace '[^A-Za-z0-9]+', '_').Trim('_')
        $pcap = Join-Path $runDir "$label.pcap"
        $arguments = "-d `"$device`" -A --inject-descriptors -o `"$pcap`""
        $process = Start-Process -FilePath $usbpcap -ArgumentList $arguments -PassThru -WindowStyle Hidden
        $processes.Add([ordered]@{ device = $device; pcap = $pcap; pid = $process.Id })
    }
    Save-Manifest

    Write-Host "Passive capture started for up to $MaxDurationSeconds seconds."
    Write-Host 'Keep ShineLab open. Do not change settings only for this tool.'
    Write-Host 'Record samples while pressure is stable during an already-approved operation.'
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($MaxDurationSeconds)
    for ($index = 1; $index -le $SampleCount; $index++) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'Maximum capture duration reached before all samples were recorded.' }
        $pressureText = Read-Host "Sample $index/$SampleCount - enter displayed pressure now"
        if ([string]::IsNullOrWhiteSpace($pressureText)) { throw "Sample $index pressure is required." }
        $observedAtUtc = [DateTimeOffset]::UtcNow
        $flowText = Read-Host 'Optional displayed actual flow (press Enter to skip)'
        $stateText = Read-Host 'Optional state/operation note (press Enter to skip)'
        $annotations.Add([ordered]@{
            sample = $index
            observedAtUtc = $observedAtUtc.ToString('o')
            displayedPressureText = $pressureText
            displayedPressureValue = ConvertTo-OptionalDecimal $pressureText
            displayedFlowText = $flowText
            stateNote = $stateText
        })
        Save-Manifest
        Write-Host "Recorded sample $index at $($observedAtUtc.ToString('o'))."
    }
}
catch {
    $failure = $_.Exception.Message
    Write-Host "Capture stopped: $failure"
}
finally {
    foreach ($entry in $processes) {
        Get-Process -Id ([int]$entry.pid) -ErrorAction SilentlyContinue |
            Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 2
}

$endedAtUtc = [DateTimeOffset]::UtcNow
Save-Manifest $endedAtUtc

$pcapFiles = @(Get-ChildItem -LiteralPath $runDir -Filter '*.pcap' -File -ErrorAction SilentlyContinue)
$knownProcessRequest = [byte[]](0x01,0x04,0x17,0xD4,0x00,0x12,0x35,0x8B)
$knownRequestCaptured = $false
foreach ($pcap in $pcapFiles) {
    $bytes = [IO.File]::ReadAllBytes($pcap.FullName)
    $hex = [BitConverter]::ToString($bytes).Replace('-', '')
    $requestHex = [BitConverter]::ToString($knownProcessRequest).Replace('-', '')
    if ($hex.IndexOf($requestHex, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $knownRequestCaptured = $true
    }
}

[ordered]@{
    pcapFiles = @($pcapFiles | Select-Object Name,Length,FullName)
    knownProcessReadRequestHex = '01 04 17 D4 00 12 35 8B'
    knownProcessReadRequestCaptured = $knownRequestCaptured
    sampleCount = $annotations.Count
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runDir 'capture-summary.json') -Encoding UTF8

Get-ChildItem -LiteralPath $runDir -File |
    Get-FileHash -Algorithm SHA256 |
    Select-Object Algorithm,Hash,Path |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $runDir 'SHA256.json') -Encoding UTF8

$zip = Join-Path $OutputRoot "d160-pressure-correlation-result-$stamp.zip"
Compress-Archive -Path (Join-Path $runDir '*') -DestinationPath $zip -Force
Write-Host "Result package: $zip"

if ($failure -or $annotations.Count -lt $SampleCount -or -not $knownRequestCaptured) {
    if (-not $knownRequestCaptured) { Write-Host 'The process read request was not found in the capture.' }
    exit 1
}
Write-Host 'Capture completed. Return only the ZIP file.'
exit 0
