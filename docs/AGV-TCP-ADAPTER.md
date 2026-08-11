# Real AGV TCP Adapter

## Current physical-integration status

A historical, lower-level controller integration was completed on 2026-08-05.
It verified vendor-frame communication and read-only APIs `1060`, `1100`,
`1101`, and `1110`; it also completed one controlled navigation from `LM5` to
`LM1` through the controller-confirmed path `LM5 -> LM4 -> LM1`.

This is not a complete application acceptance. It does not prove a
`WPF -> MES -> Adapter -> AGV` production workflow, DI/DO behavior, emergency
handling, obstacle recovery, or release readiness. The vehicle is not contacted
as part of offline development. Historical results must not be treated as
current control, map, localization, or safety state.

The controller snapshot recorded for future re-verification is:

- map: `guangzhou606`, version `1.0.6`
- MD5: `e1b8d6b2b24362c1d44f1884c0abd8fb`
- stations: `LM1`, `LM2`, `LM3`, `LM4`, `LM5`
- confirmed directed edges: `LM1 -> LM2`, `LM2 -> LM3`, `LM1 -> LM4`,
  `LM4 -> LM1`, `LM4 -> LM5`, `LM5 -> LM4`, `LM1 -> LM5`

There is no direct `LM5 -> LM1` edge. The Simulator remains the default driver
and default configuration contains no physical controller address.

## Driver and deployment configuration

`vendor-tcp` is the canonical Adapter driver value:

```json
{
  "Agv": {
    "Driver": "vendor-tcp"
  }
}
```

`tcp` remains a backward-compatible alias only. Physical host, port, and
credential values belong in protected environment-specific deployment
configuration, never in the committed default `appsettings.json`. See
[physical acceptance configuration](physical-acceptance/README.md).

## Adapter run modes

`Adapter:RunMode` is a startup-only deployment setting. The supported values
are `standard` and `read-only-preflight`; it cannot be changed from WPF or an
HTTP request. The physical acceptance template defaults to
`read-only-preflight`, with `AcquireControl=false` and `EnablePush=false`.

In read-only preflight, every non-`GET`/`HEAD` HTTP request returns `405`, and
the TCP driver rejects control acquisition, control release, push
configuration, navigation, pause/resume, and cancellation before opening a
mutation API. Use
`GET /health` to confirm the mode and `GET /physical/preflight` to collect the
current read-only result. Changing to another mode requires stopping and
restarting the Adapter with a separately approved configuration.

## Vendor protocol mapping

| Capability | Port | API |
|---|---:|---:|
| Control owner query/acquire/release | 19204 / 19207 | 1060 / 4005 / 4006 |
| Fixed route navigation | 19206 | 3066 |
| Task status reconciliation | 19204 | 1110 |
| Pause/resume | 19206 | 3001 / 3002 |
| Standard route cancellation | 19206 | 3067 |
| Status push configuration | 19207 | 9300 |
| Status push stream | 19301 | 19301 |
| Device model/version (read-only) | 19204 | 1000 |
| Loaded/stored map catalog | 19204 | 1300 |
| Current-map station catalog | 19204 | 1301 |
| Map MD5 query | 19204 | 1302 |
| Read-only full map download | 19207 | 4011 |
| Localization status | 19204 | 1021 |

Packets use the vendor 16-byte header, big-endian payload length and API number,
followed by UTF-8 JSON. The expected response API is request API plus `10000`.
A non-zero `ret_code` is an AGV error.

For API `1101`, vendor field `mode` is the vehicle operating mode: `0` is
manual and `1` is automatic. It must not be inferred from `dispatch_mode`, SRC
ownership, or mechanism-only fields. API `1021` is the dedicated localization
status query; only `reloc_status=1` proves currently accepted localization.
Status `3` means relocation completed but still requires operator confirmation
through a separate control workflow and is not dispatch-ready.

Every request channel is serialized. A timeout is unresolved until API `1110`
reconciliation completes; the Adapter must not generate a replacement `task_id`
merely because the original request timed out.

The vendor reference defines API `4006` on the control port `19207` as the
no-payload control-release request. The Adapter sends it only in standard mode,
after API `1060` confirms that the current owner is `MesControlAgv.Adapter`; if
another owner (or no owner) is reported, the Adapter returns without opening
the control mutation path and without sending `4006`. The request and response
are included in the allowlisted mutation audit, and a non-zero `ret_code` is a
failure. The Adapter then queries `1060` again; if ownership still resolves to
`adapter`, the release is explicitly reported as unconfirmed rather than as a
success. Read-only preflight rejects the release before any channel is opened.

Standard mode also exposes `POST /agv/control/release` for explicit operator or
session cleanup. It returns a conflict when the Adapter does not own control or
when the release cannot be confirmed. Like the rest of the local Adapter HTTP
API, this endpoint is unauthenticated and must remain restricted to the
approved local host/network.

