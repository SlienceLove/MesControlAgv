# 开盖分液单步骤正式业务链实施计划

日期：2026-09-17
设计依据：[`2026-09-17-sample-workstation-single-step-business-chain-design.md`](../specs/2026-09-17-sample-workstation-single-step-business-chain-design.md)

## 1. 实施目标

通过现有正式入口完成一次：

```text
实验方案 -> 实验任务 -> 排程 -> 运行准入 -> WorkflowRun
  -> SAMPLE-WORKSTATION-01 单次启动 TEST-001
  -> Running -> Completed
  -> 任务/排程完成并释放工作站租约
```

本计划不启用实体多步骤复合运行，不增加任务创建、任务表导入、远程初始化或远程停止能力。

## 2. 总体策略

现有代码已经分别覆盖单步骤准入、资源租约、工作站单次发令、运行证据、状态投影和 WPF 操作。本阶段先增加一条跨边界自动化用例，把这些能力串成同一条业务链；只有该用例暴露真实缺口时，才修改最小责任模块。

随后在已有现场隔离运行数据中创建单步骤实验方案和任务，通过 WPF 正式页面执行一次真机验收。任何自动化测试都不得访问真实设备。

## Task 1：建立跨边界业务链自动化用例

### 文件

- 新增：`tests/MesControlAgv.Mes.Tests/ExperimentSampleWorkstationBusinessChainTests.cs`
- 可能复用：`tests/MesControlAgv.Mes.Tests/MesWebApplicationFactory.cs`
- 参考：`tests/MesControlAgv.Mes.Tests/ExperimentRuntimeAdmissionApiTests.cs`
- 参考：`tests/MesControlAgv.Mes.Tests/WorkflowSampleWorkstationWorkerTests.cs`

### 步骤

1. 创建仅用于测试的物理 Profile：
   - `UseSimulator=false`。
   - `SAMPLE-WORKSTATION-01` 启用且允许控制。
   - 声明 `sample-workstation.start-existing-task`。
2. 用测试替身替换 `ISampleWorkstationReader` 和 `ISampleWorkstationCommands`，禁止真实 HTTP；替身只返回：
   - 发令前 Idle/0 + 旧 Completed。
   - 发令后 Running/1 + 状态码 3。
   - 最终 Idle/0 + Completed + 状态码 0。
3. 通过现有 MES API 创建并发布无人工门禁的工作流：开始 -> `sample-workstation.execute-existing-task` -> 结束，节点参数为 `deviceId=SAMPLE-WORKSTATION-01`、`taskNo=TEST-001`。
4. 通过实验方案 API 创建、校验并发布一个单步骤方案，资源要求为 `workstation/SAMPLE-WORKSTATION-01`。
5. 创建实验任务并排程该工作站资源。
6. 调用正式运行准入 API，不直接调用工作流执行 API。
7. 验证准入后：
   - 只创建一个 `WorkflowExecutionRecord`。
   - 任务和排程为 Admitted。
   - 只有一个活动工作站租约，且绑定该 WorkflowRun。
8. 显式调用工作站 Dispatcher 的单轮处理方法，避免依赖后台线程时序。
9. 验证完成后：
   - 替身的 `StartTask` 调用数为 1，`Initialize` 调用数为 0。
   - 只有一条工作站设备操作。
   - 工作流 Completed，节点 Succeeded。
   - 实验任务 Completed，排程 Completed。
   - 工作站租约 Released 且 `ActiveResourceKey=null`。
   - 排程查询返回一条关联该任务、WorkflowRun 和工作站资源的实际设备活动。

### 先失败再通过

先运行新用例并确认它确实覆盖完整链路。若用例直接通过，不修改产品代码；若失败，记录失败边界并进入 Task 2。

