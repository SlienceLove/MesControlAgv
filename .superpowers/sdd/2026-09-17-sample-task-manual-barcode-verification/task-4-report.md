# Task 4 report: WPF 页内样品核对

## Status

Completed. The existing “任务排程” detail area now provides the minimal MES-backed manual sample-verification entry point. It does not add navigation, device access, scanner input, or a workstation-specific start action.

## Implementation

- Added the central-sample and current-verification HTTP operations to `IMesClient` and `MesClient`, including `404`-to-null handling for a missing current snapshot.
- Added presentation models for snapshot rows and row-level MES validation results.
- The scheduling ViewModel clears snapshot state on selection, loads the selected job's verification/sample data through `IMesClient`, and ignores late responses for a previously selected job.
- It identifies a workstation workflow from its immutable MES workflow version, disables `CanAdmit` until the current snapshot is `Verified`, and shows an explicit reminder that the backend is authoritative. Non-workstation jobs retain the prior admission behavior.
- Added central sample registration/edit + snapshot-row save, direct (no second confirmation) visual verification completion, verified-row read-only display, and an invalidation message after a changed verified snapshot is saved.
- Added the page-inline “样品核对” panel with batch, revision, status, short snapshot summary, last verifier/time, ordered rows, row validation, and minimal editor. The completion message explicitly does not claim task-table upload or instrument arrival.

## Changed files

- `src/MesControlAgv.Wpf/Services/IMesClient.cs`
- `src/MesControlAgv.Wpf/Services/MesClient.cs`
- `src/MesControlAgv.Wpf/ViewModels/ExperimentSampleVerificationModels.cs` (new)
- `src/MesControlAgv.Wpf/ViewModels/ExperimentSchedulingViewModel.cs`
- `src/MesControlAgv.Wpf/Experiments/ExperimentSchedulingView.xaml`
- `tests/MesControlAgv.Wpf.Tests/MesClientExperimentSchedulingHttpContractTests.cs`
- `tests/MesControlAgv.Wpf.Tests/ExperimentSchedulingViewModelTests.cs`
- `tests/MesControlAgv.Wpf.Tests/ExperimentPlanningViewBindingTests.cs`

## Tests

Command:

```powershell
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
```

Result: passed — 20 total, 20 passed, 0 failed, 0 skipped (1 s).

An initial isolated-artifacts attempt did not start because a single shared `BaseIntermediateOutputPath` made projects share an incompatible `project.assets.json`; it performed no test execution. The final command used the normal existing build paths and completed successfully without stopping any process.

## TDD evidence

The new ViewModel test initially failed on selecting a scheduled task from the task-pool-only collection (`Sequence contains no matching element`); the test was corrected to switch via the existing refresh/select path. Review then identified real async-state defects: optimistic admission while workstation detection was pending, stale mutation response application after task switching, and save input/row data being read after a selection change. The ViewModel now blocks admission until the predicate resolves, checks the active job/load generation before applying write results, and freezes save intent before its first await; deterministic tests cover all three races. The focused suite then passed 20/20.

## Self-review

- All WPF operations cross the MES HTTP boundary through `IMesClient`; no SQLite, Adapter, vendor address, or device code was added.
- Current verification load and mutation-result application are selection-version guarded to prevent stale data appearing after a task switch; save requests also snapshot the originating task's inputs/rows before I/O.
- Workstation admission is conservatively disabled while its verification requirement is unknown or cannot be read.
- Completion bypasses `IExperimentSchedulingConfirmation`; admission still preserves its existing confirmation and backend gate.
- The XAML adds no scanner control and leaves navigation/field-acceptance surfaces untouched.
- `git diff --check` completed without whitespace errors.

## Concerns

- The page’s workstation predicate is a timely UX hint based on the fetched immutable workflow version. MES runtime admission remains the final authority, including all concurrent-change checks.

## Fix round 1

- Added the additive `BusinessSampleId` snapshot field. Snapshot identity now hashes only business sample identifier, barcode, position and order; `DisplayName` is retained for display but no longer causes invalidation. Existing JSON without the new field remains readable.
- A registered business-identifier change now invalidates snapshots alongside existing registered-sample drift. Added API coverage proving each identity field changes a verified revision while a display-name-only row change reuses it.
- WPF retains the complete batch sample map across save and completion projections, snapshots action actor/reason before I/O, refreshes the current verification after a failed second-stage snapshot save, and treats workflow-predicate/read failures as unresolved (therefore admission-blocking). Ordinary jobs resolve before optional verification projection calls.

Commands/results:

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore
# Passed: 336 / 336
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
# Passed: 20 / 20
```

## Fix round 2

- Added a registration-API regression test proving a business-sample-identifier-only central update invalidates an already verified snapshot and is rejected by the verification gate.
- Treat missing workflow versions as unresolved/error rather than ordinary tasks; failed post-registration authoritative reload now clears the cached verification and conservatively disables admission.

Commands: `ExperimentSampleVerificationApiTests` 8/8; MES full 337/337; Task 4 WPF directional suite 20/20.

## Fix round 3

- Added `Multi_row_sample_numbers_survive_save_and_completion`, proving row business identifiers remain intact after completion rather than falling back to display labels.
- Extended `Saving_a_row_snapshots_old_task_inputs_before_selection_changes` to prove both MES write requests retain the actor/reason captured before the first await.

Commands: `ExperimentSchedulingViewModelTests` 15/15; Task 4 WPF directional suite 21/21.

## Fix round 4

- Corrected `Multi_row_sample_numbers_survive_save_and_completion` so both legacy snapshot rows omit `BusinessSampleId`, their real sample numbers come from the complete central-sample map, and the test exercises both `SaveSampleRowAsync` and completion. It asserts each row number after save and again after completion.
- Added `Failed_snapshot_save_and_failed_authoritative_reload_clears_cached_verified_state` for both current-verification and sample-list reload failures. Each case starts from `Verified`, proves both write stages were reached, and asserts that the cached verification is cleared, the requirement becomes unresolved, and admission stays blocked.
- Added job-scoped operation ownership to every existing job-bound command (schedule, unschedule, cancel, admit, sample save, and verification completion). Selection changes release only the old job's busy ownership and clear its running text; stale continuations must still own both their operation ID and job ID before refreshing or publishing state.
- Added deterministic operation-race tests `Old_job_failure_cannot_pollute_new_job_operation_or_release_its_busy_state`, `Old_job_success_leaves_no_status_text_after_selection_changes`, and `Old_scheduling_success_cannot_reselect_over_or_end_busy_for_the_new_job`.
- Added predicate tests `Missing_workflow_version_keeps_sample_requirement_unresolved_and_blocks_admission`, `Workflow_version_errors_keep_sample_requirement_unresolved_and_block_admission` (ordinary exception and `NotSupportedException`), and `Ordinary_workflow_preserves_admission_without_loading_irrelevant_sample_projection` (including zero current/sample reads).
- Red-state evidence: after adding the tests and before the production ownership fix, the focused suite reported 21 passed / 23 total; the two operation-ownership tests failed on stale busy and stale status respectively. Existing projection/reload/predicate code passed its new regression tests, so no unrelated production logic was changed.

Commands/results:

```powershell
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSchedulingViewModelTests"
# Passed: 24 / 24
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
# Passed: 30 / 30
git diff --check
# Passed: no whitespace errors
```

No MES production files changed in this round, so the MES full suite was not rerun. The MES backend remains the final admission authority.
