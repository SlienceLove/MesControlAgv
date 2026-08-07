param(
    [int]$TimeoutSeconds = 30,
    [string]$MesUrl = 'http://localhost:5045',
    [string]$AdapterUrl = 'http://localhost:5041',
    [string]$SimulatorUrl = 'http://localhost:5183',
    [string]$MesDatabasePath,
    [string]$AdapterDatabasePath,
    [string]$IsolationLabel,
    [switch]$RequireIsolatedStores
)

$ErrorActionPreference = 'Stop'
$mes = $MesUrl.TrimEnd('/')
$adapter = $AdapterUrl.TrimEnd('/')
$simulator = $SimulatorUrl.TrimEnd('/')

# This script verifies already-running services; it does not start or stop them.
# For process-level checks, start MES and Adapter with fresh SQLite stores, for
# example by setting ConnectionStrings__Mes and ConnectionStrings__Adapter, then
# pass the same paths here and use -RequireIsolatedStores. This prevents a prior
# active task in data/mes.db or data/adapter.db from affecting fleet correlation.
# Simulator state is in-memory, so use a freshly started simulator process/port
# for the same run (there is no simulator database path to pass here).
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$defaultMesDatabasePaths = @(
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'data\mes.db')),
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'src\MesControlAgv.Mes\data\mes.db'))
)
$defaultAdapterDatabasePaths = @(
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'data\adapter.db')),
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'src\MesControlAgv.Adapter\data\adapter.db'))
)

function Assert-DatabaseIsolation {
    param(
        [string]$ServiceName,
        [string]$DatabasePath,
        [string[]]$DefaultDatabasePaths,
        [bool]$Required
    )

    if ([string]::IsNullOrWhiteSpace($DatabasePath)) {
        if ($Required) {
            throw "$ServiceName database path is required with -RequireIsolatedStores. Start the service with a temporary SQLite path and pass it to this script."
        }
        return
    }

    $resolvedPath = [IO.Path]::GetFullPath($DatabasePath)
    if ($DefaultDatabasePaths -contains $resolvedPath) {
        throw "$ServiceName database path '$resolvedPath' is the default shared store. Use a temporary path for process-level verification."
    }

    $parentPath = Split-Path -Parent $resolvedPath
    if (-not [string]::IsNullOrWhiteSpace($parentPath) -and -not (Test-Path -LiteralPath $parentPath -PathType Container)) {
        Write-Host "$ServiceName database directory will be created by the service: $parentPath"
    }

    Write-Host "$ServiceName verification database: $resolvedPath"
}

if ($RequireIsolatedStores -and (([string]::IsNullOrWhiteSpace($MesDatabasePath)) -or ([string]::IsNullOrWhiteSpace($AdapterDatabasePath)))) {
    throw '-RequireIsolatedStores requires both -MesDatabasePath and -AdapterDatabasePath.'
}

Assert-DatabaseIsolation 'MES' $MesDatabasePath $defaultMesDatabasePaths $RequireIsolatedStores
Assert-DatabaseIsolation 'Adapter' $AdapterDatabasePath $defaultAdapterDatabasePaths $RequireIsolatedStores

if (-not [string]::IsNullOrWhiteSpace($IsolationLabel)) {
    Write-Host "Verification isolation label: $IsolationLabel"
}

