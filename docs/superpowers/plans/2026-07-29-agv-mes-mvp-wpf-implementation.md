# 单 AGV 中控 MES WPF MVP 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建可本地验证的单 AGV 中控 MES MVP：WPF 操作台驱动中控服务，经独立 Adapter 调度 AGV 模拟器，验证样品位到前处理站的搬运、人工确认、异常与恢复。

**Architecture:** 一个 .NET 8 解决方案包含共享领域模型、MES ASP.NET Core API、AGV Adapter ASP.NET Core API、AGV Simulator ASP.NET Core API 和 WPF MVVM 客户端。MES 是任务状态与审计事件的唯一写入者；Adapter 隔离站点映射、控制权、幂等和设备协议；模拟器提供可控 AGV 行为，后续真实客户端仅替换 Adapter 内部实现。

**Tech Stack:** C# 12、.NET 8、ASP.NET Core Minimal API、Entity Framework Core SQLite、WPF、CommunityToolkit.Mvvm、xUnit、Microsoft.AspNetCore.Mvc.Testing。

## Global Constraints

- 仅支持一台 AGV、固定 7 个只读站点和 `SAMPLE_01`（2）到 `ST_PREP_01`（4）的路线。
- WPF 客户端只能调用中控动作 API，不能直接变更任务状态。
- `task_id` 为 GUID；Adapter 必须对相同 task ID 幂等，不能重复发送导航命令。
- 请求超时先查询设备真实状态；查询不能确定时才进入 `UNKNOWN`，禁止盲目重发。
- Adapter 控制权不是 `adapter` 时拒绝派单。
- 不实现多车、路径规划、WMS、库存、真实 PLC/机械臂或真实 AGV 厂商协议。
- 使用本地 SQLite；不跟踪三份原始 `.docx`。

---

## Solution Layout

```text
src/
  MesControlAgv.Domain/        # 枚举、站点、状态机、DTO 契约
  MesControlAgv.Mes/           # MES API、SQLite、任务编排、审计与恢复
  MesControlAgv.Adapter/       # Adapter API、幂等存储、模拟器客户端
  MesControlAgv.Simulator/     # AGV 模拟器 API 与故障注入
  MesControlAgv.Wpf/           # WPF MVVM 运行看板
 tests/
  MesControlAgv.Domain.Tests/
  MesControlAgv.Mes.Tests/
  MesControlAgv.Adapter.Tests/
  MesControlAgv.E2E.Tests/
MesControlAgv.sln
Directory.Build.props
README.md
```

### Task 1: 创建 .NET 8 多项目解决方案骨架

**Files:**
- Create: `MesControlAgv.sln`, `Directory.Build.props`, `.gitignore`, `appsettings.Development.json`
- Create: `src/MesControlAgv.Domain/MesControlAgv.Domain.csproj`
- Create: `src/MesControlAgv.Mes/MesControlAgv.Mes.csproj`, `src/MesControlAgv.Mes/Program.cs`
- Create: `src/MesControlAgv.Adapter/MesControlAgv.Adapter.csproj`, `src/MesControlAgv.Adapter/Program.cs`
- Create: `src/MesControlAgv.Simulator/MesControlAgv.Simulator.csproj`, `src/MesControlAgv.Simulator/Program.cs`
- Create: `src/MesControlAgv.Wpf/MesControlAgv.Wpf.csproj`, `src/MesControlAgv.Wpf/App.xaml`, `src/MesControlAgv.Wpf/App.xaml.cs`, `src/MesControlAgv.Wpf/MainWindow.xaml`, `src/MesControlAgv.Wpf/MainWindow.xaml.cs`
- Create: `src/MesControlAgv.Domain/SolutionMarker.cs`
- Create: `tests/MesControlAgv.Domain.Tests/MesControlAgv.Domain.Tests.csproj`, `tests/MesControlAgv.Domain.Tests/AssemblySmokeTests.cs`

**Interfaces:** Each API offers `GET /health`, returning `{"service":"mes|adapter|simulator","status":"ok"}`. WPF opens with title `MES Control AGV`.

- [ ] **Step 1: Write failing smoke tests**

```csharp
[Fact]
public void Domain_assembly_loads()
{
    Assert.Equal("MesControlAgv.Domain", typeof(SolutionMarker).Assembly.GetName().Name);
}
```

- [ ] **Step 2: Generate solution and projects**

