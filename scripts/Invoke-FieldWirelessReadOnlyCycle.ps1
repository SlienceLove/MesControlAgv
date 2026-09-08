[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$StaticAddress,

    [Parameter(Mandatory = $true)]
    [ValidateRange(1, 32)]
    [int]$PrefixLength,

    [string]$InterfaceAlias = 'WLAN',
    [string]$AmrProfileName = 'AMR',
    [string]$ReturnProfileName = 'SHINE',
    [ValidateRange(5, 120)]
    [int]$ConnectTimeoutSeconds = 30,
    [ValidateRange(100, 60000)]
    [int]$DeviceTimeoutMs = 5000,
    [string]$ArtifactRoot,

    # Device checks are opt-in. Hosts have no defaults so an old address cannot
    # be silently reused at another site.
    [switch]$VerifyDevices,
    [string]$AgvHost,
    [ValidateRange(1, 65535)]
    [int]$AgvStatusPort = 19204,
    [string]$AuboHost,
    [ValidateRange(1, 65535)]
    [int]$AuboPort = 9012,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')]
    [string]$RobotName = 'rob1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $PSScriptRoot '..\artifacts\physical-acceptance'
}
$ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must run in an elevated local PowerShell window.'
    }
}

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Quote-ProcessArgument {
    param([AllowEmptyString()] [string]$Value)

    # All current arguments are simple values. Quoting also preserves a caller
    # supplied artifact path containing spaces when UAC relaunches the script.
    return '"' + $Value.Replace('"', '\"') + '"'
}

function Start-ElevatedSelf {
    $arguments = New-Object System.Collections.Generic.List[string]
    $arguments.Add('-NoProfile')
    $arguments.Add('-ExecutionPolicy')
    $arguments.Add('Bypass')
    $arguments.Add('-File')
    $arguments.Add((Quote-ProcessArgument $PSCommandPath))
    $commonParameters = [ordered]@{
        '-StaticAddress' = $StaticAddress
        '-PrefixLength' = [string]$PrefixLength
        '-InterfaceAlias' = $InterfaceAlias
        '-AmrProfileName' = $AmrProfileName
        '-ReturnProfileName' = $ReturnProfileName
        '-ConnectTimeoutSeconds' = [string]$ConnectTimeoutSeconds
        '-DeviceTimeoutMs' = [string]$DeviceTimeoutMs
        '-ArtifactRoot' = $ArtifactRoot
    }
    foreach ($name in $commonParameters.Keys) {
        $arguments.Add($name)
        $arguments.Add((Quote-ProcessArgument ([string]$commonParameters[$name])))
    }
    if ($VerifyDevices) {
        $deviceParameters = [ordered]@{
            '-AgvHost' = $AgvHost
            '-AgvStatusPort' = [string]$AgvStatusPort
            '-AuboHost' = $AuboHost
            '-AuboPort' = [string]$AuboPort
            '-RobotName' = $RobotName
        }
        foreach ($name in $deviceParameters.Keys) {
            $arguments.Add($name)
            $arguments.Add((Quote-ProcessArgument ([string]$deviceParameters[$name])))
        }
        $arguments.Add('-VerifyDevices')
    }

    try {
        $child = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ($arguments -join ' ') -Wait -PassThru
        exit $child.ExitCode
    }
    catch {
        throw "Unable to start the elevated PowerShell child process: $($_.Exception.Message)"
    }
}

function Get-ProfileName {
    param([Parameter(Mandatory = $true)] [string]$Alias)

    # netsh can list multiple WLAN interfaces. Track the interface name so a
    # second adapter (for example SHINE on WLAN1) cannot be mistaken for AMR.
    # Chinese Windows labels the profile field as "配置文件"; SSID is retained
    # as a locale-independent fallback when the profile field is absent.
    $lines = @(netsh wlan show interfaces 2>$null)
    $currentAlias = $null
    $ssidName = $null
    $profileName = $null
    foreach ($line in $lines) {
        if ([string]$line -match '^\s*(?:Name|名称)\s*:\s*(?<name>.+?)\s*$') {
            if ([string]$currentAlias -eq $Alias) {
                if ([string]::IsNullOrWhiteSpace($profileName)) { return $ssidName }
                return $profileName
            }

            $currentAlias = $Matches.name.Trim()
            $ssidName = $null
            $profileName = $null
            continue
        }
        if ([string]$currentAlias -ne $Alias) { continue }
        if ([string]$line -match '^\s*(?:Profile|配置文件)\s*:\s*(?<name>.+?)\s*$') {
            $profileName = $Matches.name.Trim()
            continue
        }
        if ([string]$line -match '^\s*SSID\s*:\s*(?<name>.+?)\s*$') {
            $ssidName = $Matches.name.Trim()
        }
    }

    if ([string]$currentAlias -eq $Alias) {
        if ([string]::IsNullOrWhiteSpace($profileName)) { return $ssidName }
        return $profileName
    }
    return $null
}