function Wait-Health {
    param(
        [string]$BaseUrl,
        [string]$ServiceName,
        [int]$Timeout
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        try {
            $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 2
            if ($health.service -eq $ServiceName -and $health.status -eq 'ok') { return }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$ServiceName did not become healthy at $BaseUrl."
}

function Get-FleetEntries {
    param([AllowNull()][object]$Response)

    if ($null -eq $Response) {
        return @()
    }

    # Depending on the hosting/client combination, an API collection can be
    # returned as a JSON array or as an object wrapper such as { value: [...] }.
    # Unwrap known collection properties before applying fleet assertions.
    foreach ($propertyName in @('value', 'items', 'data')) {
        $property = $Response.PSObject.Properties[$propertyName]
        if ($null -ne $property) {
            return @(Get-FleetEntries $property.Value)
        }
    }

    if ($Response -is [System.Array]) {
        return @($Response)
    }

    return @($Response)
}

function Get-FleetEntryForTask {
    param(
        [AllowNull()][object]$Response,
        [Guid]$TaskId
    )

    $taskIdText = $TaskId.ToString()
    return @(Get-FleetEntries $Response |
        Where-Object {
            $transportTaskId = $_.activeTask.transportTaskId
            $null -ne $transportTaskId -and $transportTaskId.ToString() -eq $taskIdText
        } |
        Select-Object -First 1)
}

Wait-Health $simulator 'simulator' $TimeoutSeconds
Wait-Health $adapter 'adapter' $TimeoutSeconds
Wait-Health $mes 'mes' $TimeoutSeconds

$verificationId = [Guid]::NewGuid().ToString('N')
$externalId = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
    "verify-local-$verificationId"
} else {
    "$IsolationLabel-$verificationId"
}
$createBody = @{
    sourceStationCode = 2
    targetStationCode = 4
    externalId = $externalId
    description = "Offline process verification ($externalId)"
} | ConvertTo-Json
$task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
if ($task.status -ne 'Created') { throw "Unexpected created status: $($task.status)" }

$task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
if ($task.status -ne 'MovingToPickup') { throw "Unexpected pickup dispatch status: $($task.status)" }

$operationId = $task.activeDeviceTaskId
if ([string]::IsNullOrWhiteSpace($operationId)) { throw 'MES did not return the active pickup operation ID.' }
$operationGuid = [Guid]::Parse($operationId)

$fleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$activeStatus = @(Get-FleetEntryForTask $fleetStatus ([Guid]$task.id)) | Select-Object -First 1
if ($null -eq $activeStatus) { throw 'Fleet status did not correlate the dispatched MES task to an AGV.' }
if ($activeStatus.activeTask.mesStatus -ne 'MovingToPickup') { throw "Unexpected fleet MES status: $($activeStatus.activeTask.mesStatus)" }
if ($activeStatus.activeTask.deviceState -ne 'moving') { throw "Unexpected fleet device state: $($activeStatus.activeTask.deviceState)" }

$pauseBody = @{ command = 'pause'; taskId = $operationGuid } | ConvertTo-Json
$paused = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/AGV-01/command" -ContentType 'application/json' -Body $pauseBody
if ($paused.state -ne 'paused') { throw "Adapter did not confirm pause: $($paused.state)" }
$pausedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($pausedTask.task.status -ne 'Paused') { throw "MES did not record Paused: $($pausedTask.task.status)" }
$pausedFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$pausedActive = @(Get-FleetEntryForTask $pausedFleetStatus ([Guid]$task.id)) | Select-Object -First 1
if ($null -eq $pausedActive -or $pausedActive.activeTask.mesStatus -ne 'Paused') { throw 'Fleet status did not record the paused MES task.' }

$resumeBody = @{ command = 'resume'; taskId = $operationGuid } | ConvertTo-Json
$resumed = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/AGV-01/command" -ContentType 'application/json' -Body $resumeBody
if ($resumed.state -notin @('accepted', 'moving')) { throw "Adapter did not confirm resume: $($resumed.state)" }
$resumedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($resumedTask.task.status -ne 'MovingToPickup') { throw "MES did not record resumed pickup: $($resumedTask.task.status)" }
$resumedFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$resumedActive = @(Get-FleetEntryForTask $resumedFleetStatus ([Guid]$task.id)) | Select-Object -First 1
if ($null -eq $resumedActive -or $resumedActive.activeTask.mesStatus -ne 'MovingToPickup') { throw 'Fleet status did not restore the pickup leg after resume.' }

Invoke-RestMethod -Method Post -Uri "$simulator/controls/arrive" | Out-Null
$arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
if ($arrived.status -ne 'WaitingPickupConfirmation') { throw "Unexpected pickup arrival status: $($arrived.status)" }

$operatorBody = @{ operatorName = 'verify-local' } | ConvertTo-Json
$pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
if ($pickup.status -ne 'MovingToDropoff') { throw "Unexpected dropoff status: $($pickup.status)" }

Invoke-RestMethod -Method Post -Uri "$simulator/controls/arrive" | Out-Null
$arrivedAtDropoff = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
if ($arrivedAtDropoff.status -ne 'WaitingDropoffConfirmation') { throw "Unexpected dropoff arrival status: $($arrivedAtDropoff.status)" }

$completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
if ($completed.status -ne 'Completed') { throw "Unexpected terminal status: $($completed.status)" }

$detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
$eventTypes = @($detail.events | ForEach-Object { $_.eventType })
foreach ($requiredEvent in @(
    'TaskCreated',
    'DispatchRequested',
    'PauseRequested',
    'ResumeRequested',
    'PickupArrived',
    'PickupConfirmed',
    'DropoffArrived',
    'DropoffConfirmed')) {
    if ($eventTypes -notcontains $requiredEvent) { throw "Missing audit event: $requiredEvent" }
}

Write-Host "Live AGV transport verification passed for task $($task.id)."
