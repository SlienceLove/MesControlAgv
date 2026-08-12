# Physical AGV acceptance configuration

`adapter.physical-acceptance.example.json` is a versioned template for a future
physical-AGV acceptance deployment. It is not a live deployment configuration,
does not grant permission to connect to a controller, and does not replace
on-site safety approval.

The default `src/MesControlAgv.Adapter/appsettings.json` remains Simulator-only.
Do not put a physical controller address, credential, or customer-network detail
into that file.

## Historical snapshot

The template records controller facts observed during the 2026-08-05 lower-level
integration:

- map: `guangzhou606`, version `1.0.6`
- MD5: `e1b8d6b2b24362c1d44f1884c0abd8fb`
- stations: `LM1`, `LM2`, `LM3`, `LM4`, `LM5`
- confirmed directed edges: `LM1 -> LM2`, `LM2 -> LM3`, `LM1 -> LM4`,
  `LM4 -> LM1`, `LM4 -> LM5`, `LM5 -> LM4`, `LM1 -> LM5`

The timestamp and every snapshot value must be replaced with a fresh, read-only
controller inspection before any future dispatch. A local `.smap` file must not
be used as a substitute for controller data.

## 2026-08-06 map change and pause status

The historical Profile snapshot above is no longer current. The last supplied
controller status before the vehicle was powered off reported map
`guangzhou606` with MD5 `816e68b9a367d9c8d5eaee9331a7ef58`, which differs from
the template MD5. That status did not contain an authoritative map version,
station catalog, or directed-edge list, so it is not sufficient to update the
Profile or permit a dispatch.

The vehicle is currently powered off. Treat both the historical template and
the last supplied status as stale. Do not replace the template MD5 with the new
value by hand and do not start an Adapter against the controller until a future
authorized session completes a new read-only preflight. See
`2026-08-06-pause-checkpoint.md` for the recorded handoff state.

This paragraph is historical. The vehicle was powered on again for the
authorized 2026-08-11 status-port-only preflight recorded below. That newer
session did not make the historical map snapshot current and did not authorize
movement.

## Read-only preflight mode

The committed physical template and `appsettings.PhysicalAcceptance.json` use
the startup mode `Adapter:RunMode=read-only-preflight`. In this mode the Adapter
accepts only `GET` and `HEAD` HTTP requests, and the TCP driver is limited to
the status/control-owner/task-read APIs, dedicated localization API `1021`, and
documented read-only map APIs `1300`, `1301`, `1302`, and `4011` used by
`/physical/preflight`. API `4011`
uses the controller configuration channel but downloads the selected map; it
does not acquire control or mutate controller state. Read-only mode never calls
`4005`, configures push status, navigates, pauses, resumes, cancels, or accepts
a dispatch through another caller.

The mode is selected once during process startup. It is not a WPF toggle and
there is no HTTP endpoint that can change it while the process is running. To
change modes, stop the Adapter, change deployment configuration, and restart it;
the physical standard-mode profile still requires a separate approved release
process and explicit safety authorization. A protected environment override is
supported without editing the template:

