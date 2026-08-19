param(
    [int]$TimeoutMs = 3000,
    [string]$Com = 'COM4',
    [string]$OperatorId = '',
    [string]$Confirm = '',
    [string]$Output = (Join-Path $PSScriptRoot 'd160-noop-write-validation.json'),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$requiredConfirmation = 'I-AUTHORIZE-D160-NOOP-WRITES'
$expectedIdentifier = 'YA7261078'

$reads = @{
    Identity = [ordered]@{ name = 'Identity'; count = 24; requestHex = '01 04 19 00 00 18 F7 5C' }
    Process = [ordered]@{ name = 'Process'; count = 18; requestHex = '01 04 17 D4 00 12 35 8B' }
    Suppressor = [ordered]@{ name = 'Suppressor'; count = 10; requestHex = '01 04 18 38 00 0A F7 60' }
}
$writes = @(
    [ordered]@{ name = 'RewriteCurrentPumpFlow'; register = '0x13DA'; rawValue = 700; requestHex = '01 06 13 DA 02 BC AC 64' },
    [ordered]@{ name = 'RewriteCurrentColumnTemperature'; register = '0x1389'; rawValue = 3500; requestHex = '01 06 13 89 0D AC 58 49' }
)

function ConvertFrom-Hex([string]$Hex) {
    $cleaned = $Hex.Replace(' ', '').Replace('-', '')
    if (($cleaned.Length % 2) -ne 0) { throw 'Hex text must contain an even number of digits.' }
    $bytes = New-Object byte[] ($cleaned.Length / 2)
    for ($index = 0; $index -lt $bytes.Length; $index++) {
        $bytes[$index] = [Convert]::ToByte($cleaned.Substring($index * 2, 2), 16)
    }
    return $bytes
}

function ConvertTo-Hex([byte[]]$Bytes) {
    return [BitConverter]::ToString($Bytes).Replace('-', '')
}

function Get-ModbusCrc([byte[]]$Bytes) {
    [uint16]$crc = 0xFFFF
    foreach ($value in $Bytes) {
        $crc = $crc -bxor $value
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) { $crc = [uint16](($crc -shr 1) -bxor 0xA001) }
            else { $crc = [uint16]($crc -shr 1) }
        }
    }
    return $crc
}

function Assert-FrameCrc([byte[]]$Frame) {
    if ($Frame.Length -lt 4) { throw 'Modbus frame is too short.' }
    $expected = Get-ModbusCrc ([byte[]]$Frame[0..($Frame.Length - 3)])
    $supplied = [uint16](([int]$Frame[-2]) -bor (([int]$Frame[-1]) -shl 8))
    if ($expected -ne $supplied) {
        throw ('CRC mismatch: expected 0x{0:X4}, received 0x{1:X4}.' -f $expected, $supplied)
    }
}

function Read-Exactly([System.IO.Ports.SerialPort]$Port, [int]$Length) {
    $buffer = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Port.Read($buffer, $offset, $Length - $offset)
        if ($read -le 0) { throw "Serial port closed after $offset of $Length bytes." }
        $offset += $read
    }
    return $buffer
}

function Read-Response([System.IO.Ports.SerialPort]$Port, [byte]$ExpectedFunction) {
    $header = Read-Exactly $Port 3
    if ($header[1] -eq ($ExpectedFunction -bor 0x80)) {
        return [byte[]]($header + (Read-Exactly $Port 2))
    }
    if ($header[1] -ne $ExpectedFunction) {
        throw ('Expected response function 0x{0:X2}, received 0x{1:X2}.' -f $ExpectedFunction, $header[1])
    }
    if ($ExpectedFunction -eq 4) {
        if ($header[2] -gt 250) { throw "Response byte count $($header[2]) exceeds the Modbus limit." }
        return [byte[]]($header + (Read-Exactly $Port ($header[2] + 2)))
    }
    return [byte[]]($header + (Read-Exactly $Port 5))
}

