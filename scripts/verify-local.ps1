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
    [ValidateSet('positive', 'failure-retry', 'timeout-recover', 'cancel', 'workflow-publish-rollback', 'multi-agv', 'restart-resume')]
    [string]$Scenario = 'positive',
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

# This script verifies already-running services. All scenarios except
# restart-resume leave process ownership to the caller; restart-resume invokes
# the guarded restart-local.ps1 helper after it has created a persisted task.
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
        return @($Response | ForEach-Object { Get-FleetEntries $_ })
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

function Invoke-FailureRecoveryScenario {
    $verificationId = [Guid]::NewGuid().ToString('N')
    $externalId = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
        "verify-local-failure-$verificationId"
    } else {
        "$IsolationLabel-failure-$verificationId"
    }
    $createBody = @{
        sourceStationCode = $SourceStationCode
        targetStationCode = $TargetStationCode
        externalId = $externalId
        description = "Offline failure/retry verification ($externalId)"
    } | ConvertTo-Json
    $task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
    if ($task.status -ne 'Created') { throw "Unexpected created status for failure scenario: $($task.status)" }

    # The simulator consumes this fault on the next navigation request. It is
    # deliberately injected before MES dispatch so the failure is persisted
    # through the normal Adapter/MES path rather than mocked in the script.
    $fleetCandidates = @(Get-FleetEntries (Invoke-RestMethod -Uri "$adapter/agvs") |
        Where-Object { $_.online -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$_.agvId) } |
        Sort-Object agvId)
    if ($fleetCandidates.Count -eq 0) { throw 'Adapter did not return an online AGV for failure injection.' }
    $failureAgvId = [string]$fleetCandidates[0].agvId
    $encodedAgvId = [Uri]::EscapeDataString($failureAgvId)
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/fail" | Out-Null
    $failed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
    if ($failed.status -ne 'Failed') { throw "Expected injected navigation failure, got status: $($failed.status)" }
    if ($failed.lastError -ne 'navigation failed') { throw "Unexpected failure reason: $($failed.lastError)" }

    $failedDetail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    $failedEvents = @($failedDetail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('TaskCreated', 'DispatchRequested', 'DeviceFailed')) {
        if ($failedEvents -notcontains $requiredEvent) { throw "Failure scenario missing audit event: $requiredEvent" }
    }
    if (@(Get-FleetEntryForTask (Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status") ([Guid]$task.id)).Count -gt 0) {
        throw 'Failed task still appears as an active fleet task before retry.'
    }

    $retried = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/retry"
    if ($retried.status -ne 'MovingToPickup') { throw "Expected retry to resume pickup, got status: $($retried.status)" }
    if ($retried.retryCount -ne 1) { throw "Expected retry count 1, got: $($retried.retryCount)" }
    if ([string]::IsNullOrWhiteSpace($retried.activeDeviceTaskId)) { throw 'Retry did not return a pickup operation ID.' }
    if (@($retried.activePath).Count -lt 2 -or @($retried.activePath)[-1] -eq $null) { throw 'Retry did not return a non-empty pickup execution path.' }

    $agvId = [string]$retried.activeAgvId
    if ([string]::IsNullOrWhiteSpace($agvId)) { throw 'Retry did not return the assigned AGV.' }
    $encodedAgvId = [Uri]::EscapeDataString($agvId)
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
    if ($arrived.status -ne 'WaitingPickupConfirmation') { throw "Unexpected recovered pickup arrival status: $($arrived.status)" }

    $operatorBody = @{ operatorName = 'verify-local-failure' } | ConvertTo-Json
    $pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
    if ($pickup.status -ne 'MovingToDropoff') { throw "Unexpected recovered dropoff status: $($pickup.status)" }
    if ([string]::IsNullOrWhiteSpace($pickup.activeDeviceTaskId)) { throw 'Recovered dropoff dispatch did not return an operation ID.' }
    $encodedAgvId = [Uri]::EscapeDataString([string]$pickup.activeAgvId)
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrivedAtDropoff = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
    if ($arrivedAtDropoff.status -ne 'WaitingDropoffConfirmation') { throw "Unexpected recovered dropoff arrival status: $($arrivedAtDropoff.status)" }

    $completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
    if ($completed.status -ne 'Completed') { throw "Recovered task did not complete: $($completed.status)" }
    $detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    if ($detail.task.status -ne 'Completed') { throw "Recovered task detail did not record Completed: $($detail.task.status)" }
    $eventTypes = @($detail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('DeviceFailed', 'RetryRequested', 'PickupArrived', 'PickupConfirmed', 'DropoffArrived', 'DropoffConfirmed')) {
        if ($eventTypes -notcontains $requiredEvent) { throw "Failure/retry scenario missing audit event: $requiredEvent" }
    }
    if (@(Get-FleetEntryForTask (Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status") ([Guid]$task.id)).Count -gt 0) {
        throw 'Recovered completed task still appears as an active fleet task.'
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator failure/retry verification passed for task $($task.id)$runSuffix."
}

function Invoke-TimeoutRecoveryScenario {
    $verificationId = [Guid]::NewGuid().ToString('N')
    $externalId = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
        "verify-local-timeout-$verificationId"
    } else {
        "$IsolationLabel-timeout-$verificationId"
    }
    $createBody = @{
        sourceStationCode = $SourceStationCode
        targetStationCode = $TargetStationCode
        externalId = $externalId
        description = "Offline timeout/recovery verification ($externalId)"
    } | ConvertTo-Json
    $task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
    if ($task.status -ne 'Created') { throw "Unexpected created status for timeout scenario: $($task.status)" }

    $fleetCandidates = @(Get-FleetEntries (Invoke-RestMethod -Uri "$adapter/agvs") |
        Where-Object { $_.online -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$_.agvId) } |
        Sort-Object agvId)
    if ($fleetCandidates.Count -eq 0) { throw 'Adapter did not return an online AGV for timeout injection.' }
    $agvId = [string]$fleetCandidates[0].agvId
    $encodedAgvId = [Uri]::EscapeDataString($agvId)

    # timeout-unknown returns the transport timeout without creating a
    # queryable device task. This exercises the MES Unknown state explicitly;
    # the existing timeout mode remains the queryable-device reconciliation.
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/timeout-unknown" | Out-Null
    $unknown = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
    if ($unknown.status -ne 'Unknown') { throw "Expected timeout to produce Unknown, got: $($unknown.status)" }
    if ([string]::IsNullOrWhiteSpace([string]$unknown.lastError)) { throw 'Unknown timeout did not retain a failure reason.' }
    if ([string]::IsNullOrWhiteSpace([string]$unknown.activeDeviceTaskId)) { throw 'Unknown timeout did not retain the operation ID.' }
    $operationGuid = [Guid]::Parse([string]$unknown.activeDeviceTaskId)
    $path = @($unknown.activePath)
    if ($path.Count -lt 2 -or $null -eq $path[-1]) { throw 'Unknown timeout did not retain the planned recovery path.' }

    $unknownDetail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    $unknownEvents = @($unknownDetail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('TaskCreated', 'DispatchRequested', 'Timeout')) {
        if ($unknownEvents -notcontains $requiredEvent) { throw "Timeout scenario missing audit event: $requiredEvent" }
    }
    if ($unknownEvents -contains 'PickupMoveStarted') { throw 'Unknown timeout was incorrectly marked as moving before recovery.' }
    $unknownFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
    $unknownFleetTask = @(Get-FleetEntryForTask $unknownFleetStatus ([Guid]$task.id)) | Select-Object -First 1
    if ($null -eq $unknownFleetTask -or $unknownFleetTask.activeTask.mesStatus -ne 'Unknown') {
        throw 'MES fleet status did not retain the Unknown task for recovery visibility.'
    }

    # Recreate only the device-side operation through Simulator, then let MES
    # recover it. No second MES dispatch is allowed for this operation.
    $recreateBody = @{
        taskId = $operationGuid
        sourceStationId = [string]$path[0]
        targetStationId = [string]$path[-1]
        path = $path
    } | ConvertTo-Json
    $recreated = Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/commands/navigate" -ContentType 'application/json' -Body $recreateBody
    if ($recreated.state -ne 'moving') { throw "Simulator did not recreate the unknown operation: $($recreated.state)" }
    if ($recreated.deviceTaskId -ne $unknown.activeDeviceTaskId) { throw 'Simulator recreated a different operation ID.' }

    $recovered = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/recover"
    if ($recovered.status -ne 'MovingToPickup') { throw "MES did not reconcile Unknown to MovingToPickup: $($recovered.status)" }
    if ($recovered.activeDeviceTaskId -ne $unknown.activeDeviceTaskId) { throw 'Recovery changed the active operation ID.' }
    $recoveredDetail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    $recoveredEvents = @($recoveredDetail.events | ForEach-Object { $_.eventType })
    if (($recoveredEvents | Where-Object { $_ -eq 'DispatchRequested' }).Count -ne 1) {
        throw 'Recovery unexpectedly recorded a second MES dispatch request.'
    }
    if ($recoveredEvents -notcontains 'ReconciledMoving') { throw 'MES recovery did not record ReconciledMoving.' }

    $fleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
    $activeStatus = @(Get-FleetEntryForTask $fleetStatus ([Guid]$task.id)) | Select-Object -First 1
    if ($null -eq $activeStatus -or $activeStatus.activeTask.mesStatus -ne 'MovingToPickup') {
        throw 'MES fleet status did not expose the recovered pickup operation.'
    }
    if ($activeStatus.activeTask.deviceState -ne 'moving') { throw 'MES fleet status did not expose moving device state after recovery.' }

    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
    if ($arrived.status -ne 'WaitingPickupConfirmation') { throw "Unexpected recovered pickup arrival status: $($arrived.status)" }
    $operatorBody = @{ operatorName = 'verify-local-timeout' } | ConvertTo-Json
    $pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
    if ($pickup.status -ne 'MovingToDropoff') { throw "Unexpected recovered dropoff status: $($pickup.status)" }
    $encodedAgvId = [Uri]::EscapeDataString([string]$pickup.activeAgvId)
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrivedAtDropoff = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
    if ($arrivedAtDropoff.status -ne 'WaitingDropoffConfirmation') { throw "Unexpected recovered dropoff arrival status: $($arrivedAtDropoff.status)" }
    $completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
    if ($completed.status -ne 'Completed') { throw "Recovered timeout task did not complete: $($completed.status)" }

    $detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    if ($detail.task.status -ne 'Completed') { throw "Recovered timeout task detail did not record Completed: $($detail.task.status)" }
    $eventTypes = @($detail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('Timeout', 'ReconciledMoving', 'PickupArrived', 'PickupConfirmed', 'DropoffArrived', 'DropoffConfirmed')) {
        if ($eventTypes -notcontains $requiredEvent) { throw "Timeout/recovery scenario missing audit event: $requiredEvent" }
    }
    if (@(Get-FleetEntryForTask (Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status") ([Guid]$task.id)).Count -gt 0) {
        throw 'Recovered timeout task still appears as an active MES fleet task.'
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator timeout/recovery verification passed for task $($task.id)$runSuffix."
}

function Invoke-CancelScenario {
    $verificationId = [Guid]::NewGuid().ToString('N')
    $externalPrefix = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
        "verify-local-cancel-$verificationId"
    } else {
        "$IsolationLabel-cancel-$verificationId"
    }
    $operatorBody = @{ operatorName = 'verify-local-cancel' } | ConvertTo-Json

    # First prove that a task which has not reached the adapter is cancelled
    # by MES alone and still produces the same terminal audit contract.
    $createdBody = @{
        sourceStationCode = $SourceStationCode
        targetStationCode = $TargetStationCode
        externalId = "$externalPrefix-created"
        description = "Offline Created-task cancellation verification ($externalPrefix)"
    } | ConvertTo-Json
    $created = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createdBody
    if ($created.status -ne 'Created') { throw "Unexpected Created-task status before cancel: $($created.status)" }

    $createdCancelled = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/cancel" -ContentType 'application/json' -Body $operatorBody
    if ($createdCancelled.status -ne 'Cancelled') { throw "MES did not cancel a Created task: $($createdCancelled.status)" }
    $createdDetail = Invoke-RestMethod -Uri "$mes/api/tasks/$($created.id)"
    if ($createdDetail.task.status -ne 'Cancelled') { throw "Created-task detail did not record Cancelled: $($createdDetail.task.status)" }
    $createdEvents = @($createdDetail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('TaskCreated', 'CancelConfirmed')) {
        if ($createdEvents -notcontains $requiredEvent) { throw "Created-task cancellation missing audit event: $requiredEvent" }
    }
    if (@(Get-FleetEntryForTask (Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status") ([Guid]$created.id)).Count -gt 0) {
        throw 'Cancelled Created task still appears as an active MES fleet task.'
    }

    # Dispatch a second task, cancel it directly in the simulator, then send
    # the MES cancel command. The latter must remain idempotent and persist the
    # device-confirmed CancelConfirmed transition.
    $dispatchBody = @{
        sourceStationCode = $SourceStationCode
        targetStationCode = $TargetStationCode
        externalId = "$externalPrefix-dispatched"
        description = "Offline dispatched-task cancellation verification ($externalPrefix)"
    } | ConvertTo-Json
    $task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $dispatchBody
    if ($task.status -ne 'Created') { throw "Unexpected dispatched-task creation status: $($task.status)" }

    $task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
    if ($task.status -ne 'MovingToPickup') { throw "Unexpected dispatched-task status before cancel: $($task.status)" }
    $operationId = $task.activeDeviceTaskId
    if ([string]::IsNullOrWhiteSpace($operationId)) { throw 'MES did not return an active operation ID for cancellation.' }
    $operationGuid = [Guid]::Parse($operationId)
    $agvId = [string]$task.activeAgvId
    if ([string]::IsNullOrWhiteSpace($agvId)) { throw 'MES did not return the assigned AGV for cancellation.' }
    $encodedAgvId = [Uri]::EscapeDataString($agvId)

    $activeFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
    $activeFleetTask = @(Get-FleetEntryForTask $activeFleetStatus ([Guid]$task.id)) | Select-Object -First 1
    if ($null -eq $activeFleetTask -or $activeFleetTask.activeTask.mesStatus -ne 'MovingToPickup') {
        throw 'MES fleet status did not expose the dispatched task before cancellation.'
    }

    $simulatorCancelled = Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/commands/$operationGuid/cancel"
    if ($simulatorCancelled.state -ne 'cancelled') { throw "Simulator did not confirm cancellation: $($simulatorCancelled.state)" }
    $simulatorTask = Invoke-RestMethod -Uri "$simulator/agvs/$encodedAgvId/tasks/$operationGuid"
    if ($simulatorTask.state -ne 'cancelled') { throw "Simulator task did not persist cancelled state: $($simulatorTask.state)" }
    $simulatorSnapshot = Invoke-RestMethod -Uri "$simulator/agvs/$encodedAgvId/snapshot"
    if (-not [string]::IsNullOrWhiteSpace([string]$simulatorSnapshot.currentTaskId)) {
        throw 'Simulator still reports an active task after direct cancellation.'
    }

    $cancelled = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/cancel" -ContentType 'application/json' -Body $operatorBody
    if ($cancelled.status -ne 'Cancelled') { throw "MES did not persist device-confirmed cancellation: $($cancelled.status)" }
    $detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    if ($detail.task.status -ne 'Cancelled') { throw "Dispatched-task detail did not record Cancelled: $($detail.task.status)" }
    $eventTypes = @($detail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('TaskCreated', 'DispatchRequested', 'CancelConfirmed')) {
        if ($eventTypes -notcontains $requiredEvent) { throw "Dispatched-task cancellation missing audit event: $requiredEvent" }
    }

    $mesFleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
    if (@(Get-FleetEntryForTask $mesFleetStatus ([Guid]$task.id)).Count -gt 0) {
        throw 'Cancelled dispatched task still appears as an active MES fleet task.'
    }
    $adapterFleet = @(Get-FleetEntries (Invoke-RestMethod -Uri "$adapter/agvs") |
        Where-Object { $_.agvId -eq $agvId } |
        Select-Object -First 1)
    if ($adapterFleet.Count -eq 0) { throw "Adapter fleet did not return cancelled AGV '$agvId'." }
    if (-not [string]::IsNullOrWhiteSpace([string]$adapterFleet[0].currentTaskId)) {
        throw "Adapter fleet still reports an active task for '$agvId' after cancellation."
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator cancellation verification passed for task $($task.id)$runSuffix."
}

function Assert-WorkflowVersionState {
    param(
        [Parameter(Mandatory = $true)][object]$Version,
        [int]$Status,
        [string]$StatusName,
        [int]$PublishStatus,
        [string]$PublishStatusName
    )

    $actualStatus = [string]$Version.status
    if ($actualStatus -ne $StatusName -and $actualStatus -ne [string]$Status) {
        throw "Workflow $($Version.workflowId)/v$($Version.version) expected status $StatusName ($Status), got '$actualStatus'."
    }

    $actualPublishStatus = [string]$Version.publishStatus
    if ($actualPublishStatus -ne $PublishStatusName -and $actualPublishStatus -ne [string]$PublishStatus) {
        throw "Workflow $($Version.workflowId)/v$($Version.version) expected publish status $PublishStatusName ($PublishStatus), got '$actualPublishStatus'."
    }
}

function New-WorkflowDefinition {
    param(
        [Guid]$WorkflowId,
        [Guid]$StartNodeId,
        [Guid]$MoveNodeId,
        [Guid]$EndNodeId,
        [string]$TargetStation,
        [string]$Description
    )

    return @{
        id = $WorkflowId
        name = 'Offline transport rollback workflow'
        description = $Description
        isPreset = $false
        nodes = @(
            @{
                id = $StartNodeId
                type = 0
                name = 'Start'
                description = 'Start transport'
                targetStation = $null
                x = 0
                y = 0
                order = 1
                parameters = @()
                nextNodeIds = @($MoveNodeId)
            }
            @{
                id = $MoveNodeId
                type = 1
                name = 'Move'
                description = $Description
                targetStation = $TargetStation
                x = 160
                y = 0
                order = 2
                parameters = @()
                nextNodeIds = @($EndNodeId)
            }
            @{
                id = $EndNodeId
                type = 5
                name = 'End'
                description = 'End transport'
                targetStation = $null
                x = 320
                y = 0
                order = 3
                parameters = @()
                nextNodeIds = @()
            }
        )
    }
}

function Get-WorkflowMoveTarget {
    param([Parameter(Mandatory = $true)][object]$Version)

    $move = @($Version.definition.nodes | Where-Object { $_.name -eq 'Move' } | Select-Object -First 1)
    if ($move.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$move[0].targetStation)) {
        throw "Workflow $($Version.workflowId)/v$($Version.version) did not return a Move target station."
    }

    return [string]$move[0].targetStation
}

function Get-WorkflowAuditIndex {
    param(
        [Parameter(Mandatory = $true)][object[]]$Audits,
        [string]$EventType,
        [int]$Version
    )

    for ($index = 0; $index -lt $Audits.Count; $index++) {
        if ($Audits[$index].eventType -eq $EventType -and [int]$Audits[$index].version -eq $Version) {
            return $index
        }
    }

    return -1
}

function Invoke-WorkflowPublishRollbackScenario {
    $workflowId = [Guid]::NewGuid()
    $startNodeId = [Guid]::NewGuid()
    $moveNodeId = [Guid]::NewGuid()
    $endNodeId = [Guid]::NewGuid()
    $actor = 'verify-local-workflow'
    $v1Target = 'SAMPLE_01'
    $v2Target = 'DROP_01'

    $definitionV1 = New-WorkflowDefinition `
        -WorkflowId $workflowId `
        -StartNodeId $startNodeId `
        -MoveNodeId $moveNodeId `
        -EndNodeId $endNodeId `
        -TargetStation $v1Target `
        -Description 'Version 1 baseline transport'
    $createV1Body = $definitionV1 | ConvertTo-Json -Depth 10
    $v1 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows?actor=$actor" -ContentType 'application/json' -Body $createV1Body
    if ([int]$v1.version -ne 1) { throw "Expected workflow version 1, got $($v1.version)." }
    Assert-WorkflowVersionState $v1 0 'Draft' 0 'NotPublished'

    $validationV1 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/1/validate"
    if (-not $validationV1.isValid) { throw 'Workflow version 1 did not validate successfully.' }
    $publishedV1 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/1/publish?actor=$actor"
    Assert-WorkflowVersionState $publishedV1 2 'Published' 2 'Published'
    if ([int]$publishedV1.definition.publishedVersion -ne 1) { throw 'Published version 1 did not expose publishedVersion=1.' }

    $v1BeforeEdit = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions/1"
    if ((Get-WorkflowMoveTarget $v1BeforeEdit) -ne $v1Target) { throw 'Workflow version 1 baseline target was not persisted.' }

    # A published version is immutable. The API must reject a draft update and
    # leave both the version payload and its lifecycle audit unchanged.
    $immutableProbe = New-WorkflowDefinition `
        -WorkflowId $workflowId `
        -StartNodeId $startNodeId `
        -MoveNodeId $moveNodeId `
        -EndNodeId $endNodeId `
        -TargetStation 'SHOULD_NOT_APPLY' `
        -Description 'Rejected mutation probe'
    $immutableError = $null
    try {
        Invoke-RestMethod -Method Put `
            -Uri "$mes/api/workflows/$workflowId/versions/1/draft?actor=$actor" `
            -ContentType 'application/json' `
            -Body ($immutableProbe | ConvertTo-Json -Depth 10) | Out-Null
    }
    catch {
        $immutableError = $_.Exception
    }
    if ($null -eq $immutableError -or $null -eq $immutableError.Response) {
        throw 'Published workflow version accepted an immutable draft update.'
    }
    if ([int]$immutableError.Response.StatusCode -ne 422) {
        throw "Immutable workflow update returned HTTP $([int]$immutableError.Response.StatusCode), expected 422."
    }
    $v1AfterEdit = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions/1"
    if ((Get-WorkflowMoveTarget $v1AfterEdit) -ne $v1Target) { throw 'Published workflow version changed after rejected edit.' }
    Assert-WorkflowVersionState $v1AfterEdit 2 'Published' 2 'Published'

    $definitionV2 = New-WorkflowDefinition `
        -WorkflowId $workflowId `
        -StartNodeId $startNodeId `
        -MoveNodeId $moveNodeId `
        -EndNodeId $endNodeId `
        -TargetStation $v2Target `
        -Description 'Version 2 changed destination'
    $v2 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows?actor=$actor" -ContentType 'application/json' -Body ($definitionV2 | ConvertTo-Json -Depth 10)
    if ([int]$v2.version -ne 2) { throw "Expected workflow version 2, got $($v2.version)." }
    Assert-WorkflowVersionState $v2 0 'Draft' 0 'NotPublished'
    $validationV2 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/2/validate"
    if (-not $validationV2.isValid) { throw 'Workflow version 2 did not validate successfully.' }
    $publishedV2 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/2/publish?actor=$actor"
    Assert-WorkflowVersionState $publishedV2 2 'Published' 2 'Published'
    if ([int]$publishedV2.definition.publishedVersion -ne 2) { throw 'Published version 2 did not expose publishedVersion=2.' }

    $v1AfterV2 = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions/1"
    $v2AfterPublish = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions/2"
    Assert-WorkflowVersionState $v1AfterV2 3 'Archived' 4 'Superseded'
    Assert-WorkflowVersionState $v2AfterPublish 2 'Published' 2 'Published'
    if ((Get-WorkflowMoveTarget $v1AfterV2) -ne $v1Target) { throw 'Archived version 1 definition was mutated by publishing version 2.' }
    if ((Get-WorkflowMoveTarget $v2AfterPublish) -ne $v2Target) { throw 'Published version 2 target was not persisted.' }

    # Rollback is represented by a new immutable version carrying the known-good
    # v1 definition. The archived versions are never edited or re-published.
    $v3 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows?actor=$actor" -ContentType 'application/json' -Body ($definitionV1 | ConvertTo-Json -Depth 10)
    if ([int]$v3.version -ne 3) { throw "Expected rollback workflow version 3, got $($v3.version)." }
    Assert-WorkflowVersionState $v3 0 'Draft' 0 'NotPublished'
    $validationV3 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/3/validate"
    if (-not $validationV3.isValid) { throw 'Rollback workflow version 3 did not validate successfully.' }
    $publishedV3 = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/3/publish?actor=$actor"
    Assert-WorkflowVersionState $publishedV3 2 'Published' 2 'Published'
    if ([int]$publishedV3.definition.publishedVersion -ne 3) { throw 'Rollback publish did not expose publishedVersion=3.' }

    $workflow = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId"
    if ([int]$workflow.publishedVersion -ne 3) { throw "Workflow published pointer is $($workflow.publishedVersion), expected 3." }
    if ((Get-WorkflowMoveTarget @{ workflowId = $workflowId; version = 3; definition = $workflow }) -ne $v1Target) {
        throw 'Rollback workflow latest definition did not restore the v1 destination.'
    }

    $v1Final = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions/1"
    $v2Final = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions/2"
    $v3Final = Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions/3"
    Assert-WorkflowVersionState $v1Final 3 'Archived' 4 'Superseded'
    Assert-WorkflowVersionState $v2Final 3 'Archived' 4 'Superseded'
    Assert-WorkflowVersionState $v3Final 2 'Published' 2 'Published'
    if ((Get-WorkflowMoveTarget $v1Final) -ne $v1Target) { throw 'Rollback changed immutable version 1.' }
    if ((Get-WorkflowMoveTarget $v2Final) -ne $v2Target) { throw 'Rollback changed immutable version 2.' }
    if ((Get-WorkflowMoveTarget $v3Final) -ne $v1Target) { throw 'Rollback version 3 does not match the v1 destination.' }

    $versions = @(Get-FleetEntries (Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/versions"))
    if ($versions.Count -ne 3) { throw "Expected three immutable workflow versions, got $($versions.Count)." }
    if ([int]$versions[0].version -ne 3 -or [int]$versions[1].version -ne 2 -or [int]$versions[2].version -ne 1) {
        throw 'Workflow version list was not returned newest-first as expected.'
    }
    foreach ($version in $versions) {
        if ($null -ne $version.definition.publishedVersion -and [int]$version.definition.publishedVersion -ne 3) {
            throw "Workflow version $($version.version) points to an unexpected published version '$($version.definition.publishedVersion)'."
        }
    }

    $audits = @(Get-FleetEntries (Invoke-RestMethod -Uri "$mes/api/workflows/$workflowId/audits"))
    $expectedAudits = @(
        @{ EventType = 'WorkflowDraftCreated'; Version = 1; Outcome = 'Draft' }
        @{ EventType = 'WorkflowVersionValidated'; Version = 1; Outcome = 'Valid' }
        @{ EventType = 'WorkflowVersionPublished'; Version = 1; Outcome = 'Published' }
        @{ EventType = 'WorkflowDraftCreated'; Version = 2; Outcome = 'Draft' }
        @{ EventType = 'WorkflowVersionValidated'; Version = 2; Outcome = 'Valid' }
        @{ EventType = 'WorkflowVersionSuperseded'; Version = 1; Outcome = 'Superseded' }
        @{ EventType = 'WorkflowVersionPublished'; Version = 2; Outcome = 'Published' }
        @{ EventType = 'WorkflowDraftCreated'; Version = 3; Outcome = 'Draft' }
        @{ EventType = 'WorkflowVersionValidated'; Version = 3; Outcome = 'Valid' }
        @{ EventType = 'WorkflowVersionSuperseded'; Version = 2; Outcome = 'Superseded' }
        @{ EventType = 'WorkflowVersionPublished'; Version = 3; Outcome = 'Published' }
    )
    if ($audits.Count -ne $expectedAudits.Count) {
        throw "Expected $($expectedAudits.Count) lifecycle audits, got $($audits.Count)."
    }
    $auditIndexes = @{}
    foreach ($expected in $expectedAudits) {
        $index = Get-WorkflowAuditIndex $audits $expected.EventType $expected.Version
        if ($index -lt 0) { throw "Missing workflow audit $($expected.EventType) for version $($expected.Version)." }
        if ([string]$audits[$index].outcome -ne $expected.Outcome) {
            throw "Workflow audit $($expected.EventType)/v$($expected.Version) has outcome '$($audits[$index].outcome)', expected '$($expected.Outcome)'."
        }
        $auditIndexes["$($expected.EventType)/$($expected.Version)"] = $index
    }
    if ($auditIndexes['WorkflowDraftCreated/1'] -ge $auditIndexes['WorkflowVersionValidated/1'] -or
        $auditIndexes['WorkflowVersionValidated/1'] -ge $auditIndexes['WorkflowVersionPublished/1'] -or
        $auditIndexes['WorkflowVersionSuperseded/1'] -ge $auditIndexes['WorkflowVersionPublished/2'] -or
        $auditIndexes['WorkflowVersionSuperseded/2'] -ge $auditIndexes['WorkflowVersionPublished/3']) {
        throw 'Workflow lifecycle audits are out of order.'
    }
    foreach ($publishedAudit in @($audits | Where-Object { $_.eventType -eq 'WorkflowVersionPublished' })) {
        if ([string]$publishedAudit.actor -ne $actor) { throw 'Workflow publish audit did not preserve the publishing actor.' }
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator workflow publish/rollback verification passed for workflow $workflowId at version 3$runSuffix."
}

function Complete-TransportTask {
    param(
        [Parameter(Mandatory = $true)][object]$Task,
        [Parameter(Mandatory = $true)][string]$OperatorName
    )

    $taskId = [Guid]$Task.id
    $agvId = [string]$Task.activeAgvId
    if ([string]::IsNullOrWhiteSpace($agvId)) { throw "Task $taskId has no assigned AGV." }
    $encodedAgvId = [Uri]::EscapeDataString($agvId)

    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/arrived"
    if ($arrived.status -ne 'WaitingPickupConfirmation') {
        throw "Task $taskId did not reach pickup confirmation: $($arrived.status)"
    }

    $operatorBody = @{ operatorName = $OperatorName } | ConvertTo-Json
    $pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
    if ($pickup.status -ne 'MovingToDropoff') {
        throw "Task $taskId did not start dropoff: $($pickup.status)"
    }
    $dropoffAgvId = [string]$pickup.activeAgvId
    if ([string]::IsNullOrWhiteSpace($dropoffAgvId)) { throw "Task $taskId lost its AGV at dropoff dispatch." }
    $encodedDropoffAgvId = [Uri]::EscapeDataString($dropoffAgvId)

    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedDropoffAgvId/controls/arrive" | Out-Null
    $arrivedAtDropoff = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/arrived"
    if ($arrivedAtDropoff.status -ne 'WaitingDropoffConfirmation') {
        throw "Task $taskId did not reach dropoff confirmation: $($arrivedAtDropoff.status)"
    }

    $completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
    if ($completed.status -ne 'Completed') { throw "Task $taskId did not complete: $($completed.status)" }
    return $completed
}

function Invoke-MultiAgvContentionScenario {
    $fleet = @(Get-FleetEntries (Invoke-RestMethod -Uri "$adapter/agvs") |
        Where-Object { $_.online -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$_.agvId) } |
        Sort-Object agvId)
    if ($fleet.Count -lt 3) {
        throw "Multi-AGV contention requires at least three online Simulator AGVs; Adapter returned $($fleet.Count)."
    }

    $verificationId = [Guid]::NewGuid().ToString('N')
    $taskRecords = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt 3; $index++) {
        $externalId = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
            "verify-local-contention-$verificationId-$index"
        } else {
            "$IsolationLabel-contention-$verificationId-$index"
        }
        $createBody = @{
            sourceStationCode = $SourceStationCode
            targetStationCode = $TargetStationCode
            externalId = $externalId
            description = "Offline multi-AGV contention verification ($externalId)"
        } | ConvertTo-Json
        $created = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
        if ($created.status -ne 'Created') { throw "Contention task $index was not created: $($created.status)" }

        $dispatched = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/dispatch"
        if ($dispatched.status -ne 'MovingToPickup') {
            throw "Contention task $index did not dispatch: $($dispatched.status) ($($dispatched.lastError))"
        }
        if ([string]::IsNullOrWhiteSpace([string]$dispatched.activeAgvId)) {
            throw "Contention task $index did not return an assigned AGV."
        }
        if (@($taskRecords | Where-Object { $_.AgvId -eq [string]$dispatched.activeAgvId }).Count -gt 0) {
            throw "Scheduler assigned duplicate active AGV '$($dispatched.activeAgvId)' to contention tasks."
        }

        $fleetStatus = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
        $active = @(Get-FleetEntryForTask $fleetStatus ([Guid]$dispatched.id)) | Select-Object -First 1
        if ($null -eq $active -or $active.activeTask.mesStatus -ne 'MovingToPickup') {
            throw "Fleet status did not correlate contention task $($dispatched.id)."
        }
        if ($active.snapshot.agvId -ne $dispatched.activeAgvId) {
            throw "Fleet status assigned '$($active.snapshot.agvId)' but task reports '$($dispatched.activeAgvId)'."
        }
        $taskRecords.Add([pscustomobject]@{ Task = $dispatched; AgvId = [string]$dispatched.activeAgvId })
    }

    $fourthBody = @{
        sourceStationCode = $SourceStationCode
        targetStationCode = $TargetStationCode
        externalId = "verify-local-contention-$verificationId-exhausted"
        description = 'Offline multi-AGV resource exhaustion verification'
    } | ConvertTo-Json
    $fourthCreated = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $fourthBody
    $fourth = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($fourthCreated.id)/dispatch"
    if ($fourth.status -ne 'Failed') {
        throw "Fourth contention task should fail closed when all AGVs are busy, got $($fourth.status)."
    }
    if ([string]::IsNullOrWhiteSpace([string]$fourth.lastError)) {
        throw 'Resource-exhausted contention task did not retain a failure reason.'
    }
    $fourthDetail = Invoke-RestMethod -Uri "$mes/api/tasks/$($fourth.id)"
    $fourthEvents = @($fourthDetail.events | ForEach-Object { $_.eventType })
    if ($fourthEvents -notcontains 'DeviceFailed') { throw 'Resource exhaustion did not record DeviceFailed.' }
    if (@(Get-FleetEntryForTask (Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status") ([Guid]$fourth.id)).Count -gt 0) {
        throw 'Failed resource-exhausted task appeared as an active fleet task.'
    }

    # Keep the default AGV last. MES currently uses its single-device snapshot
    # for route planning, so this ordering avoids planning a reverse path from
    # a different AGV after the default AGV has moved to the dropoff station.
    $defaultAgvId = [string]$fleet[0].agvId
    $completionOrder = @(
        @($taskRecords | Where-Object { $_.AgvId -ne $defaultAgvId })
        @($taskRecords | Where-Object { $_.AgvId -eq $defaultAgvId })
    )
    foreach ($record in $completionOrder) {
        $completed = Complete-TransportTask $record.Task "verify-local-contention"
        if ($completed.status -ne 'Completed') { throw "Contention task $($record.Task.id) did not complete." }
    }

    $finalFleet = @(Get-FleetEntries (Invoke-RestMethod -Uri "$adapter/agvs"))
    foreach ($entry in $finalFleet) {
        if ($null -ne $entry.currentTaskId -and -not [string]::IsNullOrWhiteSpace([string]$entry.currentTaskId)) {
            throw "AGV '$($entry.agvId)' retained an active device task after contention cleanup."
        }
    }
    $finalMesFleet = @(Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status")
    foreach ($record in $taskRecords) {
        if (@(Get-FleetEntryForTask $finalMesFleet ([Guid]$record.Task.id)).Count -gt 0) {
            throw "Completed contention task $($record.Task.id) remained in MES fleet status."
        }
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator multi-AGV contention verification passed for three assigned tasks and one fail-closed task$runSuffix."
}

function Invoke-RestartResumeScenario {
    if ([string]::IsNullOrWhiteSpace($resolvedStatePath)) {
        throw 'restart-resume requires -RunId or -StatePath so the service processes can be restarted safely.'
    }
    if ($null -eq $runState -or $null -eq $runState.PSObject.Properties['Services']) {
        throw 'restart-resume requires a state file created by the current run-local.ps1.'
    }

    $verificationId = [Guid]::NewGuid().ToString('N')
    $externalId = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
        "verify-local-restart-$verificationId"
    } else {
        "$IsolationLabel-restart-$verificationId"
    }
    $createBody = @{
        sourceStationCode = $SourceStationCode
        targetStationCode = $TargetStationCode
        externalId = $externalId
        description = "Offline Adapter/MES restart-resume verification ($externalId)"
    } | ConvertTo-Json
    $created = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
    $dispatched = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/dispatch"
    if ($dispatched.status -ne 'MovingToPickup') { throw "Restart task did not dispatch before restart: $($dispatched.status)" }
    $operationId = [string]$dispatched.activeDeviceTaskId
    $agvId = [string]$dispatched.activeAgvId
    if ([string]::IsNullOrWhiteSpace($operationId) -or [string]::IsNullOrWhiteSpace($agvId)) {
        throw 'Restart task did not return operation and AGV correlation.'
    }

    $restartScript = Join-Path $PSScriptRoot 'restart-local.ps1'
    if (-not (Test-Path -LiteralPath $restartScript -PathType Leaf)) { throw "Restart script was not found at '$restartScript'." }
    & $restartScript -StatePath $resolvedStatePath -StartupTimeoutSeconds $TimeoutSeconds

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $recovered = $null
    do {
        try {
            $candidate = Invoke-RestMethod -Uri "$mes/api/tasks/$($created.id)"
            $events = @($candidate.events | ForEach-Object { $_.eventType })
            if ($candidate.task.status -eq 'MovingToPickup' -and $events -contains 'ReconciledMoving') {
                $recovered = $candidate
                break
            }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $recovered) {
        throw "MES did not reconcile task $($created.id) after Adapter/MES restart within $TimeoutSeconds seconds."
    }
    if ($recovered.task.activeDeviceTaskId -ne $operationId -or $recovered.task.activeAgvId -ne $agvId) {
        throw 'Restart recovery changed the persisted operation or AGV assignment.'
    }
    $recoveredEvents = @($recovered.events | ForEach-Object { $_.eventType })
    if (($recoveredEvents | Where-Object { $_ -eq 'DispatchRequested' }).Count -ne 1) {
        throw 'Restart recovery recorded a duplicate DispatchRequested event.'
    }
    if (($recoveredEvents | Where-Object { $_ -eq 'Timeout' }).Count -lt 1) {
        throw 'Restart recovery did not record the fail-closed Timeout event.'
    }

    $encodedAgvId = [Uri]::EscapeDataString($agvId)
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/arrived"
    if ($arrived.status -ne 'WaitingPickupConfirmation') { throw "Restart task pickup did not arrive: $($arrived.status)" }
    $operatorBody = @{ operatorName = 'verify-local-restart' } | ConvertTo-Json
    $pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
    $dropoffAgvId = [string]$pickup.activeAgvId
    $encodedDropoffAgvId = [Uri]::EscapeDataString($dropoffAgvId)
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedDropoffAgvId/controls/arrive" | Out-Null
    $arrivedAtDropoff = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/arrived"
    if ($arrivedAtDropoff.status -ne 'WaitingDropoffConfirmation') { throw "Restart task dropoff did not arrive: $($arrivedAtDropoff.status)" }
    $completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
    if ($completed.status -ne 'Completed') { throw "Restart-resumed task did not complete: $($completed.status)" }
    if (@(Get-FleetEntryForTask (Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status") ([Guid]$created.id)).Count -gt 0) {
        throw 'Restart-resumed completed task remained in MES fleet status.'
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator Adapter/MES restart-resume verification passed for task $($created.id)$runSuffix."
}

Wait-Health $simulator 'simulator' $TimeoutSeconds
Wait-Health $adapter 'adapter' $TimeoutSeconds
Wait-Health $mes 'mes' $TimeoutSeconds

if ($Scenario -eq 'failure-retry') {
    Invoke-FailureRecoveryScenario
    return
}

if ($Scenario -eq 'timeout-recover') {
    Invoke-TimeoutRecoveryScenario
    return
}

if ($Scenario -eq 'cancel') {
    Invoke-CancelScenario
    return
}

if ($Scenario -eq 'workflow-publish-rollback') {
    Invoke-WorkflowPublishRollbackScenario
    return
}

if ($Scenario -eq 'multi-agv') {
    Invoke-MultiAgvContentionScenario
    return
}

if ($Scenario -eq 'restart-resume') {
    Invoke-RestartResumeScenario
    return
}

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
