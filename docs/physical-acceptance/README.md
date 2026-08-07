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

## Preparing a future authorized deployment

Only after the vehicle is powered, the work area is isolated, and the site owner
authorizes the work:

1. Copy the example to a deployment-controlled local configuration file. Do not
   commit that file.
2. Set the controller host only through protected deployment configuration, for
   example `Agv__Tcp__Host`; do not commit a real address.
3. Re-read control ownership, map fingerprint, stations, direct edges, safety
   state, localization confidence, automatic mode, and active tasks.
4. Update and validate the `physicalAcceptance` snapshot before enabling any
   physical dispatch.
5. Keep `enableAutomaticDispatch=false`. The Adapter rejects physical profiles
   that enable it until live controller map verification is implemented. Each
   future movement requires an approved, isolated, low-speed test case and an
   audit record.

The Profile is invalid for physical use when it enables the simulator, uses a
driver other than `vendor-tcp`, makes map edges bidirectional, differs from the
controller snapshot, or disables any configured safety gate. At Adapter startup,
the physical Profile also requires `Agv:Driver=vendor-tcp`, matching control
nickname, `AcquireControl=true`, and an adequate `MinimumConfidence` setting.

The Profile validates the captured snapshot against routing configuration. Live
controller-to-Profile comparison remains a required read-only on-site preflight
until a controller map-query adapter is added; this template must not be treated
as a current controller-state assertion.

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

This verification is Simulator-only. The physical vehicle remains powered off
and **NO-GO**. The historical map snapshot, `manualBlock=true`, and unknown
automatic mode cannot authorize a dispatch. After a future authorized power-on,
start with a new read-only preflight and map/station/directed-edge comparison.

Do not start Adapter with this template while the AGV is unapproved, powered
off, or outside an approved physical acceptance window.
