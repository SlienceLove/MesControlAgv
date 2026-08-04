# 单 AGV 中控 MES MVP 设计说明

**日期：** 2026-07-29  
**状态：** MVP 已确认；平台化模板重构准备已确认，待分阶段实施

## 1. 目标与范围

从零开发一个用于控制 AGV 机器人的轻量中控 MES，首期只验证单车、固定站点、单次搬运的安全闭环与异常恢复能力。

### 1.1 MVP 成功标准

- 单台 AGV 在 7 个固定站点间执行搬运；
- 首条主路线为“样品位 → 液体前处理工作站”；
- 操作员完成取货与放货确认；
- 验证正常派单、到站、人工确认、完成、失败重试与服务重启恢复；
- 以 AGV 模拟器先完成验证，后续替换为真实 AGV；
- 同一 `task_id` 绝不重复派发；
- 任务状态和事件可追溯。

### 1.2 不在 MVP 范围内

- 多 AGV 调度、避障、路径规划；
- WMS、库存、批次追溯、复杂工艺配方；
- 真实机械臂、PLC、DI/DO、工作站通讯；
- 在线编辑 AGV 地图和站点；
- RoboShop 参与生产控制。RoboShop 仅用于现场建图、站点配置和调试。

## 2. 总体架构

采用“中控 MES + 独立 AGV Adapter + AGV 模拟器”方案。

```text
操作员 WPF 桌面中控界面
     │ 创建任务 / 到站确认 / 重试或取消
     ▼
中控 MES 服务
  - 任务状态机
  - 固定路线编排
  - 断点恢复
  - 操作日志
     │ 标准化任务命令
     ▼
AGV Adapter
  - task_id 幂等
  - AGV 状态缓存
  - 超时后查询真实状态
  - 站点映射
  - 后续切换 TCP API / Modbus
     │
     ├── MVP：AGV 模拟器
     └── 第二阶段：真实 AGV
```

### 2.1 组件边界

| 组件 | 负责内容 | 不负责内容 |
|---|---|---|
| 中控 MES 服务 | 任务创建、状态机、人工确认、恢复决策、持久化、操作日志 | 厂商 TCP/Modbus 报文 |
| AGV Adapter | 站点映射、幂等、控制权、协议调用、状态轮询或推送转换 | 工艺流程和界面逻辑 |
| AGV 模拟器 | 移动、到站、失败、超时、离线与恢复模拟 | 真实路径规划或避障 |
| WPF 桌面中控客户端 | 看板、任务操作、状态与日志展示 | 绕过中控自行改变任务状态 |

Adapter 是整个中控项目的一部分，但作为独立运行服务隔离厂商设备协议。MVP 可使用同一代码仓库，部署为中控服务、Adapter 服务和模拟器服务三个进程。

## 3. 站点模型

中控使用稳定的业务编号；Adapter 将其映射为 AGV 地图中配置的 ASCII 站点 ID。中控不依赖坐标，因此现场重建地图或更新坐标不需要修改业务流程。

| 业务编号 | 业务名称 | 建议机器站点 ID |
|---:|---|---|
| 0 | 充电桩 | `CHARGE_01` |
| 1 | 耗材位 | `PICK_01` |
| 2 | 样品位 | `SAMPLE_01` |
| 3 | 开盖分液工作站 | `ST_OPEN_01` |
| 4 | 液体前处理工作站 | `ST_PREP_01` |
| 5 | 自动进样器 | `ST_INJECT_01` |
| 6 | 样品回收位 | `DROP_01` |

首期站点映射由配置文件或数据库初始化数据提供，只读展示，不提供 WPF 客户端在线编辑。

## 4. 任务生命周期

一个任务描述一次从起点到终点的搬运。中控创建全局唯一的 UUID `task_id`；Adapter 用同一 ID 关联模拟器或真实 AGV 任务。

```text
CREATED
  ↓
DISPATCHING
  ↓
MOVING_TO_PICKUP
  ↓
WAITING_PICKUP_CONFIRM
  ↓
MOVING_TO_DROPOFF
  ↓
WAITING_DROPOFF_CONFIRM
  ↓
COMPLETED
```

异常状态：

- `PAUSED`：操作员暂停，或恢复时需要人工决定；
- `FAILED`：AGV 明确报告失败，例如导航失败、急停；
- `UNKNOWN`：通讯超时，尚未确认 AGV 实际执行结果；
- `CANCELLED`：操作员取消，且 Adapter 已确认设备不会继续执行。

