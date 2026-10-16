[CmdletBinding()]
param([ValidateSet('Check','Test')][string]$Mode = 'Check')
$ErrorActionPreference = 'Stop'
try {
    $osInfo = Get-CimInstance Win32_OperatingSystem
    $cpuInfo = Get-CimInstance Win32_Processor
    $memoryInfo = Get-CimInstance Win32_PhysicalMemory
    $packagePath = [IO.Path]::GetFullPath($PSScriptRoot)
    $driveInfo = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($packagePath))
    $stamp = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,6)
    $inventoryPath = Join-Path $packagePath ("host-check-" + $stamp + ".json")
    $inventory = [ordered]@{
        CapturedAt = (Get-Date).ToString('o')
        Cpu = @($cpuInfo | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors)
        Os = $osInfo.Caption
        Version = $osInfo.Version
        TotalMemoryMB = [math]::Round($osInfo.TotalVisibleMemorySize / 1024)
        FreeMemoryMB = [math]::Round($osInfo.FreePhysicalMemory / 1024)
        MemoryModules = @($memoryInfo | Select-Object Capacity,Speed,ConfiguredClockSpeed,SMBIOSMemoryType)
        PackageDrive = $driveInfo.Name
        PackageDriveFreeGB = [math]::Round($driveInfo.AvailableFreeSpace / 1GB, 1)
        Note = 'Inventory only; no instrument or service settings changed.'
    }
    $inventory | ConvertTo-Json -Depth 6 | Out-File -LiteralPath $inventoryPath -Encoding utf8
    Write-Host ($inventory | ConvertTo-Json -Depth 6)
    Write-Host ("Inventory saved: " + $inventoryPath)
    if ($Mode -eq 'Check') { exit 0 }
    Write-Host 'This test must run only while the chromatography instrument is idle.'
    $answer = Read-Host 'Type Y to run synthetic AI tests (no device commands)'
    if ($answer -ne 'Y') { Write-Host 'Cancelled.'; exit 0 }
    if ($osInfo.FreePhysicalMemory -lt 3 * 1024 * 1024) {
        throw 'Less than 3GB free RAM. Close only unused applications and run Check again. Do not close instrument services.'
    }
    if ($driveInfo.AvailableFreeSpace -lt 5GB) { throw 'Keep at least 5GB free on the package drive.' }
    $pilot = Join-Path $packagePath 'Pilot.exe'
    if (-not (Test-Path -LiteralPath $pilot)) { throw 'Pilot.exe missing. Extract the entire ZIP first.' }
    $ready = $false
    try {
        $version = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 3
        if ($version.version) { $ready = $true }
    } catch { }
    if (-not $ready) {
        $candidates = @(
            (Join-Path $packagePath 'ollama\ollama.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe')
        )
        $command = Get-Command ollama.exe -ErrorAction SilentlyContinue
        if ($command) { $candidates += $command.Source }
        $ollamaExePath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $ollamaExePath) {
            throw 'Install official Ollama from https://ollama.com/download/windows (or extract its official portable package into ollama), then rerun. This package does not install software automatically.'
        }
        & (Join-Path $packagePath 'Start-AiHost.ps1') -OllamaExe $ollamaExePath
    } else {
        Write-Host 'Existing local Ollama detected; using it without changing its startup/configuration.'
    }
    $loaded = Invoke-RestMethod 'http://127.0.0.1:11434/api/ps' -TimeoutSec 5
    if (@($loaded.models).Count -gt 0) {
        throw 'A model is already loaded. Finish other AI work first, then rerun; this test will not unload another session.'
    }
    $tags = Invoke-RestMethod 'http://127.0.0.1:11434/api/tags' -TimeoutSec 5
    if (-not ($tags.models | Where-Object { $_.name -eq 'qwen3:1.7b' -or $_.model -eq 'qwen3:1.7b' })) {
        Write-Host 'Downloading qwen3:1.7b (~1.4GB). Download requires internet; inference uses only this computer.'
        $body = @{ model = 'qwen3:1.7b'; stream = $false } | ConvertTo-Json
        $null = Invoke-RestMethod 'http://127.0.0.1:11434/api/pull' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 1800
    }
    $resultsPath = Join-Path $packagePath ('results-' + $stamp)
    Write-Host 'Running one report and five planning cases, sequentially. Each may take up to 120 seconds.'
    Write-Host 'Ctrl+C cancels waiting. No startup task or firewall rule is created.'
    & $pilot --planning $resultsPath
    $resultCode = $LASTEXITCODE
    Write-Host ("Pilot exit code: " + $resultCode)
    Write-Host ("Open: " + (Join-Path $resultsPath 'report.html'))
    Write-Host 'Send the entire results folder and host-check JSON back for review. Do not send instrument data.'
    if ($resultCode -ne 0) { Write-Host 'Some checks did not pass. The report preserves failures; do not treat this as a deployment acceptance.' }
    exit $resultCode
} catch {
    Write-Host ("STOP: " + $_.Exception.Message) -ForegroundColor Yellow
    Write-Host 'No instrument process was stopped or reconfigured.'
    exit 1
}
