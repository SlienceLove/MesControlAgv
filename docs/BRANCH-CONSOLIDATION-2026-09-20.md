# Branch consolidation — 2026-09-20

## Responsibilities

- `master`: verified release baseline. Promote only after integration regression and UI acceptance.
- `develop`: sole integration branch, initially based on UI commit `47975ad`.
- `feature/wpf-ui-layout-optimization`: subsequent UI work, synchronized with the accepted integration baseline before further optimization.
- Feature branches: bounded business/device changes targeting `develop`.
- `archive/*`: recovery references; not release candidates and not evidence of validation.

## Completed local preparation

An independent worktree exists at `.worktrees/branch-integration` on `develop`.
The original UI and three-device workspaces and staging areas have not been replaced or cleaned.

| Recovery reference | Commit | Contents |
| --- | --- | --- |
| archive/ui-base-20260920 | 47975ad | committed UI baseline |
| archive/workstation-base-20260920 | bf717ff | pinned workstation integration source |
| archive/admission-base-20260920 | 74cfb88 | material and physical-admission source |
| archive/ui-wip-20260920 | dacfc7a | UI tracked changes and selected untracked source/assets |
| archive/three-device-wip-20260920 | 4f17023 | three-device tracked changes and selected untracked source/assets |

WIP snapshots were created using an alternate index and `commit-tree`, preserving live workspaces.
They include tracked modifications and untracked files under src/tests/scripts/docs, excluding build directories, test results, logs and databases. They are source recovery snapshots, not complete backups of local field evidence. Other untracked/ignored data remains in the original directories.
No remote branches have been changed or deleted.

Baseline verification: solution NuGet restore succeeded; WPF tests passed 435/435 on the isolated, committed UI baseline. This does not validate any merged feature or WIP snapshot. The first test attempt lacked restore assets for service projects; restoring the full solution resolved that setup issue.

## Merge preflight

`git merge-tree --write-tree` detected 37 conflicts between the baseline and pinned workstation source, and 14 between the baseline and material/admission source. These were object-level previews; the integration checkout has no unresolved merge state.

The workstation conflict requires a behavioral decision, not just text resolution:

- Current baseline `WorkflowSampleWorkstationWorker.cs` uses approved template options and a controller to derive and execute multiple vendor writes.
- The workstation branch uses an existing vendor task number, preparation binding verification, sample/barcode validation and a single StartTask operation with subsequent observation.
- Both implementations use the same dispatcher/options class names and overlap on workflow contracts, persistence, recovery and WPF parameters.

Recommended decision: use the preparation/verification/existing-task path for new workstation operations, but preserve legacy saved workflows through an explicit compatibility path until migration is verified. Do not silently replace old node semantics or remove AGV/AUBO recovery behavior.
The user approved continuing with this compatibility principle. No further approval for the same choice is needed.

## Workstation integration checkpoint

All 37 workstation merge conflicts have been resolved. This checkpoint integrates the pinned workstation source, not later changes in its original worktree. Material/admission and the saved digital-twin/three-device WIP remain separate follow-up stages.

- Both template and existing-task node IDs, capabilities, schemas and command interfaces are retained.
- Sample records coexist with the new append-only preparation and verification records.
- Adapter registration uses the new driver for read access and retains the controlled legacy driver behind separate `/legacy/` routes. The MES legacy client and MES legacy control routes use that namespace too. Existing external clients using the old request-body control endpoints must explicitly migrate to `/legacy/`; read endpoints remain unchanged.
- Legacy command admission still checks ProtocolConfirmed in SampleWorkstationControlledDriver; the new command path uses its own capability/control checks.
- A duplicate vendor HTTP command method introduced by auto-merge was removed.
- Application, Adapter and MES compile successfully. Adapter regression: 441 passed; WPF: 509 passed; workflow contracts: 74 passed; MES: 545 passed, including two new merge-specific cases for node ownership and cross-capability device occupation. Total: 1,569 passing tests across these four projects.
- New and legacy workers filter by their own node type and share the transactional workstation claim gate. Persistence preserves both task number/preparation identity and legacy template version. Sample custody binding and preparation verification are both retained.
- Legacy settings now live under `WorkflowLegacySampleWorkstationWorker`. Move approved legacy Templates and activation settings there before using historical template execution. Both workers remain disabled in checked-in configurations; enabling control is a deployment decision, not part of this consolidation.
- Legacy read-error semantics use LegacySampleWorkstationAdapterClient; new preparation and task operations use SampleWorkstationAdapterClient. Old driver-name test fixtures have been adapted to the shared read interface.

Original UI/workstation/three-device workspaces remain untouched by this merge. This local integration checkpoint does not promote master, synchronize the UI checkout or publish remote refs. No physical device execution was used for verification.

## Remaining ordered work

1. Workstation integration is resolved using separate new and legacy paths; retain this checkpoint before proceeding.
2. Integrate material/admission changes; reconcile SampleManagement with inventory reservation and traceability identity.
3. Review WIP digital-twin and three-device snapshots; integrate approved source, keeping site-specific runtime configuration explicit.
4. Run builds and relevant offline regressions, then perform UI acceptance. Never run physical device commands as part of branch cleanup.
5. Promote the validated result to master, synchronize the UI branch, and publish agreed refs without force push.
6. Archive old feature branches only after verifying commit coverage and all worktree changes.

Release promotion and UI synchronization remain pending the remaining integration stages and acceptance.
