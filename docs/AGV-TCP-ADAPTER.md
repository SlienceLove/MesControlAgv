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

## Vendor protocol mapping

| Capability | Port | API |
|---|---:|---:|
| Control owner query/acquire | 19204 / 19207 | 1060 / 4005 |
| Fixed route navigation | 19206 | 3066 |
| Task status reconciliation | 19204 | 1110 |
| Pause/resume | 19206 | 3001 / 3002 |
| Standard route cancellation | 19206 | 3067 |
| Status push configuration | 19207 | 9300 |
| Status push stream | 19301 | 19301 |

Packets use the vendor 16-byte header, big-endian payload length and API number,
followed by UTF-8 JSON. The expected response API is request API plus `10000`.
A non-zero `ret_code` is an AGV error.

Every request channel is serialized. A timeout is unresolved until API `1110`
reconciliation completes; the Adapter must not generate a replacement `task_id`
merely because the original request timed out.

## Navigation request shape

API `3066` requires a `move_task_list` wrapper. A naked JSON array is not a
valid request shape.

```json
{
  "move_task_list": [
    { "task_id": "task-1", "source_id": "LM5", "id": "LM4" },
    { "task_id": "task-2", "source_id": "LM4", "id": "LM1" }
  ]
}
```

Every segment must use a controller-confirmed direct edge. Reverse travel is
allowed only when the reverse edge is independently present in the controller
snapshot and physical-acceptance Profile.

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

## Required physical safety gates

Before any future `3066` dispatch, verify from current controller data:

- Adapter owns control and no other application controls the AGV.
- The loaded map name, version, MD5, stations, and direct edges exactly match
  the approved Profile snapshot.
- Localization is valid and above the approved confidence threshold.
- No emergency stop, block, fault, fatal, or error condition is active.
- The vehicle is in approved automatic mode.
- The test is in an isolated area, at the approved low-speed limit, with
  current active-task state captured.

The current physical-acceptance configuration keeps automatic dispatch disabled
until a controller map-query or equivalent live map verification is implemented.
The historical map snapshot is not sufficient to enable unattended motion.

A mismatch is a read-only investigation condition: do not send motion, control,
or cancellation commands until a new approved snapshot and safety decision
exist.
