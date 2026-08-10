# Local Simulator verification

## Interactive WPF development

For normal local Simulator development, start WPF directly:

```powershell
dotnet run --project src/MesControlAgv.Wpf -c Debug
```

WPF starts `Simulator -> Adapter -> MES` itself, waits for every `/health`
endpoint, and begins its first refresh only after the local stack is ready. It
does not use a fixed startup delay. Closing WPF automatically stops only the
service processes that it created; an already healthy local service is reused
and is not stopped.

WPF-managed Simulator databases are stored under
`%LOCALAPPDATA%\MesControlAgv\local-simulator`. Set
`WPF_MANAGE_LOCAL_SERVICES=false` when connecting the WPF client to separately
managed endpoints. Local service management is Simulator-only and never
connects to a physical controller.

## Isolated process verification

`run-local.ps1` starts only the Simulator, Adapter, and MES service processes;
it does not launch WPF. The default ports remain Simulator `5183`, Adapter
`5041`, and MES `5045`.

For an isolated process run, choose a run id, ports, and SQLite files outside
the repository. `run-local.ps1` records the process ids, URLs, and database
paths in a run-specific state file and waits for each `/health` endpoint before
returning:

```powershell
$runId = 'offline-20260807-a'
$runRoot = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

.\scripts\run-local.ps1 `
  -Configuration Release `
  -RunId $runId `
  -SimulatorUrl http://localhost:5361 `
  -AdapterUrl http://localhost:5362 `
  -MesUrl http://localhost:5363 `
  -MesDatabasePath (Join-Path $runRoot 'mes.db') `
  -AdapterDatabasePath (Join-Path $runRoot 'adapter.db') `
  -RequireIsolatedStores

.\scripts\verify-local.ps1 `
  -RunId $runId `
  -RequireIsolatedStores `
  -SourceStationCode 2 `
  -TargetStationCode 4

.\scripts\stop-local.ps1 -RunId $runId
```

With a fresh isolated run started above, invoke the verifier using the same run
and inject one simulator navigation failure before dispatch. The scenario asserts the MES task
becomes `Failed`, records `DeviceFailed`, retries once, and then completes both
transport legs through the normal arrival and operator confirmation APIs:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario failure-retry `
  -RequireIsolatedStores
```

`failure-retry` consumes only the in-memory Simulator fault for that process;
it does not alter the profile, connect to a physical AGV, or reuse a task from
another run. Start a fresh run before repeating it so the simulator state is
clean, and keep the same `-RunId`/temporary SQLite paths for both commands.

To verify the operator cancellation path, use a fresh isolated run and cancel
after pickup confirmation. The scenario exercises cancellation of the active
dropoff operation through MES -> Adapter -> Simulator, then checks the terminal
task state, `CancelConfirmed` audit event, simulator device state, and AGV
release:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario cancellation `
  -RequireIsolatedStores
```

`cancellation` is Simulator-only and uses no physical-device connection. It
does not complete the dropoff leg after cancellation; a fresh run is required
for another scenario because Simulator state is in memory.

For timeout reconciliation, start a fresh run so the first navigation actually
has to move. The Simulator stores the accepted operation before returning its
gateway-timeout response; the verifier checks that recovery returns the same
operation ID and completes the task without a second dispatch:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario timeout-recovery `
  -RequireIsolatedStores
```

For restart-resume, leave the three services owned by `run-local.ps1` and let
the verifier restart only the recorded MES PID. It checks the persisted task,
the original operation ID, `Timeout` and `ReconciledMoving` startup audit
events, then completes both transport legs:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario restart-resume `
  -RequireIsolatedStores
```

For multi-AGV contention, keep a fresh Simulator run and dispatch two tasks
before either arrives. The Adapter must assign different idle AGVs, retain one
fleet-status entry per task, and release both after cancellation:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario multi-agv `
  -RequireIsolatedStores
```

All scenarios are Simulator-only. They never select a vendor TCP driver or
open a physical controller connection. Use a new run for any scenario that
injects an in-memory fault; a reused run may have an AGV already at the pickup
station, in which case MES correctly skips navigation and there is no timeout
to reconcile.

Use `-StatePath` instead of `-RunId` when a caller owns the state-file
location. This is preferred when several local runs are active. Calling
`stop-local.ps1` without arguments is retained for compatibility when exactly
one run state file exists; with multiple runs, provide an explicit run id or
state path to avoid stopping the wrong run.

The verification checks health, task creation and dispatch, fleet correlation,
pause/resume on both transport legs, simulator arrival, operator pickup and
dropoff confirmations, audit events, and the absence of the completed task from
active fleet status. It does not remove SQLite files, so the audit database can
be inspected or deleted by the caller after the run.