Run:
```bash
dotnet new sln --name MesControlAgv
dotnet new classlib --framework net8.0 --name MesControlAgv.Domain --output src/MesControlAgv.Domain
dotnet new web --framework net8.0 --name MesControlAgv.Mes --output src/MesControlAgv.Mes
dotnet new web --framework net8.0 --name MesControlAgv.Adapter --output src/MesControlAgv.Adapter
dotnet new web --framework net8.0 --name MesControlAgv.Simulator --output src/MesControlAgv.Simulator
dotnet new wpf --framework net8.0 --name MesControlAgv.Wpf --output src/MesControlAgv.Wpf
dotnet new xunit --framework net8.0 --name MesControlAgv.Domain.Tests --output tests/MesControlAgv.Domain.Tests
```

- [ ] **Step 3: Add projects and references**

```bash
dotnet sln MesControlAgv.sln add src/MesControlAgv.Domain src/MesControlAgv.Mes src/MesControlAgv.Adapter src/MesControlAgv.Simulator src/MesControlAgv.Wpf tests/MesControlAgv.Domain.Tests
dotnet add src/MesControlAgv.Mes reference src/MesControlAgv.Domain
dotnet add src/MesControlAgv.Adapter reference src/MesControlAgv.Domain
dotnet add src/MesControlAgv.Simulator reference src/MesControlAgv.Domain
dotnet add src/MesControlAgv.Wpf reference src/MesControlAgv.Domain
dotnet add tests/MesControlAgv.Domain.Tests reference src/MesControlAgv.Domain
```

Implement basic minimal API health endpoints and WPF window title.

- [ ] **Step 4: Verify**

Run:
```bash
dotnet restore MesControlAgv.sln
dotnet build MesControlAgv.sln --no-restore
dotnet test MesControlAgv.sln --no-build
```
Expected: all projects build and smoke tests pass.

- [ ] **Step 5: Commit**

```bash
git add MesControlAgv.sln Directory.Build.props .gitignore appsettings.Development.json src tests
git commit -m "feat: scaffold .NET AGV MES solution"
```

### Task 2: 实现领域模型、固定站点和纯状态机

**Files:**
- Create: `src/MesControlAgv.Domain/TaskStatus.cs`, `TaskEvent.cs`, `Station.cs`, `Stations.cs`, `TaskStateMachine.cs`
- Create: `tests/MesControlAgv.Domain.Tests/StationsTests.cs`, `TaskStateMachineTests.cs`

**Interfaces:** `Stations.Get(int code)`, `TaskStateMachine.Transition(TaskStatus current, TaskEvent action)` and `InvalidTaskTransitionException`; states are `Created`, `Dispatching`, `MovingToPickup`, `WaitingPickupConfirmation`, `MovingToDropoff`, `WaitingDropoffConfirmation`, `Completed`, `Paused`, `Failed`, `Unknown`, `Cancelled`.

- [ ] **Step 1: Write tests**

```csharp
[Fact]
public void Sample_station_maps_to_ascii_machine_id()
{
    var station = Stations.Get(2);
    Assert.Equal("样品位", station.Name);
    Assert.Equal("SAMPLE_01", station.AgvStationId);
}

[Fact]
public void Pickup_arrival_waits_for_operator_confirmation()
{
    Assert.Equal(TaskStatus.WaitingPickupConfirmation,
        TaskStateMachine.Transition(TaskStatus.MovingToPickup, TaskEvent.PickupArrived));
}
```

- [ ] **Step 2: Implement and verify**

Add fixed mappings 0–6 exactly as the design spec. Use an explicit transition dictionary; only reconciliation events can transition from `Unknown`.

Run: `dotnet test tests/MesControlAgv.Domain.Tests`

- [ ] **Step 3: Commit**

```bash
git add src/MesControlAgv.Domain tests/MesControlAgv.Domain.Tests
git commit -m "feat: add AGV station catalog and task state machine"
```

### Task 3: 添加 MES SQLite 持久化、审计和任务 API

**Files:**
- Create: `src/MesControlAgv.Mes/Data/MesDbContext.cs`, `Entities/TransportTask.cs`, `Entities/TaskEventRecord.cs`, `Entities/AgvSnapshot.cs`
- Create: `src/MesControlAgv.Mes/Services/TaskRepository.cs`, `TaskService.cs`, `Contracts/TaskContracts.cs`
- Modify: `src/MesControlAgv.Mes/Program.cs`
- Create: `tests/MesControlAgv.Mes.Tests/TaskRepositoryTests.cs`, `TaskApiTests.cs`