## Navigation request shape

API `3066` requires a `move_task_list` wrapper. A naked JSON array is not a
valid request shape.

```json
{
  "move_task_list": [
    { "task_id": "task-1", "source_id": "LM5", "id": "LM4", "max_speed": 0.3 },
    { "task_id": "task-2", "source_id": "LM4", "id": "LM1", "max_speed": 0.3 }
  ]
}
```

Every segment must use a controller-confirmed direct edge. Reverse travel is
allowed only when the reverse edge is independently present in the controller
snapshot and physical-acceptance Profile. Physical acceptance sets
`max_speed` on every segment from the approved
`maximumDispatchSpeedMetersPerSecond` limit; it is never left to a controller
default.

The vendor API reference describes each `3066` item by reference to the `3051`
fixed-path fields. `task_id`, `source_id`, and `id` are required; `max_speed` is
an optional per-item field in metres per second. The documented `3066` wrapper
and the configured `0.3 m/s` field are therefore valid request syntax for the
current integration; they were not the cause of the first supervised attempt
being rejected before the command write.

## Dispatch lifecycle

The safe application boundary is a two-stage route decision:

1. MES reads the Adapter snapshot and creates a candidate route from the business
   source to the target. It records a `PathPlanned` event before dispatch, including
   the observed AGV station, candidate path, cost, and observation time.
2. Adapter treats that route as a proposal. It rechecks the active profile map,
   current station, online/idle state, control owner, and dispatch policy. A stale
   route is rejected; it is not silently sent to the vehicle.
3. The Vendor TCP driver performs the final live readiness check and rechecks
   control ownership immediately before writing `3066`. Only this final accepted
   route reaches the vendor protocol.
4. Adapter returns the AGV id, device task id, and route. MES persists the result
   and reconciles it through `1110`; WPF displays the returned current route.

For physical field-navigation acceptance, the second complete assessment runs
after control acquisition and before dispatch. If that assessment rejects, the
Adapter performs one best-effort `4006` release and preserves the original
preflight rejection (including its reasons); a release failure is logged and
does not replace that rejection. This rollback exists only before dispatch. The
Adapter never releases control automatically from `DispatchCoreAsync`, timeout
or unknown handling, reconciliation, or any other path where a `3066` write may
already have been attempted, because the vehicle may be moving.

The MES route is planning and audit data, not a safety decision. The controller,
Adapter and driver remain authoritative for actual movement permission.

## Task state and cancellation semantics

| Vendor task status | Adapter state | Meaning |
|---:|---|---|
| 0 (`StatusNone`) | `unknown` | Non-active historical record; never treat as accepted. |
| 1 | `accepted` | Accepted by the controller. |
| 2 | `moving` | Executing movement. |
| 3 | `paused` | Paused. |
| 4 | `arrived` | Completed. |
| 5 | `failed` | Failed. |
| 6 | `cancelled` | Confirmed cancelled. |
| 7 / 404 | `unknown` | Unresolved or unavailable. |

API `1110` returns an explicit `404 (NotFound)` item when a requested new
`task_id` does not yet exist. During the pre-dispatch idempotency check, an
all-`404` result means that no prior controller task was found and permits the
first `3066` attempt. Once a `3066` write has been attempted, the same `404` or
an empty result means `unknown` with `dispatch_not_confirmed_by_1110`; it never
causes an automatic resend. Any non-`404` status remains an idempotency hit and
also prevents a replacement command.

Mutation logging records an allowlisted audit summary. For `3066`, it includes
the segment task IDs, station IDs, and configured speed, followed by the raw
response `ret_code`, `err_msg`, and `create_on` fields. It does not log the
controller host or arbitrary unfiltered response data.

`3067` is the only configured standard cancellation API. A `ret_code=0`
response means the cancellation request was accepted; it is not proof that the
task is cancelled. Poll API `1110` and report `cancelled` only after the
controller confirms status `6`. Timeout, missing data, or any other terminal
ambiguity remains `unknown`.

API `3068` is not documented as a standard controller API in the confirmed
integration and is disabled. Do not configure it, probe it, or infer task
cleanup from its return value. A historical `StatusNone` record may remain in
the controller list; no deletion or status-rewrite operation is assumed.

## 2026-08-06 read-only preflight

The authorized read-only preflight reached the current controller with APIs
`1100` and `1101`. API `1101` requires the request body
`{"return_laser":false}`; the Adapter now sends and tests that exact semantic
payload.

The live response confirmed map `guangzhou606`, MD5
`e1b8d6b2b24362c1d44f1884c0abd8fb`, station `LM1`, localization confidence
`0.9859`, stopped motion, and no reported emergency, block, error, or fatal
condition. It did not provide a confirmed automatic-mode signal, map version,
or direct-edge list. The observed `dispatch_mode=0` is not treated as proof of
automatic mode because its site-specific safety meaning has not been approved.

