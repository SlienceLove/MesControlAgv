# 单 AGV 中控 MES WPF MVP 收尾实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在已完成的 .NET 8 + WPF MVP 上统一本地服务端口，补齐真实进程联通与连续搬运验收，并完成运行文档收尾。

**Architecture:** 实施目标是 `.claude/worktrees/agv-mes-mvp-wpf` 中的五项目 .NET 解决方案。MES 仍是任务状态和审计事件的唯一写入者，Adapter 继续隔离模拟器与设备协议，WPF 只调用 MES 动作 API；本计划只补配置、验收和文档，不重写已通过测试的核心业务。

**Tech Stack:** C# 12、.NET 8、ASP.NET Core Minimal API、Entity Framework Core SQLite、WPF、xUnit、PowerShell。

## Global Constraints

- 实施代码根目录是 `.claude/worktrees/agv-mes-mvp-wpf`；本计划文件位于主仓库的 `docs/superpowers/plans/`。
- 仅支持一台 AGV、固定 7 个只读站点和 `SAMPLE_01`（2）到 `ST_PREP_01`（4）的路线。
- WPF 客户端只能调用中控动作 API，不能直接变更任务状态。
- `task_id` 为 GUID；Adapter 必须对相同 task ID 幂等，不能重复发送导航命令。
- 请求超时先查询设备真实状态；查询不能确定时才进入 `UNKNOWN`，禁止盲目重发。
- Adapter 控制权不是 `adapter` 时拒绝派单。
- 不实现多车、路径规划、WMS、库存、真实 PLC/机械臂或真实 AGV 厂商协议。
- 使用本地 SQLite；不跟踪三份原始 `.docx`。
- 端口固定为 Simulator `5183`、Adapter `5041`、MES `5045`；所有配置、默认值、脚本和文档必须一致。
- worktree 当前已有未提交改动；执行每个任务前运行 `git status --short`，保留不属于本任务的文件和 diff，不使用回滚命令覆盖它们。
- 根目录 Python/FastAPI 计划已删除；不得新增 Python、FastAPI、React、Vite 或 Docker Compose 文件。

---

## Current Baseline

从 `.claude/worktrees/agv-mes-mvp-wpf` 执行 `dotnet test MesControlAgv.sln` 的基线结果为 30 项通过：Domain 7、Simulator 1、WPF 3、Adapter 5、MES 8、E2E 6。

当前需要收尾的已知不一致：

- `src/MesControlAgv.Mes/appsettings.Development.json` 的 Adapter 地址仍为 `5045`，应为 `5041`。
- `src/MesControlAgv.Mes/Program.cs` 和 `src/MesControlAgv.Adapter/Program.cs` 的默认回退地址仍为 `5001`/`5002`，应为 `5041`/`5183`。
- `src/MesControlAgv.Wpf/App.xaml.cs` 的默认 MES 地址仍为 `5041`，应为 `5045`。
- `scripts/run-local.ps1` 输出的 Adapter 和 MES 标签互换。
- `Readme.md` 中的 WPF `MES_BASE_URL` 和开发环境说明仍使用旧端口。
- 根 `appsettings.Development.json` 中的旧辅助地址为 `5001`/`5002`，需要与当前端口契约保持一致。

### Task 1: 统一三层服务端口契约和启动入口

**Files:**
- Create: `tests/MesControlAgv.E2E.Tests/LocalPortContractTests.cs`
- Modify: `appsettings.Development.json`
- Modify: `src/MesControlAgv.Mes/appsettings.Development.json`
- Verify: `src/MesControlAgv.Adapter/appsettings.Development.json`
- Modify: `src/MesControlAgv.Mes/Program.cs`
- Modify: `src/MesControlAgv.Adapter/Program.cs`
- Modify: `src/MesControlAgv.Wpf/App.xaml.cs`
- Modify: `scripts/run-local.ps1`
- Modify: `Readme.md`
- Verify: `src/MesControlAgv.Mes/Properties/launchSettings.json`
- Verify: `src/MesControlAgv.Adapter/Properties/launchSettings.json`
- Verify: `src/MesControlAgv.Simulator/Properties/launchSettings.json`

**Interfaces:**
- Consumes: the existing three ASP.NET Core projects and the WPF startup code.
- Produces: Simulator `5183`, Adapter `5041`, MES `5045`; MES uses `Adapter:BaseUrl`, Adapter uses `Simulator:BaseUrl`, and WPF uses `MES_BASE_URL` or its `5045` fallback.

- [ ] **Step 1: Add a failing local port contract test**

