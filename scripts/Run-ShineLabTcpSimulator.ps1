param(
    [string]$ServerAddress = '127.0.0.1',
    [ValidateRange(1, 65535)]
    [int]$Port = 5500,
    [string]$EquipmentCode = 'SHA18I',
    [ValidateRange(5, 3600)]
    [int]$RunSeconds = 120
)

$ErrorActionPreference = 'Stop'

function New-MessageId { [Guid]::NewGuid().ToString('N') }

function Send-JsonLine {
    param([System.IO.StreamWriter]$Writer, [object]$Message)
    $json = $Message | ConvertTo-Json -Depth 12 -Compress
    $Writer.WriteLine($json)
    $Writer.Flush()
    Write-Host "TX $json"
}

function Send-Push {
    param([System.IO.StreamWriter]$Writer, [string]$Method, [object]$Body)
    Send-JsonLine -Writer $Writer -Message ([ordered]@{
        strID = New-MessageId
        strMethod = $Method
        equipmentCode = $EquipmentCode
        body = $Body
    })
}

$client = [System.Net.Sockets.TcpClient]::new()
try {
    $client.Connect($ServerAddress, $Port)
    $stream = $client.GetStream()
    $encoding = [System.Text.UTF8Encoding]::new($false)
    $reader = [System.IO.StreamReader]::new($stream, $encoding, $false, 4096, $true)
    $writer = [System.IO.StreamWriter]::new($stream, $encoding, 4096, $true)
    $writer.NewLine = "`n"
    $writer.AutoFlush = $true

    Send-Push -Writer $writer -Method 'Certification' -Body ([ordered]@{
        clientName = 'ShineLab-Command-Simulator'
        protocolVersion = 'draft-1'
    })
    $certification = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($certification)) { throw 'Certification response was not received.' }
    Write-Host "RX $certification"

    $deadline = (Get-Date).AddSeconds($RunSeconds)
    $nextHeartbeat = Get-Date
    while ((Get-Date) -lt $deadline) {
        if ($stream.DataAvailable) {
            $line = $reader.ReadLine()
            if ([string]::IsNullOrWhiteSpace($line)) { break }
            Write-Host "RX $line"
            $request = $line | ConvertFrom-Json
            $taskUuid = $request.body.task_uuid
            Send-JsonLine -Writer $writer -Message ([ordered]@{
                strID = $request.strID
                strMethod = $request.strMethod
                equipmentCode = $EquipmentCode
                body = [ordered]@{
                    task_uuid = $taskUuid
                    result = 'Success'
                    msg = 'accepted by simulator'
                }
            })

            if ($request.strMethod -eq 'Command' -and [int]$request.body.action -eq 0) {
                Send-Push -Writer $writer -Method 'UpdateInfo' -Body ([ordered]@{
                    status = 1
                    task_uuid = $taskUuid
                    sampleID = $request.body.sampleID
                    sampleName = $request.body.sampleName
                    channel = $request.body.channel
                    position = 11
                    stage = 'Detecting'
                    progress = 50
                })
                Start-Sleep -Seconds 1
                Send-Push -Writer $writer -Method 'SampleFinish' -Body ([ordered]@{
                    task_uuid = $taskUuid
                    sampleID = $request.body.sampleID
                    finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
                })
                Send-Push -Writer $writer -Method 'Result' -Body ([ordered]@{
                    task_uuid = $taskUuid
                    sampleID = $request.body.sampleID
                    testDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
                    data = @([ordered]@{ testItem = 'Li'; value = 3.2; unit = 'mg/L'; quality = 'OK' })
                })
                Send-Push -Writer $writer -Method 'TaskFinish' -Body ([ordered]@{
                    task_uuid = $taskUuid
                    finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
                    result = 'Success'
                })
            }
        }

        if ((Get-Date) -ge $nextHeartbeat) {
            Send-Push -Writer $writer -Method 'UpdateInfo' -Body ([ordered]@{
                status = 0
                stage = 'Idle'
                progress = 0
            })
            $nextHeartbeat = (Get-Date).AddSeconds(1)
        }
        Start-Sleep -Milliseconds 100
    }
}
finally {
    if ($writer) { $writer.Dispose() }
    if ($reader) { $reader.Dispose() }
    if ($stream) { $stream.Dispose() }
    $client.Dispose()
}