function Exchange([System.IO.Ports.SerialPort]$Port, [byte[]]$Request, [byte]$ExpectedFunction) {
    Assert-FrameCrc $Request
    if ($Request[0] -ne 1 -or $Request[1] -ne $ExpectedFunction) {
        throw 'Request slave or function is outside the fixed operation definition.'
    }
    $Port.DiscardInBuffer()
    $Port.DiscardOutBuffer()
    $Port.Write($Request, 0, $Request.Length)
    $response = Read-Response $Port $ExpectedFunction
    Assert-FrameCrc $response
    if ($response[0] -ne 1) { throw ('Expected slave 0x01, received 0x{0:X2}.' -f $response[0]) }
    if ($response[1] -eq ($ExpectedFunction -bor 0x80)) {
        throw ('D160+ returned Modbus exception 0x{0:X2}.' -f $response[2])
    }
    return $response
}

function Read-UInt16BigEndian([byte[]]$Data, [int]$Offset) {
    return [uint16]((([int]$Data[$Offset]) -shl 8) -bor ([int]$Data[$Offset + 1]))
}

function Decode-Read([string]$Name, [byte[]]$Data) {
    if ($Name -eq 'Identity') {
        return [ordered]@{
            observedIdentifier = [Text.Encoding]::ASCII.GetString($Data, 0, 16).Trim([char]0).Trim()
        }
    }
    if ($Name -eq 'Process') {
        return [ordered]@{
            temperatureControlRaw = Read-UInt16BigEndian $Data 0
            columnTemperatureSetpointRaw = Read-UInt16BigEndian $Data 6
            columnTemperatureActualRaw = Read-UInt16BigEndian $Data 8
            flowSetpointRaw = Read-UInt16BigEndian $Data 14
            flowActualRaw = Read-UInt16BigEndian $Data 16
            pressureRaw = Read-UInt16BigEndian $Data 18
            pumpModeRaw = Read-UInt16BigEndian $Data 20
            pumpStateRaw = Read-UInt16BigEndian $Data 22
        }
    }
    return [ordered]@{
        suppressorEluentStateRaw = Read-UInt16BigEndian $Data 4
        faultCode1Raw = Read-UInt16BigEndian $Data 6
        faultCode2Raw = Read-UInt16BigEndian $Data 18
    }
}

foreach ($query in $reads.Values) {
    $frame = ConvertFrom-Hex $query.requestHex
    Assert-FrameCrc $frame
    if ($frame[0] -ne 1 -or $frame[1] -ne 4) { throw 'Read definition is not slave 1 function 0x04.' }
}
foreach ($operation in $writes) {
    $frame = ConvertFrom-Hex $operation.requestHex
    Assert-FrameCrc $frame
    if ($frame[0] -ne 1 -or $frame[1] -ne 6) { throw 'Write definition is not slave 1 function 0x06.' }
}

if ($ValidateOnly) {
    Write-Host 'Validation passed: fixed read and no-op write frames have valid CRCs and expected functions.'
    exit 0
}

if ($TimeoutMs -lt 1) { throw 'TimeoutMs must be positive.' }
if ($Com -ne 'COM4') { throw 'This procedure is restricted to the confirmed D160+ port COM4.' }
if ([string]::IsNullOrWhiteSpace($OperatorId)) { throw 'OperatorId is required for the field audit.' }
if ($Confirm -ne $requiredConfirmation) {
    throw "Explicit confirmation is required: -Confirm $requiredConfirmation"
}

$busyNames = @('ShineLab', 'ShineControl-Normal', 'ShineDataAcquire-Normal')
$busy = Get-Process -Name $busyNames -ErrorAction SilentlyContinue
if ($busy) {
    $names = ($busy | Select-Object -ExpandProperty ProcessName -Unique) -join ', '
    throw "ShineLab is running ($names). Close it before opening COM4."
}

