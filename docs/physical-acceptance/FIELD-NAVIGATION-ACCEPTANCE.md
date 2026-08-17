# Supervised field-navigation acceptance

Last updated: 2026-08-17

This is a software boundary for one approved, low-speed physical navigation
check. It is separate from normal MES transport tasks and is disabled by
default. It does not authorize a controller connection, a movement command, or
automatic dispatch by itself. It is a future **standard-mode** flow and must
never be enabled in `Adapter:RunMode=read-only-preflight`.

## Hard gates

Do not use these endpoints until the site owner has approved the session, the
vehicle is powered, the work area is isolated, an emergency stop and manual
takeover path are staffed, and a fresh read-only preflight has been recorded.

The active physical profile must keep normal automatic dispatch disabled. The
separate `EnableFieldNavigationAcceptance` switch must be explicitly enabled
only for the approved acceptance session. The Adapter performs its read-only
preflight immediately before the navigation command; map mismatch, an
unresolved required automatic-mode policy, explicit manual mode, manual block,
missing control ownership, safety faults,
localization failure, or an expired permit prevent motion.

## MES lifecycle

1. Create a draft with an enabled AGV, a source station, and a target station.
   MES plans the directed route from the approved local Profile map and stores
   the approved map name and MD5 with the record.
2. Authorize the draft with an operator, safety observer, unique permit ID, and
   an expiry time. An expired or duplicate permit is rejected.
3. Dispatch the authorized record once. MES marks the permit consumed and calls
   the Adapter's dedicated field-acceptance endpoint, not the normal transport
   dispatch endpoint.
4. Adapter runs read-only physical preflight, verifies the AGV is at the
   approved source station, then sends the approved path only if every gate
   passes.
5. MES persists `moving`, `accepted`, `rejected`, `failed`, or `unknown` and
   writes an audit entry for every transition. Gateway timeout or an unconfirmed
   result is `unknown`; it is never retried automatically.
6. Cancellation requires device confirmation. An absent or unconfirmed device
   result remains `unknown` and requires on-site investigation.

## Operator API surface (standard mode only)

The MES endpoints are intended for a controlled operator client or an approved
future WPF acceptance module:

```text
POST /api/field-navigation-acceptances
GET  /api/field-navigation-acceptances/{id}
POST /api/field-navigation-acceptances/{id}/authorize
POST /api/field-navigation-acceptances/{id}/dispatch
POST /api/field-navigation-acceptances/{id}/cancel
```

The endpoints above are state-changing operations and are rejected with `405`
by the read-only middleware. Use only a separate, restarted standard-mode
process after the hard gates and written authorization are complete. The detail
response includes the planned path, permit metadata, device task ID,
last error, and ordered audit trail. Store the correlated acceptance ID, MES
audit export, Adapter logs, and redacted controller responses in the field
acceptance record.

## Current boundary

The 2026-08-13 evidence supersedes the earlier no-movement attempt as the latest
completed field record:

- Stage 4 **PASS**: localization confidence reached `0.9708` and the session's
  map, model, localization, alarm, and idle gates passed.
- Stage 5 **PASS**: the separately authorized `LM1 -> LM2` segment completed at
  the configured `0.3 m/s` maximum, and control was released.
- Stage 6 **PARTIAL SUCCESS**: because there is no direct `LM2 -> LM1` edge, the
  return was split into sequential segments. `LM2 -> LM3` completed. During
  `LM3 -> LM1`, an obstacle event and external control takeover were recorded;
  the task paused and was then cancelled. `LM3 -> LM1` did not complete.

The temporary `0.92` confidence override used during Stage 6 is not a production
policy. A reviewed threshold decision and explicit risk acceptance are pending.
Normal automatic/batch dispatch and Push remain disabled. A multi-edge route
must be orchestrated as sequential single-segment tasks, with confirmed arrival
before dispatching the next segment.

The flow is still not production field-accepted. The last recorded `LM3`/idle
state is historical evidence only. Any new live connection or movement requires
a fresh isolated `read-only-preflight`, proof that control owner is `none` and
all map/model/localization/alarm/idle gates pass, shutdown of that read-only
process, a new unique permit/task ID, renewed site authorization, and the same
supervised low-speed boundary. It must not be treated as production **GO** or as
authorization for unattended or automatic dispatch.