状态变更必须由中控服务处理并写入事件日志。WPF 客户端只请求操作，不能直接修改状态。

## 5. 异常恢复与幂等规则

| 场景 | 处理规则 |
|---|---|
| 下发后网络超时 | 转为 `UNKNOWN`，按 `task_id` 查询真实 AGV 状态；不得直接重发。 |
| AGV 仍在执行 | 恢复至相应运行状态并继续显示进度。 |
| AGV 已到站但回调丢失 | 根据当前站点和任务阶段恢复至相应人工确认状态。 |
| AGV 明确失败 | 转为 `FAILED`，记录错误码、错误信息、最近 AGV 状态；操作员可重试或取消。 |
| 中控重启 | 读取全部未完成任务，逐一查询 Adapter 后恢复状态。 |
| 重复派发或重试 | 同一 `task_id` 只允许一个有效 AGV 执行实例；Adapter 返回既有状态而非重新派单。 |
| RoboShop 占用控制权 | Adapter 拒绝派单，明确提示设备处于调试控制；操作员结束调试后再重试。 |

## 6. 内部接口

中控仅调用 Adapter 的稳定业务接口，不直接依赖厂商协议：

```text
dispatch(taskId, targetStationId)
getTaskStatus(taskId)
getAgvSnapshot()
pause(taskId)
resume(taskId)
cancel(taskId)
```

Adapter 向中控归一化返回：

```text
accepted | moving | arrived | failed | unknown | cancelled
```

真实 AGV 客户端和模拟器客户端在 Adapter 内实现同一能力。切换时不改变中控任务状态机或页面流程。

## 7. MVP 功能清单

1. 固定站点展示：内置 7 个站点及业务编号到机器站点 ID 的映射。
2. 搬运任务：创建样品位到前处理站的任务；保留通用数据结构，但前端只开放首条主路线。
3. 任务执行：派发、实时状态刷新、人工取货/放货确认、暂停、继续、重试和取消。
4. 运行监控：AGV 在线状态、控制权、当前位置、当前任务、最近错误。
5. 恢复能力：中控重启后恢复未完成任务；异常后可查询、恢复、重试或取消。
6. 操作日志：记录创建、派发、设备状态、人工确认、异常、恢复和取消。
7. 模拟器控制：可模拟正常到站、导航失败、网络超时、离线和恢复；仅开发和演示环境启用。

## 8. 数据模型

```text
agv_station
- code: 0..6
- name
- agv_station_id
- enabled

transport_task
- id: UUID
- source_station_code
- target_station_code
- status
- created_at
- updated_at
- retry_count
- last_error

task_event
- id
- task_id
- event_type
- payload
- created_at

agv_snapshot
- agv_id
- online_status
- control_owner
- current_station_id
- current_task_id
- raw_status
- updated_at
```

## 9. 最小界面

### 9.1 运行看板

- 显示 AGV 在线状态、控制权、当前位置和当前任务；
- 显示当前任务状态与下一步动作；
- 提供“样品位 → 前处理站”任务创建按钮；
- AGV 到对应站点时展示“确认已取货”或“确认已放货”。

### 9.2 任务列表

- 显示任务 ID、起终点、状态、创建时间和最近错误；
- 可筛选进行中、异常和已完成任务；
- 对 `FAILED` 和 `UNKNOWN` 提供重试、恢复查询和取消。

### 9.3 任务详情

- 显示任务状态时间线；
- 展示 Adapter 请求、AGV 返回和人工操作事件；
- 服务于现场排查，不包含复杂报表。

### 9.4 模拟器控制页

- 控制模拟到站、失败、网络超时、离线和恢复；
- 仅用于开发、测试和演示。

## 10. 测试与验收

### 10.1 测试分层

1. 状态机单元测试：验证合法和非法状态迁移、重复确认、重复派单、取消后的回调。
2. Adapter 集成测试：基于模拟器验证派发、到站、状态查询、重复 `task_id`、超时、离线和导航失败。
3. 端到端演示测试：创建任务、完成两次人工确认、服务重启恢复、处理 `UNKNOWN` 和 `FAILED`。

### 10.2 验收标准

