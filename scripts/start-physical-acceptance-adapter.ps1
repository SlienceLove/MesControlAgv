param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('read-only-preflight', 'standard')]
    [string]$ExpectedRunMode,
    [Parameter(Mandatory = $true)]
    [string]$ControllerHost,
    [Parameter(Mandatory = $true)]
    [string]$AdapterDatabasePath,
    [string]$AdapterUrl = 'http://127.0.0.1:5041',
    [string]$DllPath,
    [string]$StatePath,
    [string]$LogDirectory,
    [string]$RunId,
    [int]$StartupTimeoutSeconds = 30,
    [switch]$EnableAuboReadOnly,
    [switch]$EnableAuboControl,
    [switch]$ConfirmPhysical,
    [string[]]$AuboAllowedProgramNames = @(),
    [string]$AuboHost,
    [ValidateRange(1, 65535)]
    [int]$AuboPort = 9012,
    [string]$AuboRobotName = 'rob1',
    [ValidateRange(1, 65535)]
    [int]$AgvStatusPort = 19204,
    [ValidateRange(1, 65535)]
    [int]$AgvCommandPort = 19206,
    [ValidateRange(1, 65535)]
    [int]$AgvControlPort = 19207,
    [ValidateRange(1, 65535)]
    [int]$AgvOtherPort = 19210,
    [ValidateRange(1, 65535)]
    [int]$AgvPushPort = 19301,
    [switch]$EnableFieldNavigationAcceptance,
    [switch]$AllowExistingDatabase
)

$ErrorActionPreference = 'Stop'

# PowerShell callers often pass a pair of names as one comma-delimited value
# (for example, `-AuboAllowedProgramNames '测试1,测试2'`).  Normalize that
# boundary form into individual exact names before projecting indexed
# environment variables for the .NET configuration binder.  The Adapter still
# performs the authoritative program-name validation.
$AuboAllowedProgramNames = @(
    foreach ($rawName in @($AuboAllowedProgramNames)) {
        if ($null -eq $rawName) { continue }
        foreach ($candidate in ([string]$rawName -split '[,;]')) {
            $trimmed = $candidate.Trim()
            if ($trimmed.Length -gt 0) { $trimmed }
        }
    }
)

if ($StartupTimeoutSeconds -lt 1) {
    throw 'StartupTimeoutSeconds must be at least 1.'
}
if ([string]::IsNullOrWhiteSpace($ControllerHost)) {
    throw 'ControllerHost is required and must come from the approved site configuration.'
}
if ($ExpectedRunMode -eq 'read-only-preflight' -and $EnableFieldNavigationAcceptance) {
    throw 'Field navigation acceptance cannot be enabled in read-only-preflight mode.'
}
if ($EnableAuboReadOnly -and [string]::IsNullOrWhiteSpace($AuboHost)) {
    throw 'AuboHost is required when EnableAuboReadOnly is specified.'
}
if ($EnableAuboControl -and -not $EnableAuboReadOnly) {
    throw 'EnableAuboControl requires EnableAuboReadOnly so the same session has an explicit read gate.'
}
if ($EnableAuboControl -and $ExpectedRunMode -ne 'standard') {
    throw 'EnableAuboControl is allowed only with ExpectedRunMode=standard.'
}
if ($EnableAuboControl -and -not $ConfirmPhysical) {
    throw 'EnableAuboControl requires -ConfirmPhysical during a supervised field test.'
}
if ($EnableAuboControl -and @($AuboAllowedProgramNames).Count -eq 0) {
    throw 'EnableAuboControl requires at least one exact AuboAllowedProgramNames value.'
}
if ([string]::IsNullOrWhiteSpace($AuboRobotName)) {
    throw 'AuboRobotName must not be empty.'
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
    param(
        [string]$Path,
        [switch]$CreateParent
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return $null
    }

    $resolved = [IO.Path]::GetFullPath($Path)
    if ($CreateParent) {
        $parent = Split-Path -Parent $resolved
        if (-not [string]::IsNullOrWhiteSpace($parent) -and -not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
    }

    return $resolved
}

function Test-ExistingAcceptanceDatabase {
    param([string]$Path)

    $item = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($item.Length -eq 0) {
        return $true
    }
    if ($item.Length -lt 100) {
        return $false
    }

    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $header = New-Object byte[] 16
        $read = $stream.Read($header, 0, $header.Length)
        return $read -eq 16 -and [Text.Encoding]::ASCII.GetString($header).StartsWith("SQLite format 3$([char]0)", [StringComparison]::Ordinal)
    }
    finally {
        $stream.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($DllPath)) {
    $DllPath = Join-Path $repoRoot 'src\MesControlAgv.Adapter\bin\Release\net8.0\MesControlAgv.Adapter.dll'
}
$DllPath = Resolve-AbsolutePath $DllPath
if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) {
    throw "Adapter DLL was not found at '$DllPath'. Build the approved Release source first or pass -DllPath."
}