Create `tests/MesControlAgv.E2E.Tests/LocalPortContractTests.cs`:

```csharp
using System.Text.Json;

namespace MesControlAgv.E2E.Tests;

public sealed class LocalPortContractTests
{
    [Fact]
    public void Development_files_and_defaults_use_the_shared_port_contract()
    {
        var root = FindRepositoryRoot();
        using var rootConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "appsettings.Development.json")));
        using var mesConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "appsettings.Development.json")));
        using var adapterConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Adapter", "appsettings.Development.json")));
        using var mesLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "Properties", "launchSettings.json")));
        using var adapterLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Adapter", "Properties", "launchSettings.json")));
        using var simulatorLaunch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Simulator", "Properties", "launchSettings.json")));

        Assert.Equal("http://localhost:5041", rootConfig.RootElement.GetProperty("Mes").GetProperty("AdapterBaseUrl").GetString());
        Assert.Equal("http://localhost:5183", rootConfig.RootElement.GetProperty("Adapter").GetProperty("SimulatorBaseUrl").GetString());
        Assert.Equal("http://localhost:5041/", mesConfig.RootElement.GetProperty("Adapter").GetProperty("BaseUrl").GetString());
        Assert.Equal("http://localhost:5183/", adapterConfig.RootElement.GetProperty("Simulator").GetProperty("BaseUrl").GetString());
        Assert.Equal("http://localhost:5045", mesLaunch.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString());
        Assert.Equal("http://localhost:5041", adapterLaunch.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString());
        Assert.Equal("http://localhost:5183", simulatorLaunch.RootElement.GetProperty("profiles").GetProperty("http").GetProperty("applicationUrl").GetString());

        Assert.Contains("http://localhost:5041/", File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Mes", "Program.cs")));
        Assert.Contains("http://localhost:5183/", File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Adapter", "Program.cs")));
        Assert.Contains("http://localhost:5045/", File.ReadAllText(Path.Combine(root, "src", "MesControlAgv.Wpf", "App.xaml.cs")));
        var launcher = File.ReadAllText(Path.Combine(root, "scripts", "run-local.ps1"));
        Assert.Contains("Adapter:   http://localhost:5041", launcher);
        Assert.Contains("MES:       http://localhost:5045", launcher);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MesControlAgv.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MesControlAgv.sln was not found.");
    }
}
```

- [ ] **Step 2: Run the contract test and record the expected failures**

Run:

```powershell
dotnet test tests/MesControlAgv.E2E.Tests/MesControlAgv.E2E.Tests.csproj --filter FullyQualifiedName~LocalPortContractTests
```

Expected: FAIL because the current MES development URL is `5045`, the fallback URLs are `5001`/`5002`, the WPF fallback is `5041`, and the launcher labels are reversed.

- [ ] **Step 3: Update every runtime source of the endpoint contract**

Apply these exact values:

```csharp
// src/MesControlAgv.Mes/Program.cs
client.BaseAddress = new Uri(
    builder.Configuration["Adapter:BaseUrl"] ?? "http://localhost:5041/");

// src/MesControlAgv.Adapter/Program.cs
var simulatorUrl = builder.Configuration["Simulator:BaseUrl"] ?? "http://localhost:5183/";

// src/MesControlAgv.Wpf/App.xaml.cs
var mesUrl = Environment.GetEnvironmentVariable("MES_BASE_URL") ?? "http://localhost:5045/";
```

Set the development configuration values to:

```json
// src/MesControlAgv.Mes/appsettings.Development.json
"Adapter": { "BaseUrl": "http://localhost:5041/" }

// src/MesControlAgv.Adapter/appsettings.Development.json
"Simulator": { "BaseUrl": "http://localhost:5183/" }

// appsettings.Development.json
"Mes": { "AdapterBaseUrl": "http://localhost:5041" },
"Adapter": { "SimulatorBaseUrl": "http://localhost:5183" }
```

Change `scripts/run-local.ps1` to print:

```powershell
Write-Host 'Simulator: http://localhost:5183'
Write-Host 'Adapter:   http://localhost:5041'
Write-Host 'MES:       http://localhost:5045'
```

Keep the three existing `launchSettings.json` HTTP URLs at `5045`, `5041`, and `5183`, respectively. Update `Readme.md` so the WPF command uses `$env:MES_BASE_URL = 'http://localhost:5045/'` and the development configuration sentence says MES calls Adapter at `5041`.

- [ ] **Step 4: Run targeted and full verification**

Run:

