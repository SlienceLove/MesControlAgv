$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnet = (Get-Command dotnet.exe).Source
$statePath = Join-Path ([IO.Path]::GetTempPath()) 'MesControlAgv-local-pids.json'
$services = @(
    [pscustomobject]@{
        Name = 'Simulator'
        ProjectRoot = Join-Path $root 'src\MesControlAgv.Simulator'
        Dll = Join-Path $root 'src\MesControlAgv.Simulator\bin\Debug\net8.0\MesControlAgv.Simulator.dll'
        Url = 'http://localhost:5183'
        Port = 5183
    }
    [pscustomobject]@{
        Name = 'Adapter'
        ProjectRoot = Join-Path $root 'src\MesControlAgv.Adapter'
        Dll = Join-Path $root 'src\MesControlAgv.Adapter\bin\Debug\net8.0\MesControlAgv.Adapter.dll'
        Url = 'http://localhost:5041'
        Port = 5041
    }
    [pscustomobject]@{
        Name = 'MES'
        ProjectRoot = Join-Path $root 'src\MesControlAgv.Mes'
        Dll = Join-Path $root 'src\MesControlAgv.Mes\bin\Debug\net8.0\MesControlAgv.Mes.dll'
        Url = 'http://localhost:5045'
        Port = 5045
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
        $psi.UseShellExecute = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden

        $process = [System.Diagnostics.Process]::Start($psi)

        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            $owners = @(Get-PortOwners $service.Port)
            if ($owners.Count -gt 0) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)

        if ($owners.Count -eq 0) {
            throw "$($service.Name) did not listen on port $($service.Port)."
        }

        $started.Add([pscustomobject]@{
            Name = $service.Name
            Port = $service.Port
            ProcessId = [int]$owners[0]
            Executable = $dotnet
        })
    }

    $started | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8

    Write-Host 'Simulator: http://localhost:5183'
    Write-Host 'Adapter:   http://localhost:5041'
    Write-Host 'MES:       http://localhost:5045'
    Write-Host "Service PIDs saved to: $statePath"
    Write-Host 'Stop services with: .\scripts\stop-local.ps1'
    Write-Host 'Start the desktop client separately: dotnet run --project src/MesControlAgv.Wpf'
}
catch {
    foreach ($item in $started) {
        Stop-Process -Id $item.ProcessId -Force -ErrorAction SilentlyContinue
    }

    throw
}
