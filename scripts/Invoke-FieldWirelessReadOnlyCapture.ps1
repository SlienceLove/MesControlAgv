[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')]
    [string]$AgvHost,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')]
    [string]$AuboHost,

    [string]$InterfaceAlias = 'WLAN',
    [string]$ExpectedWlanProfile = 'AMR',
    [string]$ExpectedLocalAddress = '192.168.1.11',
    [ValidateRange(1, 32)]
    [int]$ExpectedPrefixLength = 24,
    [ValidateRange(1, 65535)]
    [int]$AgvStatusPort = 19204,
    [ValidateRange(1, 65535)]
    [int]$AuboPort = 9012,
    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')]
    [string]$RobotName = 'rob1',
    [ValidateRange(100, 60000)]
    [int]$TimeoutMs = 5000,
    [string]$ArtifactRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $PSScriptRoot '..\artifacts\physical-acceptance'
}

function Get-WlanProfileName {
    param([Parameter(Mandatory = $true)] [string]$Alias)

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
    return [ordered]@{
        observedAt = [DateTimeOffset]::Now
        interfaceAlias = $Alias
        interfaceIndex = [int]$adapter.ifIndex
        adapterStatus = [string]$adapter.Status
        mediaConnectionState = [string]$adapter.MediaConnectionState
        macAddress = [string]$adapter.MacAddress
        wlanProfile = Get-WlanProfileName -Alias $Alias
        ipv4 = @($addresses)
        routes = @($routes)
    }
}

function Test-TcpPortOnce {
    param(
        [Parameter(Mandatory = $true)] [string]$HostName,
        [Parameter(Mandatory = $true)] [int]$Port
    )

    $result = Test-NetConnection -ComputerName $HostName -Port $Port -InformationLevel Detailed -WarningAction SilentlyContinue
    $sourceText = if ($null -eq $result.SourceAddress) { $null } else { [string]$result.SourceAddress }
    $remoteText = if ($null -eq $result.RemoteAddress) { $null } else { [string]$result.RemoteAddress }
    return [ordered]@{
        observedAt = [DateTimeOffset]::Now
        host = $HostName
        port = $Port
        tcpTestSucceeded = [bool]$result.TcpTestSucceeded
        interfaceAlias = $result.InterfaceAlias
        sourceAddress = $sourceText
        remoteAddress = $remoteText
    }
}

function Test-PingOnce {
    param([Parameter(Mandatory = $true)] [string]$HostName)

    $output = ping.exe -n 1 -w 1000 $HostName 2>&1 | Out-String
    return [ordered]@{
        observedAt = [DateTimeOffset]::Now
        host = $HostName
        succeeded = ($LASTEXITCODE -eq 0)
        exitCode = $LASTEXITCODE
        output = $output.Trim()
    }
}

function Invoke-ReadOnlyDevices {
    param(
        [Parameter(Mandatory = $true)] [string]$Root,
        [Parameter(Mandatory = $true)] [string]$AgvTarget,
        [Parameter(Mandatory = $true)] [int]$AgvPort,
        [Parameter(Mandatory = $true)] [string]$AuboTarget,
        [Parameter(Mandatory = $true)] [int]$AuboRpcPort,
        [Parameter(Mandatory = $true)] [string]$AuboRobot,
        [Parameter(Mandatory = $true)] [int]$ReadTimeoutMs,
        [Parameter(Mandatory = $true)] [object]$AgvTcp,
        [Parameter(Mandatory = $true)] [object]$AuboTcp
    )

    $checks = [ordered]@{
        observedAt = [DateTimeOffset]::Now
        writesAttempted = $false
        modbusAttempted = $false
        agvCommandPortAttempted = $false
        auboControlMethodsAttempted = $false
        agvStatus = $null
        auboWebSocket = $null
    }

    if ($AgvTcp.tcpTestSucceeded) {
        $agvPath = Join-Path $Root 'agv-status-readonly.txt'
        try {
            $agvScript = Join-Path $PSScriptRoot 'Invoke-AgvIoApi.ps1'
            $text = (& $agvScript -Operation read -ControllerHost $AgvTarget -StatusPort $AgvPort -TimeoutMs $ReadTimeoutMs 2>&1 | Out-String).Trim()
            $text | Set-Content -LiteralPath $agvPath -Encoding UTF8
            $checks.agvStatus = [ordered]@{
                success = $true
                evidence = $agvPath
                api = 1013
                host = $AgvTarget
                port = $AgvPort
                commandPortAttempted = $false
            }
        }
        catch {
            $checks.agvStatus = [ordered]@{
                success = $false
                error = $_.Exception.Message
                api = 1013
                host = $AgvTarget
                port = $AgvPort
                commandPortAttempted = $false
            }
        }
    }
    else {
        $checks.agvStatus = [ordered]@{
            success = $false
            skipped = $true
            reason = 'AGV status TCP port did not open'
            api = 1013
            host = $AgvTarget
            port = $AgvPort
            commandPortAttempted = $false
        }
    }

    if ($AuboTcp.tcpTestSucceeded) {
        $auboPath = Join-Path $Root 'aubo-ws-readonly-preflight.json'
        try {
            $auboScript = Join-Path $PSScriptRoot 'Invoke-AuboWsReadOnlyPreflight.ps1'
            # No variable keys and no Modbus signal reads are requested.
            & $auboScript -ControllerHost $AuboTarget -Port $AuboRpcPort -RobotName $AuboRobot -TimeoutMs $ReadTimeoutMs -IncludeModbusSignals:$false -OutputPath $auboPath | Out-Null
            $checks.auboWebSocket = [ordered]@{
                success = $true
                evidence = $auboPath
                host = $AuboTarget
                port = $AuboRpcPort
                modbusAttempted = $false
                variableKeysSupplied = $false
            }
        }
        catch {
            $checks.auboWebSocket = [ordered]@{
                success = $false
                error = $_.Exception.Message
                host = $AuboTarget
                port = $AuboRpcPort
                modbusAttempted = $false
                variableKeysSupplied = $false
            }
        }
    }
    else {
        $checks.auboWebSocket = [ordered]@{
            success = $false
            skipped = $true
            reason = 'AUBO WebSocket TCP port did not open'
            host = $AuboTarget
            port = $AuboRpcPort
            modbusAttempted = $false
            variableKeysSupplied = $false
        }
    }

    return $checks
}

