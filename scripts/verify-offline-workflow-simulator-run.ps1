param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [int]$TimeoutSeconds = 30,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) ('MesControlAgv-offline-workflow-' + [guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $runRoot 'data'
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

# Use ports outside the protected field/local-service set. Every service is
# forced to FieldSimulation and every dependency is loopback-only.
$simulatorPort = 6283
$adapterPort = 6241
$mesPort = 6245
$simulator = "http://127.0.0.1:$simulatorPort"
$adapter = "http://127.0.0.1:$adapterPort"
$mes = "http://127.0.0.1:$mesPort"
$processes = [System.Collections.Generic.List[Diagnostics.Process]]::new()

function Start-LocalService {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][int]$Port,
        [hashtable]$Environment = @{}
    )

    $projectRoot = Join-Path $repoRoot "src\MesControlAgv.$Project"
    $dll = Join-Path $projectRoot "bin\$Configuration\net8.0\MesControlAgv.$Project.dll"
    if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) {
        throw "$Name DLL was not found at $dll. Build the solution first."
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command dotnet.exe).Source
    $startInfo.WorkingDirectory = $projectRoot
    $startInfo.Arguments = '"{0}" --urls http://127.0.0.1:{1} --environment FieldSimulation' -f $dll, $Port
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'FieldSimulation'
    $startInfo.Environment['DOTNET_ENVIRONMENT'] = 'FieldSimulation'
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = [string]$entry.Value
    }

    $process = [Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) { throw "$Name process could not be started." }
    $processes.Add($process)
    return $process
}

function Wait-Health {
    param([string]$Name, [string]$BaseUrl)

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $health = Invoke-RestMethod -Method Get -Uri "$BaseUrl/health" -TimeoutSec 2
            if ($health.service -eq $Name -and $health.status -eq 'ok') { return }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$Name did not become healthy at $BaseUrl."
}

function Assert-PortAvailable {
    param([int]$Port)

    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    if ($listeners.Count -gt 0) {
        $owners = $listeners | Select-Object -ExpandProperty OwningProcess -Unique
        throw "Loopback verification port $Port is already listening (PID $($owners -join ', '))."
    }
}

function Stop-OwnedProcesses {
    foreach ($process in ($processes | Sort-Object Id -Descending)) {
        try {
            if (-not $process.HasExited) {
                $process.Kill()
                $process.WaitForExit(5000) | Out-Null
            }
        }
        catch { }
        try { $process.Dispose() } catch { }
    }
}

function Get-WorkflowStatusName {
    param([int]$Value)

    switch ($Value) {
        0 { 'Rejected' }
        1 { 'DryRunCompleted' }
        2 { 'Prepared' }
        3 { 'Running' }
        4 { 'Paused' }
        5 { 'Completed' }
        6 { 'Failed' }
        7 { 'Unknown' }
        8 { 'Cancelled' }
        default { [string]$Value }
    }
}

function Get-NodeStatusName {
    param([int]$Value)

    switch ($Value) {
        0 { 'Pending' }
        1 { 'Ready' }
        2 { 'WaitingForResource' }
        3 { 'Claimed' }
        4 { 'Running' }
        5 { 'WaitingForSignal' }
        6 { 'Succeeded' }
        7 { 'Failed' }
        8 { 'TimedOut' }
        9 { 'Unknown' }
        10 { 'Blocked' }
        11 { 'Cancelled' }
        12 { 'Skipped' }
        default { [string]$Value }
    }
}

function Get-DeviceOperationStatusName {
    param([int]$Value)

    switch ($Value) {
        0 { 'Prepared' }
        1 { 'Accepted' }
        2 { 'Running' }
        3 { 'Succeeded' }
        4 { 'Rejected' }
        5 { 'Failed' }
        6 { 'Cancelled' }
        7 { 'Unknown' }
        default { [string]$Value }
    }
}