function Get-NetworkPrefix {
    param(
        [Parameter(Mandatory = $true)] [string]$Address,
        [Parameter(Mandatory = $true)] [int]$Prefix
    )

    $ipBytes = ([Net.IPAddress]::Parse($Address)).GetAddressBytes()
    if ($ipBytes.Length -ne 4) { throw "Static address '$Address' must be IPv4." }
    $networkBytes = New-Object byte[] 4
    $fullBytes = [Math]::Floor($Prefix / 8)
    $remaining = $Prefix % 8
    for ($index = 0; $index -lt 4; $index++) {
        $mask = if ($index -lt $fullBytes) {
            255
        }
        elseif ($index -eq $fullBytes -and $remaining -gt 0) {
            [int](256 - [Math]::Pow(2, 8 - $remaining))
        }
        else {
            0
        }
        $networkBytes[$index] = [byte]($ipBytes[$index] -band $mask)
    }
    return "$([Net.IPAddress]::new($networkBytes))/$Prefix"
}

function Get-NetworkSnapshot {
    param([Parameter(Mandatory = $true)] [string]$Alias)

    $adapter = Get-NetAdapter -Name $Alias -ErrorAction Stop
    $addresses = @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' } |
        Select-Object IPAddress,PrefixLength,AddressState,Type,SkipAsSource)
    $routes = @(Get-NetRoute -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Select-Object DestinationPrefix,NextHop,RouteMetric,State)
    $profileName = Get-ProfileName -Alias $Alias

    return [ordered]@{
        observedAt = [DateTimeOffset]::Now
        interfaceAlias = $Alias
        interfaceIndex = [int]$adapter.ifIndex
        adapterStatus = [string]$adapter.Status
        mediaConnectionState = [string]$adapter.MediaConnectionState
        macAddress = [string]$adapter.MacAddress
        profile = $profileName
        ipv4 = @($addresses)
        routes = @($routes)
    }
}

function Wait-ForProfile {
    param(
        [Parameter(Mandatory = $true)] [string]$Alias,
        [Parameter(Mandatory = $true)] [string]$ExpectedProfile,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $adapter = Get-NetAdapter -Name $Alias -ErrorAction SilentlyContinue
        $profileName = Get-ProfileName -Alias $Alias
        if ($null -ne $adapter -and [string]$adapter.Status -eq 'Up' -and
            [string]$profileName -eq $ExpectedProfile) {
            return
        }
        Start-Sleep -Seconds 1
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "Timed out waiting for WLAN profile '$ExpectedProfile' on '$Alias'. Current profile: '$(Get-ProfileName -Alias $Alias)'"
}

function Connect-WlanProfile {
    param(
        [Parameter(Mandatory = $true)] [string]$Alias,
        [Parameter(Mandatory = $true)] [string]$ProfileName,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds
    )

    $output = netsh wlan connect name="$ProfileName" interface="$Alias" 2>&1 | Out-String
    Wait-ForProfile -Alias $Alias -ExpectedProfile $ProfileName -TimeoutSeconds $TimeoutSeconds
    return $output.Trim()
}

function Test-StaticAddressConflict {
    param(
        [Parameter(Mandatory = $true)] [string]$Alias,
        [Parameter(Mandatory = $true)] [string]$Address
    )

    $adapter = Get-NetAdapter -Name $Alias -ErrorAction Stop
    $local = @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { [string]$_.IPAddress -eq $Address })
    if ($local.Count -gt 0) {
        return [ordered]@{ address = $Address; conflict = $false; alreadyLocal = $true; pingExitCode = $null; neighborMac = $null }
    }

    $ping = ping.exe -n 1 -w 1000 $Address 2>&1
    $pingExitCode = $LASTEXITCODE
    $neighbor = Get-NetNeighbor -InterfaceIndex $adapter.ifIndex -IPAddress $Address -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Select-Object -First 1
    $mac = if ($null -eq $neighbor) { $null } else { [string]$neighbor.LinkLayerAddress }
    $hasMac = -not [string]::IsNullOrWhiteSpace($mac) -and $mac -notmatch '^(00-){5}00$'
    return [ordered]@{
        address = $Address
        conflict = ($pingExitCode -eq 0 -or $hasMac)
        alreadyLocal = $false
        pingExitCode = $pingExitCode
        neighborMac = $mac
        note = 'No response is not proof of absence; Windows duplicate-address detection is checked after assignment.'
    }
}

