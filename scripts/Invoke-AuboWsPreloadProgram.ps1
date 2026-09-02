[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$')]
    [string]$ControllerHost,

    [ValidateRange(1, 65535)]
    [int]$Port = 9012,

    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')]
    [string]$RobotName = 'rob1',

    [ValidateRange(0, 99)]
    [int]$Index,

    [Parameter(Mandatory = $true)]
    [string]$ProgramName,

    [ValidateRange(100, 60000)]
    [int]$TimeoutMs = 5000,

    [switch]$AllowWrite,
    [switch]$ConfirmPhysical,
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Normalize-ProgramName {
    param([Parameter(Mandatory = $true)] [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { throw 'ProgramName is required.' }
    $normalized = $Value.Trim()
    if ($normalized.EndsWith('.pro', [StringComparison]::OrdinalIgnoreCase) -or
        $normalized.EndsWith('.lua', [StringComparison]::OrdinalIgnoreCase)) {
        $normalized = $normalized.Substring(0, $normalized.Length - 4)
    }
    if ($normalized.Length -eq 0 -or $normalized.Length -gt 128 -or
        $normalized.IndexOfAny([char[]]('/\')) -ge 0 -or
        $normalized.IndexOf('..', [StringComparison]::Ordinal) -ge 0 -or
        @($normalized.ToCharArray() | Where-Object { [char]::IsControl($_) }).Count -gt 0) {
        throw 'ProgramName must be a simple controller project name, without a path.'
    }
    return $normalized
}

function Invoke-AuboWsRpc {
    param(
        [Parameter(Mandatory = $true)] [string]$Method,
        [AllowNull()] [object]$Params = @()
    )

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $timeout = [Threading.CancellationTokenSource]::new($TimeoutMs)
    $requestId = "mes-preload-$([Guid]::NewGuid().ToString('N'))"
    try {
        $uri = [Uri]("ws://{0}:{1}/" -f $ControllerHost.Trim(), $Port)
        $socket.ConnectAsync($uri, $timeout.Token).GetAwaiter().GetResult() | Out-Null
        $request = [ordered]@{ jsonrpc = '2.0'; method = $Method; id = $requestId; params = $Params }
        $payload = $request | ConvertTo-Json -Compress -Depth 20
        $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
        $socket.SendAsync(
            [ArraySegment[byte]]::new($bytes),
            [System.Net.WebSockets.WebSocketMessageType]::Text,
            $true,
            $timeout.Token).GetAwaiter().GetResult() | Out-Null

        $buffer = New-Object byte[] 65536
        $memory = [IO.MemoryStream]::new()
        try {
            do {
                $received = $socket.ReceiveAsync(
                    [ArraySegment[byte]]::new($buffer),
                    $timeout.Token).GetAwaiter().GetResult()
                if ($received.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                    throw "AUBO closed the WebSocket while waiting for '$Method'."
                }
                if ($received.Count -gt 0) { $memory.Write($buffer, 0, $received.Count) }
                if ($memory.Length -gt 1MB) { throw "AUBO response for '$Method' exceeded the read-only limit." }
            } while (-not $received.EndOfMessage)
            $responseText = [Text.Encoding]::UTF8.GetString($memory.ToArray())
        }
        finally { $memory.Dispose() }

        $response = $responseText | ConvertFrom-Json
        if ([string]$response.id -ne $requestId) { throw "AUBO response id mismatch for '$Method'." }
        if ($null -ne $response.PSObject.Properties['error']) {
            throw "AUBO method '$Method' failed: $($response.error.message)"
        }
        if ($null -eq $response.PSObject.Properties['result']) { throw "AUBO method '$Method' returned no result." }
        return $response.result
    }
    finally {
        if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $socket.CloseAsync(
                [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                'preload-complete',
                [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
        }
        $socket.Dispose()
        $timeout.Dispose()
    }
}

$normalizedProgram = Normalize-ProgramName $ProgramName
if (-not $AllowWrite -or -not $ConfirmPhysical) {
    throw 'This operation changes the controller preload table. Re-run with -AllowWrite -ConfirmPhysical only during a supervised field test.'
}

$before = [ordered]@{
    robotMode = Invoke-AuboWsRpc -Method "$RobotName.RobotState.getRobotModeType" -Params @()
    safetyMode = Invoke-AuboWsRpc -Method "$RobotName.RobotState.getSafetyModeType" -Params @()
    runtimeStatus = Invoke-AuboWsRpc -Method 'RuntimeMachine.getStatus' -Params @()
    operationalMode = Invoke-AuboWsRpc -Method "$RobotName.RobotManage.getOperationalMode" -Params @()
    slot = Invoke-AuboWsRpc -Method 'RuntimeMachine.getPreloadProgram' -Params @($Index)
}

if ([string]$before.runtimeStatus -notin @('Stopped', '6')) {
    throw "Preload refused: runtime is '$($before.runtimeStatus)'; it must be Stopped."
}
if ([string]$before.safetyMode -notin @('Normal', '1')) {
    throw "Preload refused: safety mode is '$($before.safetyMode)'."
}

# This is the sole mutating request in the script. It is intentionally never retried.
$writeResult = Invoke-AuboWsRpc -Method 'RuntimeMachine.preloadProgram' -Params @($Index, $normalizedProgram)
$after = Invoke-AuboWsRpc -Method 'RuntimeMachine.getPreloadProgram' -Params @($Index)
$verified = [string]$after -eq $normalizedProgram
$evidence = [ordered]@{
    schema = 'mes.aubo-ws-preload-program/1.0'
    observedAt = [DateTimeOffset]::Now
    endpoint = "ws://$ControllerHost`:$Port/"
    index = $Index
    requestedProgram = $normalizedProgram
    writesAttempted = $true
    sentOnce = $true
    writeResult = $writeResult
    before = $before
    after = $after
    verified = $verified
    outcome = if ($verified) { 'verified' } else { 'unknown' }
    automaticRetry = $false
    motionAttempted = $false
}
$json = $evidence | ConvertTo-Json -Depth 30
Write-Output $json
if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $absolute = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $absolute) { throw "Refusing to overwrite existing evidence file '$absolute'." }
    $parent = Split-Path -Parent $absolute
    if (-not [string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    $json | Set-Content -LiteralPath $absolute -Encoding UTF8
}
if (-not $verified) { throw 'The preload write outcome is unknown; stop and reconcile the slot manually.' }
