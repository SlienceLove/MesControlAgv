param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,
    [string]$Output = '',
    [int]$MaxMatchDeltaMs = 3000
)

$ErrorActionPreference = 'Stop'
if ($MaxMatchDeltaMs -lt 1) { throw 'MaxMatchDeltaMs must be positive.' }
$resolvedInput = [IO.Path]::GetFullPath($InputPath)
if (-not (Test-Path -LiteralPath $resolvedInput)) { throw "Input not found: $resolvedInput" }

$temporaryRoot = $null
$inputItem = Get-Item -LiteralPath $resolvedInput
if ($inputItem.Extension -ieq '.zip') {
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("d160-pressure-analysis-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    Expand-Archive -LiteralPath $resolvedInput -DestinationPath $temporaryRoot
    $workingRoot = $temporaryRoot
}
elseif ($inputItem.PSIsContainer) {
    $workingRoot = $inputItem.FullName
}
elseif ($inputItem.Extension -ieq '.pcap') {
    $workingRoot = $inputItem.DirectoryName
}
else {
    throw 'InputPath must be a pressure-correlation ZIP, directory, or PCAP file.'
}

function Read-UInt32([byte[]]$Bytes, [int]$Offset, [bool]$LittleEndian) {
    if ($LittleEndian) {
        return [uint32](([uint32]$Bytes[$Offset]) -bor
            (([uint32]$Bytes[$Offset + 1]) -shl 8) -bor
            (([uint32]$Bytes[$Offset + 2]) -shl 16) -bor
            (([uint32]$Bytes[$Offset + 3]) -shl 24))
    }
    return [uint32]((([uint32]$Bytes[$Offset]) -shl 24) -bor
        (([uint32]$Bytes[$Offset + 1]) -shl 16) -bor
        (([uint32]$Bytes[$Offset + 2]) -shl 8) -bor
        ([uint32]$Bytes[$Offset + 3]))
}

function Get-ModbusCrc([byte[]]$Bytes, [int]$Count) {
    [uint16]$crc = 0xFFFF
    for ($index = 0; $index -lt $Count; $index++) {
        $crc = $crc -bxor $Bytes[$index]
        for ($bit = 0; $bit -lt 8; $bit++) {
            if (($crc -band 1) -ne 0) { $crc = [uint16](($crc -shr 1) -bxor 0xA001) }
            else { $crc = [uint16]($crc -shr 1) }
        }
    }
    return $crc
}

function Find-ProcessResponses([string]$PcapPath) {
    $bytes = [IO.File]::ReadAllBytes($PcapPath)
    if ($bytes.Length -lt 24) { throw "PCAP is too short: $PcapPath" }
    $magic = [BitConverter]::ToString($bytes, 0, 4).Replace('-', '')
    switch ($magic) {
        'D4C3B2A1' { $littleEndian = $true; $nanoseconds = $false }
        'A1B2C3D4' { $littleEndian = $false; $nanoseconds = $false }
        '4D3CB2A1' { $littleEndian = $true; $nanoseconds = $true }
        'A1B23C4D' { $littleEndian = $false; $nanoseconds = $true }
        default { throw "Unsupported PCAP magic $magic in $PcapPath" }
    }

    $records = [Collections.Generic.List[object]]::new()
    $offset = 24
    $packetNumber = 0
    while ($offset + 16 -le $bytes.Length) {
        $packetNumber++
        $seconds = Read-UInt32 $bytes $offset $littleEndian
        $fraction = Read-UInt32 $bytes ($offset + 4) $littleEndian
        $includedLength = [int](Read-UInt32 $bytes ($offset + 8) $littleEndian)
        $packetOffset = $offset + 16
        if ($includedLength -lt 0 -or $packetOffset + $includedLength -gt $bytes.Length) {
            throw "Invalid included length in packet $packetNumber of $PcapPath"
        }
        $packet = if ($includedLength -eq 0) { [byte[]]@() } else { [byte[]]$bytes[$packetOffset..($packetOffset + $includedLength - 1)] }
        for ($index = 0; $index + 41 -le $packet.Length; $index++) {
            if ($packet[$index] -ne 0x01 -or $packet[$index + 1] -ne 0x04 -or $packet[$index + 2] -ne 0x24) { continue }
            $frame = [byte[]]$packet[$index..($index + 40)]
            $computedCrc = Get-ModbusCrc $frame 39
            $suppliedCrc = [uint16](([int]$frame[39]) -bor (([int]$frame[40]) -shl 8))
            if ($computedCrc -ne $suppliedCrc) { continue }
            $baseTime = [DateTimeOffset]::FromUnixTimeSeconds($seconds)
            $ticks = if ($nanoseconds) { [long]($fraction / 100) } else { [long]$fraction * 10 }
            $capturedAtUtc = $baseTime.AddTicks($ticks)
            $records.Add([pscustomobject][ordered]@{
                pcapFile = [IO.Path]::GetFileName($PcapPath)
                packetNumber = $packetNumber
                capturedAtUtc = $capturedAtUtc.ToString('o')
                responseHex = [BitConverter]::ToString($frame).Replace('-', '')
                temperatureControlRaw = [uint16]((([int]$frame[3]) -shl 8) -bor $frame[4])
                columnTemperatureSetpointRaw = [uint16]((([int]$frame[9]) -shl 8) -bor $frame[10])
                columnTemperatureActualRaw = [uint16]((([int]$frame[11]) -shl 8) -bor $frame[12])
                flowSetpointRaw = [uint16]((([int]$frame[17]) -shl 8) -bor $frame[18])
                flowActualRaw = [uint16]((([int]$frame[19]) -shl 8) -bor $frame[20])
                pressureRaw = [uint16]((([int]$frame[21]) -shl 8) -bor $frame[22])
                pumpStateRaw = [uint16]((([int]$frame[25]) -shl 8) -bor $frame[26])
            })
        }
        $offset = $packetOffset + $includedLength
    }
    return $records
}

try {
    $pcapFiles = if (-not $inputItem.PSIsContainer -and $inputItem.Extension -ieq '.pcap') {
        @($inputItem)
    } else {
        @(Get-ChildItem -LiteralPath $workingRoot -Recurse -Filter '*.pcap' -File)
    }
    if ($pcapFiles.Count -eq 0) { throw 'No PCAP files were found.' }

    $readRecords = [Collections.Generic.List[object]]::new()
    foreach ($pcap in $pcapFiles) {
        foreach ($record in @(Find-ProcessResponses $pcap.FullName)) { $readRecords.Add($record) }
    }
    if ($readRecords.Count -eq 0) { throw 'No CRC-valid D160+ 0x17D4/18 process responses were found.' }

    $manifestFile = Get-ChildItem -LiteralPath $workingRoot -Recurse -Filter 'pressure-correlation-manifest.json' -File |
        Select-Object -First 1
    $manifest = if ($manifestFile) { Get-Content -Raw $manifestFile.FullName | ConvertFrom-Json } else { $null }
    $correlations = [Collections.Generic.List[object]]::new()
    if ($manifest) {
        foreach ($annotation in @($manifest.annotations)) {
            $observedAt = [DateTimeOffset]::Parse($annotation.observedAtUtc)
            $nearest = $readRecords |
                Sort-Object { [Math]::Abs(([DateTimeOffset]::Parse($_.capturedAtUtc) - $observedAt).TotalMilliseconds) } |
                Select-Object -First 1
            $deltaMs = [Math]::Round(([DateTimeOffset]::Parse($nearest.capturedAtUtc) - $observedAt).TotalMilliseconds, 3)
            $matched = [Math]::Abs($deltaMs) -le $MaxMatchDeltaMs
            $correlations.Add([pscustomobject][ordered]@{
                sample = $annotation.sample
                observedAtUtc = $annotation.observedAtUtc
                displayedPressureText = $annotation.displayedPressureText
                displayedPressureValue = $annotation.displayedPressureValue
                displayedUnit = $manifest.displayUnit
                displayedFlowText = $annotation.displayedFlowText
                stateNote = $annotation.stateNote
                matched = $matched
                matchedReadAtUtc = if ($matched) { $nearest.capturedAtUtc } else { $null }
                matchDeltaMs = $deltaMs
                pressureRaw = if ($matched) { $nearest.pressureRaw } else { $null }
                flowActualRaw = if ($matched) { $nearest.flowActualRaw } else { $null }
                pumpStateRaw = if ($matched) { $nearest.pumpStateRaw } else { $null }
            })
        }
    }

    $numeric = @($correlations | Where-Object { $_.matched -and $null -ne $_.displayedPressureValue })
    $distinctRawCount = @($numeric | Select-Object -ExpandProperty pressureRaw -Unique).Count
    $fit = $null
    if ($numeric.Count -ge 3 -and $distinctRawCount -ge 2) {
        $n = [double]$numeric.Count
        $sumX = [double](($numeric | Measure-Object pressureRaw -Sum).Sum)
        $sumY = [double](($numeric | Measure-Object displayedPressureValue -Sum).Sum)
        $sumXX = [double](($numeric | ForEach-Object { [double]$_.pressureRaw * [double]$_.pressureRaw } | Measure-Object -Sum).Sum)
        $sumXY = [double](($numeric | ForEach-Object { [double]$_.pressureRaw * [double]$_.displayedPressureValue } | Measure-Object -Sum).Sum)
        $denominator = ($n * $sumXX) - ($sumX * $sumX)
        if ([Math]::Abs($denominator) -gt 0.0000001) {
            $slope = (($n * $sumXY) - ($sumX * $sumY)) / $denominator
            $intercept = ($sumY - ($slope * $sumX)) / $n
            $maxResidual = [double](($numeric | ForEach-Object {
                [Math]::Abs([double]$_.displayedPressureValue - (($slope * [double]$_.pressureRaw) + $intercept))
            } | Measure-Object -Maximum).Maximum)
            $fit = [ordered]@{
                equation = 'displayed = slope * raw + intercept'
                slope = $slope
                intercept = $intercept
                maxAbsoluteResidual = $maxResidual
                sampleCount = [int]$n
                distinctRawCount = $distinctRawCount
            }
        }
    }

    $assessment = if (-not $manifest) {
        'extraction-only: no pressure annotation manifest was present'
    } elseif ($numeric.Count -lt 3) {
        'insufficient: fewer than three timestamp-matched numeric samples'
    } elseif ($distinctRawCount -lt 2) {
        'insufficient: matched samples do not contain at least two distinct raw pressures'
    } elseif ($null -eq $fit) {
        'insufficient: linear fit could not be calculated'
    } else {
        'candidate-linear-fit-only: review residuals and collect independent confirmation before using the mapping'
    }

    $result = [ordered]@{
        generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        input = $resolvedInput
        mode = 'offline-pressure-correlation-analysis'
        displayUnit = if ($manifest) { $manifest.displayUnit } else { $null }
        validProcessResponseCount = $readRecords.Count
        correlations = $correlations
        distinctMatchedRawCount = $distinctRawCount
        linearFit = $fit
        assessment = $assessment
        safetyDecision = 'This analysis cannot authorize pump or temperature activation.'
        extractedReads = $readRecords
    }
    if ([string]::IsNullOrWhiteSpace($Output)) {
        $baseDirectory = if ($inputItem.PSIsContainer) { $inputItem.FullName } else { $inputItem.DirectoryName }
        $Output = Join-Path $baseDirectory 'd160-pressure-correlation-analysis.json'
    }
    $result | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $Output -Encoding UTF8
    Write-Host "Analysis: $([IO.Path]::GetFullPath($Output))"
    Write-Host "CRC-valid process responses: $($readRecords.Count)"
    Write-Host "Assessment: $assessment"
}
finally {
    if ($temporaryRoot -and (Test-Path -LiteralPath $temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
