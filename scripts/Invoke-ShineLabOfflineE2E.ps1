<##
.SYNOPSIS
    Replays the ShineLab TCP + MES HTTP contract without physical equipment.

.DESCRIPTION
    Starts a temporary MES instance on localhost, starts the protocol simulator,
    drives Config/Command through the public HTTP endpoints, and verifies that
    the task reaches Completed.  The script never opens an instrument, AGV, or
    adapter connection.
#>
param(
    [int]$HttpPort = 5145,
    [int]$TcpPort = 5500,
    [string]$EquipmentCode = 'SHA18I',
    [int]$StartupTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mesDll = Join-Path $repoRoot 'src\MesControlAgv.Mes\bin\Release\net8.0\MesControlAgv.Mes.dll'
$simulator = Join-Path $PSScriptRoot 'Run-ShineLabTcpSimulator.ps1'
if (-not (Test-Path -LiteralPath $mesDll)) {
    throw "MES Release binary not found: $mesDll. Run dotnet build first."
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("mes-shinelab-e2e-{0}" -f [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$dbPath = Join-Path $tempRoot 'mes.db'
$mesLog = Join-Path $tempRoot 'mes.log'
$simLog = Join-Path $tempRoot 'simulator.log'
$simErrLog = Join-Path $tempRoot 'simulator.err.log'
$mes = $null
$sim = $null

function Stop-Child([Diagnostics.Process]$process) {
    if ($null -ne $process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
        try { $process.WaitForExit(5000) | Out-Null } catch { }
        if (-not $process.HasExited) {
            try {
                Start-Process taskkill.exe -ArgumentList "/PID $($process.Id) /T /F" -WindowStyle Hidden -Wait | Out-Null
            } catch { }
        }
    }
}

try {
    $env = @{
        ASPNETCORE_ENVIRONMENT = 'PhysicalAcceptance'
        ASPNETCORE_URLS = "http://127.0.0.1:$HttpPort"
        ConnectionStrings__Mes = "Data Source=$dbPath"
        ShineLabTcp__Enabled = 'true'
        ShineLabTcp__ListenAddress = '127.0.0.1'
        ShineLabTcp__Port = [string]$TcpPort
        ShineLabTcp__StaleAfterSeconds = '10'
        ShineLabTcp__CommandTimeoutMs = '10000'
        Logging__LogLevel__Default = 'Warning'
        'Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command' = 'Warning'
    }
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.Arguments = ('"{0}"' -f $mesDll)
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.UseShellExecute = $false
    # Do not use asynchronous redirected streams here. Windows PowerShell can
    # terminate the host while those callbacks are still draining during
    # cleanup, leaving the temporary MES process behind.
    $startInfo.RedirectStandardOutput = $false
    $startInfo.RedirectStandardError = $false
    foreach ($pair in $env.GetEnumerator()) { $startInfo.Environment[$pair.Key] = $pair.Value }
    $mes = [Diagnostics.Process]::new()
    $mes.StartInfo = $startInfo
    $mes.Start() | Out-Null

    $health = "http://127.0.0.1:$HttpPort/health"
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 300
        try { $healthResponse = Invoke-RestMethod $health -TimeoutSec 2 } catch { $healthResponse = $null }
    } while ($null -eq $healthResponse -and (Get-Date) -lt $deadline)
    if ($null -eq $healthResponse) { throw "MES did not become healthy. See $mesLog" }

    $sim = Start-Process powershell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$simulator,'-ServerAddress','127.0.0.1','-Port',([string]$TcpPort),'-EquipmentCode',$EquipmentCode,'-RunSeconds','20','-HoldSeconds','3') -WorkingDirectory $repoRoot -RedirectStandardOutput $simLog -RedirectStandardError $simErrLog -PassThru
    Start-Sleep -Seconds 1

    $taskUuid = 'offline-' + (Get-Date -Format 'yyyyMMddHHmmss')
    $body = [ordered]@{
        equipmentCode = $EquipmentCode
        taskUuid = $taskUuid
        sampleData = @([ordered]@{ sampleId = 'SAMPLE-001'; sampleName = 'Offline Verification'; type = '1'; position = 11; mPos = 'A1'; channel = 'A' })
    } | ConvertTo-Json -Depth 8
    $created = Invoke-RestMethod "http://127.0.0.1:$HttpPort/api/shinelab/tasks" -Method Post -ContentType 'application/json' -Body $body
    Invoke-RestMethod "http://127.0.0.1:$HttpPort/api/shinelab/tasks/$taskUuid/config" -Method Post | Out-Null
    $command = @{ taskUuid = $taskUuid; action = 0; sampleId = 'SAMPLE-001'; sampleName = 'Offline Verification'; channel = 'A' } | ConvertTo-Json
    Invoke-RestMethod "http://127.0.0.1:$HttpPort/api/shinelab/tasks/$taskUuid/command" -Method Post -ContentType 'application/json' -Body $command | Out-Null

    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 300
        $task = Invoke-RestMethod "http://127.0.0.1:$HttpPort/api/shinelab/tasks/$taskUuid"
    } while ($task.task.status -ne 'Completed' -and (Get-Date) -lt $deadline)
    if ($task.task.status -ne 'Completed') { throw "Offline task did not complete; status=$($task.task.status). See $simLog" }
    Write-Host "PASS: ShineLab offline E2E completed task $taskUuid (status=$($task.task.status))."
    Write-Host "Evidence logs: $mesLog, $simLog"
}
finally {
    Stop-Child $sim
    Stop-Child $mes
    if ($null -ne $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
exit 0
