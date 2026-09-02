[CmdletBinding()]
param(
    [ValidateSet('status', 'readiness', 'variable', 'set-int32', 'set-string', 'load', 'run', 'stop')]
    [string]$Operation = 'status',

    [string]$ControllerHost = '192.168.1.102',
    [int]$Port = 30004,
    [string]$RobotName = 'rob1',
    [int]$TimeoutMs = 3000,

    [string]$Key = '',
    [string]$Value = '',
    [string]$Program = '',
    [switch]$AllowWrite,
    [switch]$ConfirmPhysical
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-AuboRpc {
    param(
        [Parameter(Mandatory)] [string]$Method,
        [AllowNull()] [object]$Params = $null
    )

    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $connectTask = $client.ConnectAsync($ControllerHost, $Port)
        if (-not $connectTask.Wait($TimeoutMs)) {
            throw "Timed out connecting to ${ControllerHost}:$Port."
        }

        $stream = $client.GetStream()
        $stream.ReadTimeout = $TimeoutMs
        $stream.WriteTimeout = $TimeoutMs
        $id = "mes-$([Guid]::NewGuid().ToString('N'))"
        $request = [ordered]@{
            jsonrpc = '2.0'
            method = $Method
            id = $id
        }
        if ($null -ne $Params) { $request.params = $Params }

        $json = $request | ConvertTo-Json -Compress -Depth 20
        $bytes = [Text.Encoding]::UTF8.GetBytes($json + "`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush()

        $reader = [IO.StreamReader]::new(
            $stream,
            [Text.Encoding]::UTF8,
            $false,
            4096,
            $true)
        try {
            $line = $reader.ReadLine()
        }
        finally {
            $reader.Dispose()
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            throw "AUBO closed the connection without a response to '$Method'."
        }

        $response = $line | ConvertFrom-Json
        $responseId = $response.PSObject.Properties['id']
        if ($null -ne $responseId -and [string]$responseId.Value -ne $id) {
            throw "AUBO response id '$($responseId.Value)' does not match '$id'."
        }
        $errorProperty = $response.PSObject.Properties['error']
        if ($null -ne $errorProperty -and $null -ne $errorProperty.Value) {
            $errorValue = $errorProperty.Value
            $code = if ($null -ne $errorValue.code) { $errorValue.code } else { -1 }
            $message = if ($null -ne $errorValue.message) { $errorValue.message } else { $errorValue }
            throw "AUBO method '$Method' failed with code $code`: $message"
        }
        if ($null -eq $response.PSObject.Properties['result']) {
            throw "AUBO method '$Method' returned no result member."
        }
        return $response.result
    }
    finally {
        $client.Dispose()
    }
}

function Require-WriteAuthorization {
    if (-not $AllowWrite -or -not $ConfirmPhysical) {
        throw "'$Operation' is state-changing. Re-run with -AllowWrite -ConfirmPhysical only during a supervised field test."
    }
}

function Require-Text {
    param([Parameter(Mandatory)] [string]$Text, [Parameter(Mandatory)] [string]$Name)
    if ([string]::IsNullOrWhiteSpace($Text)) { throw "$Name is required for '$Operation'." }
    return $Text.Trim()
}

switch ($Operation) {
    'status' {
        [ordered]@{
            observedAt = [DateTimeOffset]::Now
            host = $ControllerHost
            port = $Port
            robot = $RobotName
            robotMode = Invoke-AuboRpc -Method "$RobotName.RobotState.getRobotModeType"
            safetyMode = Invoke-AuboRpc -Method "$RobotName.RobotState.getSafetyModeType"
            runtimeStatus = Invoke-AuboRpc -Method 'RuntimeMachine.getStatus'
            operationalMode = Invoke-AuboRpc -Method "$RobotName.RobotManage.getOperationalMode"
        } | ConvertTo-Json -Depth 20
        break
    }
    'readiness' {
        # This operation is deliberately read-only. It gathers the same mode facts as
        # `status` and adds the currently preloaded project name; it never enables,
        # loads, resumes, aborts, or writes a named variable.
        [ordered]@{
            observedAt = [DateTimeOffset]::Now
            host = $ControllerHost
            port = $Port
            robot = $RobotName
            robotMode = Invoke-AuboRpc -Method "$RobotName.RobotState.getRobotModeType"
            safetyMode = Invoke-AuboRpc -Method "$RobotName.RobotState.getSafetyModeType"
            runtimeStatus = Invoke-AuboRpc -Method 'RuntimeMachine.getStatus'
            operationalMode = Invoke-AuboRpc -Method "$RobotName.RobotManage.getOperationalMode"
            preloadProgram = Invoke-AuboRpc -Method 'RuntimeMachine.getPreloadProgram' -Params @{ index = 0 }
            canDetermineGo = $false
        } | ConvertTo-Json -Depth 20
        break
    }
    'variable' {
        $keyValue = Require-Text -Text $Key -Name 'Key'
        $exists = [bool](Invoke-AuboRpc -Method 'RegisterControl.hasNamedVariable' -Params @{ key = $keyValue })
        $result = [ordered]@{
            observedAt = [DateTimeOffset]::Now
            host = $ControllerHost
            port = $Port
            key = $keyValue
            exists = $exists
        }
        if ($exists) {
            $type = [string](Invoke-AuboRpc -Method 'RegisterControl.getNamedVariableType' -Params @{ key = $keyValue })
            $result.type = $type
            $result.value = switch -Regex ($type.ToLowerInvariant()) {
                'int' { Invoke-AuboRpc -Method 'RegisterControl.getInt32' -Params @{ key = $keyValue; default_value = 0 }; break }
                'bool' { Invoke-AuboRpc -Method 'RegisterControl.getBool' -Params @{ key = $keyValue; default_value = $false }; break }
                'double|float' { Invoke-AuboRpc -Method 'RegisterControl.getDouble' -Params @{ key = $keyValue; default_value = 0.0 }; break }
                'string' { Invoke-AuboRpc -Method 'RegisterControl.getString' -Params @{ key = $keyValue; default_value = '' }; break }
                default { $null }
            }
        }
        [pscustomobject]$result | ConvertTo-Json -Depth 20
        break
    }
    'set-int32' {
        Require-WriteAuthorization
        $keyValue = Require-Text -Text $Key -Name 'Key'
        $intValue = 0
        if (-not [int]::TryParse($Value, [Globalization.NumberStyles]::Integer, [Globalization.CultureInfo]::InvariantCulture, [ref]$intValue)) {
            throw "Value '$Value' is not a valid Int32."
        }
        [pscustomobject]@{
            observedAt = [DateTimeOffset]::Now
            method = 'RegisterControl.setInt32'
            key = $keyValue
            value = $intValue
            result = Invoke-AuboRpc -Method 'RegisterControl.setInt32' -Params @{ key = $keyValue; value = $intValue }
        } | ConvertTo-Json -Depth 20
        break
    }
    'set-string' {
        Require-WriteAuthorization
        $keyValue = Require-Text -Text $Key -Name 'Key'
        [pscustomobject]@{
            observedAt = [DateTimeOffset]::Now
            method = 'RegisterControl.setString'
            key = $keyValue
            value = $Value
            result = Invoke-AuboRpc -Method 'RegisterControl.setString' -Params @{ key = $keyValue; value = $Value }
        } | ConvertTo-Json -Depth 20
        break
    }
    'load' {
        Require-WriteAuthorization
        $programValue = Require-Text -Text $Program -Name 'Program'
        [pscustomobject]@{
            observedAt = [DateTimeOffset]::Now
            method = 'RuntimeMachine.loadProgram'
            program = $programValue
            result = Invoke-AuboRpc -Method 'RuntimeMachine.loadProgram' -Params @{ program = $programValue }
        } | ConvertTo-Json -Depth 20
        break
    }
    'run' {
        Require-WriteAuthorization
        [pscustomobject]@{
            observedAt = [DateTimeOffset]::Now
            method = 'RuntimeMachine.resume'
            result = Invoke-AuboRpc -Method 'RuntimeMachine.resume'
        } | ConvertTo-Json -Depth 20
        break
    }
    'stop' {
        Require-WriteAuthorization
        [pscustomobject]@{
            observedAt = [DateTimeOffset]::Now
            method = 'RuntimeMachine.abort'
            result = Invoke-AuboRpc -Method 'RuntimeMachine.abort'
        } | ConvertTo-Json -Depth 20
        break
    }
}
