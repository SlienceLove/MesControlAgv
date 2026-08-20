# Experiment Workflow G2 Acceptance Record

Status: G2-A, G2-B, and G2-C implemented; waiting for project acceptance before G3 typed-node work.

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

### G2-C: single-editor and compatibility-boundary cleanup

- The historical `实验流程设计` tab, its independent `ExperimentFlowEditorViewModel`, and its editor-only dialogs have been removed. `实验流程管理` is now the only normal workflow editing entry.
- `ExperimentFlowConfigDto` remains only as a deserialization shape behind the explicit `兼容导入` action. The application never writes that format and cannot navigate back into the retired editor.
- `WorkflowEditorViewModel` owns a canonical collection of immutable `WorkflowGraphDocument` snapshots. The observable WPF models are a presentation projection only; local save, MES draft/validation/publication, canvas history, and viewport persistence read from the canonical documents.
- Property-panel edits are committed to the canonical document before the canvas is refreshed. Canvas edits, undo/redo, node deletion, edge deletion, layout, and viewport updates rebuild the presentation projection from that same document.
- `WorkflowDocumentImporter` recognizes current and v1 graph envelopes, individual/array graph documents, historical WPF arrays, and the retired experiment-editor DTO. It reports source format, schema, workflow/node/edge counts, synthesized sequential edges, unknown fields, downgraded node types, dangling references, and duplicate IDs.
- A conversion containing any error is rejected as a whole. Future schemas, duplicate workflow IDs, malformed IDs, or dangling connections cannot partially replace the active editor state. Intentionally disconnected v2 documents remain disconnected.
- The conversion report is shown immediately after explicit import and remains expandable in the workflow editor. Automatic startup migration also exposes the report instead of silently completing conversion.
- G2-C adds no node capability, execution worker, device route, serial access, or physical command.

## Verification

| Check | Result |
| --- | --- |
| Graph/domain adapter tests | 28/28 passed |
| WPF editor/store/canvas/import tests | 192/192 passed |
| MES workflow/runtime tests | 70/70 passed |
| Full Release solution tests | 577 passed, 5 existing E2E skipped, 0 failed |
| Full Release solution build | 0 warnings, 0 errors |

The main window was also started from Release output with fresh temporary service data and isolated Simulator ports `5583/5541/5545`. UI automation confirmed that `实验流程管理` is present, `实验流程设计` is absent, `兼容导入` is available, and the default canvas reports `节点 6`, `边 5`, and `已加载 5 条连接。`. The startup migration report was visible as `旧 WPF 流程：2 个流程，12 个节点，10 条边；迁移生成 10 条顺序边；转换完成`. A DPI-aware visual check confirmed that the links render and the report, canvas, and property panel do not overlap. The existing workflow file hash was unchanged, the application closed normally, and all temporary service ports were released.

## Manual acceptance node G2-B

1. Start the normal WPF application and open `实验流程管理`. Confirm that `标准搬运实验` shows six nodes, five visible links, `节点 6`, and `边 5`.
2. Select nodes from both the canvas and the right-side list. Confirm that both selections stay synchronized and that property edits immediately update the canvas.
3. Move a node, add an `仪器操作` node from the palette, create or delete a connection, then use undo and redo. Confirm that node data, parameters, layout, and links return together.
4. Use `自动布局` and `适应画布`; pan and zoom, switch workflows, and return. Confirm that the viewport is retained without consuming an undo step.
5. Save locally, restart WPF, and confirm that the JSON root is `mes.workflow.graph`, schema version is `2`, and positions, parameters, ports, explicit edges, and viewport round-trip.
6. Confirm that an intentionally disconnected v2 draft remains disconnected. Import an edge-less v1/legacy linear workflow and confirm that only that old format receives sequential success links.
7. Confirm that no acceptance action opens a serial port, sends an AGV or robot-arm command, or writes to CIC-D160+.

## Manual acceptance node G2-C

1. Start WPF and confirm that `实验流程管理` is present and the historical `实验流程设计` tab is absent.
2. Confirm that the normal preset still shows six nodes and five links, then edit a node property, undo, and redo. Confirm that the property panel and canvas return together.
3. Use `兼容导入` with an edge-less v1 graph or historical WPF array. Confirm that the report names the source format and the exact number of synthesized sequential edges.
4. Import a retired experiment-editor DTO containing an unknown field or historical node type. Confirm that the report identifies the field and marks the node type for manual review while preserving represented nodes, connections, conditions, colors, and layouts.
5. Attempt to import a future schema, dangling connection, or duplicate workflow ID. Confirm that the report contains a blocking error and that the workflow list and selected workflow remain unchanged.
6. Save an accepted import and restart. Confirm that the file is a `mes.workflow.graph` v2 envelope and no legacy format is written.
7. Confirm that none of these actions opens a serial port, contacts a physical AGV or robot arm, or writes to CIC-D160+.

## Remaining boundary before G3

- Graph-level validation focus, typed capability schemas, and schema-driven property editors remain G3 work.
- The observable WPF objects deliberately remain as a presentation projection because WPF controls require mutable observable bindings. They are not persistence, MES, or runtime owners.
- G2 does not enable runtime device execution beyond the previously accepted Simulator-only behavior.

Decision options:

- Accept G2 and continue to G3 typed nodes, capability catalog, and publication validation.
- Request G2-C import-report or main-editor interaction changes before starting G3.
