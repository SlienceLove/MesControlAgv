# Task 4 report — one-shot physical safety actions

Status: complete for offline implementation. No field IP, port, AGV command, AUBO write, or Modbus operation was used.

Implemented:

- Added `PhysicalSafetyActionRecord` persistence and contracts for AGV release and AUBO stop.
- Added request-id/fingerprint conflict detection, idempotent replay, process-wide serialized action execution, and `Prepared -> Unknown` startup reconciliation without replaying a gateway write.
- Added `POST /api/agvs/{agvId}/control/release`; it performs fresh AGV identity/online/owner/current-task and MES acceptance checks before at most one release call.
- Routed current-instance final Move cleanup through the safety action service; restart/recovery paths do not release or stop.
- Routed AUBO stop through the safety action service with explicit operation ID/operator, optional workflow correlation, fresh status preflight, and Unknown-on-ambiguous-write behavior.
- Preserved existing cancel auditing and zero automatic retry behavior.

Verification:

- `dotnet build src/MesControlAgv.Mes/MesControlAgv.Mes.csproj -c Release --no-restore -m:1`: passed, 0 warnings, 0 errors.
- Task 4 and affected MES tests: passed, 26/26 in Debug; Release filtered run passed 12/12.
- Full solution test run reached 48 Domain, 223 MES, 240 Adapter, 27 E2E (5 skipped), 5 Simulator, 71 WorkflowContract, and 80 InstrumentGateway tests passed. It exited non-zero only because an existing local `.NET Host (34304)` locked WPF service-copy DLLs during the WPF test project build; no test assertion failed.

Concerns / handoff:

- Full WPF-inclusive validation must be rerun after the unrelated local host releases `src/MesControlAgv.Wpf/bin/Debug/net8.0-windows/services/Mes` locks.
- This commit is implementation-only and has not been pushed. Real field validation remains a separate, explicitly supervised phase; it must begin with read-only preflight and operator authorization.

## Fix round 1 (2026-09-09)

Review fixes:

- `WorkflowFieldNavigationWorker` no longer invokes physical release on one-click batch cancellation; the cancellation test asserts `ReleaseControlCalls == 0`.
- `ReleaseFinalMoveAsync` now requires `WorkflowStepCompletionOutcome.Succeeded`. Any other outcome is rejected before readiness/device processing and cannot write the gateway. The service test verifies Rejected status and `ReleaseCalls == 0`.

Offline verification commands and actual output:

- `dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~PhysicalSafetyActionServiceTests|FullyQualifiedName~WorkflowFieldNavigationWorkerTests" --logger "console;verbosity=normal" -m:1`
  - `测试运行成功。测试总数: 17 通过数: 17`
- `dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --logger "console;verbosity=minimal" -m:1`
  - `已通过! - 失败: 0，通过: 224，已跳过: 0，总计: 224`
- `dotnet build src/MesControlAgv.Mes/MesControlAgv.Mes.csproj -c Release --no-restore -m:1`
  - `已成功生成。0 个警告 0 个错误`

No field IP/port was accessed, no real service was started, and no AGV/AUBO/Modbus/DI/DO write was sent. Existing unrelated WPF changes, artifacts, and user deletion items were preserved.

### Fix round 1 verification rerun (2026-09-09)

Command:

`dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~PhysicalSafetyActionServiceTests|FullyQualifiedName~WorkflowFieldNavigationWorkerTests" --logger "console;verbosity=normal" -m:1`

Actual result:

`测试运行成功。测试总数: 17；通过数: 17；失败数: 0。总时间: 2.7949 秒。`

The rerun remained offline and used only local fakes/SQLite; no field IP/port was accessed and no real device/service or AGV/AUBO/Modbus/DI/DO write was used.
