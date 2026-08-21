# AGV MES MVP Progress

Last updated: 2026-08-21

This is the concise active record. Detailed history remains in Git and the
linked acceptance documents.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2, G3, and G4 have passed overall acceptance.
- G5-A contracts, additive SQLite storage, and read-only scheduling projections
  have passed project acceptance.
- G5-B manual planning, deterministic conflicts, blocking reasons, and audit are
  implemented and verified, pending project acceptance. G5-C runtime leases and
  G5-D WPF pages have not started.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G4-D controls MES scheduling
state only: it does not send pause/cancel commands to devices, retry Unknown
operations, add serial access, expose protocol/register fields, or enable any
CIC-D160+ write path.

## Latest verification

The G5-B Release gate completed on 2026-08-21:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **662 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 57, MES 104, WPF 212, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- Focused coverage proves version pinning, idempotent replay, deterministic
  half-open conflicts, peak-capacity handling, additive upgrade, and that
  scheduling creates no workflow run, device operation, or runtime lease.

## Recent changes

### 2026-08-21 - G5-B manual experiment scheduling

- Added draft validation/publication/version-copy commands and published-plan
  job creation with immutable plan/workflow references.
- Added manual schedule, reschedule, unschedule, and cancel behavior with
  Profile-backed resources, peak-capacity conflicts, and stable blocking reasons.
- Added request-ID idempotency, append-only audits, resource availability, and
  in-place G5-A schema upgrades without runtime admission or device behavior.
- Implementation commit: `2e5ef8f`; project acceptance is pending.

### 2026-08-21 - G5-A planning record foundation

- Added separate plan, job, schedule-entry, reservation, and runtime-lease
  contracts with immutable plan/workflow version references.
- Added five additive SQLite tables and read-only plan, job, and schedule APIs.
- Kept legacy transport tasks and direct workflow admission unchanged; no
  scheduling command, lease acquisition, UI, or device behavior was added.
- Implementation commit: `b690289`.

### 2026-08-21 - G4-D audited run controls

- Added server-authorized pause/resume, safe cancellation, and two explicit
  Unknown conclusions with required request ID, operator, reason, idempotency,
  and append-only audit evidence.
- Pause blocks future claims but does not pause devices. Cancel rejects active
  or Unknown evidence. Unknown resolution updates existing evidence and never
  resends a command.
- Added WPF permission checks, disabled reasons, confirmations, and a dedicated
  Unknown panel without a Retry action.
- Project acceptance and G4 overall acceptance passed; implementation commit:
  `379fd59`.

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

## Historical trace

| Date | Retained trace |
| --- | --- |
| 2026-08-21 | G4 overall acceptance: durable runtime evidence, Simulator-only node execution, monitoring, and audited controls. See [G4 acceptance](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md). |
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
- Robot arm and vision scaffolding has no G5 workflow execution authorization.

## Next gate

1. Obtain project acceptance for the completed G5-B backend slice.
2. After acceptance, G5-C may add runtime admission and lease lifecycle only.
3. WPF scheduling pages remain G5-D; physical device commands, serial control,
   protocol fields, and D160 writes remain closed.

## Planning and evidence

- [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md)
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