When `ASPNETCORE_ENVIRONMENT=PhysicalAcceptance`, the Adapter replaces the
default JSON configuration sources with `appsettings.PhysicalAcceptance.json`
before applying environment variables and command-line arguments. This avoids
retaining trailing Simulator array entries from the default Profile.

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'PhysicalAcceptance'
$env:Adapter__RunMode = 'read-only-preflight'
$env:Agv__Tcp__Host = 'controller-host-from-approved-site-config'
# 仅在现场授权、隔离并确认 host 后运行；离线验证使用 fake controller E2E 测试
$env:ConnectionStrings__Adapter = 'Data Source=C:\approved-session\adapter-readonly.db'
dotnet run --project src/MesControlAgv.Adapter --no-launch-profile --urls http://127.0.0.1:5041
```

Confirm `GET /health` reports `runMode=read-only-preflight`, then call
`GET /physical/preflight`. In this offline phase the expected result is
`HTTP 200`, `DispatchPermitted=false`, and blocking reasons including
`automatic_dispatch_disabled`. The fake-controller acceptance test also returns
matching controller map evidence through `1300/1301/1302/4011`. A live timeout,
vendor error, malformed map, or inconsistent map/station catalog instead yields
`controller_map_evidence_unavailable` or a map mismatch and remains fail-closed.
This is not a movement failure to work around. A future standard-mode release
may only consider dispatch after fresh authoritative evidence and every safety
gate are approved.

Use `scripts/start-physical-acceptance-adapter.ps1` for an approved physical
session instead of an inline startup probe. The script starts only the Adapter,
waits first for its exact PID to own the local listening port, then polls
`/health` and verifies the expected run mode. Starting the process and checking
`/health` do not open a controller channel; controller reads or mutations occur
only when a corresponding Adapter endpoint is called. The controller host and
isolated database path are runtime-only parameters and are not written into the
repository:

```powershell
.\scripts\start-physical-acceptance-adapter.ps1 `
  -ExpectedRunMode read-only-preflight `
  -ControllerHost 'controller-host-from-approved-site-config' `
  -AdapterDatabasePath 'C:\approved-session\adapter.db' `
  -AdapterUrl 'http://127.0.0.1:5141'
```

For a separately authorized supervised movement session, restart with
`-ExpectedRunMode standard -EnableFieldNavigationAcceptance`. Keep normal
automatic dispatch, push status, and task cancellation disabled. Stop only the
owned process by using the exact state-file command printed by the script.

## Preparing a future authorized deployment

Only after the vehicle is powered, the work area is isolated, and the site owner
authorizes the work:

1. Copy the example to a deployment-controlled local configuration file. Do not
   commit that file.
2. Set the controller host only through protected deployment configuration, for
   example `Agv__Tcp__Host`; do not commit a real address. Offline tests must use
   the fake controller harness, a temporary `ConnectionStrings__Adapter` store,
   and an isolated port; never point an offline process at a site host.
3. Re-read control ownership, map fingerprint, stations, direct edges, safety
   state, localization confidence, automatic mode, and active tasks.
4. Update and validate the `physicalAcceptance` snapshot before enabling any
   physical dispatch.
5. Keep `enableAutomaticDispatch=false` until a separately authorized live map
   comparison and every remaining safety gate are approved. Each future movement
   requires an approved, isolated, low-speed test case and an audit record.

The Profile is invalid for physical use when it enables the simulator, uses a
driver other than `vendor-tcp`, makes map edges bidirectional, differs from the
controller snapshot, or disables any configured safety gate. At Adapter startup,
the physical Profile also requires `Agv:Driver=vendor-tcp`, matching control
nickname, and an adequate `MinimumConfidence` setting. Standard mode additionally
requires `AcquireControl=true`; read-only preflight instead requires
`AcquireControl=false`, `EnablePush=false`, and all mutation features disabled.
The committed template contains a placeholder host; inject the approved site
host only through protected deployment configuration.

The Profile validates the captured snapshot against routing configuration. The
Vendor TCP client now implements the documented read-only controller map source,
but a fresh controller-to-Profile comparison remains a required on-site
preflight; this template must not be treated as a current controller-state
assertion.

The separate [supervised field-navigation acceptance](FIELD-NAVIGATION-ACCEPTANCE.md)
flow records one authorized low-speed route and its audit trail. It remains
disabled by default, requires a separate standard-mode process and written
authorization, and cannot be enabled in `read-only-preflight`.

`vendor-tcp` is the canonical driver name. `tcp` is accepted only as a
backward-compatible Adapter configuration alias.

## Offline validation

The following command is offline and does not connect to an AGV:

```powershell
dotnet test tests/MesControlAgv.Domain.Tests/MesControlAgv.Domain.Tests.csproj `
  -c Release --no-restore -p:UseSharedCompilation=false -m:1
```

