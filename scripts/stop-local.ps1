param(
    [string]$StatePath,
    [string]$RunId
)

$ErrorActionPreference = 'Stop'
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
    if ($candidates.Count -eq 0) {
        return $null
    }
    if ($candidates.Count -gt 1) {
        throw "Multiple local service state files were found. Specify -StatePath or -RunId: $($candidates.FullName -join ', ')"
    }

    return $candidates[0].FullName
}

$resolvedStatePath = Resolve-StatePath $StatePath $RunId
if ([string]::IsNullOrWhiteSpace($resolvedStatePath)) {
    Write-Host 'No local service PID file found.'
    exit 0
}
if (-not (Test-Path -LiteralPath $resolvedStatePath -PathType Leaf)) {
    if ([string]::IsNullOrWhiteSpace($StatePath) -and -not [string]::IsNullOrWhiteSpace($RunId)) {
        Write-Host "No local service state file found for run '$RunId'."
        exit 0
    }

    throw "Local service state file was not found at '$resolvedStatePath'."
}

function Get-PortOwners {
    param([int]$Port)

    if ($Port -le 0) {
        return @()
    }

    $owners = [System.Collections.Generic.List[int]]::new()
    foreach ($line in @(netstat -ano -p TCP | Select-String 'LISTENING')) {
        $parts = ($line.ToString() -split '\s+') | Where-Object { $_ }
        if ($parts.Count -ge 5 -and $parts[0] -eq 'TCP' -and $parts[1] -match (':{0}$' -f $Port) -and $parts[3] -eq 'LISTENING') {
            $owners.Add([int]$parts[4])
        }
    }

    @($owners | Sort-Object -Unique)
}

$state = Get-Content -Raw -LiteralPath $resolvedStatePath | ConvertFrom-Json
if ($null -eq $state) {
    throw "Local service state file '$resolvedStatePath' is empty."
}

if ($null -ne $state.PSObject.Properties['Services']) {
    $items = @($state.Services)
    $stateRunId = $state.RunId
}
else {
    # Compatibility with the original array-only PID file format.
    $items = @($state)
    $stateRunId = $null
}

if ($items.Count -eq 0) {
    Remove-Item -LiteralPath $resolvedStatePath -Force
    Write-Host "Removed empty local service state file: $resolvedStatePath"
    exit 0
}

if (-not [string]::IsNullOrWhiteSpace($stateRunId)) {
    if (-not [string]::IsNullOrWhiteSpace($RunId) -and $stateRunId -ne $RunId) {
        throw "State file run id '$stateRunId' does not match requested run id '$RunId'."
    }

    Write-Host "Stopping local service run '$stateRunId'."
}

$ownershipFailure = $false
for ($index = $items.Count - 1; $index -ge 0; $index--) {
    $item = $items[$index]
    $process = Get-Process -Id ([int]$item.ProcessId) -ErrorAction SilentlyContinue
    if ($null -eq $process) {
        Write-Host "$($item.Name) is not running (PID $($item.ProcessId))."
        continue
    }

    $pathMatches = $true
    if (-not [string]::IsNullOrWhiteSpace($item.Executable)) {
        $actualPath = $null
        try { $actualPath = $process.Path } catch { }
        $pathMatches = [string]::Equals($actualPath, [string]$item.Executable, [StringComparison]::OrdinalIgnoreCase)
    }
    if (-not $pathMatches) {
        $ownershipFailure = $true
        Write-Warning "Skipped PID $($item.ProcessId); its executable no longer matches the recorded process."
        continue
    }

    $port = 0
    if ($null -ne $item.PSObject.Properties['Port']) {
        $port = [int]$item.Port
    }
    $owners = @(Get-PortOwners $port)
    if ($owners.Count -gt 0 -and $owners -notcontains [int]$item.ProcessId) {
        $ownershipFailure = $true
        Write-Warning "Skipped PID $($item.ProcessId); port $port is now owned by PID(s) $($owners -join ', ')."
        continue
    }

    Stop-Process -Id $process.Id -Force
    try { $process.WaitForExit(5000) | Out-Null } catch { }
    if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
        Write-Host "$($item.Name) stopped (PID $($item.ProcessId))."
    }
    else {
        $ownershipFailure = $true
        Write-Warning "$($item.Name) did not exit after stop request (PID $($item.ProcessId))."
    }
}

if ($ownershipFailure) {
    Write-Warning "State file was retained for review: $resolvedStatePath"
}
else {
    Remove-Item -LiteralPath $resolvedStatePath -Force
    Write-Host "Removed local service state: $resolvedStatePath"
}