```powershell
dotnet test tests/MesControlAgv.E2E.Tests/MesControlAgv.E2E.Tests.csproj --filter FullyQualifiedName~LocalPortContractTests
dotnet build MesControlAgv.sln --no-restore
dotnet test MesControlAgv.sln --no-build
```

Expected: the port contract passes and all existing tests remain green.

- [ ] **Step 5: Review and commit only the port-contract changes**

Before staging, inspect the existing worktree diff:

```powershell
git status --short
git diff -- src/MesControlAgv.Mes/Program.cs src/MesControlAgv.Adapter/Program.cs src/MesControlAgv.Wpf/App.xaml.cs
```

Preserve unrelated hunks already present in the worktree. Then stage the files listed in this task and commit:

```powershell
git add -- appsettings.Development.json src/MesControlAgv.Mes/appsettings.Development.json src/MesControlAgv.Adapter/appsettings.Development.json src/MesControlAgv.Mes/Program.cs src/MesControlAgv.Adapter/Program.cs src/MesControlAgv.Wpf/App.xaml.cs scripts/run-local.ps1 Readme.md tests/MesControlAgv.E2E.Tests/LocalPortContractTests.cs
git commit -m "fix: align local AGV service endpoints"

### Task 2: 补齐连续闭环和真实进程联通验收

**Files:**
- Modify: `tests/MesControlAgv.E2E.Tests/TransportAcceptanceTests.cs`
- Create: `scripts/verify-local.ps1`

**Interfaces:**
- Consumes: MES `5045`、Adapter `5041`、Simulator `5183`，现有 `/health`、`/api/tasks`、`/api/tasks/{id}/arrived`、人工确认和 Simulator 控制端点。
- Produces: 10 次连续搬运回归测试，以及可重复执行的真实三进程正常闭环验证脚本。

- [ ] **Step 1: Add the 10-task regression scenario**

Add this test to `TransportAcceptanceTests` so it uses the existing `CreateService` and `AcceptanceAdapter` fixtures:

```csharp
[Fact]
public async Task Ten_sample_to_prep_tasks_complete_with_isolated_operations()
{
    var adapter = new AcceptanceAdapter();
    var service = CreateService(adapter);

    for (var index = 0; index < 10; index++)
    {
        var created = await service.CreateAsync(new CreateTaskRequest(2, 4), CancellationToken.None);
        await service.RecordArrivalAsync(created.Id, CancellationToken.None);
        await service.ConfirmPickupAsync(created.Id, "operator-a", CancellationToken.None);
        await service.RecordArrivalAsync(created.Id, CancellationToken.None);
        var completed = await service.ConfirmDropoffAsync(created.Id, "operator-a", CancellationToken.None);

        Assert.Equal("Completed", completed.Status);
    }

    Assert.Equal(20, adapter.OperationIds.Count);
    Assert.Equal(20, adapter.OperationIds.Distinct().Count());
    Assert.Equal(20, adapter.Targets.Count);
}
```

The two dispatches for one transport use its distinct deterministic pickup and dropoff operation IDs; the ten tasks must therefore produce 20 dispatch records and 20 distinct operation IDs.

- [ ] **Step 2: Run the new regression test and the full automated suite**

Run:

```powershell
dotnet test tests/MesControlAgv.E2E.Tests/MesControlAgv.E2E.Tests.csproj --filter FullyQualifiedName~Ten_sample_to_prep_tasks_complete_with_isolated_operations
dotnet test MesControlAgv.sln
```

Expected: the new test and all existing tests pass. The existing tests must continue to cover failure retry with the same operation ID, timeout reconciliation without a second dispatch, and persisted restart reconciliation.

- [ ] **Step 3: Create the live local smoke script**

Create `scripts/verify-local.ps1`:

```powershell
param(
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$mes = 'http://localhost:5045'
$adapter = 'http://localhost:5041'
$simulator = 'http://localhost:5183'

function Wait-Health {
    param(
        [string]$BaseUrl,
        [string]$ServiceName,
        [int]$Timeout
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($Timeout)
    do {
        try {
            $health = Invoke-RestMethod -Uri "$BaseUrl/health" -TimeoutSec 2
            if ($health.service -eq $ServiceName -and $health.status -eq 'ok') { return }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "$ServiceName did not become healthy at $BaseUrl."
}

Wait-Health $simulator 'simulator' $TimeoutSeconds
Wait-Health $adapter 'adapter' $TimeoutSeconds
Wait-Health $mes 'mes' $TimeoutSeconds

$createBody = @{ sourceStationCode = 2; targetStationCode = 4 } | ConvertTo-Json
$task = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks" -ContentType 'application/json' -Body $createBody
if ($task.status -ne 'MovingToPickup') { throw "Unexpected pickup status: $($task.status)" }

Invoke-RestMethod -Method Post -Uri "$simulator/controls/arrive" | Out-Null
$arrived = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
if ($arrived.status -ne 'WaitingPickupConfirmation') { throw "Unexpected pickup arrival status: $($arrived.status)" }

$operatorBody = @{ operatorName = 'verify-local' } | ConvertTo-Json
$pickup = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-pickup" -ContentType 'application/json' -Body $operatorBody
if ($pickup.status -ne 'MovingToDropoff') { throw "Unexpected dropoff status: $($pickup.status)" }

Invoke-RestMethod -Method Post -Uri "$simulator/controls/arrive" | Out-Null
$arrivedAtDropoff = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/arrived"
if ($arrivedAtDropoff.status -ne 'WaitingDropoffConfirmation') { throw "Unexpected dropoff arrival status: $($arrivedAtDropoff.status)" }

$completed = Invoke-RestMethod -Method Post -Uri "$mes/api/tasks/$($task.id)/confirm-dropoff" -ContentType 'application/json' -Body $operatorBody
if ($completed.status -ne 'Completed') { throw "Unexpected terminal status: $($completed.status)" }

$detail = Invoke-RestMethod -Uri "$mes/api/tasks/$($task.id)"
$eventTypes = @($detail.events | ForEach-Object { $_.eventType })
foreach ($requiredEvent in @('PickupConfirmed', 'DropoffConfirmed')) {
    if ($eventTypes -notcontains $requiredEvent) { throw "Missing audit event: $requiredEvent" }
}

Write-Host "Live AGV transport verification passed for task $($task.id)."
```

The script must only exercise public service endpoints. It must not write to SQLite directly or mutate MES task state outside the existing API.

- [ ] **Step 4: Verify the live golden path**

From the worktree, start the three services in separate PowerShell processes:

```powershell
.\scripts\run-local.ps1
.\scripts\verify-local.ps1
```

Expected: health checks pass for all three services, the script completes a task through `Completed`, and the final detail response contains both manual confirmation events. Stop the three development processes after the check and leave the SQLite files under `data/` for restart verification.

- [ ] **Step 5: Exercise failure, timeout and restart recovery against live services**

Run the following scenarios after the normal smoke test, using the WPF or MES APIs for task actions:

```powershell
# Navigation failure: the next task must be Failed, then retryable.
Invoke-RestMethod -Method Post -Uri 'http://localhost:5183/controls/fail'
$failed = Invoke-RestMethod -Method Post -Uri 'http://localhost:5045/api/tasks' -ContentType 'application/json' -Body (@{ sourceStationCode = 2; targetStationCode = 4 } | ConvertTo-Json)
if ($failed.status -ne 'Failed') { throw "Expected Failed, got $($failed.status)" }
$retried = Invoke-RestMethod -Method Post -Uri "http://localhost:5045/api/tasks/$($failed.id)/retry"
if ($retried.status -ne 'MovingToPickup') { throw "Expected retry to resume pickup, got $($retried.status)" }

# Timeout: the device keeps the accepted navigation queryable; recovery must not dispatch again.
Invoke-RestMethod -Method Post -Uri 'http://localhost:5183/controls/timeout'
$unknown = Invoke-RestMethod -Method Post -Uri 'http://localhost:5045/api/tasks' -ContentType 'application/json' -Body (@{ sourceStationCode = 2; targetStationCode = 4 } | ConvertTo-Json)
if ($unknown.status -ne 'Unknown') { throw "Expected Unknown, got $($unknown.status)" }
$recovered = Invoke-RestMethod -Method Post -Uri "http://localhost:5045/api/tasks/$($unknown.id)/recover"
if ($recovered.status -ne 'MovingToPickup') { throw "Expected MovingToPickup after reconciliation, got $($recovered.status)" }
```

For restart recovery, leave one task in `MovingToPickup` or `Unknown`, stop only the MES process, start it again with `dotnet run --project src/MesControlAgv.Mes --launch-profile http`, then query `GET http://localhost:5045/api/tasks/{taskId}`. Expected: the task is reconciled from the existing SQLite database and Adapter state, and its event list includes the startup recovery events.

- [ ] **Step 6: Review and commit acceptance coverage**

Run `git diff --check`, confirm no SQLite database or process output is staged, then commit only the test and script files:

```powershell
git add -- tests/MesControlAgv.E2E.Tests/TransportAcceptanceTests.cs scripts/verify-local.ps1
git commit -m "test: verify live AGV transport recovery"
```

### Task 3: 收敛运行文档和完成记录

**Files:**
- Modify: `Readme.md`
- Modify: `docs/PROGRESS.md`
- Verify: `scripts/run-local.ps1`
- Verify: `scripts/verify-local.ps1`

**Interfaces:**
- Consumes: the final port contract and verification commands from Tasks 1 and 2.
- Produces: a README that a Windows developer can follow without guessing ports or process order, plus a progress record that distinguishes automated tests from live service validation.

- [ ] **Step 1: Document prerequisites and automated checks**

Update `Readme.md` with these exact commands:

```powershell
dotnet restore MesControlAgv.sln
dotnet build MesControlAgv.sln --no-restore
dotnet test MesControlAgv.sln --no-build
```

State the prerequisite as Windows 10/11 and .NET 8 SDK. State that the current automated baseline is 30 passing tests and that the live smoke script is a separate process-level check.

- [ ] **Step 2: Document local startup and the endpoint table**

Document the startup sequence:

```powershell
.\scripts\run-local.ps1
$env:MES_BASE_URL = 'http://localhost:5045/'
dotnet run --project src/MesControlAgv.Wpf
.\scripts\verify-local.ps1
```

Include this endpoint table in the README:

| Service | URL |
|---|---|
| Simulator | `http://localhost:5183` |
| Adapter | `http://localhost:5041` |
| MES | `http://localhost:5045` |

Explain that Adapter calls Simulator, MES calls Adapter, and WPF calls MES. Include the SQLite locations `data/mes.db` and `data/adapter.db`.

- [ ] **Step 3: Document the normal and fault-injection workflows**

Describe the normal flow in this order: create `SAMPLE_01 → ST_PREP_01`, simulate arrival, confirm pickup, simulate arrival, confirm dropoff, inspect the task detail event timeline. Document these Simulator controls and their exact effects:

- `fail`: the next navigation becomes `Failed`;
- `timeout`: the accepted navigation remains queryable and the MES task becomes `Unknown` until recovery;
- `offline`: navigation is rejected while the AGV is offline;
- `recover`: the simulator becomes online again;
- `arrive`: the current simulator task becomes arrived.

Document that retry uses the existing task and operation ID, while timeout recovery first queries Adapter and never blindly sends a second navigation.

- [ ] **Step 4: Update the progress record only after verification**

In `docs/PROGRESS.md`, record:

- port contract status as complete;
- automated test count including the ten-task scenario;
- live health and golden-path smoke result;
- failure, timeout and restart recovery result;
- the remaining real-AGV boundary: replace only the Adapter device client after vendor protocol and control-owner rules are confirmed.

Do not mark live validation complete until `scripts/verify-local.ps1` and the manual recovery scenarios have actually been run.

- [ ] **Step 5: Run the final handoff checks**

Run from the worktree:

```powershell
dotnet build MesControlAgv.sln --no-restore
dotnet test MesControlAgv.sln --no-build
git diff --check
git status --short
```

Expected: build succeeds, all automated tests pass, `git diff --check` produces no output, and only intentional source, test, script and documentation files remain in the worktree.

- [ ] **Step 6: Commit the final documentation**

Review the diff for stale `5001`, `5002`, swapped `5041`/`5045`, Python/FastAPI references and claims of unexecuted validation. Then commit:

```powershell
git add -- Readme.md docs/PROGRESS.md
git commit -m "docs: finalize AGV MES MVP operation guide"
```

## Spec Coverage Review

- .NET 8 + WPF technology baseline: Global Constraints, Current Baseline, all tasks.
- Fixed seven stations and the `SAMPLE_01` to `ST_PREP_01` route: Global Constraints, Task 2 regression and Task 3 normal flow.
- MES/Adapter/Simulator boundaries: Task 1 endpoint contract and Task 3 startup documentation.
- Idempotent dispatch and timeout reconciliation: Task 2 automated and live recovery scenarios.
- Manual pickup/dropoff confirmation: Task 2 smoke script and Task 3 normal workflow.
- SQLite persistence and restart recovery: Task 2 restart procedure and Task 3 database documentation.
- WPF connection and operator workflow: Task 1 default URL and Task 3 startup instructions.
- Ten consecutive successful transport cycles: Task 2 regression test.
- No Python/FastAPI or unrelated technology migration: Global Constraints and design scope.

Plan self-review completed: every task names concrete files, commands, expected results, endpoint values, and commit boundaries; no task depends on the deleted Python/FastAPI plan.
```
