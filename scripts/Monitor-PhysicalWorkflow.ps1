[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [Guid]$ExecutionId,
    [Parameter(Mandatory = $true)] [string]$OutputDirectory,
    [string]$MesBaseUrl = 'http://127.0.0.1:5145',
    [string]$AdapterBaseUrl = 'http://127.0.0.1:5141',
    [ValidateRange(2, 60)] [int]$PollSeconds = 5,
    [ValidateRange(1, 180)] [int]$MaxMinutes = 60,
    [string]$ReplayDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
}

$jsonlPath = Join-Path $OutputDirectory 'monitor-snapshots.jsonl'
$finalPath = Join-Path $OutputDirectory 'monitor-final.json'
$statusPath = Join-Path $OutputDirectory 'monitor-status.log'

function Expand-JsonCollection {
    <#
      Windows PowerShell can expose a JSON collection as an Object[] member,
      or as a wrapper such as { value: [...] }.  Keep this normalization at
      the HTTP boundary so every monitor assertion sees a real item array.
    #>
    param([AllowNull()] [object]$Value)

    if ($null -eq $Value) { return @() }

    if ($Value -is [System.Array]) {
        $expanded = [System.Collections.Generic.List[object]]::new()
        foreach ($item in $Value) {
            foreach ($child in @(Expand-JsonCollection $item)) { $expanded.Add($child) }
        }
        return $expanded.ToArray()
    }

    foreach ($propertyName in @('value', 'items', 'data')) {
        $property = $Value.PSObject.Properties[$propertyName]
        if ($null -ne $property -and $null -ne $property.Value -and
            -not [object]::ReferenceEquals($property.Value, $Value)) {
            return @(Expand-JsonCollection $property.Value)
        }
    }

    return @($Value)
}

function Get-JsonObject {
    param([Parameter(Mandatory = $true)] [string]$Uri)
    # Exactly one read; monitor failures are reported and never retried as a
    # device mutation.  The caller controls the next read-only poll interval.
    return Invoke-RestMethod -Uri $Uri -Method Get -TimeoutSec 30
}

function Get-JsonCollection {
    param([Parameter(Mandatory = $true)] [string]$Uri)
    return @(Expand-JsonCollection (Get-JsonObject $Uri))
}

