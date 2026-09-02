[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]{0,252}$')]
    [string]$ControllerHost,

    [ValidateRange(1, 65535)]
    [int]$Port = 9012,

    [ValidateRange(1, 3)]
    [int]$MaxAttempts = 2,

    [ValidateRange(1, 3600)]
    [int]$RetryDelaySeconds = 30,

    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$scriptPath = Join-Path $PSScriptRoot 'Invoke-AuboWsReadOnlyPreflight.ps1'
if (-not (Test-Path -LiteralPath $scriptPath -PathType Leaf)) {
    throw "The read-only preflight script was not found at '$scriptPath'."
}

$root = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    New-Item -ItemType Directory -Path $root -Force | Out-Null
}

for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $outputPath = Join-Path $root ("aubo-field-readonly-backoff-{0}-attempt{1}.json" -f $stamp, $attempt)
    try {
        & $scriptPath `
            -ControllerHost $ControllerHost `
            -Port $Port `
            -OutputPath $outputPath
        Write-Output "Read-only preflight succeeded on attempt $attempt."
        Write-Output "Evidence: $outputPath"
        return
    }
    catch {
        if ($attempt -ge $MaxAttempts) {
            throw
        }

        Write-Warning "Read-only preflight attempt $attempt failed: $($_.Exception.Message)"
        Write-Warning "Waiting $RetryDelaySeconds seconds before the next read-only attempt. No mutation is retried."
        Start-Sleep -Seconds $RetryDelaySeconds
    }
}
