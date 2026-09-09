# Task 3 Report — Preserve the workflow execution rejection contract

## Baseline and scope

- Baseline HEAD: `2d7f48882daf927f96f26fdbc7d9bc544d78d692`
- Changed only the workflow execute endpoint catch and its focused MES API regression test.
- `MesClient.cs` and WPF tests were not changed.
- No physical device, field IP, port, AGV, AUBO, Modbus, DI/DO, serial, or instrument access was performed.
- Existing unrelated worktree changes were preserved.

## Implementation

When `/api/workflows/execute` catches `PhysicalExecutionAdmissionException`, it now returns HTTP 409 containing `WorkflowExecutionResult` with:

- `Status = Rejected`
- `RejectionCode = WorkflowExecutionRejectionCodes.PhysicalExecutionDisabled`
- `RejectionReason = exception.Code + exception.Detail`
- request `RequestId`, `WorkflowId`, `Version`, `RequestedAt`, `DryRun`
- rejected audit event with `RequestedBy` and `CorrelationId`
- `ExecutionId = Guid.Empty` in both result and audit

The response regression test also verifies that the physical permit value is not included in the response body.

## Verification commands and complete output

### MES focused API test

Command:

```text
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --filter FullyQualifiedName~WorkflowApiTests.Physical_execute_keeps_workflow_disabled_as_the_top_level_http_code --no-restore
```

Output:

```text
  MesControlAgv.Contracts -> D:\Project\Github\Mes\src\MesControlAgv.Contracts\bin\Debug\net8.0\MesControlAgv.Contracts.dll
  MesControlAgv.Domain -> D:\Project\Github\Mes\src\MesControlAgv.Domain\bin\Debug\net8.0\MesControlAgv.Domain.dll
  MesControlAgv.Application -> D:\Project\Github\Mes\src\MesControlAgv.Application\bin\Debug\net8.0\MesControlAgv.Application.dll
  MesControlAgv.Mes -> D:\Project\Github\Mes\src\MesControlAgv.Mes\bin\Debug\net8.0\MesControlAgv.Mes.dll
  MesControlAgv.Mes.Tests -> D:\Project\Github\Mes\tests\MesControlAgv.Mes.Tests\bin\Debug\net8.0\MesControlAgv.Mes.Tests.dll
D:\Project\Github\Mes\tests\MesControlAgv.Mes.Tests\bin\Debug\net8.0\MesControlAgv.Mes.Tests.dll (.NETCoreApp,Version=v8.0)的测试运行
VSTest 版本 17.11.1 (x64)

正在启动测试执行，请稍候...
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:     1，已跳过:     0，总计:     1，持续时间: < 1 ms - MesControlAgv.Mes.Tests.dll (net8.0)
```

### Existing WPF client focused compatibility test

Command:

```text
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --filter FullyQualifiedName~MesClientWorkflowHttpContractTests --no-build --no-restore
```

Output:

```text
D:\Project\Github\Mes\tests\MesControlAgv.Wpf.Tests\bin\Debug\net8.0-windows\MesControlAgv.Wpf.Tests.dll (.NETCoreApp,Version=v8.0)的测试运行
VSTest 版本 17.11.1 (x64)

正在启动测试执行，请稍候...
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:    10，已跳过:     0，总计:    10，持续时间: 215 ms - MesControlAgv.Wpf.Tests.dll (net8.0)
```

## Concerns

- `git diff --check` produced only pre-existing line-ending warnings across the dirty worktree; no whitespace errors were reported.
- Full solution test suite was not run; validation was limited to the focused MES API test and existing WPF client contract tests.

## Fix round 1

### Scope

- Moved physical admission rejection persistence into `WorkflowApplicationService.ExecuteAsync`.
- A non-empty `RequestId` now persists a rejected execution and execution audit; an empty `RequestId` persists only the audit.
- Same-fingerprint requests replay the durable rejected result; a different fingerprint returns `RequestIdReused` and persists its audit.
- The endpoint's existing HTTP 409 `WorkflowExecutionResult` fallback remains in place.
- Tightened the concurrent-save recovery path so only the SQLite `WorkflowExecutions.RequestId` unique-key race is recovered; cancellation and other database errors are not swallowed.
- Added API assertions for durable by-request/audit reads, replay, RequestId reuse, and permit non-disclosure.
- No device, field-network, AGV, AUBO, Modbus, DI/DO, serial, or instrument access was performed.

### Test command and complete output

Command:

```text
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --filter FullyQualifiedName~WorkflowApiTests.Physical_execute_keeps_workflow_disabled_as_the_top_level_http_code --no-restore
```

Output:

```text
  MesControlAgv.Contracts -> D:\Project\Github\Mes\src\MesControlAgv.Contracts\bin\Debug\net8.0\MesControlAgv.Contracts.dll
  MesControlAgv.Domain -> D:\Project\Github\Mes\src\MesControlAgv.Domain\bin\Debug\net8.0\MesControlAgv.Domain.dll
  MesControlAgv.Application -> D:\Project\Github\Mes\src\MesControlAgv.Application\bin\Debug\net8.0\MesControlAgv.Application.dll
  MesControlAgv.Mes -> D:\Project\Github\Mes\src\MesControlAgv.Mes\bin\Debug\net8.0\MesControlAgv.Mes.dll
  MesControlAgv.Mes.Tests -> D:\Project\Github\Mes\tests\MesControlAgv.Mes.Tests\bin\Debug\net8.0\MesControlAgv.Mes.Tests.dll
D:\Project\Github\Mes\tests\MesControlAgv.Mes.Tests\bin\Debug\net8.0\MesControlAgv.Mes.Tests.dll (.NETCoreApp,Version=v8.0)的测试运行
VSTest 版本 17.11.1 (x64)

正在启动测试执行，请稍候...
总共 1 个测试文件与指定模式相匹配。

已通过! - 失败:     0，通过:     1，已跳过:     0，总计:     1，持续时间: < 1 ms - MesControlAgv.Mes.Tests.dll (net8.0)
```

### Self-review

- Verified the service catch is the physical admission boundary and uses the existing rejection/result/audit persistence helpers.
- Verified rejected execution records use `WorkflowPersistence.GetAdmissionRuntimeStatus` and have `Guid.Empty` execution ids; no physical authorization is copied into `WorkflowExecutionResult` or its audit.
- Verified RequestId replay/conflict behavior and durable read models through the API test.
- Verified cancellation remains governed by the cancellation token and non-RequestId database failures do not match the recovery filter.
- Preserved the concurrent readiness supervisor instance parameter changes as uncommitted worktree changes.

### Concerns

- The full solution test suite was not run; the focused API test passed.
- Existing unrelated worktree changes remain untouched and uncommitted.
