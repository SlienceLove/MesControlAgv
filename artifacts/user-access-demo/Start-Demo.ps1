[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path $PSScriptRoot).Path
$env:WPF_RUNTIME_MODE = 'simulator'
$env:WPF_MANAGE_LOCAL_SERVICES = 'true'
$env:WPF_MANAGE_LOCAL_MES = 'false'
$env:WPF_LOCAL_SERVICE_ENVIRONMENT = 'FieldSimulation'
$env:WPF_LOCAL_MES_ENVIRONMENT = 'FieldSimulation'
$env:WPF_LOCAL_DATA_PATH = Join-Path $root 'data'
$env:SIMULATOR_BASE_URL = 'http://127.0.0.1:5183/'
$env:ADAPTER_BASE_URL = 'http://127.0.0.1:5041/'
$env:MES_BASE_URL = 'http://127.0.0.1:5045/'
$env:WPF_TWIN_SCHEMATIC_FOLLOW = 'false'
$env:WPF_ENABLE_PHYSICAL_BATCH = 'false'
Start-Process -FilePath (Join-Path $root 'wpf/MesControlAgv.Wpf.exe') -WorkingDirectory (Join-Path $root 'wpf')
