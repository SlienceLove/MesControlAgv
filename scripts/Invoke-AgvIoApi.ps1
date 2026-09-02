[CmdletBinding()]
param(
    [ValidateSet('read', 'set')]
    [string]$Operation = 'read',

    [Alias('Host')]
    [string]$ControllerHost = '192.168.1.2',
    [int]$StatusPort = 19204,
    [int]$ControlPort = 19207,
    [int]$OtherPort = 19210,

    [int]$DoId = 6,
    [string]$Status = 'true',
    [ValidateRange(0, 60000)]
    [int]$PulseMs = 0,
    [switch]$AcquireControl,
    [switch]$ReleaseControl,
    [string]$NickName = 'MesControlAgv.Adapter',
    [int]$TimeoutMs = 3000,
    [switch]$NoReadBack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$statusValue = switch ($Status.Trim().ToLowerInvariant()) {
    'true' { $true; break }
    '1' { $true; break }
    'false' { $false; break }
    '0' { $false; break }
    default { throw "Status must be true/false (or 1/0), received '$Status'." }
}

function New-AgvPacket {
    param(
        [Parameter(Mandatory)] [int]$ApiId,
        [AllowEmptyString()] [string]$Json = ''
    )

    $payload = [Text.Encoding]::UTF8.GetBytes($Json)
    $packet = New-Object byte[] (16 + $payload.Length)
    $packet[0] = 0x5A
    $packet[1] = 0x01
    $packet[2] = 0x00
    $packet[3] = 0x01

    $length = [BitConverter]::GetBytes([int]$payload.Length)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($length) }
    [Array]::Copy($length, 0, $packet, 4, 4)

    $api = [BitConverter]::GetBytes([UInt16]$ApiId)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($api) }
    [Array]::Copy($api, 0, $packet, 8, 2)
    [Array]::Copy($payload, 0, $packet, 16, $payload.Length)
    return $packet
}

function Read-AgvExact {
    param(
        [Parameter(Mandatory)] [System.Net.Sockets.NetworkStream]$Stream,
        [Parameter(Mandatory)] [int]$Count
    )

    $buffer = New-Object byte[] $Count
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($buffer, $offset, $Count - $offset)
        if ($read -le 0) { throw 'AGV closed the TCP connection before the full response arrived.' }
        $offset += $read
    }
    return $buffer
}

function Read-AgvPacket {
    param(
        [Parameter(Mandatory)] [System.Net.Sockets.NetworkStream]$Stream
    )

    $header = Read-AgvExact -Stream $Stream -Count 16
    if ($header[0] -ne 0x5A -or $header[1] -ne 0x01 -or $header[2] -ne 0x00 -or $header[3] -ne 0x01) {
        throw ('Invalid AGV packet header: ' + (($header | ForEach-Object { $_.ToString('X2') }) -join ' '))
    }

    $payloadLength = ([int]$header[4] -shl 24) -bor
        ([int]$header[5] -shl 16) -bor
        ([int]$header[6] -shl 8) -bor
        [int]$header[7]
    if ($payloadLength -lt 0 -or $payloadLength -gt 16MB) { throw "Invalid AGV payload length $payloadLength." }

    $apiId = ([int]$header[8] -shl 8) -bor [int]$header[9]
    $payload = if ($payloadLength -eq 0) { [byte[]]@() } else { Read-AgvExact -Stream $Stream -Count $payloadLength }
    $json = [Text.Encoding]::UTF8.GetString($payload)
    $body = if ($payloadLength -eq 0) { [pscustomobject]@{} } else { $json | ConvertFrom-Json }
    return [pscustomobject]@{ ApiId = $apiId; Body = $body; Json = $json }
}

function Invoke-AgvApi {
    param(
        [Parameter(Mandatory)] [int]$Port,
        [Parameter(Mandatory)] [int]$ApiId,
        [AllowEmptyString()] [string]$Json = ''
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $client.ConnectAsync($ControllerHost, $Port)
        if (-not $connectTask.Wait($TimeoutMs)) { throw "Timed out connecting to ${ControllerHost}:$Port." }
        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMs
        $stream.WriteTimeout = $TimeoutMs
        $packet = New-AgvPacket -ApiId $ApiId -Json $Json
        $stream.Write($packet, 0, $packet.Length)
        $stream.Flush()
        $response = Read-AgvPacket -Stream $stream
        $expected = $ApiId + 10000
        if ($response.ApiId -ne $expected) {
            throw "Expected response API $expected for request $ApiId, received $($response.ApiId)."
        }
        return $response
    }
    finally {
        if ($null -ne $client) { $client.Dispose() }
    }
}