This is a partial read-only pass, not movement acceptance. No control,
navigation, or cancellation API was sent, and the physical Profile remains
dispatch-disabled.

The offline read-only contract is covered by Adapter and E2E tests. The fake
controller observes `1060`, `1110`, `1101`, `1300`, `1301`, `1302`, and `4011`;
no `4005`, `9300`, `3066`, `3067`, `3001`, or `3002` request is accepted in that
mode. The client combines the active map, station catalog, controller MD5, and
downloaded map version/directed edges into one timestamped evidence response.
A timeout, vendor error, malformed map, or internally inconsistent catalog
fails closed as `controller_map_evidence_unavailable` or a map mismatch.

## Required physical safety gates

Before any future `3066` dispatch, verify from current controller data:

- Adapter owns control and no other application controls the AGV.
- The loaded map name, version, MD5, stations, and direct edges exactly match
  the approved Profile snapshot.
- Localization is valid and above the approved confidence threshold.
- No emergency stop, block, fault, fatal, or error condition is active.
- The vehicle is in approved automatic mode, or the explicitly approved
  `not-exposed-by-approved-model` policy is active for the exact controller
  model. Under that policy, a missing `1101.mode` is tolerated only after
  read-only API `1000.model` matches the Profile; an explicit `manual` value
  still blocks dispatch.
- The test is in an isolated area, at the approved low-speed limit, with
  current active-task state captured.

The current physical-acceptance configuration keeps automatic dispatch disabled
until a fresh live map comparison and all remaining safety gates are approved.
The historical map snapshot is not sufficient to enable unattended motion.

A mismatch is a read-only investigation condition: do not send motion, control,
or cancellation commands until a new approved snapshot and safety decision
exist.

## Current firmware operating-mode evidence

The vendor reference defines `1101.mode` as the vehicle operating mode:
`0=manual`, `1=automatic`. On the current controller firmware, direct `1101`,
passive default `19301`, and `1100` filtered with `keys:["mode"]` do not return
that field. API `1004` is documented and observed as position-only. APIs and
fields with different semantics, including `1030.dispatch_mode` and
`1028.fork_auto_flag`, must not be promoted to vehicle-mode evidence.

The supervised field-navigation entry point uses a named policy rather than
inventing an `automatic` value. The default `vendor-field-required` policy
remains fail-closed when the mode is unknown. The approved W500-SZ profile uses
`not-exposed-by-approved-model`: API `1000.model` must match `W500-SZ`, an
explicit `mode=0/manual` still blocks, and only a missing mode on that matching
model can pass this one gate. Its control sequence is:

1. Read snapshot, readiness, localization, active task, and authoritative map;
   evaluate every gate except control ownership.
2. If and only if phase 1 passes, request control.
3. Repeat the complete preflight including ownership.
4. Enter route dispatch, which rechecks ownership and driver readiness again
   immediately before `3066`.

This ordering prevents a failed map, localization, alarm, active-task, or
automatic-mode gate from causing a `4005` request.

## 2026-08-11 power-cycle read-only result

After a power cycle, the live controller returned model `W500-SZ` and version
`v3.4.8.0011` through API `1000`. It was online and idle at `LM1`, with
`reloc_status=1`, confidence `0.9651`, emergency/blocked `false/false`, and
Fatal/Error `0/0`. The controller map evidence still matched
`guangzhou606` / `1.0.6` / `816e68b9a367d9c8d5eaee9331a7ef58`, five stations, and
nine direct edges. The read-only response used policy
`not-exposed-by-approved-model`; normalized blockers were only
`adapter_does_not_hold_control` and `automatic_dispatch_disabled`.

## 2026-08-11 first supervised route attempt and diagnosis

One isolated standard-mode attempt used acceptance ID
`9fea739a-6f1e-402d-b8c2-fd70f4977c5e` for `LM1 -> LM2`. The preflight passed
and the Adapter acquired control, but the vehicle remained at `LM1`, no active
task appeared, a task-specific `1110` query returned `404`, and the global task
list remained empty. The operator then released control in the robot test
software; a subsequent `1060` read confirmed `locked=false`.

The old session did not contain raw mutation audit output, so no `3066`
response is claimed retroactively. Offline reproduction identified the actual
software gate: the pre-dispatch idempotency check treated the non-empty
`1110 status=404` response as an existing task and returned `unknown` before
the `3066` write. The persisted `dispatching -> unknown` transition with no
device error is consistent with that path. The client now distinguishes
NotFound from an existing task, confirms the result after the first write, and
prevents every automatic repeat of an attempted task ID.
