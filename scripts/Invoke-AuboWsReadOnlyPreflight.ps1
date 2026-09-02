[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$')]
    [string]$ControllerHost,

    [ValidateRange(1, 65535)]
    [int]$Port = 9012,

    [ValidateRange(1, 65535)]
    [int]$DashboardPort = 29999,

    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')]
    [string]$RobotName = 'rob1',

    [ValidateRange(100, 60000)]
    [int]$TimeoutMs = 5000,

    # Named-variable reads are opt-in. No candidate key is guessed by the script.
    [string[]]$VariableKey = @(),

    [bool]$IncludeModbusSignals = $true,

    # Evidence is created once and never overwritten.
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$normalizedHost = $ControllerHost.Trim()
$normalizedRobot = $RobotName.Trim()
$keys = @(
    $VariableKey |
        ForEach-Object { if ($null -ne $_) { $_.Trim() } } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
)

function Invoke-AuboWsRpc {
    param(
        [Parameter(Mandatory)] [string]$Method,
        [AllowNull()] [object]$Params = $null
    )

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $timeout = [Threading.CancellationTokenSource]::new($TimeoutMs)
    $requestId = "mes-$([Guid]::NewGuid().ToString('N'))"
    try {
        $uri = [Uri]("ws://{0}:{1}/" -f $normalizedHost, $Port)
        $socket.ConnectAsync($uri, $timeout.Token).GetAwaiter().GetResult() | Out-Null

        $request = [ordered]@{
            jsonrpc = '2.0'
            method = $Method
            id = $requestId
        }
        if ($null -ne $Params) {
            $request.params = $Params
        }

        $payload = $request | ConvertTo-Json -Compress -Depth 30
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
                if ($received.Count -gt 0) {
                    $memory.Write($buffer, 0, $received.Count)
                }
                if ($memory.Length -gt 1MB) {
                    throw "AUBO response for '$Method' exceeded the 1 MiB read-only limit."
                }
            } while (-not $received.EndOfMessage)

            $responseText = [Text.Encoding]::UTF8.GetString($memory.ToArray())
        }
        finally {
            $memory.Dispose()
        }

        $response = $responseText | ConvertFrom-Json
        if ([string]$response.id -ne $requestId) {
            throw "AUBO response id '$($response.id)' does not match '$requestId'."
        }
        if ($null -ne $response.PSObject.Properties['error']) {
            $code = $response.error.code
            $message = $response.error.message
            throw "AUBO method '$Method' failed with code ${code}: $message"
        }
        if ($null -eq $response.PSObject.Properties['result']) {
            throw "AUBO method '$Method' returned no result member."
        }
        return $response.result
    }
    finally {
        if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
            $socket.CloseAsync(
                [System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure,
                'read-only-complete',
                [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
        }
        $socket.Dispose()
        $timeout.Dispose()
    }
}

function Get-DashboardLoadedProgram {
    # The documented Dashboard Server query is read-only and reports the
    # currently loaded file, which is distinct from RuntimeMachine preload slots.
    $client = [Net.Sockets.TcpClient]::new()
    $timeout = [Threading.CancellationTokenSource]::new($TimeoutMs)
    try {
        $client.ReceiveTimeout = $TimeoutMs
        $client.SendTimeout = $TimeoutMs
        $connectTask = $client.ConnectAsync($normalizedHost, $DashboardPort)
        if (-not $connectTask.Wait($TimeoutMs)) { return $null }
        $connectTask.GetAwaiter().GetResult() | Out-Null
        $stream = $client.GetStream()
        $command = [Text.Encoding]::ASCII.GetBytes(('get loaded program' + [char]10))
        $stream.Write($command, 0, $command.Length)
        $stream.Flush()
        $buffer = New-Object byte[] 4096
        $memory = [IO.MemoryStream]::new()
        try {
            do {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { break }
                $memory.Write($buffer, 0, $read)
                if ($memory.Length -gt 16KB) { break }
                $text = [Text.Encoding]::UTF8.GetString($memory.ToArray())
            } while (-not $text.Contains([char]10))
            $responseText = [Text.Encoding]::UTF8.GetString($memory.ToArray())
        }
        finally { $memory.Dispose() }

        foreach ($line in $responseText -split '[\r\n]') {
            $trimmed = $line.Trim().Trim([char]0)
            if ($trimmed -match '(?i)^Loaded program:\s*<?(?<path>[^>]+)>?$') {
                $path = $Matches.path.Trim()
                $separator = $path.LastIndexOfAny([char[]]('/\'))
                $name = if ($separator -ge 0) { $path.Substring($separator + 1) } else { $path }
                if ($name.EndsWith('.pro', [StringComparison]::OrdinalIgnoreCase) -or
                    $name.EndsWith('.lua', [StringComparison]::OrdinalIgnoreCase)) {
                    $name = $name.Substring(0, $name.Length - 4)
                }
                return $name.Trim()
            }
            if ($trimmed -match '(?i)^No program loaded$') { return $null }
        }
    }
    catch {
        # Dashboard Server is an additive observation. WebSocket state remains
        # authoritative if this optional query is unavailable.
        return $null
    }
    finally {
        if ($null -ne $client) { $client.Dispose() }
        if ($null -ne $timeout) { $timeout.Dispose() }
    }
    return $null
}

function Read-NamedVariable {
    param([Parameter(Mandatory)] [string]$Key)

    $exists = [bool](Invoke-AuboWsRpc 'RegisterControl.hasNamedVariable' @{ key = $Key })
    $item = [ordered]@{
        key = $Key
        exists = $exists
    }
    if (-not $exists) {
        return [pscustomobject]$item
    }

    $type = [string](Invoke-AuboWsRpc 'RegisterControl.getNamedVariableType' @{ key = $Key })
    $item.type = $type
    $item.value = switch -Regex ($type.ToLowerInvariant()) {
        'int' { Invoke-AuboWsRpc 'RegisterControl.getInt32' @{ key = $Key; default_value = 0 }; break }
        'bool' { Invoke-AuboWsRpc 'RegisterControl.getBool' @{ key = $Key; default_value = $false }; break }
        'double|float' { Invoke-AuboWsRpc 'RegisterControl.getDouble' @{ key = $Key; default_value = 0.0 }; break }
        'string' { Invoke-AuboWsRpc 'RegisterControl.getString' @{ key = $Key; default_value = '' }; break }
        default { $null }
    }
    return [pscustomobject]$item
}

$evidence = [ordered]@{
    schema = 'mes.aubo-ws-read-only-preflight/1.0'
    observedAt = [DateTimeOffset]::Now
    endpoint = [ordered]@{
        host = $normalizedHost
        port = $Port
        transport = 'websocket-jsonrpc-2.0'
        robot = $normalizedRobot
    }
    writesAttempted = $false
    programLoadAttempted = $false
    programStartAttempted = $false
    motionAttempted = $false
    canDetermineGo = $false
}

$evidence.identity = [ordered]@{
    robotNames = @(Invoke-AuboWsRpc 'getRobotNames' @())
    robotType = Invoke-AuboWsRpc "$normalizedRobot.RobotConfig.getRobotType" @()
    robotSubType = Invoke-AuboWsRpc "$normalizedRobot.RobotConfig.getRobotSubType" @()
    controlBoxType = Invoke-AuboWsRpc "$normalizedRobot.RobotConfig.getControlBoxType" @()
    controlSoftwareVersionCode = Invoke-AuboWsRpc 'SystemInfo.getControlSoftwareVersionCode' @()
    interfaceVersionCode = Invoke-AuboWsRpc 'SystemInfo.getInterfaceVersionCode' @()
}

$evidence.state = [ordered]@{
    robotMode = Invoke-AuboWsRpc "$normalizedRobot.RobotState.getRobotModeType" @()
    safetyMode = Invoke-AuboWsRpc "$normalizedRobot.RobotState.getSafetyModeType" @()
    runtimeStatus = Invoke-AuboWsRpc 'RuntimeMachine.getStatus' @()
    operationalMode = Invoke-AuboWsRpc "$normalizedRobot.RobotManage.getOperationalMode" @()
    preloadProgramIndex0 = Invoke-AuboWsRpc 'RuntimeMachine.getPreloadProgram' @(0)
    dashboardLoadedProgram = Get-DashboardLoadedProgram
}

$evidence.namedVariables = @($keys | ForEach-Object { Read-NamedVariable $_ })

if ($IncludeModbusSignals) {
    $names = @(Invoke-AuboWsRpc 'RegisterControl.modbusGetSignalNames' @())
    $types = @(Invoke-AuboWsRpc 'RegisterControl.modbusGetSignalTypes' @())
    $values = @(Invoke-AuboWsRpc 'RegisterControl.modbusGetSignalValues' @())
    $signals = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $names.Count; $index++) {
        $name = [string]$names[$index]
        $signals.Add([pscustomobject]@{
            name = $name
            index = Invoke-AuboWsRpc 'RegisterControl.modbusGetSignalIndex' @{ signal_name = $name }
            type = if ($index -lt $types.Count) { $types[$index] } else { $null }
            value = if ($index -lt $values.Count) { $values[$index] } else { $null }
            status = Invoke-AuboWsRpc 'RegisterControl.modbusGetSignalStatus' @{ signal_name = $name }
            errorRaw = Invoke-AuboWsRpc 'RegisterControl.modbusGetSignalError' @{ signal_name = $name }
        })
    }
    $evidence.modbusSignals = @($signals)
}

$blockingReasons = [System.Collections.Generic.List[string]]::new()
if ([string]$evidence.state.robotMode -ne 'Running') {
    $blockingReasons.Add("Robot mode is '$($evidence.state.robotMode)', expected Running.")
}
if ([string]$evidence.state.safetyMode -ne 'Normal') {
    $blockingReasons.Add("Safety mode is '$($evidence.state.safetyMode)', expected Normal.")
}
if ([string]$evidence.state.runtimeStatus -ne 'Stopped') {
    $blockingReasons.Add("Runtime is '$($evidence.state.runtimeStatus)', expected Stopped before a deliberate program start.")
}
if ([string]$evidence.state.operationalMode -ne 'Automatic') {
    $blockingReasons.Add("Operational mode is '$($evidence.state.operationalMode)', expected Automatic.")
}
if ([string]::IsNullOrWhiteSpace([string]$evidence.state.dashboardLoadedProgram) -and
    [string]::IsNullOrWhiteSpace([string]$evidence.state.preloadProgramIndex0)) {
    $blockingReasons.Add('No project is currently loaded and no project is preloaded at index 0.')
}
$evidence.readiness = [ordered]@{
    readyForProgramStart = $blockingReasons.Count -eq 0
    blockingReasons = @($blockingReasons)
    requiresOnsiteOperatorDecision = $true
}

$json = $evidence | ConvertTo-Json -Depth 40
Write-Output $json

if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
    $absolute = [IO.Path]::GetFullPath($OutputPath)
    if (Test-Path -LiteralPath $absolute) {
        throw "Refusing to overwrite existing evidence file '$absolute'."
    }
    $parent = Split-Path -Parent $absolute
    if (-not [string]::IsNullOrWhiteSpace($parent) -and
        -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $json | Set-Content -LiteralPath $absolute -Encoding UTF8
}