- 至少连续完成 10 次“样品位 → 前处理站”搬运闭环；
- 每次均要求取货和放货人工确认；
- 正确处理网络超时、导航失败、服务重启三类异常；
- 同一 `task_id` 不重复派发；
- 每一次任务状态和事件均可追溯；
- 替换真实 AGV 时，只需替换 Adapter 内部设备客户端，不修改中控状态机和界面流程。

## 11. 实施顺序

1. 实现任务状态机、数据库持久化和事件日志；
2. 实现 Adapter 的统一接口、幂等和模拟器客户端；
3. 实现 AGV 模拟器及异常注入；
4. 实现运行看板、任务列表、详情和人工确认；
5. 实现服务重启恢复与端到端验收脚本；
6. 在真实 AGV 接口、地图站点与控制权规则确认后，实现真实客户端并替换模拟器客户端。

## 12. 标准平台化与扩展性重构准备

### 12.1 产品定位

当前项目后续不以“复制一份代码开发一个客户版本”为目标，而是建设一个可交付、可配置、可扩展的标准中控平台。标准平台应覆盖主流中控软件的通用能力，客户项目通过配置、流程、策略、设备驱动和 UI 模块进行二次开发，尽量不修改平台核心。

当前 MVP 仍然是单一实验运输场景的验证基线。平台化改造采用渐进方式，不推倒重来，也不影响 Simulator、MES、Adapter 和 WPF 的现有验收闭环。

### 12.2 目标分层

```text
Control Center Host
  └─ WPF Shell：导航、菜单、主题、权限、消息、模块装载
      ├─ 标准模块：任务、AGV、流程、批量导入、KPI、报警、配置
      └─ 客户模块：仪器、条码、特殊实验流程、客户报表

Application 应用用例层
  └─ 创建、派发、确认、取消、重试、恢复、调度、设备控制

Ports / Contracts 接口契约层
  └─ AGV、仪器、MES/WMS、流程、调度、报警、审计接口

Domain 核心领域层
  └─ 任务、流程、站点、设备能力、规则和领域事件

Infrastructure / Drivers 基础设施层
  └─ TCP、HTTP、PLC、仪器协议、数据库、文件和第三方 SDK
```

依赖方向必须保持为：

```text
WPF/Host → Application → Domain/Contracts
Infrastructure/Drivers → Contracts/Domain
Domain 不依赖具体厂商协议、WPF 或数据库实现
```

### 12.3 当前架构与目标架构的调整边界

当前 `Domain`、`MES`、`Adapter`、`Simulator`、`WPF` 的拆分可以保留，先通过接口和职责调整实现平台化，不要求立即物理拆成大量项目。

| 当前位置 | 平台化方向 | 需要调整的重点 |
|---|---|---|
| `MesControlAgv.Domain` | Platform Core | 从固定路线和固定客户流程中提取通用领域模型 |
| `MesControlAgv.Mes` | Application、API Host、Persistence | 将业务用例、API 契约、持久化职责分开 |
| `MesControlAgv.Adapter` | Device Infrastructure | 以驱动接口承载 Simulator、TCP 和后续厂商协议 |
| `MesControlAgv.Simulator` | Device Simulator | 与真实设备实现同一套能力契约 |
| `MesControlAgv.Wpf` | Shell + Standard Modules | 拆分 `MainViewModel`，增加模块注册和能力驱动的 UI |

当前需要优先处理的耦合点：

1. [x] Establish shared Contracts for tasks, KPI, stations, device snapshots, commands, and planning responses.
2. [x] Establish the Application use-case boundary for task and KPI operations and normalized AGV gateway ports.
3. [x] Keep MES services behind Application interfaces and keep WPF API mapping behind shared Contracts.
4. [x] Keep AGV HTTP/TCP protocol infrastructure behind Adapter/MES gateway ports without leaking vendor DTOs into the UI.
5. [x] Establish the first device capability model and WPF module registration boundary; vendor-specific `IAgvDriver` extraction remains a follow-up.
6. [ ] Move fixed stations, maps, device parameters, and timeouts into Profile/configuration or persistence.
7. [ ] Split `MainViewModel` into task-monitor, AGV, batch-import, KPI, workflow, and future alarm/device modules.
8. [ ] Add workflow versioning, validation, publishing, and runtime execution entry points.

### 12.3.1 First Contracts and Application boundary (2026-08-04)

The first platform boundary is now implemented without changing the validated MVP runtime behavior.

