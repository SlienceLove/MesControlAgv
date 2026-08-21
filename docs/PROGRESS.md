# AGV MES MVP Progress

Last updated: 2026-08-21

This is the concise active record. Detailed history remains in Git and the
linked acceptance documents.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2 and G3 have passed overall acceptance.
- G4-A and G4-B provide durable node/device records and a Simulator-only node
  worker while retaining legacy read compatibility.
- G4-C read-only runtime monitoring has passed project acceptance.
- G4-D audited pause, resume, safe cancellation, and explicit Unknown
  resolution are implemented and at the project acceptance gate.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G4-D controls MES scheduling
state only: it does not send pause/cancel commands to devices, retry Unknown
operations, add serial access, expose protocol/register fields, or enable any
CIC-D160+ write path.

## Latest verification

The G4-D Release gate completed on 2026-08-21:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **648 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 54, MES 93, WPF 212, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- A Release WPF process is connected to an isolated Simulator-only MES at
  `http://localhost:5045/`; five stable acceptance runs are listed in the
  [G4 acceptance record](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md).

## Recent changes

### 2026-08-21 - G4-D audited run controls

- Added server-authorized pause/resume, safe cancellation, and two explicit
  Unknown conclusions with required request ID, operator, reason, idempotency,
  and append-only audit evidence.
- Pause blocks future claims but does not pause devices. Cancel rejects active
  or Unknown evidence. Unknown resolution updates existing evidence and never
  resends a command.
- Added WPF permission checks, disabled reasons, confirmations, and a dedicated
  Unknown panel without a Retry action.

### 2026-08-21 - G3 typed input compatibility correction

- Added a shared narrow runtime input projection so published Timed Wait uses
  immutable `durationSeconds`, while legacy free-form overrides remain valid.
- Prevented Timed Wait audits from fabricating device evidence; legacy audits
  without the new field retain their fallback behavior.

### 2026-08-21 - G4-C read-only runtime monitor

- Added a monitor for the pinned version, node attempts, device operations,
  and audit timeline by run ID.
- Reused the read-only Nodify Runtime surface while preserving viewport state,
  and rejected cross-run, cross-version, or unlinked evidence.
- Kept Unknown visually distinct and explicitly non-retryable.

### 2026-08-21 - G4-B node-driven Simulator worker

- Migrated existing Simulator Move and Timed Wait dispatch/recovery to durable
  node records and stable device operation IDs.
- Timed Wait persists its start time and creates no device operation. Existing
  G3 runs are backfilled only when active G4 records do not already exist.

### 2026-08-21 - G4-A runtime records and read APIs

- Added node execution, device operation, and timeline contracts, SQLite
  storage, compatibility projection/dual writes, and read-only APIs.
- Existing execution reads remain unchanged; no worker or device behavior was
  added in G4-A.

## Historical trace

| Date | Retained trace |
| --- | --- |
| 2026-08-21 | G3 overall acceptance: typed catalog, strict publication gate, schema-driven inspector, and issue navigation. See [G3 acceptance](EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md). |
| 2026-08-20 | G2 overall acceptance: one canonical v2 graph editor, Nodify canvas, compatibility importer, and lossless MES lifecycle. See [G2 acceptance](EXPERIMENT-WORKFLOW-G2-ACCEPTANCE.md). |
| 2026-08-19 | G1 canvas evaluation and graph convergence baseline. See [G1 acceptance](EXPERIMENT-WORKFLOW-G1-ACCEPTANCE.md). |
| 2026-08-17 to 2026-08-18 | Durable recovery/audit, Simulator-only Wait/Move, read-only Instrument Gateway, and D160 protocol evidence. |
| 2026-08-04 to 2026-08-13 | MES/Adapter/Simulator MVP, WPF operations/map work, physical AGV supervised acceptance, idempotent transport, and read-only preflight. Production remained NO-GO. |

## Retained safety baselines

- CIC-D160+ production integration remains read-only and allowlisted. Pump,
  temperature, flow, method, injection, and analysis controls remain disabled.
- Every physical AGV connection or movement requires fresh authorization and a
  separate read-only preflight. Production and unattended operation remain
  **NO-GO**.
- Robot arm and vision scaffolding has no G4 workflow execution authorization.

## Next gate

1. Complete the G4-D manual checks recorded in
   [EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md).
2. Record the project acceptance conclusion and stop at G4 overall acceptance.
3. Do not begin G5 without explicit authorization. Physical device commands,
   serial control, and D160 writes remain outside the authorized scope.

## Planning and evidence

- [G4 acceptance](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md)
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