$records = [Collections.Generic.List[object]]::new()
$startedAtUtc = [DateTimeOffset]::UtcNow
$completed = $false
$parent = Split-Path -Parent ([IO.Path]::GetFullPath($Output))
if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }

function Save-Report {
    $report = [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        startedAtUtc = $startedAtUtc.ToString('o')
        operatorId = $OperatorId
        endpoint = "serial://$Com"
        mode = 'controlled-no-op-write-validation'
        completed = $completed
        authorizedWrites = @(
            [ordered]@{ register = '0x13DA'; rawValue = 700; meaning = 'rewrite current pump-flow setpoint' },
            [ordered]@{ register = '0x1389'; rawValue = 3500; meaning = 'rewrite current column-temperature setpoint' }
        )
        excludedWrites = @('pump enable/disable', 'temperature enable/disable', 'password', '0x157F', 'start', 'stop')
        records = $records
    }
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding UTF8
    return $report
}

function Add-Record(
    [string]$Step,
    [string]$Type,
    [bool]$Success,
    [string]$RequestHex,
    [byte[]]$Response,
    $Decoded,
    [string]$ErrorMessage) {
    $records.Add([ordered]@{
        step = $Step
        type = $Type
        capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        success = $Success
        requestHex = $RequestHex.Replace(' ', '')
        responseHex = if ($Response) { ConvertTo-Hex $Response } else { $null }
        decoded = $Decoded
        error = if ($ErrorMessage) { $ErrorMessage } else { $null }
    })
    Save-Report | Out-Null
}

function Invoke-RecordedRead([System.IO.Ports.SerialPort]$Port, $Query, [string]$Step) {
    $request = ConvertFrom-Hex $Query.requestHex
    $response = $null
    try {
        $response = Exchange $Port $request 4
        $expectedDataLength = [int]$Query.count * 2
        if ($response.Length -ne ($expectedDataLength + 5) -or $response[2] -ne $expectedDataLength) {
            throw "Read response length does not match $($Query.count) registers."
        }
        $data = [byte[]]$response[3..($response.Length - 3)]
        $decoded = Decode-Read $Query.name $data
        Add-Record $Step 'read' $true $Query.requestHex $response $decoded ''
        return $decoded
    }
    catch {
        Add-Record $Step 'read' $false $Query.requestHex $response $null $_.Exception.Message
        throw
    }
}

function Invoke-RecordedWrite([System.IO.Ports.SerialPort]$Port, $Operation, [string]$Step) {
    $request = ConvertFrom-Hex $Operation.requestHex
    $response = $null
    try {
        $response = Exchange $Port $request 6
        if ($response.Length -ne 8 -or (ConvertTo-Hex $response) -ne (ConvertTo-Hex $request)) {
            throw 'Write response did not exactly echo the fixed request.'
        }
        Add-Record $Step 'write' $true $Operation.requestHex $response ([ordered]@{
            register = $Operation.register
            rawValue = $Operation.rawValue
        }) ''
    }
    catch {
        Add-Record $Step 'write' $false $Operation.requestHex $response $null $_.Exception.Message
        throw
    }
}

