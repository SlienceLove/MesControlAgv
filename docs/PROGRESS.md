# AGV MES MVP Progress

Last updated: 2026-08-21

This file is the concise active progress record. Detailed historical session
logs remain available in Git history and in the linked acceptance/evidence
documents. Older work is intentionally retained only as a trace here.

## Current focus

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2 has passed overall acceptance.
- G3-A catalog/schema contracts are complete in commit `321517e`.
- G3-B shared publication validation is complete in commit `eef0271`.
- G3-C schema-driven WPF property projection and controls are implemented and
  have passed the automated gate; focused UI acceptance remains before G3-D.
- G3-D overall acceptance must pass before any G4 runtime work starts.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are still **NO-GO**. CIC-D160+ workflow
capabilities remain read-only. G3 does not authorize physical device commands,
serial access from WPF/MES, protocol/register fields in workflow nodes, or any
instrument write path.

## Latest verification

The G3-C Release gate completed on 2026-08-21:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **616 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 54, MES 74, WPF 199, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- No Adapter, InstrumentGateway, WPF device-control, serial, or D160 write path
  was added or exercised by G3-C.

## Recent changes

### 2026-08-21 - G3-C schema-driven WPF property panel

- Added stable Inspector and field ViewModels over the shared node catalog.
  Schema fields now select text, numeric, boolean, or closed-list controls.
- The WPF toolbox creates the seven G3 node types from stable `NodeTypeId` and
  schema definitions, copying catalog ports and new-node defaults.
- Station and device references are closed Profile choices. Unknown, disabled,
  or mismatched IDs cannot be submitted as free text.
- Selected-node capability, execution-mode, and safety metadata comes from the
  shared catalog; no protocol, endpoint, register, or command field is exposed.
- Unknown fields and unknown/future node schemas remain losslessly preserved,
  read-only, and explicitly marked as requiring migration.
- Configuration edits replace the canonical graph projection and participate
  in the existing canvas undo/redo history. Tests cover selection isolation,
  all seven types, Profile choices, compatibility preservation, and restart
  round trips.
- A startup binding regression was corrected by making read-only Inspector
  metadata explicitly one-way. An isolated full startup confirmed responsive
  WPF plus healthy Simulator, Adapter, and MES services, followed by clean
  shutdown and port release.

### 2026-08-21 - G3-B shared publication validation

Commit: `eef0271 feat(workflows): enforce G3 publication validation`

- `WorkflowValidator.Validate()` remains the `workflow-contract-v1` runtime
  compatibility validator for already-published definitions.
- `ValidateForPublication()` adds the strict `workflow-publication-v2` gate.
- Publication validation now covers document/node schema versions, required
  typed fields, value types and ranges, Profile support, enabled stations,
  static device declarations, capability policy, catalog-owned ports, edge
  semantics/cardinality, Start/End boundaries, reachability, default paths,
  exception paths, and cycle rejection.
- Validation issues can identify `NodeId`, `EdgeId`, and `ConfigurationKey`.
  Missing optional exception paths are warnings; warnings do not block publish.
- Static publication facts are separated from runtime state. Device online
  state, occupancy, controller readiness, serial ownership, and similar live
  facts remain runtime-admission concerns.
- The default Profile declares `CIC-D160-01` only with identify, read-status,
  and wait-until-stable capabilities. Instrument control remains disabled.
- MES in-memory preview, persisted version validation, and publish all use the
  same strict rule source. Publish always revalidates the persisted definition
  and current Profile snapshot instead of trusting cached success.
- Failed revalidation returns an unpublished version to Draft and records a
  `WorkflowPublicationBlocked` audit. Successful publication records validator,
  catalog, and Profile summary metadata.
- API and persistence tests cover Warning publication, JSON capability bypass,
  stale validation, Profile changes, and legacy v1 runtime compatibility.

### 2026-08-21 - G3-A catalog and schema contracts

Commit: `321517e feat(workflows): add G3 catalog contracts`