New shared projects:

```text
src/MesControlAgv.Contracts
  Tasks / Kpi / Devices       # shared API and device contracts
src/MesControlAgv.Application
  TaskApplicationBoundary     # task use-case boundary
  KpiApplicationBoundary      # KPI use-case boundary
  Ports/AgvGateway            # normalized AGV gateway ports
```

Dependency direction:

```text
Application -> Contracts + Domain
MES         -> Application + Contracts + Domain
Adapter     -> Contracts + Domain
WPF         -> Contracts + Domain
```

Boundary decisions:

1. `Contracts` owns transport-safe records and does not depend on EF Core, WPF, or vendor protocol DTOs.
2. `Application` owns task/KPI use-case interfaces and AGV gateway ports; it does not own HTTP/TCP or WPF concerns.
3. MES `TaskService` implements `ITaskApplicationService`, `KpiDashboardService` implements `IKpiDashboardApplicationService`, and API endpoints depend on those interfaces.
4. AGV HTTP/TCP communication remains in Adapter/MES infrastructure behind `IAgvGateway`, `IRouteAwareAgvGateway`, and `IFleetAwareAgvGateway`.
5. WPF `MesClient` deserializes shared Contracts and maps them into UI models instead of exposing API DTOs directly to pages.

Device capability and module boundary:

- `AgvCapabilitiesResponse` travels with each AGV snapshot. Adapter fills missing capability metadata with the standard capability set, and WPF gates pause/resume/cancel commands from declared capabilities.
- WPF now has `ControlCenterModuleRegistry` and stable IDs for task monitoring, AGV communication, batch import, KPI dashboard, and workflow design.
- The registry currently owns module metadata, ordering, enablement, and duplicate-ID checks. View, Command, Service, and Permission registration will be added in the next slice.
- Vendor-specific `IAgvDriver`, Profile/configuration, and full `MainViewModel` decomposition are intentionally not claimed complete yet.

### 12.4 设备协议与能力扩展

设备协议只负责连接、状态读取、命令发送、协议转换和厂商错误转换，不负责 MES 任务生命周期、客户流程或 UI。

最小 AGV 扩展边界如下：

```csharp
public interface IAgvDriver
{
    string DriverId { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<AgvSnapshot> GetSnapshotAsync(
        string agvId,
        CancellationToken cancellationToken);

    Task<AgvCommandResult> DispatchAsync(
        AgvDispatchCommand command,
        CancellationToken cancellationToken);

    Task<AgvCommandResult> PauseAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken);

    Task<AgvCommandResult> ResumeAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken);

    Task<AgvCommandResult> CancelAsync(
        AgvControlCommand command,
        CancellationToken cancellationToken);
}
```

不同设备通过能力模型决定可执行操作，不在业务层大量使用厂商判断：

```csharp
public sealed record DeviceCapabilities(
    bool SupportsPause,
    bool SupportsResume,
    bool SupportsCancel,
    bool SupportsEmergencyStop,
    bool SupportsLift,
    bool SupportsBarcode,
    bool SupportsStationConfirmation);
```

后续可分别增加 `IInstrumentDriver`、`IBarcodeScanner`、`IPlcDriver` 等专用接口。暂不设计包含所有设备行为的万能设备接口。

### 12.5 流程、策略和模块扩展

流程定义与流程执行器分离。标准流程和客户流程均应有唯一标识、版本和生命周期：

```csharp
public interface IWorkflowDefinition
{
    string WorkflowId { get; }
    string Version { get; }
    IReadOnlyList<WorkflowStepDefinition> Steps { get; }

    Task<WorkflowTransitionResult> HandleEventAsync(
        WorkflowContext context,
        WorkflowEvent workflowEvent,
        CancellationToken cancellationToken);
}
```

流程步骤应逐步覆盖 AGV 运输、人工确认、扫码、仪器检测、等待信号、接口调用、异常分支、重试和超时。标准状态机继续负责通用安全约束，客户差异通过流程定义或策略扩展，不通过 `if (customer == ...)` 污染核心代码。

调度规则也应通过 `IAgvSchedulingStrategy` 抽象，支持最近 AGV、优先级、电量、区域、设备能力和路径冲突等不同策略。

中控模块采用注册机制：