### 验证命令

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleWorkstationBusinessChainTests"
```

### 提交

```text
test: cover workstation experiment business chain
```

## Task 2：仅按失败边界做最小修复

本 Task 是条件任务。Task 1 直接通过时跳过，不制造无需求改动。

### 候选责任文件

- 准入或固定版本错误：`src/MesControlAgv.Mes/Services/ExperimentRuntimeAdmissionService.cs`
- 工作流到实验任务的状态同步错误：`src/MesControlAgv.Mes/Services/ExperimentRuntimeLeaseLifecycle.cs`
- 工作站运行完成后没有触发生命周期同步：`src/MesControlAgv.Mes/Services/WorkflowApplicationService.cs` 或实际完成节点的现有运行时服务
- 实际活动缺失或资源关联错误：`src/MesControlAgv.Mes/Services/ExperimentSchedulingQueryService.cs`
- WPF 正式入口显示或刷新阻断：`src/MesControlAgv.Wpf/ViewModels/ExperimentSchedulingViewModel.cs`

### 修复规则

1. 只修改测试证明失败的最小责任文件。
2. 不在排程 UI 中直接调用设备接口。
3. 不新增工作站专用业务页面。
4. 不改变工作站节点的单次发令、Running 证据、Unknown 和只读恢复语义。
5. 修复后先运行 Task 1 用例，再运行责任模块既有测试。

### 可能的定向命令

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentRuntimeAdmissionApiTests|FullyQualifiedName~ExperimentSchedulingPersistenceTests|FullyQualifiedName~WorkflowSampleWorkstationWorkerTests|FullyQualifiedName~ExperimentSampleWorkstationBusinessChainTests"
```

若涉及 WPF：

```powershell
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~WorkflowRunMonitorViewModelTests"
```

### 提交

仅在发生产品代码修复时提交：

```text
fix: complete workstation experiment business chain
```

## Task 3：回归验证与构建

### 测试

```powershell
dotnet test tests/MesControlAgv.WorkflowContract.Tests/MesControlAgv.WorkflowContract.Tests.csproj --no-restore
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --filter "FullyQualifiedName~SampleWorkstation"
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~WorkflowRunMonitorViewModelTests|FullyQualifiedName~ExperimentPlanManagementViewModelTests"
```

### 构建

使用独立输出目录，避免覆盖正在运行的现场程序：

```powershell
$buildRoot = Join-Path $env:TEMP "mes-workstation-business-chain-build"
dotnet build MesControlAgv.sln --no-restore --artifacts-path "$buildRoot"
```

如全量 WPF 测试仍只有既有 linked-worktree 仓库根目录识别失败，需在记录中明确区分，不把它归因于本阶段改动。

## Task 4：准备现场隔离运行实例

### 边界

- 继续使用隔离工作区和现场隔离数据目录。
- 不修改仓库中的默认 `appsettings.json` 或 `appsettings.PhysicalAcceptance.json` 控制开关。
- 不覆盖主工作区构建产物。
- `ExperimentCompositeRuntimeWorker` 保持关闭。
- 此 Task 只启动/核对服务和创建业务数据，不发送工作站启动命令。

### 配置核对

Adapter 运行覆盖：

```json
{
  "Devices": {
    "SampleWorkstation": {
      "DeviceId": "SAMPLE-WORKSTATION-01",
      "EquipmentNo": "CYC-001-1000",
      "BaseUrl": "http://192.168.200.157:8082/Service/",
      "Enabled": true,
      "ControlEnabled": true
    }
  }
}
```

MES Profile 运行覆盖必须包含：

```json
{
  "deviceId": "SAMPLE-WORKSTATION-01",
  "deviceFamily": "sample-workstation",
  "capabilityIds": ["sample-workstation.status", "sample-workstation.start-existing-task"],
  "enabled": true,
  "controlEnabled": true
}
```

MES Worker：

```json
{
  "WorkflowSampleWorkstationWorker": {
    "Enabled": true,
    "PollIntervalMs": 1000,
    "ReadinessRetryIntervalMs": 2000,
    "StartObservationTimeoutMs": 30000,
    "CompletionTimeoutMs": 600000
  },
  "ExperimentCompositeRuntimeWorker": {
    "Enabled": false
  }
}
```

### 业务数据准备

1. 核对 Workflow ID `5d409b9b-45fc-46e9-bd4f-46868ddefbcf` 的 v2 为 Published。
2. 在 WPF“实验方案”新建 `开盖分液单步骤正式方案-20260917`：
   - 唯一步骤引用该 Workflow v2。
   - 预计时长 10 分钟。
   - 资源要求 `workstation/SAMPLE-WORKSTATION-01`、数量 1、独占。
