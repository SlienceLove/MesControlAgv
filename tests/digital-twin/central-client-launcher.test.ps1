param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$AdapterConfigurationPath,
    [Parameter(Mandatory = $true)][string]$WorkflowStorePath,
    [Parameter(Mandatory = $true)][string]$MapSmapPath
)
$ErrorActionPreference = 'Stop'
$launcher = Join-Path $PSScriptRoot '../../scripts/digital-twin/Start-CentralClient.ps1'
$injected = @{
    WORKFLOW_EXECUTION_ID = '11111111-1111-1111-1111-111111111111'
    WORKFLOW_REQUEST_ID = '22222222-2222-2222-2222-222222222222'
    WPF_INITIAL_WORKFLOW_EXECUTION_ID = '33333333-3333-3333-3333-333333333333'
    WPF_INITIAL_WORKFLOW_REQUEST_ID = '44444444-4444-4444-4444-444444444444'
    WPF_RUNTIME_MODE = 'simulator'
    WPF_MANAGE_LOCAL_SERVICES = 'true'
    WPF_MANAGE_LOCAL_MES = 'true'
    WPF_ENABLE_PHYSICAL_BATCH = 'true'
    SIMULATOR_BASE_URL = 'http://old-simulator.invalid/'
}
$saved = @{}
try {
    foreach ($entry in $injected.GetEnumerator()) {
        $saved[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $json = & $launcher @PSBoundParameters -InspectOnly
    $plan = $json | ConvertFrom-Json
    $envPlan = $plan.Environment
    foreach ($key in @('WORKFLOW_EXECUTION_ID', 'WORKFLOW_REQUEST_ID', 'WPF_INITIAL_WORKFLOW_EXECUTION_ID',
        'WPF_INITIAL_WORKFLOW_REQUEST_ID', 'SIMULATOR_BASE_URL')) {
        if ($null -ne $envPlan.PSObject.Properties[$key]) { throw "Inherited binding/options escaped cleanup: $key" }
    }
    if ($plan.Arguments.Count -ne 0 -or $envPlan.WPF_RUNTIME_MODE -ne 'physical' -or
        $envPlan.WPF_MANAGE_LOCAL_SERVICES -ne 'false' -or $envPlan.WPF_MANAGE_LOCAL_MES -ne 'false' -or
        $envPlan.WPF_TWIN_SCHEMATIC_FOLLOW -ne 'true' -or $envPlan.WPF_ENABLE_PHYSICAL_BATCH -ne 'false') {
        throw 'Daily launch profile changed'
    }
    foreach ($entry in $injected.GetEnumerator()) {
        if ([Environment]::GetEnvironmentVariable($entry.Key, 'Process') -ne $entry.Value) { throw 'Launcher changed parent environment' }
    }
    $enabled = & $launcher @PSBoundParameters -InspectOnly -EnablePhysicalBatch | ConvertFrom-Json
    if ($enabled.Environment.WPF_ENABLE_PHYSICAL_BATCH -ne 'true') { throw 'Explicit existing batch UI option lost' }
    # Feed this to CentralClientSmoke --inspect-startup for real inspector validation.
    $json
}
finally {
    foreach ($entry in $saved.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
}