```csharp
public interface IControlCenterModule
{
    string ModuleId { get; }
    string DisplayName { get; }

    void RegisterServices(IServiceCollection services);
    void RegisterViews(IControlCenterViewRegistry registry);
    void RegisterCommands(IControlCenterCommandRegistry registry);
}
```

标准模块包括任务监控、AGV 通讯、工作流、批量任务、KPI、报警、审计和系统配置；客户模块可增加仪器、条码、特殊实验流程和客户报表。

### 12.6 配置、Profile 与客户二次开发边界

使用三级扩展机制：

1. **配置化**：设备地址、驱动选择、站点、地图、功能开关、页面显示、超时和权限。
2. **策略/插件化**：客户流程、调度策略、仪器驱动、专属报表和专属页面。
3. **平台核心升级**：只处理核心任务模型、安全模型、公共契约和基础设施的稳定演进。

Profile 示例：

```json
{
  "ProductProfile": "CustomerA",
  "Agv": {
    "Driver": "VendorTcp",
    "Ids": ["AGV-01", "AGV-02"]
  },
  "Features": {
    "KpiDashboard": true,
    "BatchImport": true,
    "WorkflowDesigner": true,
    "InstrumentControl": false
  }
}
```

配置不承载所有复杂业务规则；复杂规则使用流程、策略或插件实现。插件需要声明兼容的平台 API 版本，生产环境采用白名单、签名或受控发布机制，避免未知 DLL 被加载。

### 12.7 推荐的代码与发布边界

推荐最终形成“共享平台 + 客户扩展”的结构：

```text
Platform.Core / Contracts / Application / DeviceAbstractions
Standard WPF Shell / Standard Modules / Simulator / Common Drivers
CustomerExtensions/<CustomerId>/Profile / Workflow / Driver / UI Module
```

不复制整个 WPF 项目作为客户版本。平台通过稳定版本发布，客户扩展声明兼容版本；标准平台升级时保留客户配置、流程版本和数据库迁移能力。

### 12.8 平台化重构准备阶段

#### P0: before the first deep customer customization

1. [x] Establish shared Contracts for tasks, KPI, stations, device snapshots, commands, and planning responses.
2. [x] Establish the Application use-case boundary so WPF does not own business-state transitions.
3. [ ] Move fixed stations, maps, device parameters, and timeouts into Profile/configuration or persistence.
4. [ ] Split WPF `MainViewModel` into task-monitor, AGV, batch-import, KPI, and workflow modules.
5. [x] Establish the first device capability model and WPF module registration boundary; vendor-specific `IAgvDriver` extraction remains a follow-up.
6. [ ] Add workflow versioning, validation, publish status, and runtime execution entry points.

#### P1：平台能力增强

1. 增加流程执行器、调度策略接口和统一报警模型。
2. 增加仪器、扫码、PLC 等设备的专用抽象。
3. 增加 API/插件兼容版本和客户 Profile 管理。
4. 增加合同测试，确保 Simulator、Vendor Driver 遵循同一设备契约。

#### P2：客户交付准备

1. 形成插件清单、兼容性检查、配置迁移和数据库升级规范。
2. 建立标准模块与客户扩展的示例工程。
3. 建立签名、白名单、审计、回滚和生产发布流程。
4. 形成真实设备、仪器、流程和安全策略的现场验收清单。

### 12.9 暂不进行的过度设计

- 暂不拆分为微服务；
- 暂不建设通用商业 BPM 或无限制低代码平台；
- 暂不把所有业务规则塞进 JSON；
- 暂不设计覆盖所有设备类型的万能接口；
- 暂不为了平台化破坏当前 Simulator 验收链路。

### 12.10 平台化验收标准

平台化准备完成后，应满足：

1. 新增 AGV 厂商只增加 Driver，不修改 Domain、标准任务服务和 WPF 页面。
2. 新增客户流程只增加 Workflow Definition、策略或客户模块，不复制整个中控项目。
3. 新增客户页面通过模块注册、Profile 和权限加载。
4. 修改站点、设备地址、AGV 数量和功能开关不需要修改平台核心代码。
5. 标准平台升级后，客户扩展仍可通过兼容性检查，客户流程、配置和数据可迁移或回滚。
6. 所有设备命令具备能力检查、幂等、超时和审计记录。

本节是 MVP 之后的平台化重构准备基线；在 P0 完成前，新增客户需求优先通过接口和配置验证，不直接复制项目或在核心代码中堆叠客户分支。