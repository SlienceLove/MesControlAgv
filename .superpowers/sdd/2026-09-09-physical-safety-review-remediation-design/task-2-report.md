# Task 2 report: readiness epoch allocation consistency

## Scope

Audited and completed only the Task 2 implementation/test scope:

- `src/MesControlAgv.Mes/Services/PhysicalReadinessStateStore.cs`
- `tests/MesControlAgv.Mes.Tests/PhysicalReadinessSupervisorTests.cs`

The pre-existing unrelated worktree changes were preserved. No physical
defaults, endpoints, adapter protocols, device-access behavior, or
`docs/SHINELAB-DOWNSTREAM-TCP-PROTOCOL.md` were changed.

## Implementation audit

All device-epoch advances now go through the single monotonic `NextEpoch`
allocator. Its per-device allocation history survives removal, so a
remove/re-add cannot reuse an earlier epoch. The audited advancing paths are:

- first observation and probe/online reconnect boundaries;
- identity/map changes reported by authoritative full preflight;
- Ready-to-failure transitions, except when the blockers are exclusively
  `ActiveTask` and/or `TemporaryObstacle`;
- stale observation invalidation;
- descriptor changes and supervisor configuration changes;
- remove/re-add, through retained per-device allocation history.

Descriptor and supervisor-configuration changes use one reset operation. The
reset clears observation history, full-preflight state, stability/Ready state,
identity state, and authorization; it allocates the new epoch once and marks
that epoch reserved. The first observation consumes the reserved epoch without
allocating another one. Reapplying an unchanged descriptor/configuration does
not advance the epoch.

Authorization remains fail-closed for safety/session/configuration changes:
the changed epoch and `RequiresReauthorization` prevent the old authorization
from passing. Active-task and temporary-obstacle blockers retain the existing
exception: they block readiness/scheduling while preserving the authorized
epoch so it can be used after the transient condition clears.

## Focused behavior coverage

The test suite covers configuration stability, descriptor/configuration reset
and first-observation epoch reservation, remove/re-add non-reuse, reconnects,
stale observations, full-preflight identity changes after partial polls,
Ready-to-failure invalidation, fail-closed authorization, and the
active-task/temporary-obstacle exception. The added boundary test verifies
that an epoch invalidated by a Ready-to-failure transition is not reused after
remove/re-add.

## Verification

Command:

```text
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --filter FullyQualifiedName~PhysicalReadinessSupervisorTests --no-restore
```

Complete result:

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

已通过! - 失败:     0，通过:    18，已跳过:     0，总计:    18，持续时间: 70 ms - MesControlAgv.Mes.Tests.dll (net8.0)
```

Self-review also ran `git diff --check` on both Task 2 source/test files; it
reported no whitespace errors.

## Concerns

None identified within Task 2 scope.
