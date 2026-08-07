param(
    [int]$TimeoutSeconds = 30,
    [string]$MesUrl,
    [string]$AdapterUrl,
    [string]$SimulatorUrl,
    [string]$MesDatabasePath,
    [string]$AdapterDatabasePath,
    [string]$StatePath,
    [string]$RunId,
    [string]$IsolationLabel,
    [int]$SourceStationCode = 2,
    [int]$TargetStationCode = 4,
    [switch]$RequireIsolatedStores
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Resolve-StatePath {
    param(
        [string]$RequestedPath,
        [string]$RequestedRunId
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return [IO.Path]::GetFullPath($RequestedPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedRunId)) {
        $safeRunId = $RequestedRunId.Trim()
        if ($safeRunId -notmatch '^[A-Za-z0-9._-]+$') {
            throw 'RunId may contain only letters, digits, dot, underscore, and hyphen.'
        }

        return Join-Path ([IO.Path]::GetTempPath()) ("MesControlAgv-local-{0}-pids.json" -f $safeRunId)
    }

    return $null
}

function Get-StateService {
    param(
        [AllowNull()][object]$State,
        [string]$ServiceName
    )

    if ($null -eq $State -or $null -eq $State.PSObject.Properties['Services']) {
        return $null
    }

    @($State.Services | Where-Object { $_.Name -eq $ServiceName } | Select-Object -First 1)
}

$resolvedStatePath = Resolve-StatePath $StatePath $RunId
$runState = $null
if (-not [string]::IsNullOrWhiteSpace($resolvedStatePath)) {
    if (-not (Test-Path -LiteralPath $resolvedStatePath -PathType Leaf)) {
        throw "Local service state file was not found at '$resolvedStatePath'."
    }

    $runState = Get-Content -Raw -LiteralPath $resolvedStatePath | ConvertFrom-Json
    if ($null -eq $runState) {
        throw "Local service state file '$resolvedStatePath' is empty."
    }

    if ([string]::IsNullOrWhiteSpace($RunId) -and $null -ne $runState.PSObject.Properties['RunId']) {
        $RunId = [string]$runState.RunId
    }
    elseif (-not [string]::IsNullOrWhiteSpace($RunId) -and $null -ne $runState.PSObject.Properties['RunId'] -and [string]$runState.RunId -ne $RunId) {
        throw "State file run id '$($runState.RunId)' does not match requested run id '$RunId'."
    }
}

$stateSimulator = Get-StateService $runState 'Simulator'
$stateAdapter = Get-StateService $runState 'Adapter'
$stateMes = Get-StateService $runState 'MES'
if ([string]::IsNullOrWhiteSpace($SimulatorUrl) -and $null -ne $stateSimulator) { $SimulatorUrl = [string]$stateSimulator.Url }
if ([string]::IsNullOrWhiteSpace($AdapterUrl) -and $null -ne $stateAdapter) { $AdapterUrl = [string]$stateAdapter.Url }
if ([string]::IsNullOrWhiteSpace($MesUrl) -and $null -ne $stateMes) { $MesUrl = [string]$stateMes.Url }
if ([string]::IsNullOrWhiteSpace($AdapterDatabasePath) -and $null -ne $stateAdapter) { $AdapterDatabasePath = [string]$stateAdapter.DatabasePath }
if ([string]::IsNullOrWhiteSpace($MesDatabasePath) -and $null -ne $stateMes) { $MesDatabasePath = [string]$stateMes.DatabasePath }

if ([string]::IsNullOrWhiteSpace($MesUrl)) { $MesUrl = 'http://localhost:5045' }
if ([string]::IsNullOrWhiteSpace($AdapterUrl)) { $AdapterUrl = 'http://localhost:5041' }
if ([string]::IsNullOrWhiteSpace($SimulatorUrl)) { $SimulatorUrl = 'http://localhost:5183' }

$mes = $MesUrl.TrimEnd('/')
$adapter = $AdapterUrl.TrimEnd('/')
$simulator = $SimulatorUrl.TrimEnd('/')

# This script verifies already-running services; it does not start or stop them.
# For process-level checks, start MES and Adapter with fresh SQLite stores (the
# run-local script accepts database paths and supplies `Data Source=` connection
# strings), then pass the same paths here and use -RequireIsolatedStores. This
# prevents a prior active task in data/mes.db or data/adapter.db from affecting
# fleet correlation.
# Simulator state is in-memory, so use a freshly started simulator process/port
# for the same run (there is no simulator database path to pass here).
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
    sourceStationCode = $SourceStationCode
    targetStationCode = $TargetStationCode
    externalId = $externalId
    description = "Offline process verification ($externalId)"
} | ConvertTo-Json
$task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
if ($task.status -ne 'Created') { throw "Unexpected created status: $($task.status)" }

$task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
if ($task.status -ne 'MovingToPickup') { throw "Unexpected pickup dispatch status: $($task.status)" }
if (@($task.activePath).Count -lt 2 -or @($task.activePath)[-1] -eq $null) { throw 'MES did not return a non-empty pickup execution path.' }

$operationId = $task.activeDeviceTaskId
if ([string]::IsNullOrWhiteSpace($operationId)) { throw 'MES did not return the active pickup operation ID.' }
$operationGuid = [Guid]::Parse($operationId)
$agvId = $task.activeAgvId
if ([string]::IsNullOrWhiteSpace($agvId)) { throw 'MES did not return the AGV assigned to the pickup operation.' }
$encodedAgvId = [Uri]::EscapeDataString($agvId)

$fleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$activeStatus = @(Get-FleetEntryForTask $fleetStatus ([Guid]$task.id)) | Select-Object -First 1
if ($null -eq $activeStatus) { throw 'Fleet status did not correlate the dispatched MES task to an AGV.' }
if ($activeStatus.activeTask.mesStatus -ne 'MovingToPickup') { throw "Unexpected fleet MES status: $($activeStatus.activeTask.mesStatus)" }
if ($activeStatus.activeTask.deviceState -ne 'moving') { throw "Unexpected fleet device state: $($activeStatus.activeTask.deviceState)" }

$pauseBody = @{ command = 'pause'; taskId = $operationGuid } | ConvertTo-Json
$paused = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/$encodedAgvId/command" -ContentType 'application/json' -Body $pauseBody
if ($paused.state -ne 'paused') { throw "Adapter did not confirm pause: $($paused.state)" }
$pausedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($pausedTask.task.status -ne 'Paused') { throw "MES did not record Paused: $($pausedTask.task.status)" }
$pausedFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$pausedActive = @(Get-FleetEntryForTask $pausedFleetStatus ([Guid]$task.id)) | Select-Object -First 1
if ($null -eq $pausedActive -or $pausedActive.activeTask.mesStatus -ne 'Paused') { throw 'Fleet status did not record the paused MES task.' }

$resumeBody = @{ command = 'resume'; taskId = $operationGuid } | ConvertTo-Json
$resumed = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/$encodedAgvId/command" -ContentType 'application/json' -Body $resumeBody
if ($resumed.state -notin @('accepted', 'moving')) { throw "Adapter did not confirm resume: $($resumed.state)" }
$resumedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($resumedTask.task.status -ne 'MovingToPickup') { throw "MES did not record resumed pickup: $($resumedTask.task.status)" }
$resumedFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$resumedActive = @(Get-FleetEntryForTask $resumedFleetStatus ([Guid]$task.id)) | Select-Object -First 1
if ($null -eq $resumedActive -or $resumedActive.activeTask.mesStatus -ne 'MovingToPickup') { throw 'Fleet status did not restore the pickup leg after resume.' }

Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
$arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
if ($arrived.status -ne 'WaitingPickupConfirmation') { throw "Unexpected pickup arrival status: $($arrived.status)" }

$operatorBody = @{ operatorName = 'verify-local' } | ConvertTo-Json
$pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
if ($pickup.status -ne 'MovingToDropoff') { throw "Unexpected dropoff status: $($pickup.status)" }
$operationId = $pickup.activeDeviceTaskId
if ([string]::IsNullOrWhiteSpace($operationId)) { throw 'MES did not return the active dropoff operation ID.' }
$operationGuid = [Guid]::Parse($operationId)
$agvId = $pickup.activeAgvId
if ([string]::IsNullOrWhiteSpace($agvId)) { throw 'MES did not return the AGV assigned to the dropoff operation.' }
$encodedAgvId = [Uri]::EscapeDataString($agvId)

$dropoffFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$dropoffActive = @(Get-FleetEntryForTask $dropoffFleetStatus ([Guid]$task.id)) | Select-Object -First 1
if ($null -eq $dropoffActive -or $dropoffActive.activeTask.mesStatus -ne 'MovingToDropoff') { throw 'Fleet status did not record the active dropoff leg.' }

$dropoffPauseBody = @{ command = 'pause'; taskId = $operationGuid } | ConvertTo-Json
$dropoffPaused = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/$encodedAgvId/command" -ContentType 'application/json' -Body $dropoffPauseBody
if ($dropoffPaused.state -ne 'paused') { throw "Adapter did not confirm dropoff pause: $($dropoffPaused.state)" }
$dropoffPausedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($dropoffPausedTask.task.status -ne 'Paused') { throw "MES did not record dropoff Paused: $($dropoffPausedTask.task.status)" }

$dropoffResumeBody = @{ command = 'resume'; taskId = $operationGuid } | ConvertTo-Json
$dropoffResumed = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/$encodedAgvId/command" -ContentType 'application/json' -Body $dropoffResumeBody
if ($dropoffResumed.state -notin @('accepted', 'moving')) { throw "Adapter did not confirm dropoff resume: $($dropoffResumed.state)" }
$dropoffResumedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($dropoffResumedTask.task.status -ne 'MovingToDropoff') { throw "MES did not record resumed dropoff: $($dropoffResumedTask.task.status)" }

Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
$arrivedAtDropoff = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
if ($arrivedAtDropoff.status -ne 'WaitingDropoffConfirmation') { throw "Unexpected dropoff arrival status: $($arrivedAtDropoff.status)" }

$completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
if ($completed.status -ne 'Completed') { throw "Unexpected terminal status: $($completed.status)" }

$detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($detail.task.status -ne 'Completed') { throw "Task detail did not record Completed: $($detail.task.status)" }
$finalFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
$remainingActive = @(Get-FleetEntryForTask $finalFleetStatus ([Guid]$task.id))
if ($remainingActive.Count -gt 0) { throw 'Completed task still appears as an active fleet task.' }
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

$runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
Write-Host "Local Simulator transport verification passed for task $($task.id)$runSuffix."
