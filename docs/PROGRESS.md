# AGV MES MVP Progress

Last updated: 2026-08-21

This is the concise active record. Detailed session history remains in Git and
the linked acceptance/evidence documents.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2 and G3 have passed overall acceptance.
- G4-A runtime record contracts, SQLite tables, compatibility dual writes, and
  read-only APIs are implemented and have passed the automated gate.
- **G4-B worker migration has not started.** Existing `PendingStep` behavior
  remains the execution source until that gate is implemented and verified.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G4-A adds records and reads;
it does not authorize new device commands, serial access from WPF/MES,
protocol/register fields in workflow nodes, or any CIC-D160+ write path.

## Latest verification

The G4-A Release gate completed on 2026-08-21:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **626 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 54, MES 80, WPF 203, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- Upgrade coverage starts MES against an existing G3 SQLite schema and verifies
  both runtime tables and indexes are added without recreating prior tables.
- Persistence coverage verifies restart reads, stable legacy backfill IDs,
  attempts, device evidence, timeline links, and unchanged legacy reads.
- Verification did not start services or call device, serial, or D160 write
  endpoints.

## Recent changes

### 2026-08-21 - G4-A runtime records and read APIs

- Added `NodeExecution`, `DeviceOperation`, and run timeline read contracts with
  whitelisted summaries and stable identity fields.
- Added `WorkflowNodeExecutions` and `WorkflowDeviceOperations` with in-place
  SQLite startup upgrade support.
- Admission, claim, and completion atomically dual-write the legacy execution
  row and new evidence records. Old pending/running rows project stable IDs and
  are backfilled without losing their attempt number.
- Added `/api/workflow-runs/{id}` read endpoints and WPF client methods. Existing
  `/api/workflow-executions/...` reads remain unchanged.
- Timed Wait does not fabricate a device operation, and no worker behavior or
  device control path changed in G4-A.

### 2026-08-21 - G3 overall acceptance

- Catalog/schema contracts, shared publication validation, schema-driven WPF
  inspection, issue navigation, and typed graph lifecycle verification passed.
- Project confirmation is recorded in
  [EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md](EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md).
- Commits: `321517e`, `eef0271`, `ded1752`, and `6b8e059`.

## Historical trace

| Date | Retained trace |
| --- | --- |
| 2026-08-21 | G3 overall acceptance: typed catalog, strict publication gate, schema-driven inspector, and issue navigation. See [G3 acceptance](EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md). |
| 2026-08-20 | G2 overall acceptance: one canonical v2 graph editor, Nodify canvas, compatibility importer, and lossless MES lifecycle. See [G2 acceptance](EXPERIMENT-WORKFLOW-G2-ACCEPTANCE.md). |
| 2026-08-19 | G1 canvas evaluation and graph convergence baseline. See [G1 acceptance](EXPERIMENT-WORKFLOW-G1-ACCEPTANCE.md). |
| 2026-08-17 to 2026-08-18 | Durable workflow recovery and audit, Simulator-only Wait/Move, read-only Instrument Gateway, and D160 protocol evidence. |
| 2026-08-12 to 2026-08-13 | Supervised physical AGV acceptance stages; the outbound segment passed, return remained partial, production stayed NO-GO. |
| 2026-08-04 to 2026-08-11 | MES/Adapter/Simulator MVP, WPF operations/map work, idempotent transport, recovery, Profile admission, and read-only physical preflight. |

## Retained safety baselines

- CIC-D160+ production-facing integration remains read-only and allowlisted.
  Pressure/fault/password semantics are not sufficiently closed for general
  control; activation, pump/temperature/flow control, method loading,
  injection, analysis start/stop, and workflow writes remain disabled.
- Every future physical AGV connection or movement requires a fresh authorized
  read-only preflight and separate task authorization. Production and
  unattended operation remain **NO-GO**.
- Robot arm and vision scaffolding has no G4 workflow execution authorization.

## Next gate

1. Review G4-A contracts, upgrade behavior, dual-write evidence, and read APIs.
2. G4-B may then migrate Simulator Move and Timed Wait to use node execution
   records as the execution and restart-recovery source.
3. Physical device commands, serial control, and D160 writes remain outside the
   authorized scope throughout G4.

## Planning and evidence

- [G3 acceptance](EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md)
- [Experiment workflow architecture](EXPERIMENT-WORKFLOW-ARCHITECTURE.md)
- [Experiment workflow UI design](EXPERIMENT-WORKFLOW-UI-DESIGN.md)
- [Experiment workflow implementation plan](EXPERIMENT-WORKFLOW-IMPLEMENTATION-PLAN.md)
- [Physical acceptance index](physical-acceptance/README.md)
- [Ion chromatography protocol verification](ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md)

## Maintenance rule

Keep only the current state and the latest three to five milestones detailed.
Reduce older work to one trace row and link to its acceptance record, evidence,
and commits instead of appending session transcripts.