3. 校验并发布方案。
4. 在 WPF“任务排程”创建现场验收任务：
   - `SampleBatchId=WS-CHAIN-20260917-001`。
   - `SampleId` 由现场样品标识决定，可留空。
5. 将任务排入当前测试窗口，选择 `SAMPLE-WORKSTATION-01`。
6. 停在“运行准入”之前，记录 Plan、Job、Schedule ID。

### 停止条件

出现任一情况即不进入真机执行：

- v2 不存在或不是 Published。
- 方案校验失败。
- 资源列表没有 `SAMPLE-WORKSTATION-01`。
- 任务或排程不是 Scheduled。
- WPF 的“运行准入”不可用且原因无法由缺少操作人/原因字段解释。

## Task 5：执行一次正式真机验收

### 发令前只读核对

必须同时满足：

- 厂家程序已经监听 `192.168.200.157:8082`。
- 工作站 Online。
- 设备 Idle/0。
- 错误码 0。
- `TEST-001` 不处于 Running。
- 本实验任务没有关联既有 WorkflowRun。
- `SAMPLE-WORKSTATION-01` 没有活动资源租约。

### 执行

1. 在 WPF“任务排程”选中 `WS-CHAIN-20260917-001`。
2. 填写操作人和原因。
3. 点击“运行准入”，在 WPF 确认框确认一次。
4. 不使用隐藏联调入口，不直接调用厂家启动 API，不重复点击运行准入。
5. 观察：
   - Job：Scheduled -> Admitted -> Running -> Completed；Schedule：Scheduled -> Admitted -> Completed。
   - Workflow：Running -> Completed。
   - 工作站节点：Ready -> Running -> Succeeded。
   - 设备操作：Prepared/StartPending/Accepted -> Running -> Succeeded。
   - 厂家任务：本轮 Running -> Completed。
   - 设备：Running/1 -> Idle/0；最终错误码 0。
6. 完成后刷新“任务排程”“流程运行监控”和资源时间轴。

### 禁止行为

- 不自动重试启动请求。
- 不在结果不明时再点一次运行准入。
- 不远程初始化或停止设备。
- 不把发令前已经存在的 Completed 当作本轮完成。
- 不在同一轮制造断线、关闭厂家程序或人工中断。

### 现场验收记录

至少记录：

- Plan ID/Version。
- Job ID。
- Schedule Entry ID。
- Admission Request ID。
- WorkflowRun ID。
- Node Execution ID。
- Device Operation ID。
- Running 首次观察时间。
- Completed 时间。
- 工作站启动请求计数。
- 租约释放状态。
- WPF 运行监控和资源时间轴截图。

## Task 6：归档和交接

### 文件

- 更新：`docs/SAMPLE-WORKSTATION-FORMAL-WORKFLOW-HANDOFF-2026-09-16.md`
- 更新：`docs/SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md`
- 新增：`docs/diagnostics/2026-09-17-workstation-business-chain-validation.md`

### 内容

1. 记录自动化验证结果和是否发生产品代码修复。
2. 记录现场所有业务和执行 ID。
3. 记录单次发令、Running、Completed、任务/排程收口和租约释放证据。
4. 若验收失败，记录失败层级和现场事实，不把 Unknown 改写为 Failed 或 Completed。
5. 明确后续仍未实现：多步骤实体运行、任务表导入、动态任务创建和远程停止。

### 提交

```text
docs: archive workstation business-chain validation
```

## 9. 完成判定

只有同时满足以下条件，才宣布本阶段完成：

- 跨边界自动化用例通过。
- 相关回归测试和隔离构建通过，或仅保留已知且无关的 WPF linked-worktree 失败。
- 正式 WPF 业务入口完成一次真机运行。
- 工作站启动调用恰好一次。
- 本轮 Running 证据存在。
- Workflow、Job、Schedule 均完成。
- 工作站租约已释放。
- 实际设备活动在排程时间轴可见。
- 验收证据和交接文档已提交。
