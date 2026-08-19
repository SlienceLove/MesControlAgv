param(
    [int]$Count = 3,
    [int]$IntervalMs = 250,
    [int]$TimeoutMs = 3000,
    [string]$Com = 'COM4',
    [string]$Output = (Join-Path $PSScriptRoot 'd160-protocol-read-validation.json'),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$protocolSha256 = '0C86E1B869E6BF21E9DAFAC13C8ACFDA5D7247A2CE9054E40094A4373F8B428F'

if ($Count -lt 1) { throw 'Count must be at least 1.' }
if ($IntervalMs -lt 0) { throw 'IntervalMs cannot be negative.' }
if ($TimeoutMs -lt 1) { throw 'TimeoutMs must be positive.' }
if ($Com -ne 'COM4') { throw 'This field procedure is restricted to the confirmed D160+ port COM4.' }

$queries = @(
    [ordered]@{ name = 'Identity'; startAddress = '0x1900'; registerCount = 24; requestHex = '01 04 19 00 00 18 F7 5C' },
    [ordered]@{ name = 'Detector'; startAddress = '0x1770'; registerCount = 12; requestHex = '01 04 17 70 00 0C F4 60' },
    [ordered]@{ name = 'Process'; startAddress = '0x17D4'; registerCount = 18; requestHex = '01 04 17 D4 00 12 35 8B' },
    [ordered]@{ name = 'Suppressor'; startAddress = '0x1838'; registerCount = 10; requestHex = '01 04 18 38 00 0A F7 60' }
)

function ConvertFrom-Hex([string]$Hex) {
    $cleaned = $Hex.Replace(' ', '').Replace('-', '')
    if (($cleaned.Length % 2) -ne 0) { throw 'Hex text must contain an even number of digits.' }
    $bytes = [byte[]]::new($cleaned.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($cleaned.Substring($index * 2, 2), 16)
    }
    return $bytes
}

function Get-ModbusCrc([byte[]]$Bytes) {
    [uint16]$crc = 0xFFFF
    foreach ($value in $Bytes) {
        $crc = $crc -bxor $value
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) {
                $crc = [uint16](($crc -shr 1) -bxor 0xA001)
            }
            else {
                $crc = [uint16]($crc -shr 1)
            }
        }
    }
    return $crc
}

function Test-Request([byte[]]$Request) {
    if ($Request.Length -ne 8) { throw 'Every request must be exactly 8 bytes.' }
    if ($Request[0] -ne 1 -or $Request[1] -ne 4) {
        throw 'Only slave 1 Modbus function 0x04 requests are permitted.'
    }
    $payload = [byte[]]$Request[0..5]
    $expected = Get-ModbusCrc $payload
    $supplied = [uint16](([int]$Request[6]) -bor (([int]$Request[7]) -shl 8))
    if ($expected -ne $supplied) {
        throw ('Request CRC mismatch: expected 0x{0:X4}, received 0x{1:X4}.' -f $expected, $supplied)
    }
}

function Read-Exactly([System.IO.Ports.SerialPort]$Port, [int]$Length) {
    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Port.Read($buffer, $offset, $Length - $offset)
        if ($read -le 0) { throw "Serial port closed after $offset of $Length bytes." }
        $offset += $read
    }
    return $buffer
}

function Read-Response([System.IO.Ports.SerialPort]$Port) {
    $header = Read-Exactly $Port 3
    if ($header[1] -eq 0x84) {
        $tail = Read-Exactly $Port 2
        return [byte[]]($header + $tail)
    }
    if ($header[1] -ne 4) { throw ('Expected response function 0x04, received 0x{0:X2}.' -f $header[1]) }
    if ($header[2] -gt 250) { throw "Response byte count $($header[2]) exceeds the Modbus limit." }
    $tail = Read-Exactly $Port ($header[2] + 2)
    return [byte[]]($header + $tail)
}

