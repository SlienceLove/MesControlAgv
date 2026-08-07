# Local Simulator verification

The local scripts run only the Simulator, Adapter, and MES processes. They do
not connect to a physical controller. The default ports remain Simulator
`5183`, Adapter `5041`, and MES `5045`.

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