function Set-StaticControlAddress {
    param(
        [Parameter(Mandatory = $true)] [string]$Alias,
        [Parameter(Mandatory = $true)] [string]$Address,
        [Parameter(Mandatory = $true)] [int]$Prefix
    )

    $adapter = Get-NetAdapter -Name $Alias -ErrorAction Stop
    Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -Dhcp Disabled

    $existing = @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' })
    foreach ($item in $existing) {
        Remove-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $item.IPAddress -Confirm:$false
    }

    $defaultRoutes = @(Get-NetRoute -InterfaceIndex $adapter.ifIndex -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue)
    foreach ($route in $defaultRoutes) {
        Remove-NetRoute -InterfaceIndex $adapter.ifIndex -DestinationPrefix '0.0.0.0/0' -NextHop $route.NextHop -Confirm:$false
    }

    New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $Address -PrefixLength $Prefix -AddressFamily IPv4 | Out-Null
    Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ResetServerAddresses
}

function Restore-ConversationNetwork {
    param(
        [Parameter(Mandatory = $true)] [string]$Alias,
        [Parameter(Mandatory = $true)] [string]$ReturnProfile,
        [Parameter(Mandatory = $true)] [string]$StaticAddress,
        [Parameter(Mandatory = $true)] [int]$TimeoutSeconds
    )

    $restore = [ordered]@{ attempted = $true; success = $false; errors = @() }
    try {
        # Association does not require an IP, so connect first, then restore DHCP.
        $restore.connectOutput = Connect-WlanProfile -Alias $Alias -ProfileName $ReturnProfile -TimeoutSeconds $TimeoutSeconds
    }
    catch {
        $restore.errors += $_.Exception.Message
    }
    try {
        # Keep cleanup independent from association: even if SHINE is temporarily
        # unavailable, do not leave the adapter on the field static address.
        $adapter = Get-NetAdapter -Name $Alias -ErrorAction Stop
        Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -Dhcp Enabled
        $static = @(Get-NetIPAddress -InterfaceIndex $adapter.ifIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { [string]$_.IPAddress -eq $StaticAddress })
        foreach ($item in $static) {
            Remove-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress $item.IPAddress -Confirm:$false
        }
        Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ResetServerAddresses
    }
    catch {
        $restore.errors += $_.Exception.Message
    }
    try {
        $restore.snapshot = Get-NetworkSnapshot -Alias $Alias
    }
    catch {
        $restore.errors += $_.Exception.Message
        $restore.snapshot = $null
    }
    $restore.success = ($restore.errors.Count -eq 0 -and
        $null -ne $restore.snapshot -and
        [string]$restore.snapshot.profile -eq $ReturnProfile -and
        [string]$restore.snapshot.adapterStatus -eq 'Up')
    return $restore
}

