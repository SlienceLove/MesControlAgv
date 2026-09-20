[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$AdapterConfigurationPath,
    [Parameter(Mandatory = $true)][string]$WorkflowStorePath,
    [Parameter(Mandatory = $true)][string]$MapSmapPath,
    [switch]$EnablePhysicalBatch,
    [switch]$InspectOnly
)
$ErrorActionPreference = 'Stop'

# Only start a client. Never start, stop or reconfigure MES / Adapter here.
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$exe = Join-Path $package 'MesControlAgv.Wpf.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "WPF package missing: $exe" }
$adapterConfig = (Resolve-Path -LiteralPath $AdapterConfigurationPath).Path
$workflowStore = (Resolve-Path -LiteralPath $WorkflowStorePath).Path
$map = (Resolve-Path -LiteralPath $MapSmapPath).Path

$environment = [ordered]@{
    WPF_RUNTIME_MODE = 'physical'
    WPF_MANAGE_LOCAL_SERVICES = 'false'
    WPF_MANAGE_LOCAL_MES = 'false'
    WPF_TWIN_SCHEMATIC_FOLLOW = 'true'
    MES_BASE_URL = 'http://127.0.0.1:15445/'
    ADAPTER_BASE_URL = 'http://127.0.0.1:15441/'
    WPF_ADAPTER_CONFIG_PATH = $adapterConfig
    WPF_WORKFLOW_STORE_PATH = $workflowStore
    MAP_SMAP_PATH = $map
    WPF_ENABLE_PHYSICAL_BATCH = $EnablePhysicalBatch.IsPresent.ToString().ToLowerInvariant()
}
$start = [System.Diagnostics.ProcessStartInfo]::new()
$start.FileName = $exe
$start.WorkingDirectory = $package
$start.UseShellExecute = $false
# Do not leak parent-session WPF options into this daily profile. All settings
# below belong to the child only; the shell/user/machine environment is unchanged.
foreach ($key in @($start.EnvironmentVariables.Keys)) {
    if ($key -like 'WPF_*' -or $key -in @('MES_BASE_URL', 'ADAPTER_BASE_URL', 'SIMULATOR_BASE_URL',
        'MAP_SMAP_PATH', 'MAP_STATION_MAPPING_PATH', 'WORKFLOW_EXECUTION_ID', 'WORKFLOW_REQUEST_ID')) {
        $start.EnvironmentVariables.Remove($key)
    }
}
foreach ($entry in $environment.GetEnumerator()) { $start.EnvironmentVariables[$entry.Key] = $entry.Value }
if ($InspectOnly) {
    # Report only application settings, never unrelated inherited secrets.
    $inspected = [ordered]@{}
    foreach ($key in @($start.EnvironmentVariables.Keys)) {
        if ($key -like 'WPF_*' -or $key -in @('MES_BASE_URL', 'ADAPTER_BASE_URL', 'SIMULATOR_BASE_URL',
            'MAP_SMAP_PATH', 'MAP_STATION_MAPPING_PATH', 'WORKFLOW_EXECUTION_ID', 'WORKFLOW_REQUEST_ID')) {
            $inspected[$key] = $start.EnvironmentVariables[$key]
        }
    }
    [pscustomobject]@{ Executable = $start.FileName; Arguments = @(); Environment = $inspected } | ConvertTo-Json -Depth 4
    return
}
# No workflow run binding, command arguments, or implicit action approval.
$process = [System.Diagnostics.Process]::Start($start)
[pscustomobject]@{ ProcessId = $process.Id; Executable = $exe; Mes = $environment.MES_BASE_URL }