try {
    $agvParsed = [Net.IPAddress]::Parse($AgvHost)
    $auboParsed = [Net.IPAddress]::Parse($AuboHost)
    if ($agvParsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or
        $auboParsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) {
        throw 'AGV and AUBO hosts must be IPv4 addresses.'
    }
}
catch {
    throw "Invalid explicit device address: $($_.Exception.Message)"
}

$runId = 'wireless-readonly-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([Guid]::NewGuid().ToString('N').Substring(0, 8))
$root = Join-Path ([IO.Path]::GetFullPath($ArtifactRoot)) $runId
New-Item -ItemType Directory -Path $root -Force | Out-Null
$evidencePath = Join-Path $root 'wireless-readonly-evidence.json'
$result = [ordered]@{
    schema = 'mes.field-wireless-read-only-capture/1.0'
    runId = $runId
    startedAt = [DateTimeOffset]::Now
    status = 'NO-GO'
    writesAttempted = $false
    modbusAttempted = $false
    deviceRequestsAttempted = $false
    interfaceAlias = $InterfaceAlias
    expectedWlanProfile = $ExpectedWlanProfile
    expectedLocalAddress = "$ExpectedLocalAddress/$ExpectedPrefixLength"
    agvTarget = [ordered]@{ host = $AgvHost; statusPort = $AgvStatusPort; commandPortAttempted = $false }
    auboTarget = [ordered]@{ host = $AuboHost; websocketPort = $AuboPort; controlMethodsAttempted = $false }
    network = $null
    checks = $null
    errors = @()
}

try {
    $network = Get-NetworkSnapshot -Alias $InterfaceAlias
    $result.network = $network
    $expectedNetwork = Get-NetworkPrefix -Address $ExpectedLocalAddress -Prefix $ExpectedPrefixLength
    $actualAddress = @($network.ipv4 | Where-Object { $_.IPAddress -eq $ExpectedLocalAddress -and [int]$_.PrefixLength -eq $ExpectedPrefixLength })
    $matchingRoute = @($network.routes | Where-Object { $_.DestinationPrefix -eq $expectedNetwork })
    $defaultRoute = @($network.routes | Where-Object { $_.DestinationPrefix -eq '0.0.0.0/0' })
    $result.networkChecks = [ordered]@{
        expectedNetwork = $expectedNetwork
        profileMatches = ([string]$network.wlanProfile -eq $ExpectedWlanProfile)
        adapterUp = ([string]$network.adapterStatus -eq 'Up')
        addressMatches = ($actualAddress.Count -gt 0 -and [string]$actualAddress[0].AddressState -ne 'Duplicate')
        expectedNetworkRoutePresent = ($matchingRoute.Count -gt 0)
        defaultRouteOnWlanPresent = ($defaultRoute.Count -gt 0)
    }
    if (-not $result.networkChecks.profileMatches) { throw "Current WLAN profile is '$($network.wlanProfile)', expected '$ExpectedWlanProfile'." }
    if (-not $result.networkChecks.adapterUp) { throw "WLAN adapter status is '$($network.adapterStatus)', expected Up." }
    if (-not $result.networkChecks.addressMatches) { throw "Expected static address '$ExpectedLocalAddress/$ExpectedPrefixLength' was not observed or is duplicate." }
    if (-not $result.networkChecks.expectedNetworkRoutePresent) { throw "Expected connected route '$expectedNetwork' was not observed." }
    if ($result.networkChecks.defaultRouteOnWlanPresent) { throw 'A default route is present on WLAN; this local AMR capture requires no WLAN default gateway.' }

    $agvPing = Test-PingOnce -HostName $AgvHost
    $auboPing = Test-PingOnce -HostName $AuboHost
    $result.pingChecks = [ordered]@{ agv = $agvPing; aubo = $auboPing }
    $agvTcp = Test-TcpPortOnce -HostName $AgvHost -Port $AgvStatusPort
    $auboTcp = Test-TcpPortOnce -HostName $AuboHost -Port $AuboPort
    $result.portChecks = [ordered]@{ agvStatus = $agvTcp; auboWebSocket = $auboTcp }
    $result.deviceRequestsAttempted = $true
    $result.checks = Invoke-ReadOnlyDevices -Root $root -AgvTarget $AgvHost -AgvPort $AgvStatusPort -AuboTarget $AuboHost -AuboRpcPort $AuboPort -AuboRobot $RobotName -ReadTimeoutMs $TimeoutMs -AgvTcp $agvTcp -AuboTcp $auboTcp
    if (-not $result.checks.agvStatus.success -or -not $result.checks.auboWebSocket.success) {
        throw 'One or more approved read-only device checks failed.'
    }
    $result.status = 'READ-ONLY-CHECK-COMPLETED'
}
catch {
    $result.errors += $_.Exception.Message
}
finally {
    $result.endedAt = [DateTimeOffset]::Now
    $json = $result | ConvertTo-Json -Depth 40
    $json | Set-Content -LiteralPath $evidencePath -Encoding UTF8
    Write-Output $json
    Write-Output "Evidence: $evidencePath"
}

if ($result.errors.Count -gt 0) { exit 2 }
