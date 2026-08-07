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

To verify an unconfirmed timeout and recovery, start another fresh isolated run
and use `timeout-recover`. It injects the Simulator-only `timeout-unknown`
fault, asserts MES enters `Unknown` with one `Timeout` event, recreates the same
device operation in the Simulator, then calls MES `/recover` and requires
`ReconciledMoving` before completing both transport legs:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario timeout-recover `
  -RequireIsolatedStores
```

The existing Simulator `timeout` mode remains the queryable-device timeout and
is intentionally reconciled to `MovingToPickup`; `timeout-unknown` models a
timeout where no device status was confirmed. Neither mode connects to a
physical AGV.

To verify cancellation semantics on a fresh isolated run, invoke the dedicated
`cancel` scenario. It first cancels a `Created` task through MES, then dispatches
another task, cancels the active Simulator operation directly, and sends the MES
cancel command. The verifier requires `Cancelled` plus `CancelConfirmed`, and
checks that MES fleet status, Adapter fleet snapshots, and the Simulator snapshot
all report no active task:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario cancel `
  -RequireIsolatedStores
```

To rehearse workflow publication and rollback without an AGV, use a fresh
isolated run and the `workflow-publish-rollback` scenario. It creates, validates,
and publishes v1, proves that the published version rejects draft mutation,
publishes a changed v2, then creates v3 from the v1 definition as an immutable
rollback version. The verifier reads the workflow audit endpoint and checks the
draft/validate/publish/supersede event sequence, version snapshots, and the
published pointer. No transport task or AGV command is sent:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario workflow-publish-rollback `
  -RequireIsolatedStores
```

To verify fleet contention, use a fresh run with the default three Simulator
AGVs. `multi-agv` dispatches three concurrent tasks and requires distinct AGV
assignments, then proves a fourth task fails closed with `DeviceFailed` while
the existing tasks remain correlated. It completes the three assigned tasks
and checks every Simulator, Adapter, and MES fleet entry is idle:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario multi-agv `
  -RequireIsolatedStores
```

For process restart recovery, use a fresh run created by the current
`run-local.ps1` (the state file must include DLL and project metadata). The
`restart-resume` scenario dispatches one task, runs
`scripts/restart-local.ps1` to restart only Adapter and MES, keeps Simulator
alive, waits for `Timeout` plus `ReconciledMoving`, and completes both legs:

```powershell
.\scripts\verify-local.ps1 `
  -RunId $runId `
  -Scenario restart-resume `
  -RequireIsolatedStores
```

`restart-local.ps1` validates executable identity and port ownership before
stopping anything. It never restarts or reconnects the Simulator, so the
in-memory device operation remains available for MES reconciliation. The
scenario is local-process verification only and must not be used with a
physical-acceptance profile.

Use `-StatePath` instead of `-RunId` when a caller owns the state-file
location. This is preferred when several local runs are active. Calling
`stop-local.ps1` without arguments is retained for compatibility when exactly
one run state file exists; with multiple runs, provide an explicit run id or
state path to avoid stopping the wrong run.

The positive verification checks health, task creation and dispatch, fleet
correlation, pause/resume on both transport legs, simulator arrival, operator
pickup and dropoff confirmations, audit events, and the absence of the completed
task from active fleet status. `failure-retry` adds simulator fault injection and
retry audit checks; `timeout-recover` adds Unknown/reconciliation checks;
`cancel` adds device-confirmed cancellation and fleet-idle checks;
`multi-agv` adds three-way assignment and resource-exhaustion checks;
`restart-resume` adds Adapter/MES process recovery checks; and
`workflow-publish-rollback` stays on the workflow API and verifies immutable
version/audit behavior without creating a transport task. None of these scenarios
remove SQLite files, so the audit database can be inspected or deleted by the
caller after the run.
