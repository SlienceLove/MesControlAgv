# Experiment Workflow G2 Acceptance Record

Status: G2-A and G2-B implemented; waiting for project acceptance before G2-C cleanup and G3 typed-node work.

Date: 2026-08-20

## Scope delivered

### G2-A: document and contract convergence

- `WorkflowGraphDocument` carries stable node type identifiers, schema versions, explicit edges, ports, edge metadata, layout, viewport, preset state, and published-version state.
- `WorkflowGraphContractAdapter` converts graph documents to the existing MES `WorkflowDefinition` contract and back. `NextNodeIds` remains populated for the current runtime, while explicit edges and layout remain available for the editor.
- `WorkflowDocumentMapper` isolates WPF observable collections from Contracts/Domain. MES draft, validate, publish, version, and dry-run paths use the graph adapter rather than a second hand-written node mapping.
- `WorkflowStore` writes the `mes.workflow.graph` envelope. The historical WPF array remains import-only and is never written again.

### G2-B: main-window Nodify integration

- The hand-written main-window canvas has been replaced by `NodifyCanvasAdapter` behind `IWorkflowCanvasSurface`; Contracts, Domain, MES, and device projects do not reference Nodify.
- Node selection is synchronized with the existing property panel. Node properties, parameters, layout, connections, deletion, palette drag/drop, undo, redo, auto-layout, fit-to-content, and keyboard commands all update the same graph editor history.
- Viewport pan/zoom is persisted with throttling but does not create undo steps. Undo and redo preserve the current viewport and published-version metadata.
- Graph storage schema is now v2. Edge-less v1 graph documents and edge-less legacy WPF arrays receive sequential success edges during import. A v2 draft may intentionally remain disconnected and is not auto-repaired.
- Both preset workflows now contain six nodes, five explicit success edges, and matching `NextNodeIds`; the canvas no longer relies on implicit visual-only links.
- No AGV, robot arm, CIC-D160+, serial, gateway, or instrument write path was added or called.

## Verification

| Check | Result |
| --- | --- |
| Graph/domain adapter tests | 28/28 passed |
| WPF editor/store/canvas tests | 185/185 passed |
| MES workflow/runtime tests | 70/70 passed |
| Full Release solution tests | 570 passed, 5 existing E2E skipped, 0 failed |
| Full Release solution build | 0 warnings, 0 errors |

The main window was also started from Release output with a fresh temporary data directory and isolated Simulator ports `5583/5541/5545`. UI automation confirmed `节点 6`, `边 5`, and `已加载 5 条连接。`; a DPI-aware visual check confirmed that all five connections render and the right property panel does not overlap the canvas. The temporary services were stopped after the check.

## Manual acceptance node G2-B

1. Start the normal WPF application and open `实验流程管理`. Confirm that `标准搬运实验` shows six nodes, five visible links, `节点 6`, and `边 5`.
2. Select nodes from both the canvas and the right-side list. Confirm that both selections stay synchronized and that property edits immediately update the canvas.
3. Move a node, add an `仪器操作` node from the palette, create or delete a connection, then use undo and redo. Confirm that node data, parameters, layout, and links return together.
4. Use `自动布局` and `适应画布`; pan and zoom, switch workflows, and return. Confirm that the viewport is retained without consuming an undo step.
5. Save locally, restart WPF, and confirm that the JSON root is `mes.workflow.graph`, schema version is `2`, and positions, parameters, ports, explicit edges, and viewport round-trip.
6. Confirm that an intentionally disconnected v2 draft remains disconnected. Import an edge-less v1/legacy linear workflow and confirm that only that old format receives sequential success links.
7. Confirm that no acceptance action opens a serial port, sends an AGV or robot-arm command, or writes to CIC-D160+.

## Remaining G2-C boundary

- The historical `实验流程设计` tab and `ExperimentFlowConfigDto` compatibility path still exist. They must be retired from normal navigation or converted to an explicit import-only entry before claiming a single final business editor.
- The WPF observable workflow projection remains as a compatibility boundary for the property panel. G2-C should either remove it or document it as a deliberate presentation projection with one-way ownership from the graph document.
- Legacy import currently performs deterministic conversion but does not yet produce a user-visible conversion report. G2-C must report migrated edges and any unsupported fields instead of silently completing the import.
- Graph-level validation focus and schema-driven property editors remain G3 work. G2-B does not enable runtime device execution.

Decision options:

- Accept G2-B and continue with the bounded G2-C cleanup above.
- Request main-editor interaction or layout changes before removing the compatibility entry.
