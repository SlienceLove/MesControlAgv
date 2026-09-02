[CmdletBinding()]
param(
    [string]$ControllerHost = '192.168.1.2',
    [int]$Port = 19204,
    [int]$Seconds = 60,
    [int]$IntervalMs = 250,
    [int]$DoId = 6
)

$ErrorActionPreference = 'Stop'

function Read-Exact {
    param(
        [System.IO.Stream]$Stream,
        [byte[]]$Buffer
    )

    $offset = 0
    while ($offset -lt $Buffer.Length) {
        $read = $Stream.Read($Buffer, $offset, $Buffer.Length - $offset)
        if ($read -le 0) { throw 'AGV closed the connection.' }
        $offset += $read
    }
}

function Write-U16 {
    param([byte[]]$Buffer, [int]$Offset, [int]$Value)
    $Buffer[$Offset] = [byte](($Value -shr 8) -band 0xff)
    $Buffer[$Offset + 1] = [byte]($Value -band 0xff)
}

function Write-I32 {
    param([byte[]]$Buffer, [int]$Offset, [int]$Value)
    $Buffer[$Offset] = [byte](($Value -shr 24) -band 0xff)
    $Buffer[$Offset + 1] = [byte](($Value -shr 16) -band 0xff)
    $Buffer[$Offset + 2] = [byte](($Value -shr 8) -band 0xff)
    $Buffer[$Offset + 3] = [byte]($Value -band 0xff)
}

function Read-U16 {
    param([byte[]]$Buffer, [int]$Offset)
    return (($Buffer[$Offset] -shl 8) -bor $Buffer[$Offset + 1])
}

function Read-I32 {
    param([byte[]]$Buffer, [int]$Offset)
    return ([int]$Buffer[$Offset] * 16777216) +
        ([int]$Buffer[$Offset + 1] * 65536) +
        ([int]$Buffer[$Offset + 2] * 256) +
        [int]$Buffer[$Offset + 3]
}

function Get-DoStatus {
    param(
        [string]$Json,
        [int]$Id
    )
    $object = $Json | ConvertFrom-Json
    $item = @($object.DO) | Where-Object { [int]$_.id -eq $Id } | Select-Object -First 1
    if ($null -eq $item) { return $null }
    return [int]([bool]$item.status)
}

$payload = [byte[]]@()
$deadline = (Get-Date).AddSeconds($Seconds)
$last = $null
$samples = 0
$transitions = 0
$client = $null
$stream = $null

Write-Host "Watching AGV $ControllerHost`:$Port API 1013, DO$DoId every ${IntervalMs}ms for ${Seconds}s (read-only)."

try {
    while ((Get-Date) -lt $deadline) {
        try {
            if ($null -eq $client -or -not $client.Connected) {
                if ($stream) { $stream.Dispose() }
                if ($client) { $client.Dispose() }
                $client = [Net.Sockets.TcpClient]::new()
                $client.ReceiveTimeout = 2000
                $client.SendTimeout = 2000
                $client.Connect($ControllerHost, $Port)
                $stream = $client.GetStream()
            }

            $request = New-Object byte[] (16 + $payload.Length)
            $request[0] = 0x5a
            $request[1] = 1
            $request[2] = 0
            $request[3] = 1
            Write-I32 $request 4 $payload.Length
            Write-U16 $request 8 1013
            [Array]::Copy($payload, 0, $request, 16, $payload.Length)
            $stream.Write($request, 0, $request.Length)
            $stream.Flush()

            $header = New-Object byte[] 16
            Read-Exact $stream $header
            $length = Read-I32 $header 4
            if ($length -lt 0 -or $length -gt 16777216) { throw "Invalid payload length $length." }
            $body = New-Object byte[] $length
            if ($length -gt 0) { Read-Exact $stream $body }
            $do6 = Get-DoStatus ([Text.Encoding]::UTF8.GetString($body)) $DoId
            $samples++
            if ($do6 -ne $last) {
                $transitions++
                $last = $do6
                Write-Host ("{0:o} DO{1}={2}" -f [DateTimeOffset]::Now, $DoId, $do6)
            }
        }
        catch {
            Write-Warning $_.Exception.Message
            if ($stream) { $stream.Dispose(); $stream = $null }
            if ($client) { $client.Dispose(); $client = $null }
        }
        Start-Sleep -Milliseconds $IntervalMs
    }
}
finally {
    if ($stream) { $stream.Dispose() }
    if ($client) { $client.Dispose() }
    Write-Host "Finished: samples=$samples transitions=$transitions lastDO$DoId=$last"
}