**Interfaces:** MES exposes `GET /api/stations`, `POST /api/tasks`, `GET /api/tasks`, `GET /api/tasks/{id}`, `POST /api/tasks/{id}/confirm-pickup`, `confirm-dropoff`, `retry`, `cancel`, `recover`, and `GET /api/agv`.

- [ ] **Step 1: Write API tests**

```csharp
[Fact]
public async Task Create_task_only_accepts_sample_to_prep_route()
{
    var response = await _client.PostAsJsonAsync("/api/tasks", new { sourceStationCode = 1, targetStationCode = 4 });
    Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
}
```

- [ ] **Step 2: Implement data and API**

Use EF Core SQLite. Persist a GUID task, source/target station codes, status, timestamps, retry count, last error and chronological JSON event payload. Only accept 2→4. State changes append an event in the same SaveChanges call.

- [ ] **Step 3: Verify and commit**

Run: `dotnet test tests/MesControlAgv.Mes.Tests`

Commit: `git commit -m "feat: persist MES tasks and audit events"`

### Task 4: 实现模拟器和幂等 AGV Adapter

**Files:**
- Create: `src/MesControlAgv.Simulator/SimulatorState.cs`, `Contracts.cs`
- Modify: `src/MesControlAgv.Simulator/Program.cs`
- Create: `src/MesControlAgv.Adapter/Data/AdapterDbContext.cs`, `Entities/AdapterTask.cs`, `Services/SimulatorClient.cs`, `AdapterService.cs`, `Contracts.cs`
- Modify: `src/MesControlAgv.Adapter/Program.cs`
- Create: `tests/MesControlAgv.Adapter.Tests/AdapterServiceTests.cs`

**Interfaces:** Simulator: `POST /commands/navigate`, `GET /tasks/{taskId}`, `GET /snapshot`, `POST /controls/arrive|fail|timeout|offline|recover`. Adapter: `POST /tasks/{taskId}/dispatch`, `GET /tasks/{taskId}`, `GET /agv/snapshot`, and pause/resume/cancel actions. Normalized state: `accepted|moving|arrived|failed|unknown|cancelled`.

- [ ] **Step 1: Write adapter behavior tests**

```csharp
[Fact]
public async Task Duplicate_dispatch_does_not_send_a_second_navigation()
{
    var first = await service.DispatchAsync(taskId, "SAMPLE_01", default);
    var second = await service.DispatchAsync(taskId, "SAMPLE_01", default);
    Assert.Equal(first.DeviceTaskId, second.DeviceTaskId);
    Assert.Equal(1, simulator.NavigateCalls);
}
```

- [ ] **Step 2: Implement and verify**

Persist Adapter `taskId`, device task ID, target station, normalized state and error. Before dispatch check control owner equals `adapter`; otherwise return 409. On timeout query task status once; persist `unknown` only when that reconciliation cannot establish actual state.

Run: `dotnet test tests/MesControlAgv.Adapter.Tests`

- [ ] **Step 3: Commit**

```bash
git add src/MesControlAgv.Adapter src/MesControlAgv.Simulator tests/MesControlAgv.Adapter.Tests
git commit -m "feat: add idempotent AGV adapter and simulator"
```

### Task 5: 连接 MES 编排、人工确认和重启恢复

**Files:**
- Create: `src/MesControlAgv.Mes/Services/AdapterClient.cs`, `RecoveryService.cs`
- Modify: `src/MesControlAgv.Mes/Services/TaskService.cs`, `Program.cs`
- Create: `tests/MesControlAgv.Mes.Tests/TransportWorkflowTests.cs`

**Interfaces:** On create MES dispatches pickup (`SAMPLE_01`); on simulator arrival transitions to pickup confirmation; pickup confirmation dispatches dropoff (`ST_PREP_01`); dropoff confirmation completes. On host startup `RecoveryService` reconciles incomplete tasks with Adapter.

- [ ] **Step 1: Write workflow tests**