function New-LegacyWorkflowDefinition {
    param(
        [Guid]$WorkflowId,
        [Guid]$StartNodeId,
        [Guid]$MoveNodeId,
        [Guid]$EndNodeId
    )

    # Omitting nodeTypeId/ports intentionally exercises the compatibility
    # adapter used by existing WPF/legacy workflow JSON. The server fills in
    # catalog ports and the typed $targetStation binding during persistence.
    return @{
        id = $WorkflowId
        name = 'Offline WPF simulator run'
        description = 'One Move using isolated Simulator/Adapter/MES.'
        isPreset = $false
        nodes = @(
            @{
                id = $StartNodeId
                type = 0
                nodeTypeId = 'core.start'
                schemaVersion = '1.0'
                name = 'Start'
                description = 'Start offline simulation'
                targetStation = $null
                x = 0
                y = 0
                order = 1
                parameters = @()
                nextNodeIds = @($MoveNodeId)
                ports = @(
                    @{ key = 'success'; displayName = 'success'; direction = 1; dataType = 'control'; cardinality = 0; edgeKind = 0 }
                )
            },
            @{
                id = $MoveNodeId
                type = 1
                nodeTypeId = 'agv.move'
                schemaVersion = '1.0'
                name = 'Move'
                description = 'Move to LM4 in the local simulator'
                targetStation = 'LM4'
                x = 180
                y = 0
                order = 2
                parameters = @()
                nextNodeIds = @($EndNodeId)
                configuration = @{
                    '$targetStation' = 'LM4'
                    timeoutSeconds = '30'
                    retryCount = '0'
                }
                ports = @(
                    @{ key = 'in'; displayName = 'in'; direction = 0; dataType = 'control'; cardinality = 1 },
                    @{ key = 'success'; displayName = 'success'; direction = 1; dataType = 'control'; cardinality = 0; edgeKind = 0 },
                    @{ key = 'failure'; displayName = 'failure'; direction = 1; dataType = 'control'; cardinality = 0; edgeKind = 1 },
                    @{ key = 'timeout'; displayName = 'timeout'; direction = 1; dataType = 'control'; cardinality = 0; edgeKind = 2 }
                )
            },
            @{
                id = $EndNodeId
                type = 5
                nodeTypeId = 'core.end'
                schemaVersion = '1.0'
                name = 'End'
                description = 'End offline simulation'
                targetStation = $null
                x = 360
                y = 0
                order = 3
                parameters = @()
                nextNodeIds = @()
                ports = @(
                    @{ key = 'in'; displayName = 'in'; direction = 0; dataType = 'control'; cardinality = 0 }
                )
            }
        )
        edges = @(
            @{ sourceNodeId = $StartNodeId; sourcePort = 'success'; targetNodeId = $MoveNodeId; targetPort = 'in'; kind = 0 },
            @{ sourceNodeId = $MoveNodeId; sourcePort = 'success'; targetNodeId = $EndNodeId; targetPort = 'in'; kind = 0 }
        )
    }
}

