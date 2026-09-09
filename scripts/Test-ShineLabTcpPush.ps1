param(
    [string]$ServerAddress = '127.0.0.1',
    [ValidateRange(1, 65535)]
    [int]$Port = 5500,
    [string]$EquipmentCode = 'SHA18I',
    [string]$TaskUuid = ("test-{0}" -f (Get-Date -Format 'yyyyMMddHHmmss')),
    [ValidateRange(1, 300)]
    [int]$RunningSeconds = 5,
    [ValidateRange(0, 600)]
    [int]$HoldSeconds = 10,
    [switch]$SendAlarm
)

$ErrorActionPreference = 'Stop'

function New-MessageId { [Guid]::NewGuid().ToString('N') }

function ConvertTo-ShineLabFrame {
    param([Parameter(Mandatory)] [string]$Json)

    $encoding = [System.Text.UTF8Encoding]::new($false)
    $jsonBytes = $encoding.GetBytes($Json)
    if ($jsonBytes.Length -gt 65534) { throw 'ShineLab JSON payload exceeds the native 16-bit frame limit.' }
    $length = $jsonBytes.Length + 1
    $frame = [byte[]]::new($jsonBytes.Length + 5)
    $frame[0] = 0x55
    $frame[1] = 0xAA
    $frame[2] = [byte](($length -shr 8) -band 0xff)
    $frame[3] = [byte]($length -band 0xff)
    [Buffer]::BlockCopy($jsonBytes, 0, $frame, 4, $jsonBytes.Length)
    $frame[$frame.Length - 1] = 0
    Write-Output -NoEnumerate $frame
}

function Read-ExactBytes {
    param([Parameter(Mandatory)] [System.IO.Stream]$Stream, [Parameter(Mandatory)] [int]$Count)
    $bytes = [byte[]]::new($Count)
    $offset = 0
    while ($offset -lt $Count) {
        $read = $Stream.Read($bytes, $offset, $Count - $offset)
        if ($read -le 0) { throw 'ShineLab peer closed before a complete frame was received.' }
        $offset += $read
    }
    Write-Output -NoEnumerate $bytes
}

function Read-ShineLabFrame {
    param([Parameter(Mandatory)] [System.IO.Stream]$Stream)
    $header = Read-ExactBytes -Stream $Stream -Count 4
    if ($header[0] -ne 0x55 -or $header[1] -ne 0xAA) { throw 'Invalid ShineLab native frame header.' }
    $length = ($header[2] * 256) + $header[3]
    if ($length -lt 1) { throw 'Invalid ShineLab native frame length.' }
    $payload = Read-ExactBytes -Stream $Stream -Count $length
    if ($payload[$length - 1] -ne 0) { throw 'ShineLab native frame is missing its NUL terminator.' }
    return [System.Text.UTF8Encoding]::new($false, $true).GetString($payload, 0, $length - 1)
}

function Send-ShineLabMessage {
    param(
        [Parameter(Mandatory)] [System.IO.Stream]$Stream,
        [Parameter(Mandatory)] [string]$Method,
        [Parameter(Mandatory)] [object]$Body
    )

    $message = [ordered]@{
        strID = New-MessageId
        strMethod = $Method
        equipmentCode = $EquipmentCode
        body = $Body
    }
    $json = $message | ConvertTo-Json -Depth 10 -Compress
    $frame = ConvertTo-ShineLabFrame -Json $json
    $Stream.Write($frame, 0, $frame.Length)
    $Stream.Flush()
    Write-Host ("TX {0}: {1}" -f $Method, $json)
}

$client = [System.Net.Sockets.TcpClient]::new()
$client.ReceiveTimeout = 5000
$client.SendTimeout = 5000

try {
    Write-Host "Connecting native ShineLab simulator to $ServerAddress`:$Port ..."
    $client.Connect($ServerAddress, $Port)
    $stream = $client.GetStream()

    Send-ShineLabMessage -Stream $stream -Method 'Certification' -Body ([ordered]@{
        clientName = 'ShineLab-TCP-Simulator'
        protocolVersion = 'draft-1'
    })
    $certificationResponse = Read-ShineLabFrame -Stream $stream
    if ([string]::IsNullOrWhiteSpace($certificationResponse)) {
        throw 'The central server did not return a framed Certification response.'
    }
    Write-Host "RX Certification: $certificationResponse"

    Send-ShineLabMessage -Stream $stream -Method 'Device' -Body ([ordered]@{
        status = 0
        stage = 'Idle'
        progress = 0
    })
    Start-Sleep -Milliseconds 500

    for ($second = 1; $second -le $RunningSeconds; $second++) {
        $progress = [Math]::Min(95, [int](($second / [double]$RunningSeconds) * 90))
        Send-ShineLabMessage -Stream $stream -Method 'Device' -Body ([ordered]@{
            status = 1
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            sampleName = 'Integration Standard'
            chan = 'A'
            pos = 11
            stage = 'Detecting'
            progress = $progress
            startDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        })
        Start-Sleep -Seconds 1
    }

    if ($SendAlarm) {
        Send-ShineLabMessage -Stream $stream -Method 'AlarmInfo' -Body ([ordered]@{
            task_uuid = $TaskUuid
            errorCode = 'SIM-ALARM-001'
            errorMsg = 'Simulated integration alarm'
        })
        Send-ShineLabMessage -Stream $stream -Method 'TaskError' -Body ([ordered]@{
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            lastKnownStage = 'Detecting'
            errorCode = 'SIM-ALARM-001'
            errorMsg = 'Simulated integration task failure'
            finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        })
    }
    else {
        Send-ShineLabMessage -Stream $stream -Method 'SampleFinish' -Body ([ordered]@{
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            sampleName = 'Integration Standard'
            chan = 'A'
            pos = 11
            finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        })
        Send-ShineLabMessage -Stream $stream -Method 'Result' -Body ([ordered]@{
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            testDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            data = @(
                [ordered]@{ testItem = 'Li'; value = 3.2; unit = 'mg/L'; quality = 'OK' }
            )
        })
        Send-ShineLabMessage -Stream $stream -Method 'EndMission' -Body ([ordered]@{
            task_uuid = $TaskUuid
            finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            result = 'Success'
        })
    }

    for ($second = 1; $second -le $HoldSeconds; $second++) {
        Send-ShineLabMessage -Stream $stream -Method 'Device' -Body ([ordered]@{
            status = 0
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            stage = 'Idle'
            progress = 100
        })
        Start-Sleep -Seconds 1
    }
    Write-Host "Native ShineLab TCP push simulation completed. task_uuid=$TaskUuid"
}
finally {
    if ($stream) { $stream.Dispose() }
    $client.Dispose()
}
