# AGV MES MVP Progress

Last updated: 2026-08-24

This is the concise active record. Detailed history remains in Git and the
linked acceptance documents.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2, G3, and G4 have passed overall acceptance.
- G5-A contracts, additive SQLite storage, and read-only scheduling projections
  have passed project acceptance.
- G5-B manual planning, G5-C runtime admission, and G5-D independent WPF
  planning/scheduling pages have passed project acceptance. G5 overall
  acceptance passed on 2026-08-24; the project authorized entry into G6.
- G6-A versioned advanced-flow contracts and deterministic static publication
  gates have passed project acceptance. G6-B durable conditions, external
  signals, and manual confirmations are implemented and await project acceptance.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G6-B changes MES workflow state
only: it does not send pause/cancel commands to devices, retry Unknown operations,
add serial access, expose protocol/register fields, or enable any CIC-D160+ write
path.

## Latest verification

The G6-B Release gate completed on 2026-08-24:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **705 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 70, MES 120, WPF 226, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- G6-B focused coverage: **22/22 passed**. It covers typed decisions, durable
  interaction replay, timeout, pause/cancel, Unknown continuation, restart
  recovery, HTTP mappings, additive schema upgrade, and absence of advanced-node
  device operations.

## Recent changes

### 2026-08-24 - G6-B durable interaction runtime

- Enabled server-side typed conditions, durable early external signals, and
  audited manual outcomes with explicit success/timeout/cancelled paths.
- Added additive interaction storage, restart-safe matching, serialized controls,
  output evidence retention, and isolated background recovery. Parallel, subflow,
  compensation, automatic scheduling, device control, serial, and D160 writes stay closed.
- Implementation commit: `e749489`; project acceptance is pending.

### 2026-08-24 - G6-A advanced-flow contracts

- Advanced the graph schema to v3 while keeping v2 publishable and preserving
  legacy free-form conditions as unpublishable compatibility data.
- Added typed condition, signal wait, parallel, pinned subflow, and compensation
  contracts with deterministic publication rules. All advanced nodes remain
  runtime-disabled in the G6-A baseline.
- Implementation commit: `3f184d7`; project acceptance passed on 2026-08-24.

### 2026-08-24 - G5-D planning and scheduling UI

- Added separate experiment-plan and task-scheduling pages with structured plan
  editing, completeness issues, task pool, resource timeline, and audit detail.
- Added manual job creation, schedule/reschedule, unschedule, cancel, and explicit
  admission against existing G5 APIs. No scheduler optimization, node advance,
  device command, serial access, or D160 write path was added.
- Implementation commit: `27d63c8`; final header alignment: `858d3bd`; project
  acceptance and G5 overall acceptance passed on 2026-08-24.

## Historical trace

| Date | Retained trace |
| --- | --- |
| 2026-08-22 to 2026-08-24 | G5-C transactional runtime admission, active lease mutex and restart reconciliation passed; implementation `75655cd`. See [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md). |
| 2026-08-21 | G5-B manual planning, scheduling, idempotent commands and additive upgrade passed; implementation `2e5ef8f`. See [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md). |
| 2026-08-21 | G4-D audited pause/resume, cancellation and Unknown resolution passed; implementation `379fd59`. See [G4 acceptance](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md). |
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

1. Complete project acceptance for G6-B using the reproducible service/API steps
   in the G6 acceptance record. Until confirmation, fix G6-B only and do not begin
   parallel, subflow, or compensation runtime.
2. Keep automatic scheduling, physical device commands, serial control,
   protocol/register fields, and D160 writes closed unless separately authorized
   by a later device-specific safety gate.

## Planning and evidence

- [G6 acceptance](EXPERIMENT-WORKFLOW-G6-ACCEPTANCE.md)
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
