# Supervised field-navigation acceptance

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

The post-power-cycle read-only comparison against the live W500-SZ controller
passed. API `1000` returned model `W500-SZ`/version `v3.4.8.0011`; the approved
`not-exposed-by-approved-model` policy permits a missing `1101.mode`, while an
explicit manual value remains a hard blocker. The live map, localization,
alarm, and idle checks also passed.

The first supervised standard-mode attempt used acceptance ID
`9fea739a-6f1e-402d-b8c2-fd70f4977c5e` and route `LM1 -> LM2`. Control was
acquired, but the vehicle did not move, the requested task read as
`404 (NotFound)`, and the global task list stayed empty. The operator released
control in the robot test software and `1060` then confirmed `locked=false`.

Offline reproduction found that the Adapter's pre-dispatch idempotency check
mistook the non-empty `404` record for an existing task and returned `unknown`
before writing `3066`. Because that session predated mutation audit logging, it
does not provide a raw `3066` response and none is inferred. This condition is
now covered by a regression test: all-`404` permits exactly one first write;
after a write attempt, `404` or an empty result remains
`dispatch_not_confirmed_by_1110` and can never trigger an automatic resend.

The flow is still not field-accepted. A new live attempt requires the repaired
build, a fresh read-only preflight, a new unique permit/task ID, renewed site
authorization, and the same supervised low-speed boundary.
