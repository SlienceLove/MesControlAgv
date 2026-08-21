# AGV MES MVP Progress

Last updated: 2026-08-21

This is the concise active record. Detailed session history remains in Git and
the linked acceptance/evidence documents.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2 has passed overall acceptance.
- G3-A catalog/schema contracts are complete in `321517e`.
- G3-B shared publication validation is complete in `eef0271`.
- G3-C schema-driven WPF inspection is complete in `ded1752`.
- G3-D issue navigation, lifecycle verification, and the acceptance package are
  implemented and have passed the automated gate.
- **G3 overall acceptance now awaits project confirmation. G4 has not started.**

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G3 does not authorize device
commands, serial access from WPF/MES, protocol/register fields in workflow
nodes, or any CIC-D160+ write path.

## Latest verification

The final G3 Release gate completed on 2026-08-21:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **621 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 54, MES 75, WPF 203, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- An isolated Release UI smoke showed 21 locatable validation issues (17 errors,
  4 warnings), correctly blocked publication, and preserved the user workflow
  file after `WPF_WORKFLOW_STORE_PATH` isolation was introduced.
- An isolated MES typed-graph lifecycle validated and published version 1, then
  read back all node/edge IDs, configuration, layout, viewport, and published
  version without loss.
- No dry-run, execution, device, serial, or D160 write endpoint was called by
  the G3 acceptance smoke.

## Recent changes

### 2026-08-21 - G3-D validation navigation and acceptance

- Added a validation issue panel with error/warning and location filters,
  contract metadata, configuration keys, and suggested actions.
- Selecting an issue now selects and focuses its node or edge through the
  existing canvas abstraction.
- Local and remote validation results share the same projection; invalid
  workflows cannot enable Publish.
- Added real WPF binding/navigation coverage and MES Draft -> Validate ->
  Publish -> version round-trip coverage for typed graphs.
- Added optional `WPF_WORKFLOW_STORE_PATH` injection for isolated UI/process
  checks. Existing dry-run and audit summaries remain visible.
- Full details and the focused manual gate are in
  [EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md](EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md).

### 2026-08-21 - G3-C schema-driven WPF inspector

- The seven catalog node types now create schema-backed fields and ports from
  stable IDs and defaults.
- Text, numeric, boolean, and closed-list controls replace free-form key/value
  editing; Profile stations/devices/capabilities are closed choices.
- Capability, execution-mode, and safety metadata are read-only and expose no
  protocol detail. Unknown fields/future schemas remain preserved and marked
  for migration.
- Configuration changes participate in canonical graph history, undo/redo,
  save, and restart round trips.

### 2026-08-21 - G3-A/G3-B contract and publication foundation

- Immutable node/capability catalogs define the first seven node types,
  schemas, ports, modes, safety policy, and Profile support while retaining
  graph document schema v2 compatibility.
- `workflow-publication-v2` validates typed configuration, Profile facts,
  capability policy, graph topology, default/exception paths, and cycles.
- MES preview, version validation, and publish use the same strict rule source;
  disabled D160 write capabilities cannot be enabled through JSON.
- `workflow-contract-v1` remains available for legacy published-runtime
  compatibility; optional exception-path findings remain non-blocking warnings.

## Historical trace

| Date | Retained trace |
| --- | --- |
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
- Robot arm and vision scaffolding has no G3 workflow execution authorization.

## Next gate

1. Project owner performs the focused G3 UI checks and responds **pass** or
   **changes required** using the G3 acceptance record.
2. Until that confirmation, only G3 fixes are allowed.
3. G4 is the earliest stage that may add runtime execution records/workers, but
   even G4 does not authorize physical device commands or D160 writes.

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