- Added immutable workflow node-type and device-capability catalogs with stable
  IDs, schema versions, configuration/result schemas, execution modes, safety
  classifications, Profile support, and enabled/control-enabled policy.
- Added the first seven typed node definitions: Start, End, AGV Move, Timed
  Wait, Manual Confirmation, Instrument Read Status, and Instrument Wait Until
  Stable.
- Restricted D160 write capabilities are visible as disabled catalog entries;
  graph JSON cannot enable them.
- Unknown node types and future schemas remain losslessly preservable for draft
  editing but are not publishable. Migrations are explicit and never silent.
- Graph Document schema remains v2; no unnecessary document-version bump or
  runtime/device integration was introduced.

### 2026-08-20 - G2 overall workflow editor acceptance

Primary commits: `c236fd7`, `c43c447`, and `041ddae`.

- The versioned graph document became the canonical workflow shape, including
  stable node type/schema fields, explicit edges, ports, layouts, and viewport.
- The main WPF workflow editor converged on one Nodify canvas behind
  `IWorkflowCanvasSurface`; the historical independent editor was removed.
- Canvas selection, property editing, connections, delete, drag/drop,
  undo/redo, layout, pan/zoom persistence, local storage, MES lifecycle calls,
  and compatibility imports operate on one canonical document history.
- Legacy WPF arrays and edge-less schema-v1 graphs are import-only inputs.
  Accepted output is always `mes.workflow.graph` schema v2, with a visible
  structured conversion report.
- Acceptance follow-up corrected Nodify link selection/hit testing/Delete and
  handled the actual `ValueTuple` connection-completion payload. The follow-up
  remained isolated from G3 commits and added no device path.
- Overall acceptance is recorded in
  [EXPERIMENT-WORKFLOW-G2-ACCEPTANCE.md](EXPERIMENT-WORKFLOW-G2-ACCEPTANCE.md).

### 2026-08-19 - G1 canvas evaluation and graph convergence start

Primary commits: `d5b137f` and `c236fd7`.

- The isolated canvas Spike established the diagram-library boundary and
  performance/interaction baseline before main-editor integration.
- The architecture selected a single shared graph contract rather than a WPF
  owned business model.
- Acceptance and evaluation details are in
  [EXPERIMENT-WORKFLOW-G1-ACCEPTANCE.md](EXPERIMENT-WORKFLOW-G1-ACCEPTANCE.md)
  and
  [EXPERIMENT-WORKFLOW-CANVAS-EVALUATION.md](EXPERIMENT-WORKFLOW-CANVAS-EVALUATION.md).

### 2026-08-17 to 2026-08-18 - runtime and instrument boundary baseline

- MES gained durable workflow snapshots, idempotent requests, stable transport
  operation IDs, fail-closed claim/completion, read-only recovery, and audit
  correlation. Ambiguous outcomes remain `Unknown` and are not auto-retried.
- Simulator-only Move and explicit Timed Wait advancement were verified.
  Legacy Instrument Operation remained metadata/prepared-only with no I/O.
- CIC-D160+ COM4 read-only verification and protocol comparison established the
  supported identity/status reads. The dedicated InstrumentGateway exposes
  normalized read-only health/identity/status routes and defaults disabled.
- Captured write mappings and narrowly supervised field probes are evidence,
  not production authorization. No general raw-register or workflow write API
  was opened.

## Retained operational baselines

### CIC-D160+

- Validated transport baseline: USB virtual serial, Modbus RTU, COM4,
  `115200 8N1`; the known read-only acceptance completed 30/30 valid responses.
- Supported production-facing integration remains read-only and allowlisted.
- Pressure safety semantics, some fault bits, `0x157F` meanings, and password
  session rules are not sufficiently closed for general control.
- Direct activation, pump/temperature control, method loading, injection,
  start/stop analysis, automatic retry, and workflow execution remain disabled.