function Test-Response([byte[]]$Response, [int]$RegisterCount) {
    if ($Response.Length -lt 4) { throw 'Response is too short to contain a Modbus CRC.' }
    $payload = [byte[]]$Response[0..($Response.Length - 3)]
    $expectedCrc = Get-ModbusCrc $payload
    $suppliedCrc = [uint16](([int]$Response[-2]) -bor (([int]$Response[-1]) -shl 8))
    if ($expectedCrc -ne $suppliedCrc) {
        throw ('Response CRC mismatch: expected 0x{0:X4}, received 0x{1:X4}.' -f $expectedCrc, $suppliedCrc)
    }
    if ($Response.Length -eq 5 -and $Response[1] -eq 0x84) {
        throw ('D160+ returned Modbus exception 0x{0:X2}.' -f $Response[2])
    }
    $expectedDataLength = $RegisterCount * 2
    $expectedFrameLength = $expectedDataLength + 5
    if ($Response.Length -ne $expectedFrameLength) {
        throw "Expected $expectedFrameLength response bytes, received $($Response.Length)."
    }
    if ($Response[0] -ne 1) { throw ('Expected slave 0x01, received 0x{0:X2}.' -f $Response[0]) }
    if ($Response[1] -ne 4) { throw ('Expected function 0x04, received 0x{0:X2}.' -f $Response[1]) }
    if ($Response[2] -ne $expectedDataLength) {
        throw "Expected $expectedDataLength data bytes, received $($Response[2])."
    }
    return [byte[]]$Response[3..($Response.Length - 3)]
}

function Read-UInt16BigEndian([byte[]]$Data, [int]$Offset) {
    return [uint16]((([int]$Data[$Offset]) -shl 8) -bor ([int]$Data[$Offset + 1]))
}

function Decode-Data([string]$Name, [byte[]]$Data) {
    switch ($Name) {
        'Identity' {
            $identifier = [Text.Encoding]::ASCII.GetString($Data, 0, [Math]::Min(16, $Data.Length)).Trim([char]0).Trim()
            return [ordered]@{ observedIdentifier = $identifier }
        }
        'Detector' {
            return [ordered]@{
                conductivity = [BitConverter]::ToSingle($Data, 0)
                totalConductivity = [BitConverter]::ToSingle($Data, 12)
            }
        }
        'Process' {
            return [ordered]@{
                temperatureControlRaw = Read-UInt16BigEndian $Data 0
                columnTemperatureSetpointC = (Read-UInt16BigEndian $Data 6) / 100.0
                columnTemperatureActualC = (Read-UInt16BigEndian $Data 8) / 100.0
                flowSetpointMlMin = (Read-UInt16BigEndian $Data 14) / 1000.0
                flowActualMlMin = (Read-UInt16BigEndian $Data 16) / 1000.0
                pressureRaw = Read-UInt16BigEndian $Data 18
                pumpModeRaw = Read-UInt16BigEndian $Data 20
                pumpStateRaw = Read-UInt16BigEndian $Data 22
            }
        }
        'Suppressor' {
            return [ordered]@{
                suppressor1SetpointRaw = Read-UInt16BigEndian $Data 0
                suppressor1ActualRaw = Read-UInt16BigEndian $Data 2
                suppressorEluentStateRaw = Read-UInt16BigEndian $Data 4
                faultCode1Raw = Read-UInt16BigEndian $Data 6
                suppressor2SetpointRaw = Read-UInt16BigEndian $Data 8
                suppressor2ActualRaw = Read-UInt16BigEndian $Data 10
                eluentSetpointRaw = Read-UInt16BigEndian $Data 12
                eluentActualRaw = Read-UInt16BigEndian $Data 14
                eluentRemainingRaw = Read-UInt16BigEndian $Data 16
                faultCode2Raw = Read-UInt16BigEndian $Data 18
            }
        }
    }
}

foreach ($query in $queries) {
    Test-Request (ConvertFrom-Hex $query.requestHex)
}

if ($ValidateOnly) {
    foreach ($query in $queries) {
        $dataLength = $query.registerCount * 2
        $payload = [Collections.Generic.List[byte]]::new()
        $payload.Add(1)
        $payload.Add(4)
        $payload.Add($dataLength)
        for ($index = 0; $index -lt $dataLength; $index++) { $payload.Add(0) }
        $crc = Get-ModbusCrc $payload.ToArray()
        $response = [byte[]]($payload.ToArray() + [byte]($crc -band 0xFF) + [byte]($crc -shr 8))
        $data = Test-Response $response $query.registerCount
        Decode-Data $query.name $data | Out-Null
    }
    Write-Host 'Validation passed: all four requests and synthetic response decoders passed strict Modbus checks.'
    exit 0
}

