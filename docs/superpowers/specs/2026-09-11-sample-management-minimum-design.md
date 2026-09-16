# Sample Management Minimum Design (P0)

## Goal

Provide a traceable sample record for the two-week single-run acceptance. This
is a custody ledger, not a full WMS: no inventory optimization, stock-taking,
or dynamic warehouse planning is included in this milestone.

## Entry paths

WPF supports both:

1. barcode/manual registration (`SampleId`, `Barcode`, batch, source location,
   optional container position and `RunId`);
2. operator-maintained CSV/XLSX template import with the same fields.

The template parser accepts English and Chinese header aliases. Import is
row-level partial success: valid rows are committed, while invalid or
conflicting rows return a row number and error without rolling back other rows.
Repeated SampleId/Barcode pairs are idempotent.

## Durable API and data flow

`SampleRecords` stores the current custody projection and `SampleEvents` stores
registration, Run binding, and device movement/operation events. `OperationId`
is the event idempotency key. A sample may be preloaded without a RunId, but a
device move is rejected until a workflow run is bound.

WPF talks only to MES. MES workers append events after confirmed device
operations:

```text
WPF scan/import -> MES sample record -> Run binding
AGV success -> InTransit + target location
AUBO/workstation success -> Processing/AtWorkstation + device id
Ion chromatography protocol_pending -> retain sample + explicit blocked state
```

The final report joins `RunId`, `OperationId`, sample identifiers, locations,
device IDs, vendor task numbers and result references. Unknown device outcomes
remain Unknown and never create a false Completed sample state.

## Two-week acceptance boundary

One physical sample can be registered or imported, bound to a run, moved by
AGV, processed by the available real devices, and queried with a complete event
timeline from WPF. The two CIC instruments remain `protocol_pending` until the
vendor module and field protocol are confirmed; this does not block sample
registration or the other device lanes.
