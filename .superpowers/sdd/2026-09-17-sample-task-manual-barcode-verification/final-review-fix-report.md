# Final review fix report

Date: 2026-09-17

Baseline: `fff9488`; worktree: `D:/Project/Github/Mes-worktrees/sample-workstation-http-readonly`.

Status: completed. This is the plan's single final-review fix wave, limited to the four Important findings. The two accepted Minor follow-ups (duplicate unknown-sample feedback and the MesClient explicit 404/body contract) remain outside this commit.

## Implementation

1. New snapshot saves resolve BusinessSampleId from central ExperimentSamples by SampleId before validation, persistence, and hashing. Client omission and forgery cannot freeze a blank or invented identifier. The command fingerprint still reflects the original request for stable replay. Missing legacy identity remains readable and produces a row validation issue; it cannot be verified or admitted directly, and saving creates a new revision with the registered identity. Existing JSON and database columns remain unchanged.
2. Registration updates identify all historical snapshot references and check their jobs before assigning any central sample field. A change affecting an Admitted, Running, Cancelled, Completed, or Failed task is rejected with the existing version-conflict code. Both previously Invalidated and Verified history remain byte-for-byte equivalent under rejected updates. WPF additionally disables the identity editor, row-save command, and verification command based on the selected job and schedule; direct save method calls also guard before their first HTTP write.
3. WPF compares the four editor identity fields (business number, barcode, position, order) with the selected loaded row. New-row editor values compare with the empty editor baseline. Unsaved identity changes immediately block workstation admission and verification and show `未保存修改（核对需重新确认）`. Reverting restores the original state. DisplayName alone does not affect identity. This never changes the cached backend Status to a fabricated Invalidated value. Applying the returned snapshot refreshes editor baselines and row selection.
4. Task-scoped `ExperimentSampleVerificationInvalidated` events include verificationId, revision, snapshotHash, originating requestId, reason and stable code, plus the existing task/actor/reason columns. Each affected mutable task receives its own linked event. The command retains its unique RequestId, while related events have their own IDs. Invalidation and audit are committed in the same EF Core SaveChanges transaction. Verification failures use the existing `ExperimentSampleVerificationVerified` event with `Outcome=Rejected`, stable Code and serialized failure; current-snapshot identifiers and requested revision/hash are retained. Version/route/hash mismatch, non-Ready state and drift failures replay the same HTTP 409 `{detail, code}` without another audit or state transition. Different payload reuse still fails. Gate-discovered drift also receives a task invalidation audit (actor `MES admission gate`).

Compatibility self-review found that adding a body revision indiscriminately to the fingerprint would prevent replay of pre-upgrade successful commands. The implementation preserves the old fingerprint shape whenever route and body revisions match, using the additional body revision only for malformed mismatches. The existing successful API test now verifies the exact old fingerprint calculation.

## Directed tests

MES `ExperimentSampleVerificationApiTests` (22 total, including 14 newly added theory/fact cases):

- `Save_freezes_central_business_identity_when_client_omits_or_forges_it` — 2 cases; raw JSON omission/forgery, HTTP and persisted central value, same snapshot hash and successful verification.
- `Legacy_missing_business_identity_is_readable_but_requires_a_new_revision_before_verification` — legacy JSON seed, readable issue, verify 409, new revision identity repair and successful re-verification.
- `Registration_update_for_protected_task_is_rejected_without_any_partial_write` — 5 task-state cases; exact center/history serialization and audit count unchanged, stable 409 code.
- `Verification_rejection_is_audited_and_replayed_with_exact_failure` — 5 scenarios: hash, revision, route/body mismatch, Draft, central business-id drift. Checks exact problem/result JSON keys and values, task/snapshot metadata, audit counts, stable repeated response, different-payload request-id rejection, and drift invalidation linkage.
- `Registration_drift_audits_each_affected_task_once_and_replay_does_not_repeat_invalidation` — two mutable tasks, exact invalidation detail keys and identity values, actor/reason, both records invalidated, no repeated events.
- Updated `Verification_identity_tracks_business_id_barcode_position_and_order_but_not_display_name` to change a business identifier through its authoritative central registration endpoint, rather than forging a row identifier.
- Extended `Registered_rows_are_snapshotted_verified_and_replayed` with the legacy successful-command fingerprint assertion.

