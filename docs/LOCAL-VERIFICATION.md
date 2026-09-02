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

### Offline startup diagnostics

Before starting or connecting to any service, WPF parses its environment and
optional local configuration files. The **启动诊断** page shows every result;
this inspection does not perform ping, HTTP, TCP, or serial probing.

Use these optional variables when a deployment wants the diagnostic page to
validate the same files that its external services will load:

```powershell
$env:WPF_ADAPTER_CONFIG_PATH = 'D:\deploy\Adapter\appsettings.PhysicalAcceptance.json'
$env:WPF_INSTRUMENT_GATEWAY_CONFIG_PATH = 'D:\deploy\InstrumentGateway\appsettings.json'
```

The Adapter inspection covers the AGV driver/run mode, AUBO arm and sample
workstation enable/control gates. The current repository has no registered
visual Adapter module, so visual status is reported as **未注册**; the in-process
`MockVisionDriver` is not treated as field integration. The instrument gateway
inspection validates the CIC-D160+ identity and serial settings and always
reports the gateway write surface as disabled because its HTTP and injected
transport contracts are read-only.

The same report also exposes the **现场只读预检输入** tab. It maps the static
`read-only-preflight`, `AcquireControl`, `EnablePush`, and
`MinimumConfidence` values to machine-readable input codes, then lists the
controller identity, control owner, current station/idle state, map identity,
localization, safety gates, authorization, and single-segment route as fields
that require fresh evidence. An explicitly open mutation gate is an offline
block. The exported `fieldPreflight` section uses schema
`mes.field-preflight/1.0`; its `canDetermineGo` value is always `false`.
This report prepares a handoff only and never changes the physical acceptance
record's **NO-GO** conclusion.

After reading a prior report, use the **配置差异审阅** tab to compare the
current local inspection with that imported snapshot. The comparison is keyed
by diagnostic code (including `FIELD_` preflight inputs), shows only redacted
values, and is an audit aid rather than a freshness check. A subsequent export
may include the `configurationDiff` section with schema
`mes.offline-diagnostic-diff/1.0`; it still cannot determine field GO.
Use the status dropdown or code/name search to narrow the table. If a human
reviews the result, enter a short local reviewer label and optional note, then
choose **记录本次审阅**; this appends one bounded, redacted in-memory audit
entry. It does not persist automatically or alter either snapshot.

The **快照生命周期** tab scans only a selected directory's
`mes-offline-diagnostics*.json` files. It reports format, size, age, retention
candidates, and the active policy (30 days, 3 protected latest files, 100 files,
50 MB total by default). Scanning never creates the directory and never deletes,
uploads, or overwrites a file. To process candidates, an operator must check the
confirmation box and enter the exact phrase shown in the page; the action moves
only unchanged candidates into `.offline-diagnostics-recycle/<timestamp>/` and
writes a redacted `manifest.json`, so the files remain recoverable. Incorrect
confirmation, a changed source file, or a path outside the scan directory is
rejected. There is no background cleanup.

Invalid JSON or an impossible enabled-module configuration blocks WPF startup.
Missing optional files remain warnings and do not authorize any field action.

### Offline release gate

Run the release gate from the repository root before packaging or handing off
an offline build:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-offline-release.ps1
```

It runs the single-process solution test, checks the diagnostic schema and
fixture redaction, and requires the physical handoff/acceptance documents to
retain their `NO-GO` wording. Use `-SkipTests` only for a quick static check;
the default mode is the release result. The generated JSON report is written
to the system temporary directory unless `-OutputPath` is provided.
The report contains aggregated test counts and a `releaseEligible` flag; only
this JSON should be archived or uploaded by CI. Temporary TRX files and raw
test output are removed when the gate exits. The CI workflow additionally runs
`scripts/assert-offline-release-report.ps1`, which rejects unredacted paths,
credentials, raw-log properties, unexpected skipped tests, and any automatic
field GO decision before uploading the single JSON artifact.

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
