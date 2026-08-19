# Experiment Workflow G2 Acceptance Record

Status: implementation complete for the document and contract convergence slice; waiting for project acceptance before the main-window canvas replacement.

Date: 2026-08-19

## Scope delivered

- `WorkflowGraphDocument` now carries stable node type identifiers, schema versions, explicit edges, ports, edge metadata, layout, viewport, preset state, and published-version state.
- `WorkflowGraphContractAdapter` converts graph documents to the existing MES `WorkflowDefinition` contract and back. `NextNodeIds` remains populated for the current runtime, while explicit edges and layout remain available for the editor.
- `WorkflowDocumentMapper` isolates WPF observable collections from Contracts/Domain. The MES lifecycle in `WorkflowEditorViewModel` now goes through this graph adapter instead of a second hand-written node mapping.
- `WorkflowStore` writes the `mes.workflow.graph` envelope. It still reads the previous WPF JSON array and converts it on load; the old array is import-only and is not written again.
- The historical Nodify editor exports a graph document. Its `ExperimentFlowConfigDto` is retained only as an import compatibility shape; legacy node type, connection condition, connection color, and layout are preserved during conversion.
- No AGV, robot arm, CIC-D160+, serial, gateway, or instrument write path was added or called.

## Verification

| Check | Result |
| --- | --- |
| Graph/domain adapter tests | 26/26 passed |
| WPF editor/store/remote tests | 175/175 passed |
| MES workflow/runtime tests | 70/70 passed |
| WPF Release build | 0 warnings, 0 errors |

Full Release solution verification passed: 558 tests passed, 5 existing E2E tests skipped, and 0 tests failed.

## Manual acceptance node G2-A

1. Start the normal WPF application and open the experiment workflow tab. Save a workflow locally, close the application, and reopen it. Confirm that node names, parameters, explicit links, node positions, and viewport state remain unchanged.
2. Open the saved JSON and confirm that the root object contains `format: mes.workflow.graph`, not the legacy top-level array.
3. Load a legacy workflow JSON array from an earlier build through the compatibility import path. Confirm that it remains readable and that the next save produces the graph envelope.
4. With a local MES instance, run Load, Save Draft, Validate, Publish, and Dry Run. Confirm that the request is pinned to the returned version and that the graph is unchanged after the round trip.
5. Confirm that no manual acceptance step opens a serial port, sends an AGV command, sends a robot-arm command, or writes to CIC-D160+.

## Known boundary for the next node

The main-window editor still exposes a WPF observable projection for existing bindings, and its current manual canvas remains in place. `SelectedGraphDocument` is the read-only handoff for the next Nodify integration. The next acceptance node will replace that canvas through `IWorkflowCanvasSurface`, keep the projection only for compatibility, and add graph-level validation focus without changing MES or device behavior.

Decision options:

- Accept G2-A and continue to the main-window Nodify integration.
- Request changes to the adapter or legacy import behavior before UI replacement.
