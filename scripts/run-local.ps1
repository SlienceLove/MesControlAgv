$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

Start-Process dotnet -WorkingDirectory $root -ArgumentList 'run --project src/MesControlAgv.Simulator --launch-profile http'
Start-Process dotnet -WorkingDirectory $root -ArgumentList 'run --project src/MesControlAgv.Adapter --launch-profile http'
Start-Process dotnet -WorkingDirectory $root -ArgumentList 'run --project src/MesControlAgv.Mes --launch-profile http'

Write-Host 'Simulator: http://localhost:5183'
Write-Host 'Adapter:   http://localhost:5041'
Write-Host 'MES:       http://localhost:5045'
Write-Host 'Start the desktop client separately: dotnet run --project src/MesControlAgv.Wpf'