- Authoritative documents:
  [protocol verification](ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md),
  [pressure safety gate](ION-CHROMATOGRAPHY-D160-PRESSURE-SAFETY-GATE.md), and
  [direct-control validation](ION-CHROMATOGRAPHY-DIRECT-CONTROL-VALIDATION.md).

### Physical AGV

- The supervised `LM1 -> LM2` stage passed at low speed after a fresh preflight.
- The segmented return reached `LM3`; `LM3 -> LM1` did not complete after an
  obstacle/control-owner event and was cancelled with control released.
- The last historical snapshot is not current readiness evidence. Every future
  connection or movement requires a fresh authorized read-only preflight,
  explicit movement authorization, a unique acceptance/task ID, and
  segment-by-segment dispatch for directed multi-edge routes.
- Production and unattended operation remain **NO-GO**. Evidence is indexed in
  [physical acceptance](physical-acceptance/README.md) and the retained
  `artifacts/physical-acceptance-202608*.md` records.

### Robot arm and vision

- Integration scaffolding and offline tests exist, but field protocol,
  calibration, interlock, recovery, and production safety evidence remain
  separate acceptance work.
- No robot/vision capability is authorized for G3 workflow execution.

## Historical trace

| Date | Retained trace |
| --- | --- |
| 2026-08-04 | Initial MES/Adapter/Simulator workflow, AGV communication extensions, batch import, KPI, task monitoring, and platformization planning. |
| 2026-08-05 | Platform boundary refactor, persisted workflow lifecycle/runtime work, and first physical AGV checkpoint. |
| 2026-08-06 to 2026-08-07 | Offline MES/Adapter transport loop, dispatch-gate hardening, read-only physical preflight, configurable WPF fleet flow, and recovery handoff. |
| 2026-08-10 | WPF `.smap` visualization, map/export improvements, and offline control-center readiness. |
| 2026-08-11 | Controller map identity, repeated read-only preflights, W500-SZ mode policy, serialized control release, Profile admission, and command-boundary isolation. |
| 2026-08-12 | Physical acceptance stages 0-3, active-Profile WPF settings, and fail-closed site gates. |
| 2026-08-13 | Physical stages 4-6: preflight and first route passed; return route remained partial; production stayed NO-GO. |
| 2026-08-14 | Early Nodify experiment editor and optimized SMAP visualization groundwork. |
| 2026-08-17 | Workflow recovery, read-only InstrumentGateway, direct-protocol tooling/plans, and P0/P1 offline closure. |
| 2026-08-18 | D160 read-only/protocol evidence, controlled capture correlation, pressure mapping, and workflow Wait/Instrument metadata. |

The former step-by-step entries, intermediate test counts, local pause notes,
and repeated resume instructions are intentionally omitted here. Git history
and the linked evidence documents remain the authoritative audit trail.

## Next gates

1. **G3-C focused UI acceptance:** verify the seven toolbox entries, schema
   controls, Profile selectors, node switching, undo/redo, and save/restart in
   the desktop editor.
2. **G3-D:** add validation issue presentation and node/edge focus, complete MES
   lifecycle/UI smoke coverage, and write the G3 acceptance record.
3. **G4:** remains blocked until G3 overall acceptance. It is the earliest stage
   that may discuss new runtime workers, and any physical device operation still
   requires its own evidence and authorization.

## Planning and evidence index

- [Experiment workflow architecture](EXPERIMENT-WORKFLOW-ARCHITECTURE.md)
- [Experiment workflow UI design](EXPERIMENT-WORKFLOW-UI-DESIGN.md)
- [Experiment workflow implementation plan](EXPERIMENT-WORKFLOW-IMPLEMENTATION-PLAN.md)
- [G3 start handoff](EXPERIMENT-WORKFLOW-G3-START-HANDOFF.md)
- [Physical acceptance index](physical-acceptance/README.md)
- [Ion chromatography protocol verification](ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md)

## Maintenance rule

Keep the current state and roughly the latest three to five milestones detailed.
When a milestone becomes historical, reduce it to one trace row and retain links
to its acceptance record, evidence, and commits instead of appending another
full session transcript.