WPF `ExperimentSchedulingViewModelTests` (32 total, 8 new theory cases):

- `Unsaved_identity_edits_immediately_block_admission_and_verification_and_reverting_restores_state` — each of 4 identity fields, Verified admission state, Ready verification state, revert, DisplayName-only edits, command state, explicit unsaved text and unchanged backend Status; no save request.
- `Protected_tasks_disable_sample_identity_editor_and_commands` — Admitted, Running, Completed, and Scheduled-job/Admitted-schedule cases; editor/commands disabled and direct save causes no registration or snapshot request.
- Extended `Independent_plan_and_scheduling_views_bind_lifecycle_manual_actions_and_timeline` with the actual SampleIdentityEditor IsEnabled binding and effective enabled value.

## RED/GREEN evidence

With the new backend tests present, temporarily restored only ExperimentSampleVerificationService.cs from `fff9488` using apply_patch and ran:

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~Save_freezes_central|FullyQualifiedName~Legacy_missing|FullyQualifiedName~Registration_update_for_protected|FullyQualifiedName~Verification_rejection_is_audited|FullyQualifiedName~Registration_drift_audits_each" --verbosity minimal
```

RED: 14 failed / 0 passed / 0 skipped, exit 1. Failures were substantive: returned FORGED/empty business identifiers, missing legacy validation, protected registration returned OK, missing invalidation/rejection audit rows, and unrecognized business drift. Immediately restored the full implementation using apply_patch. No baseline source remains in the worktree.

The first implementation run had one test-fixture error (attempting to seed legacy JSON through the append-only application write guard). Corrected the fixture to seed the legacy persisted JSON with ExecuteUpdateAsync. The application append-only guard remains unchanged. No WPF baseline RED run was performed; the new behavioral tests exercise command changes before saving and would detect the old enabled admission/save behavior.

GREEN commands, final results:

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerificationApiTests|FullyQualifiedName~Admission|FullyQualifiedName~BusinessChain" --verbosity minimal
# 52 passed / 0 failed / 0 skipped; exit 0

dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter FullyQualifiedName~ExperimentSchedulingViewModelTests --verbosity minimal
# 32 passed / 0 failed / 0 skipped; exit 0

dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --verbosity minimal
# 352 passed / 0 failed / 0 skipped; exit 0

dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerification|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentPlanningViewBindingTests" --verbosity minimal
# 38 passed / 0 failed / 0 skipped; exit 0

dotnet test tests/MesControlAgv.WorkflowContract.Tests/MesControlAgv.WorkflowContract.Tests.csproj --no-restore --verbosity minimal
# 74 passed / 0 failed / 0 skipped; exit 0

dotnet restore MesControlAgv.sln --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-sample-verification-final-review-fix-20260917
# exit 0

dotnet build MesControlAgv.sln --no-restore --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-sample-verification-final-review-fix-20260917
# exit 0; 0 warnings; 0 errors
```

The combined targeted MES checks and full MES suite were rerun after the fingerprint compatibility adjustment; Task 4 WPF was rerun after the editor binding assertion. Build output is entirely under the independent artifacts path above.

## Final self-review and boundaries

- No schema migration, contract breaking change, deletion or overwrite of snapshot identity rows. Legacy data remains readable; append-only protection is untouched.
- Mutations continue using the scheduling mutation gate. All affected registration references are checked before mutation, so a shared sample referenced by any protected task cannot partially update another task.
- State changes and related audits use one SaveChanges call per mutation. Replayed failures are read from the unique originating command audit before mutable-state checks.
- The UI's unsaved status is a projection only. MES HTTP and backend admission checks remain authoritative; no local device or storage access was introduced.
- No connection to 192.168.200.157, site process start/stop, site switch edits, device command, scan/upload/remote-stop feature, or real-device test occurred.
- No remaining concern identified within these four Important findings. Accepted Minor follow-ups remain explicitly deferred.