function Get-RetCode {
    param([Parameter(Mandatory)] [object]$Body)
    $property = $Body.PSObject.Properties['ret_code']
    if ($null -eq $property -or $null -eq $property.Value) { return 0 }
    return [int]$property.Value
}

function Assert-AgvSuccess {
    param(
        [Parameter(Mandatory)] [object]$Response,
        [Parameter(Mandatory)] [int]$ApiId
    )
    $code = Get-RetCode -Body $Response.Body
    if ($code -ne 0) {
        $message = $Response.Body.PSObject.Properties['err_msg']
        $text = if ($null -eq $message) { '' } else { [string]$message.Value }
        throw "AGV API $ApiId failed: ret_code=$code $text"
    }
}

function Query-AgvIo {
    $response = Invoke-AgvApi -Port $StatusPort -ApiId 1013
    Assert-AgvSuccess -Response $response -ApiId 1013
    return [pscustomobject]@{
        observedAt = [DateTimeOffset]::Now
        host = $ControllerHost
        port = $StatusPort
        api = 1013
        io = $response.Body
    }
}

if ($Operation -eq 'read') {
    Query-AgvIo | ConvertTo-Json -Depth 20
    exit 0
}

if ($DoId -lt 0) { throw 'DoId must be non-negative.' }

$ownership = Invoke-AgvApi -Port $StatusPort -ApiId 1060
Assert-AgvSuccess -Response $ownership -ApiId 1060
$locked = $ownership.Body.PSObject.Properties['locked']
$ownerName = $ownership.Body.PSObject.Properties['nick_name']
$isOurs = $null -ne $locked -and [bool]$locked.Value -and
    $null -ne $ownerName -and [string]$ownerName.Value -eq $NickName

if (-not $isOurs -and $AcquireControl) {
    $acquireJson = @{ nick_name = $NickName } | ConvertTo-Json -Compress
    $acquire = Invoke-AgvApi -Port $ControlPort -ApiId 4005 -Json $acquireJson
    Assert-AgvSuccess -Response $acquire -ApiId 4005
    $ownership = Invoke-AgvApi -Port $StatusPort -ApiId 1060
    Assert-AgvSuccess -Response $ownership -ApiId 1060
    $locked = $ownership.Body.PSObject.Properties['locked']
    $ownerName = $ownership.Body.PSObject.Properties['nick_name']
    $isOurs = $null -ne $locked -and [bool]$locked.Value -and
        $null -ne $ownerName -and [string]$ownerName.Value -eq $NickName
}

if (-not $isOurs) {
    $ownerText = if ($null -eq $ownerName) { 'unknown' } else { [string]$ownerName.Value }
    throw "AGV control is not owned by '$NickName' (reported owner: $ownerText). Use -AcquireControl only when taking control is approved."
}

$setJson = @{ id = $DoId; status = $statusValue } | ConvertTo-Json -Compress
$setResponse = Invoke-AgvApi -Port $OtherPort -ApiId 6001 -Json $setJson
Assert-AgvSuccess -Response $setResponse -ApiId 6001

$result = [ordered]@{
    observedAt = [DateTimeOffset]::Now
    host = $ControllerHost
    port = $OtherPort
    api = 6001
    request = @{ id = $DoId; status = $statusValue }
    response = $setResponse.Body
}
if ($PulseMs -gt 0) {
    if (-not $statusValue) { throw '-PulseMs requires -Status true.' }
    Start-Sleep -Milliseconds $PulseMs
    $clearJson = @{ id = $DoId; status = $false } | ConvertTo-Json -Compress
    $clearResponse = Invoke-AgvApi -Port $OtherPort -ApiId 6001 -Json $clearJson
    Assert-AgvSuccess -Response $clearResponse -ApiId 6001
    $result.pulseCleared = $true
    $result.clearResponse = $clearResponse.Body
}
if (-not $NoReadBack) {
    $result.readBack = (Query-AgvIo).io
}

if ($ReleaseControl) {
    $release = Invoke-AgvApi -Port $ControlPort -ApiId 4006
    Assert-AgvSuccess -Response $release -ApiId 4006
    $result.released = $true
}

[pscustomobject]$result | ConvertTo-Json -Depth 20