function Invoke-DeviceReadOnlyChecks {
    param(
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [string]$AgvTarget,
        [Parameter(Mandatory = $true)] [int]$AgvPort,
        [Parameter(Mandatory = $true)] [string]$AuboTarget,
        [Parameter(Mandatory = $true)] [int]$AuboRpcPort,
        [Parameter(Mandatory = $true)] [string]$AuboRobot,
        [Parameter(Mandatory = $true)] [int]$TimeoutMs
    )

    $agvTcp = Test-NetConnection -ComputerName $AgvTarget -Port $AgvPort -InformationLevel Detailed -WarningAction SilentlyContinue
    $auboTcp = Test-NetConnection -ComputerName $AuboTarget -Port $AuboRpcPort -InformationLevel Detailed -WarningAction SilentlyContinue
    $result = [ordered]@{
        observedAt = [DateTimeOffset]::Now
        writesAttempted = $false
        agvCommandPortAttempted = $false
        auboControlMethodsAttempted = $false
        modbusAttempted = $false
        targets = [ordered]@{
            agv = [ordered]@{ host = $AgvTarget; port = $AgvPort; tcpTestSucceeded = [bool]$agvTcp.TcpTestSucceeded; interfaceAlias = $agvTcp.InterfaceAlias; remoteAddress = $agvTcp.RemoteAddress }
            aubo = [ordered]@{ host = $AuboTarget; port = $AuboRpcPort; tcpTestSucceeded = [bool]$auboTcp.TcpTestSucceeded; interfaceAlias = $auboTcp.InterfaceAlias; remoteAddress = $auboTcp.RemoteAddress }
        }
        agvReadOnly = $null
        auboWebSocketReadOnly = $null
    }

    if ($agvTcp.TcpTestSucceeded) {
        $agvScript = Join-Path $PSScriptRoot 'Invoke-AgvIoApi.ps1'
        try {
            $agvText = (& $agvScript -Operation read -ControllerHost $AgvTarget -StatusPort $AgvPort -TimeoutMs $TimeoutMs 2>&1 | Out-String).Trim()
            $agvPath = Join-Path $Root 'agv-status-readonly.txt'
            $agvText | Set-Content -LiteralPath $agvPath -Encoding UTF8
            $result.agvReadOnly = [ordered]@{ success = $true; evidence = $agvPath; api = 1013; port = $AgvPort; commandPortAttempted = $false }
        }
        catch {
            $result.agvReadOnly = [ordered]@{ success = $false; error = $_.Exception.Message; api = 1013; port = $AgvPort; commandPortAttempted = $false }
        }
    }
    else {
        $result.agvReadOnly = [ordered]@{ success = $false; skipped = $true; reason = 'AGV status TCP port did not open'; api = 1013; port = $AgvPort; commandPortAttempted = $false }
    }

    if ($auboTcp.TcpTestSucceeded) {
        $auboScript = Join-Path $PSScriptRoot 'Invoke-AuboWsReadOnlyPreflight.ps1'
        $auboPath = Join-Path $Root 'aubo-ws-readonly-preflight.json'
        try {
            # Explicitly disable Modbus signal reads. No variable keys are supplied.
            & $auboScript -ControllerHost $AuboTarget -Port $AuboRpcPort -RobotName $AuboRobot -TimeoutMs $TimeoutMs -IncludeModbusSignals:$false -OutputPath $auboPath | Out-Null
            $result.auboWebSocketReadOnly = [ordered]@{ success = $true; evidence = $auboPath; port = $AuboRpcPort; modbusAttempted = $false; variableKeysSupplied = $false }
        }
        catch {
            $result.auboWebSocketReadOnly = [ordered]@{ success = $false; error = $_.Exception.Message; port = $AuboRpcPort; modbusAttempted = $false; variableKeysSupplied = $false }
        }
    }
    else {
        $result.auboWebSocketReadOnly = [ordered]@{ success = $false; skipped = $true; reason = 'AUBO WebSocket TCP port did not open'; port = $AuboRpcPort; modbusAttempted = $false; variableKeysSupplied = $false }
    }

    return $result
}

if (-not (Test-IsAdministrator)) {
    Start-ElevatedSelf
    exit 1
}
Assert-Administrator
if ($VerifyDevices -and ([string]::IsNullOrWhiteSpace($AgvHost) -or [string]::IsNullOrWhiteSpace($AuboHost))) {
    throw '-VerifyDevices requires explicit -AgvHost and -AuboHost values; no device host is guessed.'
}

$runId = 'wireless-cycle-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$root = Join-Path ([IO.Path]::GetFullPath($ArtifactRoot)) $runId
New-Item -ItemType Directory -Path $root -Force | Out-Null
$evidencePath = Join-Path $root 'cycle-evidence.json'
Write-Output ("[{0}] Wireless read-only cycle started. Evidence: {1}" -f (Get-Date -Format o), $root)
$result = [ordered]@{
    schema = 'mes.field-wireless-read-only-cycle/1.0'
    runId = $runId
    startedAt = [DateTimeOffset]::Now
    status = 'NO-GO'
    writesAttempted = $false
    deviceRequestsAttempted = $false
    interfaceAlias = $InterfaceAlias
    amrProfile = $AmrProfileName
    returnProfile = $ReturnProfileName
    requestedStaticAddress = "$StaticAddress/$PrefixLength"
    deviceVerificationRequested = [bool]$VerifyDevices
    preSwitch = $null
    amrConnection = $null
    staticConflictCheck = $null
    amrNetwork = $null
    deviceChecks = $null
    restore = $null
    errors = @()
}