$AdapterDatabasePath = Resolve-AbsolutePath $AdapterDatabasePath -CreateParent
$defaultDatabasePaths = @(
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'data\adapter.db')),
    [IO.Path]::GetFullPath((Join-Path $repoRoot 'src\MesControlAgv.Adapter\data\adapter.db'))
)
if ($defaultDatabasePaths -contains $AdapterDatabasePath) {
    throw "Adapter database path '$AdapterDatabasePath' is a shared default store. Use an isolated acceptance path."
}
$acceptanceArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\physical-acceptance'))
$adapterDatabaseExists = Test-Path -LiteralPath $AdapterDatabasePath -PathType Leaf
if ($adapterDatabaseExists) {
    if (-not $AllowExistingDatabase) {
        throw "Adapter database already exists at '$AdapterDatabasePath'. Use a new path, or use an approved acceptance database under '$acceptanceArtifactsRoot' with -AllowExistingDatabase."
    }

    $databaseDirectory = Split-Path -Parent $AdapterDatabasePath
    $databaseInsideAcceptanceArtifacts = $databaseDirectory.StartsWith(
        $acceptanceArtifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($databaseDirectory, $acceptanceArtifactsRoot, [StringComparison]::OrdinalIgnoreCase)
    if (-not $databaseInsideAcceptanceArtifacts) {
        throw "Existing Adapter database '$AdapterDatabasePath' is not under the controlled acceptance artifacts directory '$acceptanceArtifactsRoot'. Existing databases are refused outside that directory."
    }

    if (-not (Test-ExistingAcceptanceDatabase $AdapterDatabasePath)) {
        throw "Existing Adapter database '$AdapterDatabasePath' is neither an empty file nor a SQLite acceptance database."
    }
}

if ([string]::IsNullOrWhiteSpace($StatePath)) {
    $StatePath = Join-Path ([IO.Path]::GetTempPath()) ("MesControlAgv-physical-{0}-pids.json" -f $RunId)
}
$StatePath = Resolve-AbsolutePath $StatePath -CreateParent
if (Test-Path -LiteralPath $StatePath) {
    throw "State file already exists at '$StatePath'. Stop that run or choose another -RunId/-StatePath."
}

if ([string]::IsNullOrWhiteSpace($LogDirectory)) {
    $LogDirectory = Join-Path ([IO.Path]::GetTempPath()) ("MesControlAgv-physical-{0}-logs" -f $RunId)
}
$logDirectoryPreExisted = Test-Path -LiteralPath $LogDirectory -PathType Container
$LogDirectory = Resolve-AbsolutePath $LogDirectory -CreateParent
if (-not (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
}
$standardOutputPath = Join-Path $LogDirectory 'adapter.stdout.log'
$standardErrorPath = Join-Path $LogDirectory 'adapter.stderr.log'
foreach ($logPath in @($standardOutputPath, $standardErrorPath)) {
    if (Test-Path -LiteralPath $logPath) {
        throw "Log file already exists at '$logPath'. Use a new -RunId or -LogDirectory."
    }
}

try {
    $endpoint = [Uri]$AdapterUrl
}
catch {
    throw "Adapter URL '$AdapterUrl' is invalid."
}
if (-not $endpoint.IsAbsoluteUri -or -not $endpoint.IsLoopback -or $endpoint.Scheme -ne 'http') {
    throw "Adapter URL '$AdapterUrl' must be an absolute loopback HTTP URL."
}
if ($endpoint.AbsolutePath -ne '/' -or -not [string]::IsNullOrWhiteSpace($endpoint.Query)) {
    throw "Adapter URL '$AdapterUrl' must not contain a path or query string."
}
$AdapterUrl = $AdapterUrl.TrimEnd('/')
$adapterPort = $endpoint.Port

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

function Get-PortOwnerDescriptions {
    param([int[]]$ProcessIds)

    $descriptions = [System.Collections.Generic.List[string]]::new()
    foreach ($processId in @($ProcessIds | Sort-Object -Unique)) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            $descriptions.Add("PID $processId (exited before inspection)")
            continue
        }

        $path = $null
        try { $path = $process.Path } catch { }
        if ([string]::IsNullOrWhiteSpace($path)) {
            $descriptions.Add("PID $processId ($($process.ProcessName))")
        }
        else {
            $descriptions.Add("PID $processId ($($process.ProcessName), $path)")
        }
    }

    return @($descriptions)
}

