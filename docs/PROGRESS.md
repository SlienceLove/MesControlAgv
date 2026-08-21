# AGV MES MVP Progress

Last updated: 2026-08-21

This is the concise active record. Detailed session history remains in Git and
the linked acceptance/evidence documents.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2 and G3 have passed overall acceptance.
- G4-A runtime record contracts, SQLite tables, compatibility dual writes, and
  read-only APIs are implemented and have passed the automated gate.
- G4-B has migrated Simulator Move and Timed Wait dispatch/recovery to durable
  node execution records while retaining legacy run-level compatibility APIs.
- **G4-C has not started.** Read-only runtime visualization remains the next
  gate and must use the existing G4 records and APIs.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G4-A/B add durable records and
migrate only the existing Simulator worker; they do not authorize new device
commands, serial access from WPF/MES, protocol/register fields in workflow
nodes, or any CIC-D160+ write path.

## Latest verification

The G4-B Release gate completed on 2026-08-21:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **629 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 54, MES 83, WPF 203, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- MES coverage verifies node-driven Move/Timed Wait dispatch, persisted adapter
  progress, exact node-operation completion matching, and no wait-device record.
- Restart coverage recreates the DbContext, application service, dispatcher,
  and adapter before reconciling from persisted node and device evidence.
- Compatibility coverage verifies pre-G4 backfill and proves conflicting legacy
  mirrors cannot create duplicate operations or replace active node records.
- Verification did not start services or call device, serial, or D160 write
  endpoints.

## Recent changes

### 2026-08-21 - G4-B node-driven Simulator worker

- Added durable node work-item and completion boundaries for trusted runtime
  workers; legacy execution-level methods remain available for compatibility.
- Migrated existing Simulator Move and Timed Wait processing and startup
  recovery from `PendingStep` to `NodeExecution` plus `DeviceOperation`.
- Move dispatch reuses its persisted operation ID and records Accepted/Running
  evidence without duplicate audits. Timed Wait uses persisted `StartedAt` and
  never creates a device operation.
- Existing G3 runs are backfilled only when no active node record exists;
  conflicting legacy mirrors are ignored once G4 records are present.
- No Adapter command, physical-device path, serial access, or D160 write was
  added.

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

1. Review G4-B node-driven dispatch, persisted progress, restart recovery, and
   legacy compatibility evidence.
2. G4-C may then add read-only runtime visualization and node/device evidence
   details using the existing G4 contracts and APIs.
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