try {
    Write-Output ("[{0}] Capturing pre-switch network state." -f (Get-Date -Format o))
    $result.preSwitch = Get-NetworkSnapshot -Alias $InterfaceAlias
    Write-Output ("[{0}] Connecting to WLAN profile '{1}'." -f (Get-Date -Format o), $AmrProfileName)
    $result.amrConnection = Connect-WlanProfile -Alias $InterfaceAlias -ProfileName $AmrProfileName -TimeoutSeconds $ConnectTimeoutSeconds
    Write-Output ("[{0}] AMR profile connected; reading address and route state." -f (Get-Date -Format o))
    $result.amrNetwork = Get-NetworkSnapshot -Alias $InterfaceAlias
    Write-Output ("[{0}] Checking static address conflict for {1}." -f (Get-Date -Format o), $StaticAddress)
    $conflict = Test-StaticAddressConflict -Alias $InterfaceAlias -Address $StaticAddress
    $result.staticConflictCheck = $conflict
    if ($conflict.conflict) {
        throw "Static address conflict detected for '$StaticAddress'; refusing to assign it."
    }

    Write-Output ("[{0}] Applying static address {1}/{2} without a default gateway." -f (Get-Date -Format o), $StaticAddress, $PrefixLength)
    Set-StaticControlAddress -Alias $InterfaceAlias -Address $StaticAddress -Prefix $PrefixLength
    $result.amrNetworkAfterStatic = Get-NetworkSnapshot -Alias $InterfaceAlias
    $expectedNetwork = Get-NetworkPrefix -Address $StaticAddress -Prefix $PrefixLength
    $actual = @($result.amrNetworkAfterStatic.ipv4 | Where-Object { $_.IPAddress -eq $StaticAddress })
    $connectedRoute = @($result.amrNetworkAfterStatic.routes |
        Where-Object { [string]$_.DestinationPrefix -eq $expectedNetwork })
    $defaultRoutes = @($result.amrNetworkAfterStatic.routes |
        Where-Object { [string]$_.DestinationPrefix -eq '0.0.0.0/0' })
    $result.amrNetworkChecks = [ordered]@{
        expectedNetwork = $expectedNetwork
        profileMatches = ([string]$result.amrNetworkAfterStatic.profile -eq $AmrProfileName)
        adapterUp = ([string]$result.amrNetworkAfterStatic.adapterStatus -eq 'Up')
        addressMatches = ($actual.Count -gt 0 -and [string]$actual[0].AddressState -ne 'Duplicate')
        expectedNetworkRoutePresent = ($connectedRoute.Count -gt 0)
        defaultRouteOnInterfacePresent = ($defaultRoutes.Count -gt 0)
    }
    if (-not $result.amrNetworkChecks.profileMatches) {
        throw "Current WLAN profile is '$($result.amrNetworkAfterStatic.profile)', expected '$AmrProfileName'."
    }
    if (-not $result.amrNetworkChecks.adapterUp) {
        throw "WLAN adapter status is '$($result.amrNetworkAfterStatic.adapterStatus)', expected Up."
    }
    if (-not $result.amrNetworkChecks.addressMatches) {
        throw "Static address '$StaticAddress' was not observed as a non-duplicate address."
    }
    if (-not $result.amrNetworkChecks.expectedNetworkRoutePresent) {
        throw "Expected connected route '$expectedNetwork' was not observed after static address assignment."
    }
    if ($result.amrNetworkChecks.defaultRouteOnInterfacePresent) {
        throw 'A default route remains on the AMR interface; refusing to continue the read-only cycle.'
    }

    if ($VerifyDevices) {
        Write-Output ("[{0}] Running approved read-only AGV/AUBO checks." -f (Get-Date -Format o))
        $result.deviceRequestsAttempted = $true
        $result.deviceChecks = Invoke-DeviceReadOnlyChecks -Root $root -AgvTarget $AgvHost -AgvPort $AgvStatusPort -AuboTarget $AuboHost -AuboRpcPort $AuboPort -AuboRobot $RobotName -TimeoutMs $DeviceTimeoutMs
    }
    $result.status = 'READ-ONLY-CHECK-COMPLETED'
}
catch {
    $result.errors += $_.Exception.Message
}
finally {
    Write-Output ("[{0}] Restoring conversation network '{1}' with DHCP/DNS." -f (Get-Date -Format o), $ReturnProfileName)
    try {
        $result.restore = Restore-ConversationNetwork -Alias $InterfaceAlias -ReturnProfile $ReturnProfileName -StaticAddress $StaticAddress -TimeoutSeconds $ConnectTimeoutSeconds
    }
    catch {
        $result.restore = [ordered]@{
            attempted = $true
            success = $false
            errors = @($_.Exception.Message)
        }
    }
    $restoreSucceeded = $null -ne $result.restore -and [bool]$result.restore.success
    if (-not $restoreSucceeded) {
        $result.errors += 'Automatic restore to the conversation network failed.'
    }
    $result.endedAt = [DateTimeOffset]::Now
    $json = $result | ConvertTo-Json -Depth 40
    $json | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    Write-Output $json
    Write-Output "Evidence: $evidencePath"
}

if ($result.errors.Count -gt 0 -or -not $result.restore.success) {
    exit 2
}