```csharp
[Fact]
public async Task Pickup_confirmation_dispatches_dropoff()
{
    var task = await service.CreateAsync(2, 4, default);
    await service.RecordArrivalAsync(task.Id, default);
    var updated = await service.ConfirmPickupAsync(task.Id, "operator", default);
    Assert.Equal(TaskStatus.MovingToDropoff, updated.Status);
    Assert.Equal("ST_PREP_01", adapter.LastTargetStationId);
}
```

- [ ] **Step 2: Implement and verify**

Retry only from `Failed`; cancel only after Adapter confirms cancellation. Incomplete task recovery maps Adapter `moving`, `arrived`, `failed`, `cancelled`, unresolved error to correct MES statuses and event records. Never call a second navigation for `Unknown` without reconciliation.

Run: `dotnet test tests/MesControlAgv.Mes.Tests`

- [ ] **Step 3: Commit**

```bash
git add src/MesControlAgv.Mes tests/MesControlAgv.Mes.Tests
git commit -m "feat: orchestrate AGV transport and recovery"
```

### Task 6: 实现 WPF MVVM 运行看板

**Files:**
- Create: `src/MesControlAgv.Wpf/Models/ApiContracts.cs`, `Services/MesApiClient.cs`
- Create: `src/MesControlAgv.Wpf/ViewModels/MainViewModel.cs`, `TaskViewModel.cs`, `AsyncRelayCommand` usage
- Modify: `App.xaml`, `App.xaml.cs`, `MainWindow.xaml`, `MainWindow.xaml.cs`
- Create: `src/MesControlAgv.Wpf/Views/TaskDetailView.xaml`, `TaskDetailView.xaml.cs`
- Create: `tests/MesControlAgv.Wpf.Tests/MainViewModelTests.cs`

**Interfaces:** WPF polls `/api/agv` and `/api/tasks` every two seconds. Buttons call task creation (only 2→4), pickup/dropoff confirmation, retry, recover and cancel. A development-only simulation panel invokes simulator controls.

- [ ] **Step 1: Write ViewModel tests**

```csharp
[Fact]
public async Task Create_command_posts_fixed_sample_to_prep_route()
{
    await viewModel.CreateTaskCommand.ExecuteAsync(null);
    Assert.Equal((2, 4), client.CreatedRoute);
}
```

- [ ] **Step 2: Implement MVVM UI and verify**

Use `CommunityToolkit.Mvvm`. Bind status text, task list, selected task and event timeline; do not update task status in client-side code. Use visible text/status badges plus accessible labels; render simulation controls only under `DEBUG`.

Run: `dotnet test tests/MesControlAgv.Wpf.Tests && dotnet build src/MesControlAgv.Wpf`

- [ ] **Step 3: Run UI and exercise golden path**

Start simulator, adapter and MES locally with `dotnet run --project ...`, launch WPF. Create task; arrive; confirm pickup; arrive; confirm dropoff; verify completed. Simulate failure and timeout, verify retry/recover paths.

- [ ] **Step 4: Commit**

```bash
git add src/MesControlAgv.Wpf tests/MesControlAgv.Wpf.Tests
git commit -m "feat: add WPF AGV operator dashboard"
```

### Task 7: 端到端验收、运行说明和部署配置

**Files:**
- Create: `tests/MesControlAgv.E2E.Tests/TransportRecoveryTests.cs`
- Modify: `README.md`, `appsettings.Development.json`
- Create: `scripts/run-local.ps1`

- [ ] **Step 1: Write and run acceptance tests**

Cover normal two-leg task, 10 successive tasks, navigation failure/retry with the same GUID, timeout to `Unknown` plus recovery, and MES process restart reconciliation.

Run: `dotnet test MesControlAgv.sln`
Expected: all suites pass.

- [ ] **Step 2: Document exact local workflow**

README must contain prerequisites (.NET 8 SDK), solution build/test commands, service launch commands, WPF launch command, normal task demo, error injection demo, database locations and statement that a real AGV replaces only Adapter client implementation.

- [ ] **Step 3: Commit**

```bash
git add tests/MesControlAgv.E2E.Tests README.md appsettings.Development.json scripts/run-local.ps1
git commit -m "test: verify AGV transport recovery scenarios"
```

## Coverage Review

Tasks 1–7 cover the confirmed WPF technology constraint, three-layer control architecture, fixed station catalog, state machine, SQLite audit trail, Adapter idempotency, timeout reconciliation, control ownership, manual confirmations, WPF monitoring/actions, and all MVP acceptance scenarios.
