[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$ControllerHost,

    [string]$AdapterUrl = 'http://127.0.0.1:5141',
    [string]$ArtifactRoot,
    [string]$RunId,
    [string]$DllPath,
    [ValidateRange(1, 120)]
    [int]$StartupTimeoutSeconds = 30,
    [ValidateRange(1, 300)]
    [int]$RequestTimeoutSeconds = 45,
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
    [switch]$EnableAuboReadOnly,
    [string]$AuboHost,
    [ValidateRange(1, 65535)]
    [int]$AuboPort = 9012,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')]
    [string]$AuboRobotName = 'rob1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ControllerHost)) {
    throw 'ControllerHost is required and must be supplied from the current approved site configuration.'
}
if ($EnableAuboReadOnly -and [string]::IsNullOrWhiteSpace($AuboHost)) {
    throw 'AuboHost is required when EnableAuboReadOnly is specified.'
}
if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $PSScriptRoot '..\artifacts\physical-acceptance'
}
if ([string]::IsNullOrWhiteSpace($RunId)) {
    $RunId = 'physical-readonly-{0}-{1}' -f `
        (Get-Date -Format 'yyyyMMdd-HHmmss'),
        ([Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$RunId = $RunId.Trim()
if ($RunId -notmatch '^[A-Za-z0-9._-]+$') {
    throw 'RunId may contain only letters, digits, dot, underscore, and hyphen.'
}

try {
    $adapterEndpoint = [Uri]$AdapterUrl
}
catch {
    throw "AdapterUrl '$AdapterUrl' is invalid."
}
if (-not $adapterEndpoint.IsAbsoluteUri -or
    -not $adapterEndpoint.IsLoopback -or
    $adapterEndpoint.Scheme -ne 'http' -or
    $adapterEndpoint.AbsolutePath -ne '/' -or
    -not [string]::IsNullOrWhiteSpace($adapterEndpoint.Query)) {
    throw "AdapterUrl '$AdapterUrl' must be an absolute loopback HTTP URL without a path or query string."
}
$AdapterUrl = $AdapterUrl.TrimEnd('/')

$artifactParent = [IO.Path]::GetFullPath($ArtifactRoot)
if (-not (Test-Path -LiteralPath $artifactParent -PathType Container)) {
    New-Item -ItemType Directory -Path $artifactParent -Force | Out-Null
}
$root = Join-Path $artifactParent $RunId
if (Test-Path -LiteralPath $root) {
    throw "Evidence directory already exists at '$root'; use a new RunId so evidence cannot be overwritten."
}
New-Item -ItemType Directory -Path $root | Out-Null

$evidencePath = Join-Path $root 'physical-readonly-evidence.json'
$databasePath = Join-Path $root 'adapter.db'
$statePath = Join-Path $root 'adapter-state.json'
$logDirectory = Join-Path $root 'adapter-logs'
New-Item -ItemType Directory -Path $logDirectory | Out-Null
$startScript = Join-Path $PSScriptRoot 'start-physical-acceptance-adapter.ps1'
$stopScript = Join-Path $PSScriptRoot 'stop-local.ps1'
$currentStage = 'initialize'
$ownedProcessId = $null

function Invoke-ReadOnlyJsonGet {
    param(
        [Parameter(Mandatory = $true)] [string]$BaseUrl,
        [Parameter(Mandatory = $true)] [string]$Path,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds
    )

    $started = [DateTimeOffset]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-WebRequest `
            -Uri "$BaseUrl$Path" `
            -Method Get `
            -UseBasicParsing `
            -TimeoutSec $TimeoutSeconds
        $value = $response.Content | ConvertFrom-Json
        return [ordered]@{
            ok = $true
            path = $Path
            method = 'GET'
            httpStatus = [int]$response.StatusCode
            observedAtUtc = $started
            elapsedMs = [long]$stopwatch.ElapsedMilliseconds
            value = $value
        }
    }
    catch {
        $statusCode = $null
        $responseBody = $null
        if ($null -ne $_.Exception.Response) {
            try { $statusCode = [int]$_.Exception.Response.StatusCode } catch { }
            try {
                $reader = [IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
                try { $responseBody = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
            catch { }
        }
        return [ordered]@{
            ok = $false
            path = $Path
            method = 'GET'
            httpStatus = $statusCode
            observedAtUtc = $started
            elapsedMs = [long]$stopwatch.ElapsedMilliseconds
            error = $_.Exception.Message
            responseBody = $responseBody
        }
    }
    finally {
        $stopwatch.Stop()
    }
}

function Assert-ReadSucceeded {
    param(
        [Parameter(Mandatory = $true)] [object]$Read,
        [Parameter(Mandatory = $true)] [string]$Description
    )

    if (-not [bool]$Read.ok) {
        $statusText = if ($null -eq $Read.httpStatus) { 'no HTTP status' } else { "HTTP $($Read.httpStatus)" }
        throw "$Description failed ($statusText): $($Read.error)"
    }
}

function ConvertTo-PlainEvidenceData {
    param([AllowNull()] [object]$InputObject)

    if ($null -eq $InputObject) { return $null }
    if ($InputObject -is [string] -or
        $InputObject -is [bool] -or
        $InputObject -is [byte] -or
        $InputObject -is [sbyte] -or
        $InputObject -is [int16] -or
        $InputObject -is [uint16] -or
        $InputObject -is [int32] -or
        $InputObject -is [uint32] -or
        $InputObject -is [int64] -or
        $InputObject -is [uint64] -or
        $InputObject -is [single] -or
        $InputObject -is [double] -or
        $InputObject -is [decimal]) {
        return $InputObject
    }
    if ($InputObject -is [DateTime] -or $InputObject -is [DateTimeOffset]) {
        return $InputObject.ToString('o')
    }
    if ($InputObject -is [Enum]) {
        return $InputObject.ToString()
    }
    if ($InputObject -is [Collections.IDictionary]) {
        $dictionary = [Collections.Generic.Dictionary[string, object]]::new()
        foreach ($key in $InputObject.Keys) {
            $dictionary[[string]$key] = ConvertTo-PlainEvidenceData $InputObject[$key]
        }
        return $dictionary
    }
    if ($InputObject -is [Collections.IEnumerable]) {
        $list = [Collections.Generic.List[object]]::new()
        foreach ($item in $InputObject) {
            $list.Add((ConvertTo-PlainEvidenceData $item))
        }
        return ,$list
    }

    $properties = [Collections.Generic.Dictionary[string, object]]::new()
    foreach ($property in $InputObject.PSObject.Properties) {
        # ConvertFrom-Json objects expose payload fields as NoteProperty. Do
        # not reflect arbitrary .NET Property/ParameterizedProperty members;
        # those may contain PowerShell metadata cycles.
        if ($property.MemberType -eq 'NoteProperty') {
            $properties[$property.Name] = ConvertTo-PlainEvidenceData $property.Value
        }
    }
    if ($properties.Count -eq 0) {
        return [string]$InputObject
    }
    return $properties
}

function ConvertTo-EvidenceJson {
    param([Parameter(Mandatory = $true)] [object]$InputObject)

    Add-Type -AssemblyName System.Web.Extensions
    $serializer = [Web.Script.Serialization.JavaScriptSerializer]::new()
    $plainData = ConvertTo-PlainEvidenceData $InputObject
    $builder = [Text.StringBuilder]::new()
    Write-EvidenceJsonValue -InputObject $plainData -Builder $builder -StringSerializer $serializer
    return $builder.ToString()
}

function Write-EvidenceJsonValue {
    param(
        [AllowNull()] [object]$InputObject,
        [Parameter(Mandatory = $true)] [Text.StringBuilder]$Builder,
        [Parameter(Mandatory = $true)] [Web.Script.Serialization.JavaScriptSerializer]$StringSerializer
    )

    if ($null -eq $InputObject) {
        [void]$Builder.Append('null')
        return
    }
    if ($InputObject -is [string]) {
        [void]$Builder.Append($StringSerializer.Serialize([string]$InputObject))
        return
    }
    if ($InputObject -is [bool]) {
        [void]$Builder.Append($(if ([bool]$InputObject) { 'true' } else { 'false' }))
        return
    }
    if ($InputObject -is [byte] -or
        $InputObject -is [sbyte] -or
        $InputObject -is [int16] -or
        $InputObject -is [uint16] -or
        $InputObject -is [int32] -or
        $InputObject -is [uint32] -or
        $InputObject -is [int64] -or
        $InputObject -is [uint64] -or
        $InputObject -is [single] -or
        $InputObject -is [double] -or
        $InputObject -is [decimal]) {
        if (($InputObject -is [single] -or $InputObject -is [double]) -and
            ([double]::IsNaN([double]$InputObject) -or [double]::IsInfinity([double]$InputObject))) {
            throw 'Evidence contains a non-finite number that cannot be represented in JSON.'
        }
        [void]$Builder.Append([Convert]::ToString($InputObject, [Globalization.CultureInfo]::InvariantCulture))
        return
    }
    if ($InputObject -is [Collections.IDictionary]) {
        [void]$Builder.Append('{')
        $first = $true
        foreach ($key in $InputObject.Keys) {
            if (-not $first) { [void]$Builder.Append(',') }
            $first = $false
            [void]$Builder.Append($StringSerializer.Serialize([string]$key))
            [void]$Builder.Append(':')
            Write-EvidenceJsonValue `
                -InputObject $InputObject[$key] `
                -Builder $Builder `
                -StringSerializer $StringSerializer
        }
        [void]$Builder.Append('}')
        return
    }
    if ($InputObject -is [Collections.IEnumerable]) {
        [void]$Builder.Append('[')
        $first = $true
        foreach ($item in $InputObject) {
            if (-not $first) { [void]$Builder.Append(',') }
            $first = $false
            Write-EvidenceJsonValue `
                -InputObject $item `
                -Builder $Builder `
                -StringSerializer $StringSerializer
        }
        [void]$Builder.Append(']')
        return
    }

    throw "Evidence projection retained unsupported type '$($InputObject.GetType().FullName)'."
}

$result = [ordered]@{
    schema = 'mes.physical-read-only-preflight-session/1.0'
    runId = $RunId
    startedAtUtc = [DateTimeOffset]::UtcNow
    endedAtUtc = $null
    status = 'ABORTED'
    failureStage = $null
    targets = [ordered]@{
        adapterUrl = $AdapterUrl
        controllerHost = $ControllerHost.Trim()
        agvStatusPort = $AgvStatusPort
        agvCommandPortConfiguredButNotAccessed = $AgvCommandPort
        agvControlReadPort = $AgvControlPort
        agvOtherPortConfiguredButNotAccessed = $AgvOtherPort
        agvPushPortConfiguredButNotAccessed = $AgvPushPort
        auboEnabled = [bool]$EnableAuboReadOnly
        auboHost = if ($EnableAuboReadOnly) { $AuboHost.Trim() } else { $null }
        auboPort = if ($EnableAuboReadOnly) { $AuboPort } else { $null }
        auboRobotName = if ($EnableAuboReadOnly) { $AuboRobotName } else { $null }
    }
    adapter = [ordered]@{
        started = $false
        processId = $null
        statePath = $statePath
        databasePath = $databasePath
        logDirectory = $logDirectory
        launcherOutput = $null
        stdoutTail = $null
        stderrTail = $null
        health = $null
        devices = $null
    }
    agv = [ordered]@{
        preflight = $null
    }
    aubo = [ordered]@{
        requested = [bool]$EnableAuboReadOnly
        status = $null
        readiness = $null
        programCatalogFresh = $null
    }
    safetyBoundary = [ordered]@{
        requestMethodsUsed = @('GET')
        writesAttempted = $false
        agvControlAcquisitionAttempted = $false
        agvControlReleaseAttempted = $false
        agvCommandPortAttempted = $false
        agvNavigationAttempted = $false
        agvPauseResumeCancelAttempted = $false
        agvPushConfigurationAttempted = $false
        auboControlMethodsAttempted = $false
        programLoadAttempted = $false
        programStartAttempted = $false
        programStopAttempted = $false
        modbusAttempted = $false
    }
    decision = $null
    cleanup = [ordered]@{
        attempted = $false
        success = $true
        stopExitCode = $null
        processStillRunning = $false
        stateFileStillPresent = $false
        output = $null
        error = $null
    }
    errors = @()
}

try {
    $currentStage = 'start_adapter'
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Starting isolated read-only Adapter."
    $startParameters = @{
        ExpectedRunMode = 'read-only-preflight'
        ControllerHost = $ControllerHost.Trim()
        AdapterDatabasePath = $databasePath
        AdapterUrl = $AdapterUrl
        StatePath = $statePath
        LogDirectory = $logDirectory
        RunId = $RunId
        StartupTimeoutSeconds = $StartupTimeoutSeconds
        AgvStatusPort = $AgvStatusPort
        AgvCommandPort = $AgvCommandPort
        AgvControlPort = $AgvControlPort
        AgvOtherPort = $AgvOtherPort
        AgvPushPort = $AgvPushPort
    }
    if (-not [string]::IsNullOrWhiteSpace($DllPath)) {
        $startParameters.DllPath = [IO.Path]::GetFullPath($DllPath)
    }
    if ($EnableAuboReadOnly) {
        $startParameters.EnableAuboReadOnly = $true
        $startParameters.AuboHost = $AuboHost.Trim()
        $startParameters.AuboPort = $AuboPort
        $startParameters.AuboRobotName = $AuboRobotName
    }

    $result.adapter.launcherOutput = (& $startScript @startParameters 2>&1 | Out-String).Trim()
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw "Adapter launcher returned without creating its state file at '$statePath'."
    }
    $state = Get-Content -Raw -Encoding UTF8 -LiteralPath $statePath | ConvertFrom-Json
    $service = @($state.Services) | Select-Object -First 1
    if ($null -eq $service) {
        throw 'Adapter state file did not contain an owned service record.'
    }
    $ownedProcessId = [int]$service.ProcessId
    $result.adapter.started = $true
    $result.adapter.processId = $ownedProcessId

    $currentStage = 'verify_health'
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Capturing Adapter health."
    $result.adapter.health = Invoke-ReadOnlyJsonGet `
        -BaseUrl $AdapterUrl `
        -Path '/health' `
        -TimeoutSeconds $RequestTimeoutSeconds
    Assert-ReadSucceeded -Read $result.adapter.health -Description 'Adapter health read'
    $healthValue = $result.adapter.health.value
    if ([string]$healthValue.service -ne 'adapter' -or
        [string]$healthValue.status -ne 'ok' -or
        [string]$healthValue.runMode -ne 'read-only-preflight' -or
        [string]$healthValue.driver -ne 'vendor-tcp') {
        throw 'Adapter health identity did not match adapter/ok/read-only-preflight/vendor-tcp.'
    }

    $currentStage = 'read_device_catalog'
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Capturing Adapter device catalog."
    $result.adapter.devices = Invoke-ReadOnlyJsonGet `
        -BaseUrl $AdapterUrl `
        -Path '/api/adapter/devices' `
        -TimeoutSeconds $RequestTimeoutSeconds
    Assert-ReadSucceeded -Read $result.adapter.devices -Description 'Adapter device catalog read'

    $currentStage = 'read_agv_preflight'
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Capturing authoritative AGV preflight."
    $result.agv.preflight = Invoke-ReadOnlyJsonGet `
        -BaseUrl $AdapterUrl `
        -Path '/physical/preflight' `
        -TimeoutSeconds $RequestTimeoutSeconds
    Assert-ReadSucceeded -Read $result.agv.preflight -Description 'AGV physical preflight read'
    $preflight = $result.agv.preflight.value
    if ($null -eq $preflight.PSObject.Properties['dispatchPermitted']) {
        throw 'AGV physical preflight response did not contain dispatchPermitted.'
    }
    if ([bool]$preflight.dispatchPermitted) {
        throw 'Read-only preflight unexpectedly reported dispatchPermitted=true.'
    }

    if ($EnableAuboReadOnly) {
        $currentStage = 'read_aubo_status'
        Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Capturing AUBO status."
        $result.aubo.status = Invoke-ReadOnlyJsonGet `
            -BaseUrl $AdapterUrl `
            -Path '/api/robot-arms/ARM-01/status' `
            -TimeoutSeconds $RequestTimeoutSeconds
        Assert-ReadSucceeded -Read $result.aubo.status -Description 'AUBO status read'

        $currentStage = 'read_aubo_readiness'
        Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Capturing AUBO readiness."
        $result.aubo.readiness = Invoke-ReadOnlyJsonGet `
            -BaseUrl $AdapterUrl `
            -Path '/api/robot-arms/ARM-01/readiness' `
            -TimeoutSeconds $RequestTimeoutSeconds
        Assert-ReadSucceeded -Read $result.aubo.readiness -Description 'AUBO readiness read'

        $currentStage = 'read_aubo_program_catalog'
        Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Capturing fresh AUBO program catalog."
        $result.aubo.programCatalogFresh = Invoke-ReadOnlyJsonGet `
            -BaseUrl $AdapterUrl `
            -Path '/api/robot-arms/ARM-01/programs?fresh=true' `
            -TimeoutSeconds $RequestTimeoutSeconds
        Assert-ReadSucceeded -Read $result.aubo.programCatalogFresh -Description 'AUBO program catalog read'
    }

    $blockingReasons = @($preflight.blockingReasons)
    $result.decision = [ordered]@{
        dispatchPermitted = $false
        blockingReasons = $blockingReasons
        recommendation = 'NO-GO_FOR_PHYSICAL_DISPATCH'
        reason = 'This session is read-only evidence collection and never authorizes control acquisition or motion.'
    }
    $result.status = 'READ-ONLY-PREFLIGHT-COMPLETED'
}
catch {
    $result.status = 'FAILED'
    $result.failureStage = $currentStage
    $result.errors += $_.Exception.Message
}
finally {
    $currentStage = 'stop_adapter'
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Stopping the Adapter owned by this run."
    if ((Test-Path -LiteralPath $statePath -PathType Leaf) -or $null -ne $ownedProcessId) {
        $result.cleanup.attempted = $true
        try {
            if (Test-Path -LiteralPath $statePath -PathType Leaf) {
                $stopOutput = (& $stopScript -StatePath $statePath 2>&1 | Out-String).Trim()
                $result.cleanup.output = $stopOutput
                $result.cleanup.stopExitCode = 0
                Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Owned Adapter stop command returned."
            }
            else {
                $result.cleanup.stopExitCode = $null
                $result.cleanup.error = 'Owned process id was observed, but the state file was missing before cleanup.'
            }
        }
        catch {
            $result.cleanup.error = $_.Exception.Message
        }

        $result.cleanup.stateFileStillPresent = Test-Path -LiteralPath $statePath -PathType Leaf
        $result.cleanup.processStillRunning = $null -ne $ownedProcessId -and
            $null -ne (Get-Process -Id $ownedProcessId -ErrorAction SilentlyContinue)
        $result.cleanup.success = (-not $result.cleanup.stateFileStillPresent -and
            -not $result.cleanup.processStillRunning -and
            ($null -eq $result.cleanup.stopExitCode -or [int]$result.cleanup.stopExitCode -eq 0) -and
            [string]::IsNullOrWhiteSpace([string]$result.cleanup.error))
        Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Adapter cleanup state verified."
    }

    if (-not $result.cleanup.success) {
        $cleanupMessage = if ([string]::IsNullOrWhiteSpace([string]$result.cleanup.error)) {
            'Adapter cleanup did not complete; inspect the recorded state, PID, port, and stop output.'
        }
        else {
            "Adapter cleanup failed: $($result.cleanup.error)"
        }
        $result.errors += $cleanupMessage
        if ($result.status -eq 'READ-ONLY-PREFLIGHT-COMPLETED') {
            $result.status = 'CLEANUP-FAILED'
            $result.failureStage = 'stop_adapter'
        }
    }

    $stdoutPath = Join-Path $logDirectory 'adapter.stdout.log'
    $stderrPath = Join-Path $logDirectory 'adapter.stderr.log'
    if (Test-Path -LiteralPath $stdoutPath -PathType Leaf) {
        $result.adapter.stdoutTail = @(Get-Content -Encoding UTF8 -LiteralPath $stdoutPath -Tail 100)
    }
    if (Test-Path -LiteralPath $stderrPath -PathType Leaf) {
        $result.adapter.stderrTail = @(Get-Content -Encoding UTF8 -LiteralPath $stderrPath -Tail 100)
    }
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Adapter log tails captured."

    $result.endedAtUtc = [DateTimeOffset]::UtcNow
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Serializing read-only evidence."
    # Avoid Windows PowerShell 5.1's very slow deep reflection of PSObject
    # members by projecting the evidence into plain dictionaries and lists.
    $json = ConvertTo-EvidenceJson -InputObject $result
    Write-Output "[$([DateTimeOffset]::Now.ToString('o'))] Writing read-only evidence."
    $json | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    Write-Output $json
    Write-Output "Evidence: $evidencePath"
}

if ($result.status -ne 'READ-ONLY-PREFLIGHT-COMPLETED') {
    exit 2
}