function Assert-SafeState($Identity, $Process, $Suppressor, [string]$Stage) {
    $violations = [Collections.Generic.List[string]]::new()
    if ($Identity.observedIdentifier -ne $expectedIdentifier) { $violations.Add("identifier is $($Identity.observedIdentifier)") }
    if ($Process.temperatureControlRaw -ne 0) { $violations.Add("temperature control raw is $($Process.temperatureControlRaw)") }
    if ($Process.columnTemperatureSetpointRaw -ne 3500) { $violations.Add("column setpoint raw is $($Process.columnTemperatureSetpointRaw)") }
    if ($Process.flowSetpointRaw -ne 700) { $violations.Add("flow setpoint raw is $($Process.flowSetpointRaw)") }
    if ($Process.pressureRaw -ne 0) { $violations.Add("pressure raw is $($Process.pressureRaw)") }
    if ($Process.pumpStateRaw -ne 0) { $violations.Add("pump state raw is $($Process.pumpStateRaw)") }
    if ($Suppressor.suppressorEluentStateRaw -ne 0) { $violations.Add("suppressor/eluent state raw is $($Suppressor.suppressorEluentStateRaw)") }
    if ($Suppressor.faultCode1Raw -ne 0 -or $Suppressor.faultCode2Raw -ne 0) {
        $violations.Add("fault codes are $($Suppressor.faultCode1Raw)/$($Suppressor.faultCode2Raw)")
    }
    if ($violations.Count -gt 0) { throw "$Stage safety gate failed: $($violations -join '; ')." }
}

function Assert-NoActivation($Process, [string]$Stage) {
    if ($Process.temperatureControlRaw -ne 0 -or $Process.pumpStateRaw -ne 0 -or $Process.pressureRaw -ne 0) {
        throw "$Stage detected unexpected activation or pressure. Stop and inspect the instrument manually."
    }
}

Save-Report | Out-Null
$port = [System.IO.Ports.SerialPort]::new(
    $Com, 115200, [System.IO.Ports.Parity]::None, 8, [System.IO.Ports.StopBits]::One)
$port.ReadTimeout = $TimeoutMs
$port.WriteTimeout = $TimeoutMs
$port.DtrEnable = $false
$port.RtsEnable = $false
$failed = $false

try {
    $port.Open()
    $identity = Invoke-RecordedRead $port $reads.Identity 'PreflightIdentity'
    $process = Invoke-RecordedRead $port $reads.Process 'PreflightProcess'
    $suppressor = Invoke-RecordedRead $port $reads.Suppressor 'PreflightSuppressor'
    Assert-SafeState $identity $process $suppressor 'Preflight'
    Add-Record 'PreflightGate' 'gate' $true '' $null ([ordered]@{ result = 'safe-current-value-rewrite-only' }) ''

    Invoke-RecordedWrite $port $writes[0] 'RewriteCurrentPumpFlow'
    Start-Sleep -Milliseconds 250
    $process = Invoke-RecordedRead $port $reads.Process 'ReadbackAfterPumpFlow'
    Assert-NoActivation $process 'Pump-flow readback'
    if ($process.flowSetpointRaw -ne 700) { throw 'Pump-flow readback did not remain at raw value 700.' }

    Invoke-RecordedWrite $port $writes[1] 'RewriteCurrentColumnTemperature'
    Start-Sleep -Milliseconds 250
    $process = Invoke-RecordedRead $port $reads.Process 'ReadbackAfterColumnTemperature'
    Assert-NoActivation $process 'Column-temperature readback'
    if ($process.columnTemperatureSetpointRaw -ne 3500) {
        throw 'Column-temperature readback did not remain at raw value 3500.'
    }

    $suppressor = Invoke-RecordedRead $port $reads.Suppressor 'FinalSuppressorAndFaults'
    if ($suppressor.suppressorEluentStateRaw -ne 0 -or
        $suppressor.faultCode1Raw -ne 0 -or
        $suppressor.faultCode2Raw -ne 0) {
        throw 'Final suppressor or fault state is nonzero.'
    }
    $completed = $true
}
catch {
    $failed = $true
    Add-Record 'SessionFailure' 'failure' $false '' $null $null $_.Exception.Message
    Write-Host ("FAILED: {0}" -f $_.Exception.Message)
}
finally {
    if ($port.IsOpen) { $port.Close() }
    $port.Dispose()
    Save-Report | Out-Null
}

Write-Host "Result: $([IO.Path]::GetFullPath($Output))"
if ($failed -or -not $completed) { exit 1 }
Write-Host 'Completed: two current-value rewrites were echoed and read back without activation.'
exit 0
