[CmdletBinding()]
param(
    # Deliberately mandatory: the operator must confirm the address from the
    # approved field sheet instead of accidentally probing a stale default.
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$')]
    [string]$ControllerHost,

    [ValidateRange(1, 65535)]
    [int]$Port = 30004,

    [ValidatePattern('^[A-Za-z0-9_.-]{1,64}$')]
    [string]$RobotName = 'rob1',

    [ValidateRange(100, 60000)]
    [int]$TimeoutMs = 3000,

    # Variable reads are opt-in and must be supplied from the field-confirmed
    # Lua contract. No guessed/default key is queried by this script.
    [string[]]$VariableKey = @(),

    # If supplied, the evidence file is created but never overwritten.
    [string]$OutputPath,

    [switch]$SkipPortProbe
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$rpcScript = Join-Path $PSScriptRoot 'Invoke-AuboRpc.ps1'
if (-not (Test-Path -LiteralPath $rpcScript -PathType Leaf)) {
    throw "The AUBO RPC helper was not found at '$rpcScript'."
}

$normalizedHost = $ControllerHost.Trim()
$normalizedRobot = $RobotName.Trim()
$keys = @(
    $VariableKey |
        ForEach-Object { if ($null -ne $_) { $_.Trim() } } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
)

function Invoke-RpcJson {
    param(
        [Parameter(Mandatory)] [ValidateSet('status', 'readiness', 'variable')] [string]$Operation,
        [string]$Key
    )

    $arguments = @{
        Operation = $Operation
        ControllerHost = $normalizedHost
        Port = $Port
        RobotName = $normalizedRobot
        TimeoutMs = $TimeoutMs
    }
    if ($Operation -eq 'variable') {
        $arguments.Key = $Key
    }

    $raw = (& $rpcScript @arguments | Out-String).Trim()
    if ([string]::IsNullOrWhiteSpace($raw)) {
        throw "Invoke-AuboRpc.ps1 returned no output for '$Operation'."
    }
    return $raw | ConvertFrom-Json
}

function Emit-Evidence {
    param(
        [Parameter(Mandatory)] [object]$Evidence,
        [string]$Failure
    )

    $json = $Evidence | ConvertTo-Json -Depth 30
    Write-Output $json

    if (-not [string]::IsNullOrWhiteSpace($OutputPath)) {
        $absolute = [IO.Path]::GetFullPath($OutputPath)
        if (Test-Path -LiteralPath $absolute) {
            throw "Refusing to overwrite existing evidence file '$absolute'."
        }

        $parent = Split-Path -Parent $absolute
        if (-not [string]::IsNullOrWhiteSpace($parent) -and
            -not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $json | Set-Content -LiteralPath $absolute -Encoding UTF8
    }

    if (-not [string]::IsNullOrWhiteSpace($Failure)) {
        throw $Failure
    }
}

$evidence = [ordered]@{
    schema = 'mes.aubo-read-only-preflight/1.0'
    observedAt = [DateTimeOffset]::Now
    endpoint = [ordered]@{
        host = $normalizedHost
        port = $Port
        robot = $normalizedRobot
    }
    mode = 'read-only'
    writesAttempted = $false
    canDetermineGo = $false
    variableKeysRequested = $keys
}

if (-not $SkipPortProbe) {
    try {
        $probeParameters = @{
            ComputerName = $normalizedHost
            Port = $Port
            InformationLevel = 'Quiet'
            WarningAction = 'SilentlyContinue'
        }
        $evidence.portProbe = [bool](Test-NetConnection @probeParameters)
    }
    catch {
        $evidence.portProbe = $false
        $evidence.portProbeError = $_.Exception.Message
    }

    if (-not $evidence.portProbe) {
        Emit-Evidence -Evidence $evidence -Failure (
            "AUBO endpoint $normalizedHost`:$Port is not reachable. " +
            'Keep the run at NO-GO and check the approved switch port/address.')
        return
    }
}
else {
    $evidence.portProbe = $null
    $evidence.portProbeSkipped = $true
}

try {
    # `readiness` is still a read-only observation. It does not make a GO
    # decision; the field operator/factory representative must review it.
    $evidence.rpc = Invoke-RpcJson -Operation readiness
}
catch {
    $evidence.rpcError = $_.Exception.Message
    Emit-Evidence -Evidence $evidence -Failure (
        "AUBO JSON-RPC read-only probe failed: $($_.Exception.Message)")
    return
}

$variableEvidence = [System.Collections.Generic.List[object]]::new()
foreach ($key in $keys) {
    try {
        $variableEvidence.Add([pscustomobject]@{
            key = $key
            observation = Invoke-RpcJson -Operation variable -Key $key
        })
    }
    catch {
        $variableEvidence.Add([pscustomobject]@{
            key = $key
            error = $_.Exception.Message
        })
    }
}
$evidence.variables = @($variableEvidence)
$evidence.review = [ordered]@{
    statusValuesRequireFactoryConfirmation = $true
    luaProjectAndVariableContractRequireFactoryConfirmation = $true
    noVariableWrite = $true
    noProgramStart = $true
    noMotion = $true
    nextGate = 'Do not dispatch a handshake until command/sequence/ack/result keys and completion semantics are confirmed on the现场 Lua project.'
}

Emit-Evidence -Evidence $evidence
