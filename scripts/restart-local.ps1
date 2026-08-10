param(
    [string]$StatePath,
    [string]$RunId,
    [int]$StartupTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

if ($StartupTimeoutSeconds -lt 1) {
    throw 'StartupTimeoutSeconds must be at least 1.'
}

$legacyStatePath = Join-Path ([IO.Path]::GetTempPath()) 'MesControlAgv-local-pids.json'

function Resolve-StatePath {
    param(
        [string]$RequestedPath,
        [string]$RequestedRunId
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        return [IO.Path]::GetFullPath($RequestedPath)
    }

    if (-not [string]::IsNullOrWhiteSpace($RequestedRunId)) {
        $safeRunId = $RequestedRunId.Trim()
        if ($safeRunId -notmatch '^[A-Za-z0-9._-]+$') {
            throw 'RunId may contain only letters, digits, dot, underscore, and hyphen.'
        }

        return Join-Path ([IO.Path]::GetTempPath()) ("MesControlAgv-local-{0}-pids.json" -f $safeRunId)
    }

    $candidates = [System.Collections.Generic.List[object]]::new()
    if (Test-Path -LiteralPath $legacyStatePath -PathType Leaf) {
        $candidates.Add((Get-Item -LiteralPath $legacyStatePath))
    }
    foreach ($candidate in @(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -Filter 'MesControlAgv-local-*-pids.json' -File -ErrorAction SilentlyContinue)) {
        if ($candidate.FullName -ne $legacyStatePath) {
            $candidates.Add($candidate)
        }
    }
    $candidates = @($candidates | Sort-Object LastWriteTimeUtc -Descending)
    if ($candidates.Count -eq 0) { return $null }
    if ($candidates.Count -gt 1) {
        throw "Multiple local service state files were found. Specify -StatePath or -RunId: $($candidates.FullName -join ', ')"
    }

    return $candidates[0].FullName
}

function Get-StateService {
    param(
        [AllowNull()][object]$State,
        [string]$ServiceName
    )

    if ($null -eq $State -or $null -eq $State.PSObject.Properties['Services']) { return $null }
    return @($State.Services | Where-Object { $_.Name -eq $ServiceName } | Select-Object -First 1)
}

function Get-PortOwners {
    param([int]$Port)

    $owners = [System.Collections.Generic.List[int]]::new()
    foreach ($line in @(netstat -ano -p TCP | Select-String 'LISTENING')) {
        $parts = ($line.ToString() -split '\s+') | Where-Object { $_ }
        if ($parts.Count -ge 5 -and $parts[0] -eq 'TCP' -and $parts[1] -match (':{0}$' -f $Port) -and $parts[3] -eq 'LISTENING') {
            $owners.Add([int]$parts[4])
        }
    }

    @($owners | Sort-Object -Unique)
}

function Assert-OwnedProcess {
    param([AllowNull()][object]$Service)

    if ($null -eq $Service) { throw 'The local service state does not contain the requested service.' }
    $process = Get-Process -Id ([int]$Service.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) { throw "$($Service.Name) is not running (PID $($Service.ProcessId))." }

    if (-not [string]::IsNullOrWhiteSpace([string]$Service.Executable)) {
        $actualPath = $null
        try { $actualPath = $process.Path } catch { }
        if (-not [string]::Equals($actualPath, [string]$Service.Executable, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to restart $($Service.Name): PID $($Service.ProcessId) executable does not match the recorded process."
        }
    }

    $owners = @(Get-PortOwners ([int]$Service.Port))
    if ($owners.Count -gt 0 -and $owners -notcontains [int]$Service.ProcessId) {
        throw "Refusing to restart $($Service.Name): port $($Service.Port) is owned by PID(s) $($owners -join ', ')."
    }

    return $process
}

function Stop-OwnedService {
    param([AllowNull()][object]$Service)

    $process = Assert-OwnedProcess $Service
    Stop-Process -Id $process.Id -Force
    try { $process.WaitForExit(5000) | Out-Null } catch { }
    if (Get-Process -Id $process.Id -ErrorAction SilentlyContinue) {
        throw "$($Service.Name) did not exit after stop request (PID $($Service.ProcessId))."
    }
}

function Wait-Listening {
    param(
        [int]$Port,
        [int]$Timeout,
        [string]$ServiceName,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        if ($Process.HasExited) {
            throw "$ServiceName exited before listening on port $Port (exit code $($Process.ExitCode))."
        }
        if (@(Get-PortOwners $Port).Count -gt 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$ServiceName did not listen on port $Port within $Timeout seconds."
}

function Wait-Health {
    param(
        [string]$BaseUrl,
        [string]$ServiceName,
        [int]$Timeout,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        if ($Process.HasExited) {
            throw "$ServiceName exited before health readiness (exit code $($Process.ExitCode))."
        }
        try {
            $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec ([Math]::Max(1, [Math]::Min(2, $Timeout)))
            if ($health.service -eq $ServiceName -and $health.status -eq 'ok') { return }
        }
        catch { }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$ServiceName did not become healthy at $BaseUrl within $Timeout seconds."
}

$resolvedStatePath = Resolve-StatePath $StatePath $RunId
if ([string]::IsNullOrWhiteSpace($resolvedStatePath)) { throw 'No local service state file was found.' }
if (-not (Test-Path -LiteralPath $resolvedStatePath -PathType Leaf)) {
    throw "Local service state file was not found at '$resolvedStatePath'."
}

$state = Get-Content -Raw -LiteralPath $resolvedStatePath | ConvertFrom-Json
if ($null -eq $state) { throw "Local service state file '$resolvedStatePath' is empty." }
if ($null -ne $state.PSObject.Properties['RunId']) {
    if ([string]::IsNullOrWhiteSpace($RunId)) { $RunId = [string]$state.RunId }
    elseif ([string]$state.RunId -ne $RunId) { throw "State file run id '$($state.RunId)' does not match requested run id '$RunId'." }
}

$simulator = Get-StateService $state 'Simulator'
$adapter = Get-StateService $state 'Adapter'
$mes = Get-StateService $state 'MES'
if ($null -eq $simulator -or $null -eq $adapter -or $null -eq $mes) {
    throw 'The state file must contain Simulator, Adapter, and MES services.'
}

$dotnet = (Get-Command dotnet.exe).Source
try {
    # Keep Simulator alive so its in-memory device operation is available to
    # the newly started Adapter/MES recovery loops.
    $null = Assert-OwnedProcess $simulator
    $null = Assert-OwnedProcess $adapter
    $null = Assert-OwnedProcess $mes
    Stop-OwnedService $mes
    Stop-OwnedService $adapter

    foreach ($serviceName in @('Adapter', 'MES')) {
        $service = Get-StateService $state $serviceName
        if (([string]::IsNullOrWhiteSpace([string]$service.Dll)) -or ([string]::IsNullOrWhiteSpace([string]$service.ProjectRoot))) {
            throw "The state file does not include restart metadata for $serviceName. Start a fresh run with the current run-local.ps1 first."
        }
        $arguments = '"{0}" --urls {1} --environment Development' -f [string]$service.Dll, [string]$service.Url
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $dotnet
        $psi.WorkingDirectory = [string]$service.ProjectRoot
        $psi.Arguments = $arguments
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        if ($serviceName -eq 'Adapter') {
            if (-not [string]::IsNullOrWhiteSpace([string]$service.DatabasePath)) {
                $psi.Environment['ConnectionStrings__Adapter'] = "Data Source=$([string]$service.DatabasePath)"
            }
            $psi.Environment['Simulator__BaseUrl'] = "$([string]$simulator.Url)/"
        }
        else {
            if (-not [string]::IsNullOrWhiteSpace([string]$service.DatabasePath)) {
                $psi.Environment['ConnectionStrings__Mes'] = "Data Source=$([string]$service.DatabasePath)"
            }
            $psi.Environment['Adapter__BaseUrl'] = "$([string]$adapter.Url)/"
        }

        $process = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $process) { throw "$serviceName process could not be started." }
        $service.ProcessId = [int]$process.Id
        $service.Executable = $dotnet
        $service.StartedAtUtc = [DateTime]::UtcNow.ToString('O')
        Wait-Listening ([int]$service.Port) $StartupTimeoutSeconds $serviceName $process
        Wait-Health ([string]$service.Url) ($(if ($serviceName -eq 'Adapter') { 'adapter' } else { 'mes' })) $StartupTimeoutSeconds $process
        Write-Host "$serviceName restarted (PID $($process.Id))."
    }

    if ($null -eq $state.PSObject.Properties['StatePath']) {
        $state | Add-Member -NotePropertyName StatePath -NotePropertyValue $resolvedStatePath
    }
    else {
        $state.StatePath = $resolvedStatePath
    }
    $state | Add-Member -NotePropertyName RestartedAtUtc -NotePropertyValue ([DateTime]::UtcNow.ToString('O')) -Force
    $state | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resolvedStatePath -Encoding UTF8
    Write-Host "Restarted Adapter and MES for run '$RunId'; Simulator remained running."
    Write-Host "Service state updated at: $resolvedStatePath"
}
catch {
    foreach ($serviceName in @('MES', 'Adapter')) {
        $service = Get-StateService $state $serviceName
        if ($null -ne $service -and $service.ProcessId) {
            Stop-Process -Id ([int]$service.ProcessId) -Force -ErrorAction SilentlyContinue
        }
    }
    throw
}
