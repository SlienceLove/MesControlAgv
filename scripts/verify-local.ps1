param(
    [int]$TimeoutSeconds = 30,
    [string]$MesUrl = 'http://localhost:5045',
    [string]$AdapterUrl = 'http://localhost:5041',
    [string]$SimulatorUrl = 'http://localhost:5183'
)

$ErrorActionPreference = 'Stop'
$mes = $MesUrl.TrimEnd('/')
$adapter = $AdapterUrl.TrimEnd('/')
$simulator = $SimulatorUrl.TrimEnd('/')

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

Wait-Health $simulator 'simulator' $TimeoutSeconds
Wait-Health $adapter 'adapter' $TimeoutSeconds
Wait-Health $mes 'mes' $TimeoutSeconds

$createBody = @{ sourceStationCode = 2; targetStationCode = 4 } | ConvertTo-Json
$task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
if ($task.status -ne 'Created') { throw "Unexpected created status: $($task.status)" }

$task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/dispatch"
if ($task.status -ne 'MovingToPickup') { throw "Unexpected pickup dispatch status: $($task.status)" }

$operationId = $task.activeDeviceTaskId
if ([string]::IsNullOrWhiteSpace($operationId)) { throw 'MES did not return the active pickup operation ID.' }
$operationGuid = [Guid]::Parse($operationId)

$pauseBody = @{ command = 'pause'; taskId = $operationGuid } | ConvertTo-Json
$paused = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/AGV-01/command" -ContentType 'application/json' -Body $pauseBody
if ($paused.state -ne 'paused') { throw "Adapter did not confirm pause: $($paused.state)" }
$pausedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($pausedTask.task.status -ne 'Paused') { throw "MES did not record Paused: $($pausedTask.task.status)" }

$resumeBody = @{ command = 'resume'; taskId = $operationGuid } | ConvertTo-Json
$resumed = Invoke-RestMethod -Method Post -Uri "$mes/api/agvs/AGV-01/command" -ContentType 'application/json' -Body $resumeBody
if ($resumed.state -notin @('accepted', 'moving')) { throw "Adapter did not confirm resume: $($resumed.state)" }
$resumedTask = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
if ($resumedTask.task.status -ne 'MovingToPickup') { throw "MES did not record resumed pickup: $($resumedTask.task.status)" }

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
foreach ($requiredEvent in @('PickupConfirmed', 'DropoffConfirmed')) {
    if ($eventTypes -notcontains $requiredEvent) { throw "Missing audit event: $requiredEvent" }
}

Write-Host "Live AGV transport verification passed for task $($task.id)."
