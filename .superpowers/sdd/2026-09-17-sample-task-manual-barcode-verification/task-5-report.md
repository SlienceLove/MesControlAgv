# Task 5 Report — 集成验证、文档和现场边界归档

## Scope and status

完成了两份交接/集成文档更新和一份离线验收诊断文档。验收严格使用测试替身、测试工厂的新数据库和隔离 artifacts 目录；没有触发真实工作站、访问 `192.168.200.157`、启动现场服务、发送真实命令、关闭现场进程或改变现场配置/开关。

`Verified` 的归档措辞已明确限定为：操作员核对中控保存的样品编号、条码、位置和顺序快照。它不表示厂家任务表已上传、任务已到达仪器或设备已执行。未来导入只能创建/准备任务，运行仍必须经独立运行准入动作触发。

## Files

- `docs/SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md` — API、状态机、问题码、WPF 操作、运行准入及现场边界。
- `docs/SAMPLE-WORKSTATION-FORMAL-WORKFLOW-HANDOFF-2026-09-16.md` — 正式工作流交接中的核对/准入语义和未来导入边界。
- `docs/diagnostics/2026-09-17-sample-verification-acceptance.md` — 离线验收证据、精确测试映射与命令。

## Commands and exact results

Working directory for all commands: `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`.

```powershell
dotnet test tests\MesControlAgv.WorkflowContract.Tests\MesControlAgv.WorkflowContract.Tests.csproj --no-restore
```

Result: passed 74, failed 0, skipped 0; duration 131 ms.

```powershell
dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore
```

Result: passed 337, failed 0, skipped 0; duration 5 s.

```powershell
dotnet test tests\MesControlAgv.Wpf.Tests\MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerification|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
```

Result: passed 30, failed 0, skipped 0; duration 486 ms.

```powershell
$buildRoot = Join-Path $env:TEMP "mes-sample-verification-build"
dotnet restore MesControlAgv.sln --artifacts-path "$buildRoot"
dotnet build MesControlAgv.sln --no-restore --artifacts-path "$buildRoot"
```

Result: restore succeeded for the solution; build succeeded with 0 warnings and 0 errors in 00:00:08.38.

```powershell
git diff --check
```

Result: exit 0; no whitespace errors. Git emitted only CRLF normalization notices for the two pre-existing tracked Markdown files.

## Isolated build path

`C:\Users\33206\AppData\Local\Temp\mes-sample-verification-build`

The solution restore/build wrote artifacts there rather than into a running site directory. The three `--no-restore` test commands used their normal test outputs; they did not start an external service or access a device.

## Environment differences and concerns

- The explicitly requested WPF filter passed and did not run `ShineLabHandoffRehearsalTests`.
- The linked-worktree root-directory failure in that existing rehearsal test remains outside this task and was not changed.
- No field validation was performed here by design; the recorded evidence is non-real-machine acceptance only.
- The SDD ledger's deferred Task 4 test-polish and Task 2 row-validation minors were not expanded or repaired by this task.

## Fix round 1/5 — continuous acceptance evidence

Added `ExperimentSampleWorkstationBusinessChainTests.Unverified_drifted_and_reverified_sample_snapshot_gates_workstation_admission_without_gateway_start`. In one fresh SQLite database and one continuous test method, it creates the workstation workflow/job, registers two same-batch samples, saves two ordered positions, proves unverified admission has no runtime or gateway-start side effects, records verification metadata, changes a position and proves the old verification is invalidated while the new revision is rejected as unverified, then re-verifies and admits the current revision. The successful admission audit `DetailsJson` is deserialized and asserted by exact `verificationId`, `verificationRevision` and `verificationSnapshotHash` keys; the recording fake is not dispatched, so it records zero starts and creates no device operation.

The diagnostics document now maps each requested acceptance step to that precise method. The page-level no-dialog WPF behavior remains separately evidenced by `ExperimentSchedulingViewModelTests.Sample_verification_loads_per_selected_job_gates_workstation_admission_and_never_confirms_twice`; it is no longer represented as having been exercised by the backend integration test.

Fix-round commands and results:

```powershell
dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleWorkstationBusinessChainTests.Unverified_drifted_and_reverified_sample_snapshot_gates_workstation_admission_without_gateway_start"
# passed 1, failed 0, skipped 0; duration < 1 ms
```

The focused test used `PhysicalMesWebApplicationFactory` with a fresh temp SQLite path and `RecordingWorkstation`; it did not access a real workstation or field service.

```powershell
dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore
# passed 338, failed 0, skipped 0; duration 6 s

dotnet test tests\MesControlAgv.WorkflowContract.Tests\MesControlAgv.WorkflowContract.Tests.csproj --no-restore
# passed 74, failed 0, skipped 0; duration 113 ms

dotnet test tests\MesControlAgv.Wpf.Tests\MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerification|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
# passed 30, failed 0, skipped 0; duration 1 s

$buildRoot = Join-Path $env:TEMP "mes-sample-verification-build"
dotnet build MesControlAgv.sln --no-restore --artifacts-path "$buildRoot"
# succeeded, 0 warnings, 0 errors; elapsed 00:00:08.54

git diff --check
```

Results: all commands succeeded; `git diff --check` exited 0 with no whitespace errors. The isolated build path remains `C:\Users\33206\AppData\Local\Temp\mes-sample-verification-build`.