The 2026-08-07 WPF/Simulator dispatch loop was also verified offline from
isolated Release processes: Simulator `5361`, Adapter `5362`, and MES `5363`,
with temporary MES and Adapter SQLite stores. `scripts/verify-local.ps1` accepts
`-SourceStationCode` and `-TargetStationCode` instead of assuming one fixed
route, follows the AGV returned by MES, and verifies create, dispatch,
fleet-status correlation, pause/resume, arrival confirmations, and
`COMPLETED`. The default `2 -> 4` route and a configurable `2 -> 3` route both
passed. Use `-RequireIsolatedStores` together with temporary database paths when
running a process-level check; do not reuse a live development database.

The latest completed real checks obtained controller-authoritative map
`guangzhou606`, version `1.0.6`, MD5
`816e68b9a367d9c8d5eaee9331a7ef58`, stations `LM1..LM5`, and nine direct
directed edges. The physical Profile now contains that snapshot. Dedicated
read-only `1021` returned `reloc_status=1`; the vehicle was online and idle at
`LM1`, confidence `0.9639`, with no emergency, block, fatal, or error.

The controller firmware does not return the vendor-documented `1101.mode`
field. Direct `1101`, passive unconfigured `19301`, and documented `1100`
filtering all omitted it; API `1004` is a position query and cannot fill this
gap. Do not infer vehicle automatic mode from `dispatch_mode`,
`fork_auto_flag`, control ownership, or successful status reads.

The approved W500-SZ profile makes this exception explicit with
`vehicleOperatingModePolicy=not-exposed-by-approved-model` and
`requireAutomaticMode=false`. API `1000.model` must match the Profile model;
an explicit manual value still blocks. The default policy remains
`vendor-field-required`, so an unknown mode is still fail-closed for every
other model or configuration.

The current committed template is still **NO-GO** for motion because it keeps
`read-only-preflight`, `AcquireControl=false`, and field navigation disabled.

Field-navigation dispatch uses two preflights: every non-ownership gate must
pass before control acquisition, then the full gate set is read again after
control acquisition and before dispatch. An active task blocks the first phase.
The approved physical speed limit is sent as `max_speed` on every `3066`
segment.
The committed configuration remains read-only and dispatch-disabled.

Do not start Adapter with this template while the AGV is unapproved, powered
off, or outside an approved physical acceptance window.

One supervised `LM1 -> LM2` attempt at the approved `0.3 m/s` limit did not move
the vehicle. Both preflight phases passed and control acquisition succeeded, but
the requested task read as `404 (NotFound)` and the global `1110` list was empty.
Offline diagnosis found that a non-empty pre-dispatch `1110` item with
`status=404` was misclassified as an existing task, so the client returned
`unknown` before writing `3066`. No cancellation and no second dispatch were
sent, and the old log retained no raw mutation data, so no historical `3066`
response is claimed.

An all-`404` pre-dispatch result now means the task is absent and permits exactly
one first attempt. After a write is attempted, an empty or `404` status becomes
`unknown` with `dispatch_not_confirmed_by_1110`, and the same task ID is never
resent automatically. Mutation audit logging is allowlisted: the `3066` request
summary and the response `ret_code`, `err_msg`, and `create_on` are preserved,
while the controller host and unfiltered payload are not.

The site operator released control manually in the robot test software, and a
later read-only `1060` confirmed `locked=false`. The committed template stays
read-only and dispatch-disabled, and the boundary remains **NO-GO**. A second
attempt requires a fresh authorized read-only preflight, a stop of that process,
renewed explicit movement authorization, and a new unique acceptance/task ID.

The subsequent Stage 3 offline hardening is recorded in
[`../../artifacts/physical-acceptance-20260812-stage3-offline-hardening.md`](../../artifacts/physical-acceptance-20260812-stage3-offline-hardening.md).
It adds shared physical lifecycle serialization, fail-closed pause/resume and
cancellation boundaries, transport-aware ownership rollback, and isolated
output test support. The full Release gate passed `420/420` with `0 warnings /
0 errors`. This is offline evidence only and does not authorize a controller
connection or movement.
