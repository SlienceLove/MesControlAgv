# Supervised field-navigation acceptance

This is a software boundary for one approved, low-speed physical navigation
check. It is separate from normal MES transport tasks and is disabled by
default. It does not authorize a controller connection, a movement command, or
automatic dispatch by itself.

## Hard gates

Do not use these endpoints until the site owner has approved the session, the
vehicle is powered, the work area is isolated, an emergency stop and manual
takeover path are staffed, and a fresh read-only preflight has been recorded.

The active physical profile must keep normal automatic dispatch disabled. The
separate `EnableFieldNavigationAcceptance` switch must be explicitly enabled
only for the approved acceptance session. The Adapter performs its read-only
preflight immediately before the navigation command; map mismatch, unresolved
automatic mode, manual block, missing control ownership, safety faults,
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

## Read-only API surface

The MES endpoints are intended for a controlled operator client or an approved
future WPF acceptance module:

```text
POST /api/field-navigation-acceptances
GET  /api/field-navigation-acceptances/{id}
POST /api/field-navigation-acceptances/{id}/authorize
POST /api/field-navigation-acceptances/{id}/dispatch
POST /api/field-navigation-acceptances/{id}/cancel
```

The detail response includes the planned path, permit metadata, device task ID,
last error, and ordered audit trail. Store the correlated acceptance ID, MES
audit export, Adapter logs, and redacted controller responses in the field
acceptance record.

## Current boundary

This implementation has only been unit-tested with an in-memory fake Adapter.
It has not opened, controlled, dispatched, cancelled, or moved a real AGV.
The controller map catalog and directed-edge evidence still require a fresh,
authorized on-site read-only comparison before this flow can be used.
