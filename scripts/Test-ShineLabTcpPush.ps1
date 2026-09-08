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

function New-MessageId {
    return [Guid]::NewGuid().ToString('N')
}

function Send-ShineLabMessage {
    param(
        [Parameter(Mandatory)] [System.IO.StreamWriter]$Writer,
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
    $Writer.WriteLine($json)
    $Writer.Flush()
    Write-Host ("TX {0}: {1}" -f $Method, $json)
}

$client = [System.Net.Sockets.TcpClient]::new()
$client.ReceiveTimeout = 5000
$client.SendTimeout = 5000

try {
    Write-Host "Connecting ShineLab simulator to $ServerAddress`:$Port ..."
    $client.Connect($ServerAddress, $Port)
    $stream = $client.GetStream()
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $reader = [System.IO.StreamReader]::new($stream, $encoding, $false, 4096, $true)
    $writer = [System.IO.StreamWriter]::new($stream, $encoding, 4096, $true)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true

    Send-ShineLabMessage -Writer $writer -Method 'Certification' -Body ([ordered]@{
        clientName = 'ShineLab-TCP-Simulator'
        protocolVersion = 'draft-1'
    })
    $certificationResponse = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($certificationResponse)) {
        throw 'The central server did not return a Certification response.'
    }
    Write-Host "RX Certification: $certificationResponse"

    Send-ShineLabMessage -Writer $writer -Method 'Device' -Body ([ordered]@{
        status = 0
        stage = 'Idle'
        progress = 0
    })
    Start-Sleep -Milliseconds 500

    for ($second = 1; $second -le $RunningSeconds; $second++) {
        $progress = [Math]::Min(95, [int](($second / [double]$RunningSeconds) * 90))
        Send-ShineLabMessage -Writer $writer -Method 'Device' -Body ([ordered]@{
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
        Send-ShineLabMessage -Writer $writer -Method 'AlarmInfo' -Body ([ordered]@{
            task_uuid = $TaskUuid
            errorCode = 'SIM-ALARM-001'
            errorMsg = 'Simulated integration alarm'
        })
        Send-ShineLabMessage -Writer $writer -Method 'TaskError' -Body ([ordered]@{
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            lastKnownStage = 'Detecting'
            errorCode = 'SIM-ALARM-001'
            errorMsg = 'Simulated integration task failure'
            finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        })
    }
    else {
        Send-ShineLabMessage -Writer $writer -Method 'SampleFinish' -Body ([ordered]@{
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            sampleName = 'Integration Standard'
            chan = 'A'
            pos = 11
            finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        })
        Send-ShineLabMessage -Writer $writer -Method 'Result' -Body ([ordered]@{
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            testDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            data = @(
                [ordered]@{ testItem = 'Li'; value = 3.2; unit = 'mg/L'; quality = 'OK' }
            )
        })
        Send-ShineLabMessage -Writer $writer -Method 'EndMission' -Body ([ordered]@{
            task_uuid = $TaskUuid
            finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
            result = 'Success'
        })
    }

    for ($second = 1; $second -le $HoldSeconds; $second++) {
        Send-ShineLabMessage -Writer $writer -Method 'Device' -Body ([ordered]@{
            status = 0
            task_uuid = $TaskUuid
            sampleID = 'SAMPLE-001'
            stage = 'Idle'
            progress = 100
        })
        Start-Sleep -Seconds 1
    }
    Write-Host "ShineLab TCP push simulation completed. task_uuid=$TaskUuid"
}
finally {
    if ($writer) { $writer.Dispose() }
    if ($reader) { $reader.Dispose() }
    if ($stream) { $stream.Dispose() }
    $client.Dispose()
}
