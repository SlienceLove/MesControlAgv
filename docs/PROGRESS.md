# AGV MES MVP Progress

Last updated: 2026-08-24

This is the concise active record. Detailed history remains in Git and the
linked acceptance documents.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2, G3, and G4 have passed overall acceptance.
- G5-A contracts, additive SQLite storage, and read-only scheduling projections
  have passed project acceptance.
- G5-B manual planning and G5-C runtime admission have passed project
  acceptance. G5-D independent WPF experiment-plan and task-scheduling pages,
  manual operations, resource swimlanes, and blocking explanations are
  implemented and await project acceptance.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G4-D controls MES scheduling
state only: it does not send pause/cancel commands to devices, retry Unknown
operations, add serial access, expose protocol/register fields, or enable any
CIC-D160+ write path.

## Latest verification

The G5-D Release gate completed on 2026-08-24:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **682 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 58, MES 110, WPF 225, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- G5-D focused coverage: **13/13 passed**. It covers G5 HTTP contracts, plan
  lifecycle, manual scheduling, deterministic blockers, swimlane geometry,
  active leases, page bindings, and the no-node/no-device boundary.

## Recent changes

### 2026-08-24 - G5-D planning and scheduling UI

- Added separate experiment-plan and task-scheduling pages with structured plan
  editing, completeness issues, task pool, resource timeline, and audit detail.
- Added manual job creation, schedule/reschedule, unschedule, cancel, and explicit
  admission against existing G5 APIs. No scheduler optimization, node advance,
  device command, serial access, or D160 write path was added.
- Implementation commit: `27d63c8`; project acceptance is pending.

### 2026-08-22 to 2026-08-24 - G5-C runtime admission

- Added explicit scheduled-job admission with pinned plan/workflow checks and a
  single transaction for workflow run, reservation conversion, leases, linkage,
  state projection, and audit.
- Made `ActiveResourceKey` the database mutex; runtime rejection and lease
  conflict leave no partial run or lease, while request replay stays durable.
- Added terminal release and startup reconciliation. Paused, unresolved Unknown,
  and expired non-terminal runs keep leases; recovery performs no device calls.
- Implementation commit: `75655cd`; project acceptance passed on 2026-08-24.

### 2026-08-21 - G5-B manual experiment scheduling

- Added draft validation/publication/version-copy commands and published-plan
  job creation with immutable plan/workflow references.
- Added manual schedule, reschedule, unschedule, and cancel behavior with
  Profile-backed resources, peak-capacity conflicts, and stable blocking reasons.
- Added request-ID idempotency, append-only audits, resource availability, and
  in-place G5-A schema upgrades without runtime admission or device behavior.
- Implementation commit: `2e5ef8f`; project acceptance passed.

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

## Historical trace

| Date | Retained trace |
| --- | --- |
| 2026-08-21 | G5-A planning foundation passed: immutable plan/workflow references, additive storage, and read-only projections. Implementation `b690289`; see [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md). |
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

1. Complete G5-D project acceptance using the WPF workflow in the G5 record.
2. Confirm G5 overall acceptance only after the plan, scheduling, blocker,
   resource-load, and admission views match project expectations.
3. Do not enter G6 before explicit approval; automatic
   scheduling, physical device commands, serial control, protocol fields, and
   D160 writes remain closed.

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
