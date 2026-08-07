param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$SimulatorUrl = 'http://localhost:5183',
    [string]$AdapterUrl = 'http://localhost:5041',
    [string]$MesUrl = 'http://localhost:5045',
    [string]$MesDatabasePath,
    [string]$AdapterDatabasePath,
    [string]$StatePath,
    [string]$RunId,
    [int]$StartupTimeoutSeconds = 15,
    [switch]$RequireIsolatedStores
)

$ErrorActionPreference = 'Stop'

if ($StartupTimeoutSeconds -lt 1) {
    throw 'StartupTimeoutSeconds must be at least 1.'
}

if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = [Guid]::NewGuid().ToString('N')
}
$RunId = $RunId.Trim()
if ($RunId -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'RunId may contain only letters, digits, dot, underscore, and hyphen.'
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = (Get-Command dotnet.exe).Source

function Resolve-AbsolutePath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $resolved = [IO.Path]::GetFullPath($Path)
    $parent = Split-Path -Parent $resolved
    if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    return $resolved
}

$MesDatabasePath = Resolve-AbsolutePath $MesDatabasePath
$AdapterDatabasePath = Resolve-AbsolutePath $AdapterDatabasePath
if ($RequireIsolatedStores -and ([string]::IsNullOrWhiteSpace($MesDatabasePath) -or [string]::IsNullOrWhiteSpace($AdapterDatabasePath))) {
    throw '-RequireIsolatedStores requires both -MesDatabasePath and -AdapterDatabasePath.'
}
if ($RequireIsolatedStores) {
    $defaultMesPaths = @(
        [IO.Path]::GetFullPath((Join-Path $repoRoot 'data\mes.db')),
        [IO.Path]::GetFullPath((Join-Path $repoRoot 'src\MesControlAgv.Mes\data\mes.db'))
    )
    $defaultAdapterPaths = @(
        [IO.Path]::GetFullPath((Join-Path $repoRoot 'data\adapter.db')),
        [IO.Path]::GetFullPath((Join-Path $repoRoot 'src\MesControlAgv.Adapter\data\adapter.db'))
    )
    if ($defaultMesPaths -contains $MesDatabasePath) {
        throw "MES database path '$MesDatabasePath' is the default shared store. Use a temporary path with -RequireIsolatedStores."
    }
    if ($defaultAdapterPaths -contains $AdapterDatabasePath) {
        throw "Adapter database path '$AdapterDatabasePath' is the default shared store. Use a temporary path with -RequireIsolatedStores."
    }
}

if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $StatePath = Join-Path ([IO.Path]::GetTempPath()) ("MesControlAgv-local-{0}-pids.json" -f $RunId)
}
$StatePath = Resolve-AbsolutePath $StatePath
if (Test-Path -LiteralPath $StatePath) {
    throw "State file already exists at '$StatePath'. Stop that run or choose another -RunId/-StatePath."
}

function Get-Endpoint {
    param(
        [string]$Url,
        [string]$ServiceName
    )

    try {
        $endpoint = [Uri]$Url
    }
    catch {
        throw "$ServiceName URL '$Url' is invalid."
    }

    if (-not $endpoint.IsLoopback) {
        throw "$ServiceName URL '$Url' must target localhost for run-local.ps1."
    }

    [pscustomobject]@{
        Url = $Url.TrimEnd('/')
        Port = $endpoint.Port
    }
}

$simulatorEndpoint = Get-Endpoint $SimulatorUrl 'Simulator'
$adapterEndpoint = Get-Endpoint $AdapterUrl 'Adapter'
$mesEndpoint = Get-Endpoint $MesUrl 'MES'

$services = @(
    [pscustomobject]@{
        Name = 'Simulator'
        HealthName = 'simulator'
        ProjectRoot = Join-Path $repoRoot 'src\MesControlAgv.Simulator'
        Dll = Join-Path $repoRoot ("src\MesControlAgv.Simulator\bin\{0}\net8.0\MesControlAgv.Simulator.dll" -f $Configuration)
        Url = $simulatorEndpoint.Url
        Port = $simulatorEndpoint.Port
        DatabasePath = $null
        EnvironmentVariables = @{}
    }
    [pscustomobject]@{
        Name = 'Adapter'
        HealthName = 'adapter'
        ProjectRoot = Join-Path $repoRoot 'src\MesControlAgv.Adapter'
        Dll = Join-Path $repoRoot ("src\MesControlAgv.Adapter\bin\{0}\net8.0\MesControlAgv.Adapter.dll" -f $Configuration)
        Url = $adapterEndpoint.Url
        Port = $adapterEndpoint.Port
        DatabasePath = $AdapterDatabasePath
        EnvironmentVariables = if ([string]::IsNullOrWhiteSpace($AdapterDatabasePath)) {
            @{ 'Simulator__BaseUrl' = "$($simulatorEndpoint.Url)/" }
        }
        else {
            @{ 'ConnectionStrings__Adapter' = "Data Source=$AdapterDatabasePath"; 'Simulator__BaseUrl' = "$($simulatorEndpoint.Url)/" }
        }
    }
    [pscustomobject]@{
        Name = 'MES'
        HealthName = 'mes'
        ProjectRoot = Join-Path $repoRoot 'src\MesControlAgv.Mes'
        Dll = Join-Path $repoRoot ("src\MesControlAgv.Mes\bin\{0}\net8.0\MesControlAgv.Mes.dll" -f $Configuration)
        Url = $mesEndpoint.Url
        Port = $mesEndpoint.Port
        DatabasePath = $MesDatabasePath
        EnvironmentVariables = if ([string]::IsNullOrWhiteSpace($MesDatabasePath)) {
            @{ 'Adapter__BaseUrl' = "$($adapterEndpoint.Url)/" }
        }
        else {
            @{ 'ConnectionStrings__Mes' = "Data Source=$MesDatabasePath"; 'Adapter__BaseUrl' = "$($adapterEndpoint.Url)/" }
        }
    }
)

