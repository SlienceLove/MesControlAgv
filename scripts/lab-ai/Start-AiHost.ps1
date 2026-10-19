[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OllamaExe,
    [string]$ModelDirectory = (Join-Path $PSScriptRoot 'models'),
    [switch]$PullModel,
    [switch]$Light
)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $OllamaExe).Path
if ([IO.Path]::GetFileName($exe) -ne 'ollama.exe') { throw 'Select the official ollama.exe executable.' }
if (Get-NetTCPConnection -State Listen -LocalPort 11434 -ErrorAction SilentlyContinue) {
    throw 'Port 11434 is already occupied. This script never stops or replaces an existing service.'
}
$memory = Get-CimInstance Win32_OperatingSystem
$cpu = Get-CimInstance Win32_Processor
Write-Output ($cpu.Name -join ', ')
Write-Output ('Available RAM: {0:N0} MB of {1:N0} MB' -f ($memory.FreePhysicalMemory / 1024), ($memory.TotalVisibleMemorySize / 1024))
$minimumFreeMB = if ($Light) { 1536 } else { 3072 }
if ($memory.FreePhysicalMemory -lt $minimumFreeMB * 1024) {
    throw ("Less than " + $minimumFreeMB + "MB free RAM. This script closes nothing; do not stop instrument services.")
}
$modelPath = [IO.Path]::GetFullPath($ModelDirectory)
New-Item -ItemType Directory -Path $modelPath -Force | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $exe; $start.Arguments = 'serve'; $start.UseShellExecute = $false
$start.CreateNoWindow = $true; $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$start.EnvironmentVariables['OLLAMA_HOST'] = '127.0.0.1:11434'
$start.EnvironmentVariables['OLLAMA_MODELS'] = $modelPath
$start.EnvironmentVariables['OLLAMA_NUM_PARALLEL'] = '1'
$start.EnvironmentVariables['OLLAMA_MAX_LOADED_MODELS'] = '1'
$start.EnvironmentVariables['OLLAMA_MAX_QUEUE'] = '1'
$start.EnvironmentVariables['OLLAMA_KEEP_ALIVE'] = '0'
$start.EnvironmentVariables['OLLAMA_CONTEXT_LENGTH'] = '2048'
$start.EnvironmentVariables['OLLAMA_NO_CLOUD'] = '1'
$process = [Diagnostics.Process]::Start($start)
Write-Output ('Started Ollama PID {0}, loopback only. No startup task or firewall rule was added.' -f $process.Id)
$ready = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ($process.HasExited) { throw 'Ollama exited; check the executable and available memory.' }
    try { $null = Invoke-RestMethod 'http://127.0.0.1:11434/api/version' -TimeoutSec 2; $ready = $true; break }
    catch { Start-Sleep -Seconds 1 }
}
if (-not $ready) { throw 'Host did not become ready. The launched process was not force-killed; inspect it manually.' }
if ($PullModel) {
    $modelTag = if ($Light) { 'qwen3:0.6b' } else { 'qwen3:1.7b' }
    Write-Output ("Downloading " + $modelTag + " from the configured Ollama registry. No experimental data is sent.")
    $body = @{ model = $modelTag; stream = $false } | ConvertTo-Json
    $result = Invoke-RestMethod 'http://127.0.0.1:11434/api/pull' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 1800
    $result | ConvertTo-Json
}
Write-Output 'Next: run the appropriate Test launcher in this package.'
Write-Output ('To stop only this host after testing: Stop-Process -Id {0} (verify the PID first).' -f $process.Id)
