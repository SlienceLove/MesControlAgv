param(
    [Parameter(Mandatory = $true)][string]$TaskUuid,
    [string]$EquipmentCode = 'STN61_01',
    [string]$MesBaseUrl = 'http://127.0.0.1:5145',
    [string]$OutputRoot = ''
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $PSScriptRoot '..\artifacts' }
# Diagnostic GETs only. This is a time-bounded HTTP snapshot, not an atomic DB backup.
$root = [IO.Path]::GetFullPath($OutputRoot)
$directory = Join-Path $root ('shinelab-evidence-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -ErrorAction Stop | Out-Null
$taskPath = [Uri]::EscapeDataString($TaskUuid)
$equipmentPath = [Uri]::EscapeDataString($EquipmentCode)
$queries = [ordered]@{
    'server.json' = '/api/shinelab/server/status'
    'device.json' = "/api/shinelab/devices/$equipmentPath/status"
    'task.json' = "/api/shinelab/tasks/$taskPath"
    'commands.json' = "/api/shinelab/tasks/$taskPath/command-audit"
    'pushes.json' = "/api/shinelab/devices/$equipmentPath/push-evidence?taskUuid=$taskPath&limit=500"
    'task-results.json' = "/api/shinelab/devices/$equipmentPath/push-evidence?taskUuid=$taskPath&method=Result&limit=500"
    'latest-device-results.json' = "/api/shinelab/devices/$equipmentPath/push-evidence?method=Result&limit=10"
}
$started = [DateTimeOffset]::UtcNow.ToString('o')
$records = @()
$utf8 = New-Object System.Text.UTF8Encoding($false)
foreach ($name in $queries.Keys) {
    $uri = $MesBaseUrl.TrimEnd('/') + $queries[$name]
    $record = [ordered]@{ file=$name; method='GET'; uri=$uri; requestedAtUtc=[DateTimeOffset]::UtcNow.ToString('o') }
    try {
        $response = Invoke-WebRequest -Uri $uri -Method Get -UseBasicParsing -TimeoutSec 10
        # Save the original response text; don't round JSON numbers by deserializing/reserializing.
        $path = Join-Path $directory $name
        [IO.File]::WriteAllText($path, $response.Content, $utf8)
        $record.status = [int]$response.StatusCode
        $record.sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    catch { $record.error = $_.Exception.Message }
    $record.finishedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
    $records += [pscustomobject]$record
}
$manifest = [ordered]@{
    taskUuid=$TaskUuid; equipmentCode=$EquipmentCode
    startedAtUtc=$started; finishedAtUtc=[DateTimeOffset]::UtcNow.ToString('o')
    note='Read-only GET snapshot; queries are not atomic. Push lists may be capped; no device writes, no restart, no database mutation.'
    requests=$records
}
[IO.File]::WriteAllText((Join-Path $directory 'manifest.json'), ($manifest | ConvertTo-Json -Depth 7), $utf8)
Write-Output $directory
if (@($records | Where-Object { $_.error }).Count -gt 0) { throw 'Partial evidence export; see manifest.json. No requests were retried.' }
