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
    [ValidateSet('positive', 'failure-retry', 'cancellation', 'timeout-recovery', 'restart-resume', 'multi-agv')]
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

function Get-PortOwners {
    param([int]$Port)

    if ($Port -le 0) { return @() }

    $owners = [System.Collections.Generic.List[int]]::new()
    foreach ($line in @(netstat -ano -p TCP | Select-String 'LISTENING')) {
        $parts = ($line.ToString() -split '\s+') | Where-Object { $_ }
        if ($parts.Count -ge 5 -and $parts[0] -eq 'TCP' -and $parts[1] -match (':{0}$' -f $Port) -and $parts[3] -eq 'LISTENING') {
            $owners.Add([int]$parts[4])
        }
    }

    @($owners | Sort-Object -Unique)
}

function Wait-TaskStatus {
    param(
        [Guid]$TaskId,
        [string[]]$ExpectedStatus,
        [int]$Timeout = $TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        $response = Invoke-RestMethod -Uri "$mes/api/tasks/$TaskId"
        $current = if ($null -ne $response.PSObject.Properties['task']) { $response.task } else { $response }
        if ($ExpectedStatus -contains [string]$current.status) { return $current }
        Start-Sleep -Milliseconds 400
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Task $TaskId did not reach one of [$($ExpectedStatus -join ', ')] (last status: $($current.status))."
}

function Complete-TransportTask {
    param(
        [AllowNull()][object]$Task,
        [string]$OperatorName
    )

    if ($null -eq $Task) { throw 'Cannot complete a missing transport task.' }
    $taskId = [Guid]$Task.id
    $agvId = [string]$Task.activeAgvId
    if ([string]::IsNullOrWhiteSpace($agvId)) { throw "Task $taskId has no assigned AGV." }
    $encodedAgvId = [Uri]::EscapeDataString($agvId)

    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/arrived"
    if ($arrived.status -ne 'WaitingPickupConfirmation') {
        throw "Unexpected pickup arrival status for task ${taskId}: $($arrived.status)"
    }

    $operatorBody = @{ operatorName = $OperatorName } | ConvertTo-Json
    $pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
    if ($pickup.status -ne 'MovingToDropoff') {
        throw "Unexpected dropoff status for task ${taskId}: $($pickup.status)"
    }
    $dropoffAgvId = [string]$pickup.activeAgvId
    if ([string]::IsNullOrWhiteSpace($dropoffAgvId)) { throw "Task $taskId lost its AGV assignment at dropoff." }
    $encodedDropoffAgvId = [Uri]::EscapeDataString($dropoffAgvId)

    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedDropoffAgvId/controls/arrive" | Out-Null
    $dropoffArrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/arrived"
    if ($dropoffArrived.status -ne 'WaitingDropoffConfirmation') {
        throw "Unexpected dropoff arrival status for task ${taskId}: $($dropoffArrived.status)"
    }

    $completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
    if ($completed.status -ne 'Completed') { throw "Task $taskId did not complete: $($completed.status)" }
    return $completed
}

function Restart-MesProcess {
    if ($null -eq $runState -or $null -eq $stateMes) {
        throw 'restart-resume requires a run-local state file so only the owned MES process can be restarted.'
    }
    if ($null -eq $stateMes.PSObject.Properties['ProcessId'] -or $null -eq $stateMes.PSObject.Properties['Dll']) {
        throw 'The local state file does not contain restart metadata. Start a fresh run with the current run-local.ps1.'
    }

    $oldPid = [int]$stateMes.ProcessId
    $process = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
    if ($null -eq $process) { throw "MES process $oldPid is not running." }
    if (-not [string]::IsNullOrWhiteSpace([string]$stateMes.Executable)) {
        $actualPath = $null
        try { $actualPath = $process.Path } catch { }
        if (-not [string]::IsNullOrWhiteSpace($actualPath) -and
            -not [string]::Equals($actualPath, [string]$stateMes.Executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw "MES PID $oldPid executable does not match the owned dotnet host."
        }
    }
    $owners = @(Get-PortOwners ([int]$stateMes.Port))
    if ($owners.Count -gt 0 -and ($owners | Where-Object { $_ -ne $oldPid }).Count -gt 0) {
        throw "MES port $($stateMes.Port) is also owned by another process; restart aborted."
    }

    Stop-Process -Id $oldPid -Force
    try { $process.WaitForExit(5000) | Out-Null } catch { }
    if (Get-Process -Id $oldPid -ErrorAction SilentlyContinue) {
        throw "MES process $oldPid did not exit before restart."
    }

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = if ([string]::IsNullOrWhiteSpace([string]$stateMes.Executable)) { 'dotnet' } else { [string]$stateMes.Executable }
    $startInfo.WorkingDirectory = if ($null -ne $stateMes.PSObject.Properties['WorkingDirectory'] -and -not [string]::IsNullOrWhiteSpace([string]$stateMes.WorkingDirectory)) {
        [string]$stateMes.WorkingDirectory
    } else {
        Join-Path $repoRoot 'src\MesControlAgv.Mes'
    }
    $startInfo.UseShellExecute = $true
    $dllArgument = ([string]$stateMes.Dll).Replace('"', '\"')
    $urlArgument = ([string]$stateMes.Url).Replace('"', '\"')
    $startInfo.Arguments = '"{0}" --urls "{1}" --environment Development' -f $dllArgument, $urlArgument
    if ($null -ne $stateMes.PSObject.Properties['EnvironmentVariables'] -and $null -ne $stateMes.EnvironmentVariables) {
        foreach ($entry in $stateMes.EnvironmentVariables.PSObject.Properties) {
            $startInfo.Environment[$entry.Name] = [string]$entry.Value
        }
    }

    $stdoutPath = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-restart-$RunId-out.log"
    $stderrPath = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-restart-$RunId-error.log"
    $savedEnvironment = @{}
    try {
        if ($null -ne $stateMes.PSObject.Properties['EnvironmentVariables'] -and $null -ne $stateMes.EnvironmentVariables) {
            foreach ($entry in $stateMes.EnvironmentVariables.PSObject.Properties) {
                $savedEnvironment[$entry.Name] = [Environment]::GetEnvironmentVariable($entry.Name, 'Process')
                [Environment]::SetEnvironmentVariable($entry.Name, [string]$entry.Value, 'Process')
            }
        }
        $newProcess = Start-Process -FilePath $startInfo.FileName -ArgumentList $startInfo.Arguments `
            -WorkingDirectory $startInfo.WorkingDirectory -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
    if ($null -eq $newProcess) { throw 'MES process could not be restarted.' }
    $stateMes.ProcessId = [int]$newProcess.Id
    $stateMes.StartedAtUtc = [DateTime]::UtcNow.ToString('O')
    $runState | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedStatePath -Encoding UTF8
    Wait-Health $mes 'mes' $TimeoutSeconds
    Write-Host "MES restarted for local recovery verification (new PID $($newProcess.Id))."
}

function Invoke-CancellationScenario {
    $verificationId = [Guid]::NewGuid().ToString('N')
    $externalId = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
        "verify-local-cancellation-$verificationId"
    } else {
        "$IsolationLabel-cancellation-$verificationId"
    }
    $createBody = @{
        sourceStationCode = $SourceStationCode
        targetStationCode = $TargetStationCode
        externalId = $externalId
        description = "Offline cancellation verification ($externalId)"
    } | ConvertTo-Json
    $task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
    if ($task.status -ne 'Created') { throw "Unexpected created status for cancellation scenario: $($task.status)" }

    $task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
    if ($task.status -ne 'MovingToPickup') { throw "Unexpected pickup dispatch status for cancellation scenario: $($task.status)" }
    $taskId = [Guid]$task.id
    $pickupOperationId = [Guid]::Parse([string]$task.activeDeviceTaskId)
    $agvId = [string]$task.activeAgvId
    if ([string]::IsNullOrWhiteSpace($agvId)) { throw 'Cancellation scenario did not return the assigned AGV.' }
    $encodedAgvId = [Uri]::EscapeDataString($agvId)

    # Complete pickup first so cancellation exercises the active dropoff
    # operation and proves that the simulator releases the assigned AGV.
    Invoke-RestMethod -Method Post -Uri "$simulator/agvs/$encodedAgvId/controls/arrive" | Out-Null
    $arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
    if ($arrived.status -ne 'WaitingPickupConfirmation') { throw "Unexpected pickup arrival status: $($arrived.status)" }

    $operatorBody = @{ operatorName = 'verify-local-cancellation' } | ConvertTo-Json
    $pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
    if ($pickup.status -ne 'MovingToDropoff') { throw "Unexpected dropoff status before cancellation: $($pickup.status)" }
    $dropoffOperationId = [Guid]::Parse([string]$pickup.activeDeviceTaskId)
    if ([string]$pickup.activeAgvId -ne $agvId) { throw 'Cancellation scenario changed AGV assignment between transport legs.' }

    $cancelled = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/cancel" -ContentType 'application/json' -Body $operatorBody
    if ($cancelled.status -ne 'Cancelled') { throw "Expected cancellation to complete, got status: $($cancelled.status)" }
    if ([string]::IsNullOrWhiteSpace([string]$cancelled.endedAt)) { throw 'Cancelled task did not record endedAt.' }

    $deviceTask = Invoke-RestMethod -Uri "$simulator/agvs/$encodedAgvId/tasks/$dropoffOperationId"
    if ($deviceTask.state -ne 'cancelled') { throw "Simulator did not confirm dropoff cancellation: $($deviceTask.state)" }
    $snapshot = Invoke-RestMethod -Uri "$simulator/agvs/$encodedAgvId/snapshot"
    if ($null -ne $snapshot.currentTaskId) { throw 'Cancelled AGV still reports an active simulator task.' }

    $detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    if ($detail.task.status -ne 'Cancelled') { throw "Cancelled task detail did not record Cancelled: $($detail.task.status)" }
    $eventTypes = @($detail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('TaskCreated', 'DispatchRequested', 'PickupArrived', 'PickupConfirmed', 'CancelConfirmed')) {
        if ($eventTypes -notcontains $requiredEvent) { throw "Cancellation scenario missing audit event: $requiredEvent" }
    }
    if ($eventTypes -contains 'DropoffConfirmed') { throw 'Cancelled task unexpectedly recorded DropoffConfirmed.' }
    if (@(Get-FleetEntryForTask (Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status") $taskId).Count -gt 0) {
        throw 'Cancelled task still appears as an active fleet task.'
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator cancellation verification passed for task $($task.id), pickup $pickupOperationId, dropoff $dropoffOperationId$runSuffix."
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
        description = "Offline timeout recovery verification ($externalId)"
    } | ConvertTo-Json
    $task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
    if ($task.status -ne 'Created') { throw "Unexpected created status for timeout scenario: $($task.status)" }

    # Simulator accepts and stores the operation before returning 504. Adapter
    # must reconcile that operation and never issue a second navigation request.
    Invoke-RestMethod -Method Post -Uri "$simulator/controls/timeout" | Out-Null
    $dispatched = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
    if ($dispatched.status -notin @('MovingToPickup', 'Unknown')) {
        throw "Unexpected timeout dispatch status: $($dispatched.status)"
    }
    $operationId = [string]$dispatched.activeDeviceTaskId
    if ([string]::IsNullOrWhiteSpace($operationId)) { throw 'Timeout dispatch did not persist its operation ID.' }

    $recovered = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/recover"
    if ($recovered.status -ne 'MovingToPickup') {
        throw "Timeout recovery did not return MovingToPickup: $($recovered.status)"
    }
    if ([string]$recovered.activeDeviceTaskId -ne $operationId) {
        throw 'Timeout recovery changed the operation ID; a duplicate dispatch may have occurred.'
    }

    $detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
    $eventTypes = @($detail.events | ForEach-Object { $_.eventType })
    if ($dispatched.status -eq 'Unknown') {
        foreach ($requiredEvent in @('Timeout', 'ReconciledMoving')) {
            if ($eventTypes -notcontains $requiredEvent) { throw "Timeout scenario missing audit event: $requiredEvent" }
        }
    }

    $completed = Complete-TransportTask $recovered 'verify-local-timeout'
    if ($completed.status -ne 'Completed') { throw "Timeout recovery task did not complete: $($completed.status)" }
    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator timeout recovery verification passed for task $($task.id), operation $operationId$runSuffix."
}

function Invoke-RestartResumeScenario {
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
        description = "Offline restart/resume verification ($externalId)"
    } | ConvertTo-Json
    $created = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
    $dispatched = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/dispatch"
    if ($dispatched.status -ne 'MovingToPickup') { throw "Unexpected restart setup status: $($dispatched.status)" }
    $operationId = [string]$dispatched.activeDeviceTaskId
    if ([string]::IsNullOrWhiteSpace($operationId)) { throw 'Restart setup did not return an operation ID.' }

    Restart-MesProcess
    $resumed = Wait-TaskStatus -TaskId ([Guid]$created.id) -ExpectedStatus @('MovingToPickup', 'Unknown') -Timeout $TimeoutSeconds
    if ($resumed.status -eq 'Unknown') {
        $resumed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($created.id)/recover"
    }
    if ($resumed.status -ne 'MovingToPickup') { throw "Restart recovery did not resume pickup: $($resumed.status)" }
    if ([string]$resumed.activeDeviceTaskId -ne $operationId) {
        throw 'Restart recovery changed the persisted operation ID.'
    }

    $detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($created.id)"
    $eventTypes = @($detail.events | ForEach-Object { $_.eventType })
    foreach ($requiredEvent in @('Timeout', 'ReconciledMoving')) {
        if ($eventTypes -notcontains $requiredEvent) { throw "Restart scenario missing audit event: $requiredEvent" }
    }

    Complete-TransportTask $resumed 'verify-local-restart' | Out-Null
    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator restart/resume verification passed for task $($created.id), operation $operationId$runSuffix."
}

function Invoke-MultiAgvContentionScenario {
    $verificationId = [Guid]::NewGuid().ToString('N')
    $taskIds = [System.Collections.Generic.List[Guid]]::new()
    $tasks = [System.Collections.Generic.List[object]]::new()
    foreach ($suffix in @('a', 'b')) {
        $externalId = if ([string]::IsNullOrWhiteSpace($IsolationLabel)) {
            "verify-local-contention-$suffix-$verificationId"
        } else {
            "$IsolationLabel-contention-$suffix-$verificationId"
        }
        $createBody = @{
            sourceStationCode = $SourceStationCode
            targetStationCode = $TargetStationCode
            externalId = $externalId
            description = "Offline multi-AGV contention verification ($externalId)"
        } | ConvertTo-Json
        $created = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
        if ($created.status -ne 'Created') { throw "Unexpected contention create status: $($created.status)" }
        $taskIds.Add([Guid]$created.id)
        $tasks.Add($created)
    }

    $first = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($taskIds[0])/dispatch"
    $second = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($taskIds[1])/dispatch"
    if ($first.status -ne 'MovingToPickup' -or $second.status -ne 'MovingToPickup') {
        throw "Multi-AGV contention did not keep both tasks active: $($first.status), $($second.status)"
    }
    $firstAgv = [string]$first.activeAgvId
    $secondAgv = [string]$second.activeAgvId
    if ([string]::IsNullOrWhiteSpace($firstAgv) -or [string]::IsNullOrWhiteSpace($secondAgv)) {
        throw 'Multi-AGV contention did not return both AGV assignments.'
    }
    if ($firstAgv -eq $secondAgv) { throw "Scheduler assigned both concurrent tasks to $firstAgv." }

    $fleet = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
    if (@(Get-FleetEntryForTask $fleet $taskIds[0]).Count -ne 1) { throw 'Fleet status lost the first contending task.' }
    if (@(Get-FleetEntryForTask $fleet $taskIds[1]).Count -ne 1) { throw 'Fleet status lost the second contending task.' }

    $operatorBody = @{ operatorName = 'verify-local-contention' } | ConvertTo-Json
    foreach ($taskId in $taskIds) {
        $cancelled = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$taskId/cancel" -ContentType 'application/json' -Body $operatorBody
        if ($cancelled.status -ne 'Cancelled') { throw "Contending task $taskId did not cancel: $($cancelled.status)" }
    }
    $finalFleet = Invoke-RestMethod -Uri "$mes/api/agvs/fleet/status"
    foreach ($taskId in $taskIds) {
        if (@(Get-FleetEntryForTask $finalFleet $taskId).Count -gt 0) { throw "Cancelled contending task $taskId remains active." }
    }

    $runSuffix = if ([string]::IsNullOrWhiteSpace($RunId)) { '' } else { " (run $RunId)" }
    Write-Host "Local Simulator multi-AGV contention verification passed: $firstAgv and $secondAgv handled isolated tasks$runSuffix."
}

Wait-Health $simulator 'simulator' $TimeoutSeconds
Wait-Health $adapter 'adapter' $TimeoutSeconds
Wait-Health $mes 'mes' $TimeoutSeconds

if ($Scenario -eq 'failure-retry') {
    Invoke-FailureRecoveryScenario
    return
}

if ($Scenario -eq 'cancellation') {
    Invoke-CancellationScenario
    return
}

if ($Scenario -eq 'timeout-recovery') {
    Invoke-TimeoutRecoveryScenario
    return
}

if ($Scenario -eq 'restart-resume') {
    Invoke-RestartResumeScenario
    return
}

if ($Scenario -eq 'multi-agv') {
    Invoke-MultiAgvContentionScenario
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