function Wait-Listening {
    param(
        [int]$Port,
        [int]$TimeoutSeconds,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            $ownersAfterExit = @(Get-PortOwners $Port)
            if ($ownersAfterExit.Count -gt 0) {
                $ownerDescriptions = @(Get-PortOwnerDescriptions $ownersAfterExit) -join '; '
                throw "Adapter exited before listening on port $Port (exit code $($Process.ExitCode)); the port is now owned by $ownerDescriptions."
            }

            throw "Adapter exited before listening on port $Port (exit code $($Process.ExitCode))."
        }

        $owners = @(Get-PortOwners $Port)
        if ($owners -contains [int]$Process.Id) {
            return
        }
        if ($owners.Count -gt 0) {
            $ownerDescriptions = @(Get-PortOwnerDescriptions $owners) -join '; '
            throw "Adapter PID $($Process.Id) could not bind port $Port; the port is owned by $ownerDescriptions. Choose another -AdapterUrl or stop the conflicting process."
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Adapter PID $($Process.Id) did not listen on port $Port within $TimeoutSeconds seconds."
}

function Wait-Health {
    param(
        [string]$BaseUrl,
        [string]$RunMode,
        [int]$TimeoutSeconds,
        [System.Diagnostics.Process]$Process
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($Process.HasExited) {
            throw "Adapter exited before health readiness (exit code $($Process.ExitCode))."
        }

        $health = $null
        try {
            $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec ([Math]::Max(1, [Math]::Min(2, $TimeoutSeconds)))
        }
        catch {
        }

        if ($null -ne $health) {
            if ($health.service -eq 'adapter' -and $health.status -eq 'ok') {
                if ($health.runMode -ne $RunMode) {
                    throw "Adapter health reported runMode '$($health.runMode)' instead of expected '$RunMode'."
                }

                return $health
            }
        }

        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Adapter did not become healthy at $BaseUrl within $TimeoutSeconds seconds."
}

$existingOwners = @(Get-PortOwners $adapterPort)
if ($existingOwners.Count -gt 0) {
    $ownerDescriptions = @(Get-PortOwnerDescriptions $existingOwners) -join '; '
    throw "Port $adapterPort is already in use by $ownerDescriptions. Choose another -AdapterUrl or stop the conflicting process."
}

$workingDirectory = Split-Path -Parent $DllPath
$environmentVariables = @{
    'ASPNETCORE_ENVIRONMENT' = 'PhysicalAcceptance'
    'Adapter__RunMode' = $ExpectedRunMode
    'Agv__Tcp__Host' = $ControllerHost.Trim()
    'Agv__Tcp__StatusPort' = [string]$AgvStatusPort
    'Agv__Tcp__CommandPort' = [string]$AgvCommandPort
    'Agv__Tcp__ControlPort' = [string]$AgvControlPort
    'Agv__Tcp__OtherPort' = [string]$AgvOtherPort
    'Agv__Tcp__PushPort' = [string]$AgvPushPort
    'Agv__Tcp__AcquireControl' = if ($ExpectedRunMode -eq 'standard') { 'true' } else { 'false' }
    'Agv__Tcp__EnablePush' = 'false'
    'Profile__features__enableAutomaticDispatch' = 'false'
    'Profile__features__enableFieldNavigationAcceptance' = if ($EnableFieldNavigationAcceptance) { 'true' } else { 'false' }
    'Profile__features__enableTaskCancellation' = 'false'
    # The AUBO module is opt-in for this launcher. It remains read-only unless the
    # caller supplies EnableAuboControl + ConfirmPhysical + an exact allowlist.
    'Devices__AuboArm__Enabled' = if ($EnableAuboReadOnly) { 'true' } else { 'false' }
    'Devices__AuboArm__ControlEnabled' = if ($EnableAuboControl) { 'true' } else { 'false' }
    'Devices__AuboArm__Host' = if ($EnableAuboReadOnly) { $AuboHost.Trim() } else { '' }
    'Devices__AuboArm__Port' = [string]$AuboPort
    'Devices__AuboArm__RobotName' = $AuboRobotName.Trim()
    'Devices__AuboArm__ProgramCatalogMaxSlots' = '100'
    'ConnectionStrings__Adapter' = "Data Source=$AdapterDatabasePath"
}

if ($EnableAuboReadOnly) {
    if ($AuboAllowedProgramNames.Count -gt 0) {
        $environmentVariables['Devices__AuboArm__AllowedProgramNames__0'] = $AuboAllowedProgramNames[0]
        for ($index = 1; $index -lt $AuboAllowedProgramNames.Count; $index++) {
            $environmentVariables["Devices__AuboArm__AllowedProgramNames__${index}"] = $AuboAllowedProgramNames[$index]
        }
    }
}

$process = $null
try {
    $arguments = '"{0}" --urls {1} --environment PhysicalAcceptance' -f $DllPath, $AdapterUrl
    $previousEnvironment = @{}
    foreach ($entry in $environmentVariables.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
    }

    try {
        $process = Start-Process `
            -FilePath $dotnet `
            -ArgumentList $arguments `
            -WorkingDirectory $workingDirectory `
            -WindowStyle Hidden `
            -RedirectStandardOutput $standardOutputPath `
            -RedirectStandardError $standardErrorPath `
            -PassThru
    }
    finally {
        foreach ($entry in $previousEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
    if ($null -eq $process) {
        throw 'Adapter process could not be started.'
    }

    Wait-Listening -Port $adapterPort -TimeoutSeconds $StartupTimeoutSeconds -Process $process
    $health = Wait-Health -BaseUrl $AdapterUrl -RunMode $ExpectedRunMode -TimeoutSeconds $StartupTimeoutSeconds -Process $process

    $serviceState = [pscustomobject]@{
        Name = 'Physical acceptance Adapter'
        Port = $adapterPort
        Url = $AdapterUrl
        ProcessId = [int]$process.Id
        Executable = $dotnet
        ProjectRoot = $workingDirectory
        Dll = $DllPath
        WorkingDirectory = $workingDirectory
        Configuration = 'Release'
        DatabasePath = $AdapterDatabasePath
        StartedAtUtc = [DateTime]::UtcNow.ToString('O')
        ExpectedRunMode = $ExpectedRunMode
        AgvHost = $ControllerHost.Trim()
        AgvStatusPort = $AgvStatusPort
        AgvCommandPort = $AgvCommandPort
        AgvControlPort = $AgvControlPort
        AgvOtherPort = $AgvOtherPort
        AgvPushPort = $AgvPushPort
        AuboReadOnly = [bool]$EnableAuboReadOnly
        AuboControl = [bool]$EnableAuboControl
        AuboAllowedProgramNames = @($AuboAllowedProgramNames)
        AuboHost = if ($EnableAuboReadOnly) { $AuboHost.Trim() } else { $null }
        AuboPort = if ($EnableAuboReadOnly) { $AuboPort } else { $null }
        AuboRobotName = if ($EnableAuboReadOnly) { $AuboRobotName.Trim() } else { $null }
        StandardOutputPath = $standardOutputPath
        StandardErrorPath = $standardErrorPath
    }
    $state = [pscustomobject]@{
        SchemaVersion = 1
        RunId = $RunId
        CreatedAtUtc = [DateTime]::UtcNow.ToString('O')
        StatePath = $StatePath
        IsolatedStores = $true
        Services = @($serviceState)
    }
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $StatePath -Encoding UTF8

    Write-Host "Adapter: $AdapterUrl"
    Write-Host "Health: service=$($health.service), status=$($health.status), runMode=$($health.runMode)"
    Write-Host "PID: $($process.Id)"
    Write-Host "Run ID: $RunId"
    Write-Host "Service state saved to: $StatePath"
    Write-Host "Adapter logs: $LogDirectory"
    Write-Host ("Stop this Adapter with: .\scripts\stop-local.ps1 -StatePath `"{0}`"" -f $StatePath)
}
catch {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
        Remove-Item -LiteralPath $StatePath -Force
    }
    if (-not $logDirectoryPreExisted -and (Test-Path -LiteralPath $LogDirectory -PathType Container)) {
        Remove-Item -LiteralPath $LogDirectory -Recurse -Force
    }

    throw
}