$busyNames = @('ShineLab', 'ShineControl-Normal', 'ShineDataAcquire-Normal')
$busy = Get-Process -Name $busyNames -ErrorAction SilentlyContinue
if ($busy) {
    $names = ($busy | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
    throw "ShineLab is running ($names). Close it before opening COM4."
}

$records = [Collections.Generic.List[object]]::new()
$startedAtUtc = [DateTimeOffset]::UtcNow
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

function Save-Report {
    $successful = @($records | Where-Object { $_.success -eq $true }).Count
    $report = [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        startedAtUtc = $startedAtUtc.ToString('o')
        protocolDocument = 'D160+ general protocol.xlsx'
        protocolSha256 = $protocolSha256
        transport = 'serial'
        endpoint = "serial://$Com"
        mode = 'read-only'
        settings = [ordered]@{ baud = 115200; dataBits = 8; parity = 'none'; stopBits = '1'; timeoutMs = $TimeoutMs }
        requestedRounds = $Count
        requestedQueryCount = $Count * $queries.Count
        completedQueryCount = $records.Count
        successCount = $successful
        allSuccessful = ($records.Count -eq ($Count * $queries.Count) -and $successful -eq $records.Count)
        records = $records
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding UTF8
    return $report
}

Save-Report | Out-Null

$port = [System.IO.Ports.SerialPort]::new(
    $Com,
    115200,
    [System.IO.Ports.Parity]::None,
    8,
    [System.IO.Ports.StopBits]::One)
$port.ReadTimeout = $TimeoutMs
$port.WriteTimeout = $TimeoutMs
$port.DtrEnable = $false
$port.RtsEnable = $false

try {
    $port.Open()
    for ($round = 1; $round -le $Count; $round++) {
        foreach ($query in $queries) {
            $request = ConvertFrom-Hex $query.requestHex
            $capturedAtUtc = [DateTimeOffset]::UtcNow
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            $response = $null
            $data = $null
            $errorType = $null
            $errorMessage = $null
            try {
                $port.DiscardInBuffer()
                $port.DiscardOutBuffer()
                $port.Write($request, 0, $request.Length)
                $response = Read-Response $port
                $data = Test-Response $response $query.registerCount
            }
            catch {
                $errorType = $_.Exception.GetType().FullName
                $errorMessage = $_.Exception.Message
            }
            finally {
                $stopwatch.Stop()
            }

            $success = $null -ne $data
            $records.Add([ordered]@{
                round = $round
                name = $query.name
                startAddress = $query.startAddress
                registerCount = $query.registerCount
                capturedAtUtc = $capturedAtUtc.ToString('o')
                elapsedMs = $stopwatch.ElapsedMilliseconds
                success = $success
                requestHex = ([BitConverter]::ToString($request).Replace('-', ''))
                responseHex = if ($response) { [BitConverter]::ToString($response).Replace('-', '') } else { $null }
                decoded = if ($data) { Decode-Data $query.name $data } else { $null }
                errorType = $errorType
                error = $errorMessage
            })
            Save-Report | Out-Null
            Write-Host ("Round {0}/{1} {2}: {3}" -f $round, $Count, $query.name, $(if ($success) { 'OK' } else { "FAILED - $errorMessage" }))
            if ($IntervalMs -gt 0) { Start-Sleep -Milliseconds $IntervalMs }
        }
    }
}
finally {
    if ($port.IsOpen) { $port.Close() }
    $port.Dispose()
}

$finalReport = Save-Report
Write-Host "Result: $([IO.Path]::GetFullPath($Output))"
Write-Host ("Successful reads: {0}/{1}" -f $finalReport.successCount, $finalReport.requestedQueryCount)
if (-not $finalReport.allSuccessful) { exit 1 }
exit 0