$started = [System.Collections.Generic.List[object]]::new()

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

function Wait-Listening {
    param(
        [int]$Port,
        [int]$TimeoutSeconds,
        [string]$ServiceName,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "$ServiceName exited before listening on port $Port (exit code $($Process.ExitCode))."
        }

        if (@(Get-PortOwners $Port).Count -gt 0) {
            return
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$ServiceName did not listen on port $Port within $TimeoutSeconds seconds."
}

function Wait-Health {
    param(
        [string]$BaseUrl,
        [string]$ServiceName,
        [int]$TimeoutSeconds,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "$ServiceName exited before health readiness (exit code $($Process.ExitCode))."
        }

        try {
            $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec ([Math]::Max(1, [Math]::Min(2, $TimeoutSeconds)))
            if ($health.service -eq $ServiceName -and $health.status -eq 'ok') {
                return
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$ServiceName did not become healthy at $BaseUrl within $TimeoutSeconds seconds."
}

try {
    foreach ($service in $services) {
        if (-not (Test-Path -LiteralPath $service.Dll)) {
            throw "$($service.Name) DLL was not found at $($service.Dll). Run dotnet build first."
        }

        $owners = @(Get-PortOwners $service.Port)
        if ($owners.Count -gt 0) {
            throw "Port $($service.Port) is already in use by PID $($owners -join ', '). Stop that service before running this script."
        }
    }

    foreach ($service in $services) {
        $arguments = '"{0}" --urls {1} --environment Development' -f $service.Dll, $service.Url
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $dotnet
        $psi.WorkingDirectory = $service.ProjectRoot
        $psi.Arguments = $arguments
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        foreach ($entry in $service.EnvironmentVariables.GetEnumerator()) {
            $psi.Environment[$entry.Key] = [string]$entry.Value
        }

        $process = [System.Diagnostics.Process]::Start($psi)
        if ($null -eq $process) {
            throw "$($service.Name) process could not be started."
        }

        $started.Add([pscustomobject]@{
            Name = $service.Name
            Port = $service.Port
            Url = $service.Url
            ProcessId = [int]$process.Id
            Executable = $dotnet
            ProjectRoot = $service.ProjectRoot
            Dll = $service.Dll
            Configuration = $Configuration
            DatabasePath = $service.DatabasePath
            StartedAtUtc = [DateTime]::UtcNow.ToString('O')
        })

        Wait-Listening $service.Port $StartupTimeoutSeconds $service.Name $process
        Wait-Health $service.Url $service.HealthName $StartupTimeoutSeconds $process
    }

    $state = [pscustomobject]@{
        SchemaVersion = 1
        RunId = $RunId
        CreatedAtUtc = [DateTime]::UtcNow.ToString('O')
        StatePath = $StatePath
        IsolatedStores = (-not [string]::IsNullOrWhiteSpace($MesDatabasePath) -and -not [string]::IsNullOrWhiteSpace($AdapterDatabasePath))
        Services = @($started)
    }
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StatePath -Encoding UTF8

    foreach ($service in $services) {
        Write-Host ("{0}: {1}" -f $service.Name, $service.Url)
    }
    Write-Host "Run ID: $RunId"
    Write-Host "Service state saved to: $StatePath"
    Write-Host ("Stop services with: .\scripts\stop-local.ps1 -StatePath `"{0}`"" -f $StatePath)
    Write-Host 'Start the desktop client separately: dotnet run --project src/MesControlAgv.Wpf'
}
catch {
    for ($index = $started.Count - 1; $index -ge 0; $index--) {
        Stop-Process -Id $started[$index].ProcessId -Force -ErrorAction SilentlyContinue
    }

    throw
}