function Append-Text {
    param([string]$Path, [string]$Text)
    [IO.File]::AppendAllText($Path, $Text + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Runtime-Name([object]$Value) {
    switch ([int]$Value) {
        0 { 'Rejected'; break }
        1 { 'DryRunCompleted'; break }
        2 { 'Prepared'; break }
        3 { 'Running'; break }
        4 { 'Paused'; break }
        5 { 'Completed'; break }
        6 { 'Failed'; break }
        7 { 'Unknown'; break }
        8 { 'Cancelled'; break }
        default { "Unknown($Value)" }
    }
}

function Node-Name([object]$Value) {
    switch ([int]$Value) {
        0 { 'Pending'; break }
        1 { 'Ready'; break }
        2 { 'WaitingForResource'; break }
        3 { 'Claimed'; break }
        4 { 'Running'; break }
        5 { 'WaitingForSignal'; break }
        6 { 'Succeeded'; break }
        7 { 'Failed'; break }
        8 { 'TimedOut'; break }
        9 { 'Unknown'; break }
        10 { 'Blocked'; break }
        11 { 'Cancelled'; break }
        12 { 'Skipped'; break }
        default { "Unknown($Value)" }
    }
}

function Read-ReplayJson {
    param([Parameter(Mandatory = $true)] [string]$Name)
    $path = Join-Path $ReplayDirectory $Name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Replay fixture '$path' was not found."
    }
    $raw = [IO.File]::ReadAllText($path, (New-Object System.Text.UTF8Encoding($false, $true)))
    return $raw | ConvertFrom-Json
}

function New-Snapshot {
    param(
        [Parameter(Mandatory = $true)] [object]$Run,
        [Parameter(Mandatory = $true)] [object[]]$Nodes,
        [Parameter(Mandatory = $true)] [object[]]$Operations,
        [Parameter(Mandatory = $true)] [object[]]$Acceptances,
        [Parameter(Mandatory = $true)] [object[]]$Timeline,
        [Parameter(Mandatory = $true)] [object]$Agv,
        [Parameter(Mandatory = $true)] [object]$AuboStatus,
        [Parameter(Mandatory = $true)] [object]$AuboProgram,
        [AllowNull()] [object]$Preflight
    )
    $observedAt = [DateTimeOffset]::UtcNow
    return [ordered]@{
        schema = 'mes.physical-workflow-monitor-snapshot/1.0'
        observedAtUtc = $observedAt.ToString('O')
        execution = $Run
        nodes = @($Nodes)
        deviceOperations = @($Operations)
        acceptances = @($Acceptances)
        timeline = @($Timeline)
        agv = $Agv
        auboStatus = $AuboStatus
        auboProgram = $AuboProgram
        physicalPreflight = $Preflight
    }
}

if (-not [string]::IsNullOrWhiteSpace($ReplayDirectory)) {
    # Exercise scalar normalization too, while retaining the run as the
    # single object expected by the snapshot schema.
    $runEntries = @(Expand-JsonCollection (Read-ReplayJson 'run.json'))
    if ($runEntries.Count -ne 1) { throw "Scalar run fixture normalized to $($runEntries.Count) entries." }
    $run = $runEntries[0]
    $nodes = @(Expand-JsonCollection (Read-ReplayJson 'nodes.json'))
    $operations = @(Expand-JsonCollection (Read-ReplayJson 'device-operations.json'))
    $acceptances = @(Expand-JsonCollection (Read-ReplayJson 'acceptances.json'))
    $timeline = @(Expand-JsonCollection (Read-ReplayJson 'timeline.json'))
    $agv = Read-ReplayJson 'agv.json'
    $auboStatus = Read-ReplayJson 'aubo-status.json'
    $auboProgram = Read-ReplayJson 'aubo-program.json'
    $preflight = Read-ReplayJson 'preflight.json'
    $snapshot = New-Snapshot -Run $run -Nodes $nodes -Operations $operations -Acceptances $acceptances -Timeline $timeline -Agv $agv -AuboStatus $auboStatus -AuboProgram $auboProgram -Preflight $preflight
    $snapshot.replay = $true
    [IO.File]::WriteAllText($finalPath, ($snapshot | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
    Write-Output ([ordered]@{ replay = $true; nodes = $nodes.Count; deviceOperations = $operations.Count; acceptances = $acceptances.Count; timeline = $timeline.Count; final = $finalPath } | ConvertTo-Json -Compress)
    return
}

$terminalStatuses = @(0, 1, 5, 6, 7, 8)
$unsafeNodeStatuses = @(7, 8, 9, 10, 11) # Failed, TimedOut, Unknown, Blocked, Cancelled
$deadline = [DateTime]::UtcNow.AddMinutes($MaxMinutes)
$lastSummary = $null
$lastPreflightAt = [DateTime]::MinValue
$finalReason = 'monitor-timeout'
$lastSnapshot = $null

while ([DateTime]::UtcNow -lt $deadline) {
    try {
        $mesRoot = $MesBaseUrl.TrimEnd('/')
        $adapterRoot = $AdapterBaseUrl.TrimEnd('/')
        $run = Get-JsonObject "$mesRoot/api/workflow-runs/$ExecutionId"
        $nodes = @(Get-JsonCollection "$mesRoot/api/workflow-runs/$ExecutionId/nodes")
        $operations = @(Get-JsonCollection "$mesRoot/api/workflow-runs/$ExecutionId/device-operations")
        $timeline = @(Get-JsonCollection "$mesRoot/api/workflow-runs/$ExecutionId/timeline?limit=500")
        $acceptances = @(Get-JsonCollection "$mesRoot/api/workflow-runs/$ExecutionId/field-navigation-acceptances")
        $agv = Get-JsonObject "$adapterRoot/agv/snapshot"
        $auboStatus = Get-JsonObject "$adapterRoot/api/robot-arms/ARM-01/status"
        $auboProgram = Get-JsonObject "$adapterRoot/api/robot-arms/ARM-01/program"

        $preflight = $null
        $observedAt = [DateTime]::UtcNow
        if (($observedAt - $lastPreflightAt).TotalSeconds -ge 30) {
            try {
                $preflight = Get-JsonObject "$adapterRoot/physical/preflight"
                $lastPreflightAt = $observedAt
            }
            catch { $preflight = [ordered]@{ readError = $_.Exception.Message } }
        }

        $nodeRows = @($nodes | Sort-Object createdAt | ForEach-Object {
            [ordered]@{ id = $_.id; nodeId = $_.nodeId; type = $_.nodeTypeId; name = $_.nodeName; status = [int]$_.status; statusName = Node-Name $_.status; attempt = $_.attempt; lastError = $_.lastError; outputs = $_.outputs; startedAt = $_.startedAt; completedAt = $_.completedAt }
        })
        $summary = (($nodeRows | ForEach-Object { "$($_.name)=$($_.statusName)" }) -join ' | ')
        if ($summary -ne $lastSummary) {
            $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fffK') execution=$ExecutionId runtime=$(Runtime-Name $run.runtimeStatus) :: $summary"
            Write-Host $line
            Append-Text $statusPath $line
            $lastSummary = $summary
        }

        $lastSnapshot = New-Snapshot -Run $run -Nodes $nodes -Operations $operations -Acceptances $acceptances -Timeline $timeline -Agv $agv -AuboStatus $auboStatus -AuboProgram $auboProgram -Preflight $preflight
        Append-Text $jsonlPath (($lastSnapshot | ConvertTo-Json -Depth 40 -Compress))

        $unsafeNode = @($nodes | Where-Object { $unsafeNodeStatuses -contains [int]$_.status })
        if ($unsafeNode.Count -gt 0) { $finalReason = 'unsafe-node-state'; break }
        if ([int]$run.runtimeStatus -eq 7) { $finalReason = 'workflow-unknown'; break }
        if ([int]$run.runtimeStatus -in @(6, 8)) { $finalReason = "workflow-$(Runtime-Name $run.runtimeStatus)"; break }
        if ([int]$run.runtimeStatus -eq 5) { $finalReason = 'completed'; break }
    }
    catch {
        $errorLine = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss.fffK') monitor-read-error: $($_.Exception.Message)"
        Write-Warning $errorLine
        Append-Text $statusPath $errorLine
    }
    Start-Sleep -Seconds $PollSeconds
}

if ($null -ne $lastSnapshot) {
    $lastSnapshot.finalReason = $finalReason
    $lastSnapshot.monitorFinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    [IO.File]::WriteAllText($finalPath, ($lastSnapshot | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
}
Write-Host "MONITOR_FINAL_REASON=$finalReason"
Write-Host "MONITOR_FINAL=$finalPath"
if ($finalReason -ne 'completed') { exit 2 }
