$ErrorActionPreference = 'Stop'
$statePath = Join-Path ([IO.Path]::GetTempPath()) 'MesControlAgv-local-pids.json'

if (-not (Test-Path -LiteralPath $statePath)) {
    Write-Host 'No local service PID file found.'
    exit 0
}

$items = Get-Content -Raw -LiteralPath $statePath | ConvertFrom-Json
foreach ($item in $items) {
    $process = Get-Process -Id ([int]$item.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        continue
    }

    if ($process.Path -eq $item.Executable) {
        Stop-Process -Id $process.Id -Force
        Write-Host "$($item.Name) stopped (PID $($item.ProcessId))."
    }
    else {
        Write-Warning "Skipped PID $($item.ProcessId); its executable no longer matches the recorded dotnet process."
    }
}

Remove-Item -LiteralPath $statePath -Force