try {
    foreach ($port in @($simulatorPort, $adapterPort, $mesPort)) {
        Assert-PortAvailable $port
    }

    Start-LocalService 'simulator' 'Simulator' $simulatorPort @{
        'Agv__DefaultStationId' = 'LM1'
    } | Out-Null
    Wait-Health 'simulator' $simulator

    Start-LocalService 'adapter' 'Adapter' $adapterPort @{
        'Simulator__BaseUrl' = "$simulator/"
        'ConnectionStrings__Adapter' = "Data Source=$(Join-Path $dataRoot 'adapter.db')"
        # Keep the checked-in FieldSimulation profile fail-closed; this
        # disposable loopback run opts into simulated Move dispatch only.
        'Profile__features__enableAutomaticDispatch' = 'true'
    } | Out-Null
    Wait-Health 'adapter' $adapter

    Start-LocalService 'mes' 'Mes' $mesPort @{
        'Adapter__BaseUrl' = "$adapter/"
        'ConnectionStrings__Mes' = "Data Source=$(Join-Path $dataRoot 'mes.db')"
        # This override is scoped to the disposable loopback run. The checked-in
        # FieldSimulation profile remains fail-closed for automatic dispatch.
        'Profile__features__enableAutomaticDispatch' = 'true'
    } | Out-Null
    Wait-Health 'mes' $mes

    $workflowId = [guid]::NewGuid()
    $definition = New-LegacyWorkflowDefinition $workflowId ([guid]::NewGuid()) ([guid]::NewGuid()) ([guid]::NewGuid())
    $actor = 'offline-ui-test'
    $draft = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows?actor=$actor" -ContentType 'application/json' -Body ($definition | ConvertTo-Json -Depth 12)
    $version = [int]$draft.version
    $validation = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/$version/validate"
    if (-not $validation.isValid) {
        throw "Workflow validation failed: $(($validation.issues | ConvertTo-Json -Depth 10 -Compress))"
    }
    $published = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/$workflowId/versions/$version/publish?actor=$actor"
    if ([string]$published.publishStatus -ne 'Published' -and [int]$published.publishStatus -ne 2) {
        throw "Workflow publication failed: $(($published | ConvertTo-Json -Depth 10 -Compress))"
    }

    $requestId = [guid]::NewGuid()
    $correlationId = 'offline-ui-' + [guid]::NewGuid().ToString('N')
    $request = @{
        workflowId = $workflowId
        version = $version
        requestId = $requestId
        requestedBy = $actor
        correlationId = $correlationId
        dryRun = $false
        parameters = @{}
    }
    $requestJson = $request | ConvertTo-Json -Depth 10
    $accepted = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/execute" -ContentType 'application/json' -Body $requestJson
    if (-not $accepted.isAccepted) {
        throw "Workflow execution was rejected: $(($accepted | ConvertTo-Json -Depth 10 -Compress))"
    }

    $runId = [guid]$accepted.executionId
    $preparedSeen = $false
    $runningSeen = $false
    $arriveSent = $false
    $run = $null
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $run = Invoke-RestMethod -Method Get -Uri "$mes/api/workflow-runs/$runId"
        $nodes = @(Invoke-RestMethod -Method Get -Uri "$mes/api/workflow-runs/$runId/nodes")
        $runStatus = [int]$run.runtimeStatus
        if ($runStatus -eq 2) { $preparedSeen = $true }
        if (@($nodes | Where-Object { [int]$_.status -eq 4 }).Count -gt 0) { $runningSeen = $true }
        if (-not $arriveSent -and @($nodes | Where-Object { [int]$_.status -in @(3, 4) }).Count -gt 0) {
            $simulatorSnapshot = Invoke-RestMethod -Method Get -Uri "$simulator/snapshot"
            if ($null -ne $simulatorSnapshot.currentTaskId) {
                Invoke-RestMethod -Method Post -Uri "$simulator/controls/arrive" | Out-Null
                $arriveSent = $true
            }
        }
        if ($runStatus -in @(0, 1, 5, 6, 7, 8)) { break }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)

    if ([int]$run.runtimeStatus -ne 5) {
        throw "Workflow did not complete: $(($run | ConvertTo-Json -Depth 10 -Compress))"
    }

    $replay = Invoke-RestMethod -Method Post -Uri "$mes/api/workflows/execute" -ContentType 'application/json' -Body $requestJson
    $finalNodes = @(Invoke-RestMethod -Method Get -Uri "$mes/api/workflow-runs/$runId/nodes")
    $operations = @(Invoke-RestMethod -Method Get -Uri "$mes/api/workflow-runs/$runId/device-operations")
    $timeline = @(Invoke-RestMethod -Method Get -Uri "$mes/api/workflow-runs/$runId/timeline?limit=200")
    $timelineEvents = @($timeline | ForEach-Object { ([string]$_.eventType -split '\s+') | Where-Object { $_ } })
    $result = [ordered]@{
        isolatedRoot = $runRoot
        workflowId = $workflowId
        version = $version
        runId = $runId
        requestId = $requestId
        correlationId = $correlationId
        preparedSeen = $preparedSeen
        runningSeen = $runningSeen
        arriveSentToSimulator = $arriveSent
        finalRunStatus = Get-WorkflowStatusName ([int]$run.runtimeStatus)
        finalNodeStatuses = @($finalNodes | ForEach-Object { Get-NodeStatusName ([int]$_.status) })
        deviceOperationStatuses = @($operations | ForEach-Object { Get-DeviceOperationStatusName ([int]$_.status) })
        timelineCount = $timelineEvents.Count
        timelineEventTypes = $timelineEvents
        replayIsIdempotent = [bool]$replay.isIdempotentReplay
        replayExecutionId = $replay.executionId
        dataFiles = @(Get-ChildItem -LiteralPath $dataRoot -File | Select-Object -ExpandProperty Name)
    }
    $json = $result | ConvertTo-Json -Depth 12
    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $resolvedOutput = [IO.Path]::GetFullPath($OutputPath)
        $parent = Split-Path -Parent $resolvedOutput
        if (-not [string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Set-Content -LiteralPath $resolvedOutput -Value $json -Encoding UTF8
    }
    Write-Output $json
}
finally {
    Stop-OwnedProcesses
}
