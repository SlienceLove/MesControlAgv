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

function ConvertTo-ShineLabFrame {
    param([Parameter(Mandatory)] [string]$Json)
    $jsonBytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Json)
    if ($jsonBytes.Length -gt 65534) { throw 'ShineLab JSON payload exceeds the native 16-bit frame limit.' }
    $length = $jsonBytes.Length + 1
    $frame = [byte[]]::new($jsonBytes.Length + 5)
    $frame[0] = 0x55; $frame[1] = 0xAA
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

function Send-ShineLabJson {
    param([Parameter(Mandatory)] [System.IO.Stream]$Stream, [Parameter(Mandatory)] [object]$Message)
    $json = $Message | ConvertTo-Json -Depth 12 -Compress
    $frame = ConvertTo-ShineLabFrame -Json $json
    $Stream.Write($frame, 0, $frame.Length)
    $Stream.Flush()
    Write-Host "TX $json"
}

function New-Push {
    param([Parameter(Mandatory)] [string]$Method, [Parameter(Mandatory)] [object]$Body)
    return [ordered]@{
        strID = New-MessageId
        strMethod = $Method
        equipmentCode = $EquipmentCode
        body = $Body
    }
}

$client = [System.Net.Sockets.TcpClient]::new()
try {
    $client.Connect($ServerAddress, $Port)
    $stream = $client.GetStream()

    Send-ShineLabJson -Stream $stream -Message (New-Push -Method 'Certification' -Body ([ordered]@{
        clientName = 'ShineLab-Command-Simulator'
        protocolVersion = 'draft-1'
    }))
    $certification = Read-ShineLabFrame -Stream $stream
    if ([string]::IsNullOrWhiteSpace($certification)) { throw 'Certification response was not received.' }
    Write-Host "RX $certification"

    $deadline = (Get-Date).AddSeconds($RunSeconds)
    $nextHeartbeat = Get-Date
    while ((Get-Date) -lt $deadline) {
        if ($stream.DataAvailable) {
            $requestJson = Read-ShineLabFrame -Stream $stream
            if ([string]::IsNullOrWhiteSpace($requestJson)) { break }
            Write-Host "RX $requestJson"
            $request = $requestJson | ConvertFrom-Json
            $taskUuid = $request.body.task_uuid
            Send-ShineLabJson -Stream $stream -Message ([ordered]@{
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
                Send-ShineLabJson -Stream $stream -Message (New-Push -Method 'Device' -Body ([ordered]@{
                    status = 1; task_uuid = $taskUuid; sampleID = $request.body.sampleID
                    sampleName = $request.body.sampleName; chan = $request.body.chan
                    pos = 11; stage = 'Detecting'; progress = 50
                }))
                Start-Sleep -Seconds 1
                Send-ShineLabJson -Stream $stream -Message (New-Push -Method 'SampleFinish' -Body ([ordered]@{
                    task_uuid = $taskUuid; sampleID = $request.body.sampleID
                    finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
                }))
                Send-ShineLabJson -Stream $stream -Message (New-Push -Method 'Result' -Body ([ordered]@{
                    task_uuid = $taskUuid; sampleID = $request.body.sampleID
                    testDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
                    data = @([ordered]@{ testItem = 'Li'; value = 3.2; unit = 'mg/L'; quality = 'OK' })
                }))
                Send-ShineLabJson -Stream $stream -Message (New-Push -Method 'EndMission' -Body ([ordered]@{
                    task_uuid = $taskUuid; finishDate = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); result = 'Success'
                }))
            }
        }

        if ((Get-Date) -ge $nextHeartbeat) {
            Send-ShineLabJson -Stream $stream -Message (New-Push -Method 'Device' -Body ([ordered]@{
                status = 0; stage = 'Idle'; progress = 0
            }))
            $nextHeartbeat = (Get-Date).AddSeconds(1)
        }
        Start-Sleep -Milliseconds 100
    }
}
finally {
    if ($stream) { $stream.Dispose() }
    $client.Dispose()
}
