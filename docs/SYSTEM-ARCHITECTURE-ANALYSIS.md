# AGV MES 系统架构与功能设计分析

> **文档版本**: 1.0  
> **生成日期**: 2026-08-14  
> **项目**: AGV MES MVP - 实验室自动化场景轻量级任务中控  
> **技术栈**: .NET 8 + WPF + SQLite

---

## 目录

1. [系统概述](#1-系统概述)
2. [整体架构](#2-整体架构)
3. [核心模块设计](#3-核心模块设计)
4. [领域模型](#4-领域模型)
5. [设备集成架构](#5-设备集成架构)
6. [工作流引擎](#6-工作流引擎)
7. [数据持久化](#7-数据持久化)
8. [用户界面设计](#8-用户界面设计)
9. [物理验收边界](#9-物理验收边界)
10. [技术特性](#10-技术特性)
11. [扩展性设计](#11-扩展性设计)
12. [部署架构](#12-部署架构)

---

## 1. 系统概述

### 1.1 项目定位

AGV MES MVP 是面向**实验室自动化场景**的轻量级 AGV 任务中控系统，用于编排并追踪 AGV 在固定站点之间的物料搬运任务。系统采用清晰的边界划分：

- **MES** - 负责任务状态与审计
- **Adapter** - 负责设备协议和幂等派单
- **Simulator** - 提供可重复的开发与验收环境
- **WPF** - 提供操作员看板和工作流编辑器

### 1.2 核心能力

| 能力领域 | 已支持功能 |
|---------|-----------|
| **任务编排** | 创建与显式派发、人工取货/放货确认、暂停/恢复/取消、失败重试 |
| **可靠性** | `task_id` 幂等、超时状态对账、`Unknown` 恢复、MES/Adapter 重启恢复 |
| **调度** | 多 AGV 车队状态、最短路径、活动路段冲突过滤、资源不足时闭环失败 |
| **操作界面** | WPF MVVM 看板、任务详情与审计时间线、AGV 通讯、批量 CSV/XLSX 导入、KPI、只读 `.smap` 地图 |
| **设备边界** | Simulator 默认驱动；可配置厂商 TCP Adapter；真实模式隐藏 Simulator 控制 |
| **可追溯性** | MES SQLite 任务库、Adapter 操作库、任务和工作流生命周期审计 |
| **机械臂与视觉** | Mock 驱动完整支持；V2 编排服务带设备预检、并发控制、自动回滚 |

### 1.3 设计原则

1. **边界清晰** - MES 拥有任务状态，Adapter 拥有设备协议，Simulator 提供可控环境
2. **Simulator-first** - 离线验证优先，真实设备接入只替换 Adapter 驱动
3. **配置驱动** - Profile 管理站点、地图、AGV、超时、特性开关
4. **幂等与审计** - 操作幂等、状态对账、生命周期审计、fail-closed 门禁
5. **平台化准备** - Contracts 边界、Application 用例、Driver 抽象、模块注册

---

## 2. 整体架构

### 2.1 架构图

```
┌─────────────────────────────────────────────────────────────────┐
│                         操作员/管理员                               │
└────────────────────────────┬────────────────────────────────────┘
                             │
                             ▼
┌─────────────────────────────────────────────────────────────────┐
│                      WPF 中控看板 (Windows)                        │
│  ┌──────────┬──────────┬──────────┬──────────┬──────────────┐   │
│  │任务监控   │AGV 通讯   │批量导入   │KPI 看板  │工作流设计    │   │
│  └──────────┴──────────┴──────────┴──────────┴──────────────┘   │
│           HTTP JSON ↕ (5045/5183/5041)                          │
└─────────────────────────────────────────────────────────────────┘
                             │
        ┌────────────────────┼────────────────────┐
        ▼                    ▼                    ▼
┌───────────────┐   ┌───────────────┐   ┌───────────────┐
│  MES 服务     │   │ Adapter 服务   │   │ Simulator     │
│  (5045)       │   │  (5041)        │   │  (5183)       │
│               │   │                │   │               │
│ ┌───────────┐ │   │ ┌────────────┐│   │ ┌───────────┐ │
│ │任务状态机  │ │   │ │设备协议    ││   │ │内存车队   │ │
│ │审计事件   │ │   │ │幂等派单    ││   │ │故障注入   │ │
│ │恢复决策   │ │   │ │超时对账    ││   │ │可控到站   │ │
│ └───────────┘ │   │ └────────────┘│   │ └───────────┘ │
│               │   │        │       │   │               │
│ ┌───────────┐ │   │        ▼       │   └───────────────┘
│ │ SQLite    │ │   │ ┌────────────┐│
│ │ mes.db    │ │   │ │SimulatorDriver │
│ └───────────┘ │   │ │VendorTcpDriver│
│               │   │ └────────────┘│
└───────────────┘   │        │       │
                    │        ▼       │
                    │ ┌────────────┐│
                    │ │ SQLite     ││
                    │ │adapter.db  ││
                    │ └────────────┘│
                    └───────┬───────┘
                            │
                ┌───────────┼───────────┐
                ▼                       ▼
        ┌──────────────┐        ┌──────────────┐
        │ AGV Simulator│        │ 真实 AGV      │
        │ (开发/测试)   │        │ (Vendor TCP) │
        └──────────────┘        └──────────────┘
```

### 2.2 服务职责

| 服务 | 端口 | 职责 | 数据存储 |
|-----|------|------|---------|
| **MES** | 5045 | 任务状态机、业务动作、持久化、审计事件、恢复决策 | `mes.db` (SQLite) |
| **Adapter** | 5041 | 站点映射、控制权、安全门禁、幂等派单、设备状态查询、超时对账 | `adapter.db` (SQLite) |
| **Simulator** | 5183 | 内存车队、可控到站、故障注入 | 内存（可选持久化） |
| **WPF** | - | 操作员看板、工作流编辑器、批量导入、KPI 展示 | 本地 JSON 配置 |

### 2.3 通信协议

- **WPF ↔ MES/Adapter/Simulator**: HTTP REST JSON
- **Adapter ↔ Simulator**: HTTP REST JSON
- **Adapter ↔ 真实 AGV**: TCP 16-byte frame (Vendor Protocol)
- **地图数据**: `.smap` XML 解析 (RoboshopPro 1.0.6)

---

## 3. 核心模块设计

### 3.1 项目结构

```
MesControlAgv.sln
├── src/
│   ├── MesControlAgv.Domain          # 领域模型、状态机、路径规划、Profile
│   ├── MesControlAgv.Contracts       # 跨边界契约（Tasks/Devices/Workflows）
│   ├── MesControlAgv.Application     # 应用用例边界、Driver 抽象
│   ├── MesControlAgv.Mes             # MES 服务（ASP.NET Core Minimal API）
│   ├── MesControlAgv.Adapter         # Adapter 服务（设备协议转换）
│   ├── MesControlAgv.Simulator       # Simulator 服务（开发测试）
│   ├── MesControlAgv.Wpf             # WPF 客户端（MVVM）
│   └── MesControlAgv.Launcher        # 一键启动器
└── tests/
    ├── MesControlAgv.Domain.Tests
    ├── MesControlAgv.Mes.Tests
    ├── MesControlAgv.Adapter.Tests
    ├── MesControlAgv.Wpf.Tests
    ├── MesControlAgv.E2E.Tests
    ├── MesControlAgv.Simulator.Tests
    └── MesControlAgv.WorkflowContract.Tests
```

### 3.2 Domain 层核心类

#### 任务状态机 (`TaskStateMachine`)

```csharp
public enum TaskStatus {
    Created, Dispatching, MovingToPickup, WaitingPickupConfirmation,
    MovingToDropoff, WaitingDropoffConfirmation, Completed,
    Failed, Cancelled, Unknown, Paused
}

public enum TaskEvent {
    DispatchRequested, PickupMoveStarted, DropoffMoveStarted,
    PickupArrived, PickupConfirmed, DropoffArrived, DropoffConfirmed,
    PauseRequested, ResumeRequested, RetryRequested, CancelConfirmed,
    DeviceFailed, Timeout, ReconciledMoving, ...
}
```

**状态转换规则**:
- `Created → DispatchRequested → Dispatching`
- `Dispatching → PickupMoveStarted → MovingToPickup`
- `MovingToPickup → PickupArrived → WaitingPickupConfirmation`
- `WaitingPickupConfirmation → PickupConfirmed → MovingToDropoff`
- `MovingToDropoff → DropoffArrived → WaitingDropoffConfirmation`
- `WaitingDropoffConfirmation → DropoffConfirmed → Completed`
- `Timeout → Unknown → Reconciled*` (状态对账恢复)

#### 路径规划器 (`PathPlanner`)

```csharp
public sealed class PathPlanner {
    // Dijkstra 最短路径
    public PlannedPath Plan(string from, string to, HashSet<string>? blocked);
    
    // 连续路径规划 (current → source → target)
    public PlannedPath PlanVia(string current, string source, string target);
    
    // 路径验证
    public PlannedPath ValidatePath(IReadOnlyList<string> stations);
}
```

#### 多 AGV 调度器 (`MultiAgvScheduler`)

```csharp
public sealed class MultiAgvScheduler {
    // 调度决策：选择 AGV、规划路径、检查冲突
    public SchedulingDecision Schedule(
        Guid taskId, 
        string source, 
        string target,
        IReadOnlyCollection<AgvCandidate> candidates);
    
    // 释放路径预留
    public bool Release(Guid taskId);
    
    // 批量释放空闲 AGV 路径
    public void ReleaseForIdleAgvs(IReadOnlySet<string> agvIds);
}
```

**冲突检测**:
- 活动路线预留反向路段
- 避免"相向而行"冲突

### 3.3 Contracts 层边界

#### 任务契约 (`TaskContracts.cs`)

```csharp
public sealed record CreateTaskRequest(
    string SourceStationCode, 
    string TargetStationCode,
    int? Priority, 
    string? Description, 
    string? ExternalId);

public sealed record TaskResponse(
    Guid Id, TaskStatus Status, string? Reason,
    string SourceStation, string TargetStation,
    DateTimeOffset? CreatedAt, DateTimeOffset? EndedAt,
    string? ActiveAgvId, string? ActiveDeviceTaskId,
    IReadOnlyList<string>? ActivePath);

public sealed record TaskDetailResponse(
    Guid Id, TaskStatus Status,
    IReadOnlyList<TaskEventRecord> Events);
```

#### 设备契约 (`DeviceContracts.cs`)

```csharp
public sealed record AgvSnapshotResponse(
    bool Online, string ControlOwner,
    string? CurrentStationId, Guid? CurrentTaskId,
    string AgvId, AgvCapabilitiesResponse? Capabilities,
    AgvSafetyReadinessResponse? SafetyReadiness);

public sealed record AgvTaskResponse(
    Guid TaskId, string DeviceTaskId,
    string TargetStationId, string State,
    string? LastError, string AgvId,
    IReadOnlyList<string>? Path);
```

#### 工作流契约 (`WorkflowContracts.cs`)

```csharp
public enum WorkflowNodeType { 
    Start, Move, Wait, Pickup, Dropoff, End, Custom 
}

public sealed record WorkflowDefinition(
    Guid Id, string Name, string Description,
    IReadOnlyList<WorkflowNode> Nodes,
    int? PublishedVersion);

public sealed record WorkflowExecutionRequest(
    Guid WorkflowId, int Version,
    IReadOnlyDictionary<string, string?> Parameters,
    Guid RequestId, bool DryRun);

public sealed record WorkflowExecutionResult(
    WorkflowExecutionStatus Status, Guid ExecutionId,
    string? RejectionCode, WorkflowNextStepRequest? NextStep);
```

### 3.4 Application 层用例边界

```csharp
// 任务应用服务
public interface ITaskApplicationService {
    Task<TaskResponse> CreateAsync(CreateTaskRequest, CancellationToken);
    Task<TaskResponse> DispatchAsync(Guid taskId, CancellationToken);
    Task<TaskResponse> ConfirmPickupAsync(Guid, string operator, CT);
    Task<TaskResponse> ConfirmDropoffAsync(Guid, string operator, CT);
    Task<TaskResponse> CancelAsync(Guid, string operator, CT);
    Task<IReadOnlyList<AgvFleetStatusResponse>> GetFleetStatusAsync(CT);
    Task ReconcileActiveAsync(CancellationToken);
}

// KPI 看板服务
public interface IKpiDashboardApplicationService {
    Task<KpiDashboardResponse> GetAsync(DateOnly date, CT);
}

// 工作流应用服务
public interface IWorkflowApplicationService {
    Task<WorkflowVersion> CreateDraftAsync(WorkflowDefinition, string actor, CT);
    Task<WorkflowValidationResult> ValidateAsync(WorkflowDefinition, CT);
    Task<WorkflowVersion> PublishAsync(Guid workflowId, int version, string actor, CT);
    Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest, CT);
}
```

---

## 4. 领域模型

### 4.1 任务生命周期

```
┌────────┐  DispatchRequested  ┌────────────┐
│ Created│ ──────────────────> │ Dispatching│
└────────┘                     └─────┬──────┘
                                     │ PickupMoveStarted
                                     ▼
                             ┌────────────────┐
                             │ MovingToPickup │
                             └────────┬───────┘
                                      │ PickupArrived
                                      ▼
                      ┌───────────────────────────┐
                      │WaitingPickupConfirmation  │
                      └────────────┬──────────────┘
                                   │ PickupConfirmed (人工)
                                   ▼
                           ┌────────────────┐
                           │MovingToDropoff │
                           └────────┬───────┘
                                    │ DropoffArrived
                                    ▼
                    ┌───────────────────────────┐
                    │WaitingDropoffConfirmation │
                    └────────────┬──────────────┘
                                 │ DropoffConfirmed (人工)
                                 ▼
                           ┌───────────┐
                           │ Completed │
                           └───────────┘

异常路径:
  DeviceFailed  →  Failed  →  RetryRequested  →  Dispatching
  Timeout       →  Unknown →  Reconciled*     →  (恢复到正确状态)
  CancelConfirmed → Cancelled
  PauseRequested → Paused → ResumeRequested → (恢复到移动状态)
```

### 4.2 站点与地图模型

```csharp
public sealed record Station {
    public int Code { get; init; }
    public string Name { get; init; }
    public string AgvStationId { get; init; }
    public bool Enabled { get; init; }
    public StationType? Type { get; init; }  // Sample, Pickup, Preparation, etc.
}

public enum StationType {
    Charge, Pickup, Sample, Preparation, Injection, Dropoff, Custom
}

public sealed class AgvMap {
    public IReadOnlyList<string> StationIds { get; }
    public IReadOnlyList<MapEdge> Edges { get; }
    
    // 从 Profile 构建
    public static AgvMap FromProfile(MapProfile profile);
}

public sealed record MapEdge(
    string From, string To, int Cost, bool Bidirectional);
```

### 4.3 Profile 配置模型

```csharp
public sealed record ProfileConfiguration {
    public ProductProfile Product { get; init; }
    public IReadOnlyList<AgvProfile> Agvs { get; init; }
    public IReadOnlyList<StationProfile> Stations { get; init; }
    public MapProfile Map { get; init; }
    public PhysicalAcceptanceProfile? PhysicalAcceptance { get; init; }
    public FeatureFlags Features { get; init; }
    public TimeoutOptions Timeouts { get; init; }
}

public sealed record FeatureFlags {
    public bool EnableAutomaticDispatch { get; init; }
    public bool EnableTaskCancellation { get; init; }
    public bool EnableFieldNavigationAcceptance { get; init; }
}

public sealed record PhysicalAcceptanceProfile {
    public string ExpectedControlOwner { get; init; }
    public MapSnapshotProfile MapSnapshot { get; init; }
    public SafetyProfile Safety { get; init; }
    public string VehicleOperatingModePolicy { get; init; }  // "vendor-field-required" | "not-exposed-by-approved-model"
}
```

---

## 5. 设备集成架构

### 5.1 Driver 抽象

```csharp
// AGV 驱动接口
public interface IAgvDriver {
    string DriverId { get; }
    AgvCapabilitiesResponse Capabilities { get; }
    
    Task ConnectAsync(CancellationToken);
    Task<AgvSnapshotResponse> GetSnapshotAsync(string agvId, CT);
    Task<AgvTaskResponse> DispatchAsync(AgvDispatchCommand, CT);
    Task<AgvTaskResponse?> PauseAsync(AgvControlCommand, CT);
    Task<AgvTaskResponse?> ResumeAsync(AgvControlCommand, CT);
    Task<AgvTaskResponse?> CancelAsync(AgvControlCommand, CT);
}

// 机械臂驱动接口
public interface IRobotArmDriver {
    string DriverId { get; }
    RobotArmCapabilities Capabilities { get; }
    
    Task<RobotArmStatusResponse> GetStatusAsync(CT);
    Task<RobotArmOperationResponse> PickAsync(RobotArmPickCommand, CT);
    Task<RobotArmOperationResponse> PlaceAsync(RobotArmPlaceCommand, CT);
    Task<RobotArmOperationResponse> MoveToAsync(RobotArmMoveCommand, CT);
    Task<RobotArmOperationResponse> HomeAsync(CT);
}

// 视觉系统驱动接口
public interface IVisionDriver {
    string DriverId { get; }
    VisionCapabilities Capabilities { get; }
    
    Task<VisionCaptureResponse> CaptureAsync(VisionCaptureCommand, CT);
    Task<VisionRecognitionResponse> RecognizeAsync(VisionRecognitionCommand, CT);
    Task<VisionLocalizationResponse> LocalizeAsync(VisionLocalizationCommand, CT);
    Task<VisionCalibrationResponse> GetCalibrationAsync(CT);
}
```

### 5.2 Driver 实现

| 驱动类型 | 实现 | 状态 | 位置 |
|---------|------|------|------|
| **AGV Simulator** | `SimulatorDriver` | ✅ 生产就绪 | `Adapter/Drivers/SimulatorDriver.cs` |
| **AGV Vendor TCP** | `VendorTcpDriver` | ✅ 生产就绪 | `Adapter/Drivers/VendorTcpDriver.cs` |
| **Robot Arm Mock** | `MockRobotArmDriver` | ✅ 开发完成 | `Adapter/Drivers/MockRobotArmDriver.cs` |
| **Vision Mock** | `MockVisionDriver` | ✅ 开发完成 | `Adapter/Drivers/MockVisionDriver.cs` |
| **Robot Arm Aobot** | `AobotRobotArmDriver` | 🔲 待实现 | 见 `DEVELOPMENT-ROADMAP.md` |
| **Vision VisionGroup2** | `VisionGroup2Driver` | 🔲 待实现 | 见 `DEVELOPMENT-ROADMAP.md` |

### 5.3 Adapter 服务核心逻辑

```csharp
public sealed class AdapterService {
    // 幂等派发
    public async Task<AgvTaskResponse> DispatchAsync(
        Guid taskId, 
        string? sourceStationId,
        string targetStationId,
        string? requestedAgvId,
        IReadOnlyList<string>? requestedPath,
        CancellationToken ct) 
    {
        // 1. 获取派发门锁（防止并发）
        // 2. 检查是否已有任务（幂等）
        // 3. 控制权检查
        // 4. AGV 选择与路径规划
        // 5. 写入 Adapter 数据库
        // 6. 调用设备驱动派发
        // 7. 处理超时对账
        // 8. 更新状态并返回
    }
    
    // 超时对账
    private async Task<AgvTaskResponse?> GetTaskFromDeviceAsync(
        string agvId, Guid taskId, IReadOnlyList<string>? path, CT) 
    {
        // 查询设备真实状态，决定恢复/重试/异常
    }
    
    // 物理验收门禁
    private async Task<AgvTaskResponse> DispatchFieldNavigationAcceptanceAsync(
        Guid acceptanceId, FieldNavigationDispatchCommand command, CT) 
    {
        // 两阶段预检 + 控制权获取 + 派发 + 失败自动释放控制权
    }
}
```

### 5.4 TCP 协议实现 (Vendor AGV)

```csharp
public sealed class TcpAgvClient : IAgvDeviceClient {
    // 16-byte frame 协议
    // 端口: 19204 (状态), 19206 (命令), 19207 (控制), 19301 (推送)
    
    // API 映射
    // 1060: 控制权查询
    // 4005: 控制权获取
    // 4006: 控制权释放
    // 1100/1101: 状态查询
    // 1110: 任务状态查询
    // 3066: 导航任务派发
    // 3067: 任务取消
    // 3001/3002: 暂停/恢复
    // 1021: 定位状态
    // 1000: 设备信息
    // 1300/1301/1302/4011: 地图读取
}
```

---

## 6. 工作流引擎

### 6.1 工作流模型

```csharp
public sealed record WorkflowNode {
    public Guid Id { get; init; }
    public WorkflowNodeType Type { get; init; }  // Start, Move, Wait, Pickup, Dropoff, End
    public string Name { get; init; }
    public string? TargetStation { get; init; }
    public IReadOnlyList<WorkflowParameter> Parameters { get; init; }
    public IReadOnlyList<Guid> NextNodeIds { get; init; }
}

public sealed record WorkflowDefinition {
    public Guid Id { get; init; }
    public string Name { get; init; }
    public IReadOnlyList<WorkflowNode> Nodes { get; init; }
    public int? PublishedVersion { get; init; }
}
```

### 6.2 版本管理

- **Draft** - 可编辑草稿
- **Validated** - 已校验，可发布
- **Published** - 已发布，不可变
- **Archived** - 已归档（被新版本取代）

**版本不可变性**: 发布后的版本定义不可修改，只能创建新版本

### 6.3 Runtime 执行

```csharp
public sealed class WorkflowRuntimeExecutor {
    public async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionRequest request, 
        CancellationToken ct) 
    {
        // 1. 读取已发布版本
        // 2. 校验（通过 WorkflowValidator）
        // 3. 运行准入策略（ActiveProfileWorkflowAdmissionPolicy）
        // 4. 解析参数
        // 5. 生成 NextStepRequest（第一步）
        // 6. 记录审计
        // 7. 幂等处理（RequestId）
    }
}
```

### 6.4 准入策略

```csharp
public sealed class ActiveProfileWorkflowAdmissionPolicy : IWorkflowRuntimeAdmissionPolicy {
    // 检查工作流中所有 Move/Pickup/Dropoff 节点的 TargetStation
    // 是否在当前 Profile 的启用站点列表中
    // 不匹配时返回 WORKFLOW_PROFILE_MISMATCH
}
```

### 6.5 审计与幂等

- **WorkflowExecutions** 表: 按 `RequestId` 幂等，记录执行结果
- **WorkflowAudits** 表: 记录生命周期事件（Draft/Validate/Publish/Execute）
- **WorkflowVersions** 表: 存储不可变版本快照

---

## 7. 数据持久化

### 7.1 MES 数据库 (mes.db)

| 表名 | 用途 | 关键字段 |
|-----|------|---------|
| **TransportTasks** | 运输任务 | `Id`, `Status`, `SourceStationCode`, `TargetStationCode`, `Priority`, `Description`, `ExternalId`, `CreatedAt`, `EndedAt`, `ActiveAgvId`, `ActiveDeviceTaskId`, `ActivePathJson` |
| **TaskEvents** | 任务审计事件 | `Id`, `TaskId`, `EventType`, `OccurredAt`, `OperatorName`, `Details` |
| **AgvSnapshots** | AGV 快照 | `Id`, `AgvId`, `Online`, `ControlOwner`, `CurrentStationId`, `CurrentTaskId`, `SnapshotAt` |
| **WorkflowVersions** | 工作流版本 | `WorkflowId`, `Version`, `DefinitionJson`, `Status`, `PublishStatus`, `ValidationJson`, `CreatedBy`, `PublishedBy` |
| **WorkflowExecutions** | 工作流执行记录 | `RequestId` (PK), `WorkflowId`, `Version`, `ExecutionId`, `Outcome`, `RejectionCode`, `RequestJson`, `ResultJson` |
| **WorkflowAudits** | 工作流审计 | `Id`, `EventType`, `Outcome`, `WorkflowId`, `Version`, `RequestId`, `OccurredAt` |
| **FieldNavigationAcceptances** | 现场导航验收 | `Id`, `Status`, `AgvId`, `SourceStationId`, `TargetStationId`, `MapName`, `MapMd5`, `PlannedPathJson`, `PermitId`, `AuthorizedAtUtc` |

**SQLite 兼容性**: 启动时自动 `CREATE TABLE IF NOT EXISTS` + `ALTER TABLE ADD COLUMN` 补齐新列

### 7.2 Adapter 数据库 (adapter.db)

| 表名 | 用途 | 关键字段 |
|-----|------|---------|
| **Tasks** | Adapter 任务 | `TaskId` (PK), `AgvId`, `DeviceTaskId`, `TargetStationId`, `State`, `LastError`, `PathJson` |

**状态映射**: `dispatching`, `accepted`, `moving`, `paused`, `arrived`, `completed`, `cancelled`, `failed`, `unknown`

### 7.3 事务与审计

- **任务状态机事务**: MES 状态转换与事件记录在同一事务中
- **审计时间线**: 每次状态转换记录 `TaskEvent`，包含 `EventType`, `OccurredAt`, `OperatorName`, `Details`
- **工作流审计**: Draft 创建/更新、Validate、Publish、Execute 全程审计

---

## 8. 用户界面设计

### 8.1 WPF 架构 (MVVM)

```
┌─────────────────────────────────────────────────┐
│              MainWindow (WPF)                   │
│  ┌───────────────────────────────────────────┐  │
│  │         MainViewModel                     │  │
│  │  ┌─────────────────────────────────────┐ │  │
│  │  │  TaskMonitorViewModel              │ │  │
│  │  │  AgvCommunicationViewModel         │ │  │
│  │  │  BatchImportViewModel              │ │  │
│  │  │  KpiDashboardViewModel             │ │  │
│  │  │  WorkflowEditorViewModel           │ │  │
│  │  │  MapViewModel                      │ │  │
│  │  │  ReadinessViewModel                │ │  │
│  │  └─────────────────────────────────────┘ │  │
│  └───────────────────────────────────────────┘  │
│           │ HTTP JSON                           │
│           ▼                                     │
│  ┌───────────────────────────────────────────┐  │
│  │      MesClient / SimulatorClient         │  │
│  └───────────────────────────────────────────┘  │
└─────────────────────────────────────────────────┘
```

### 8.2 功能模块

| 模块 | 视图 | ViewModel | 功能 |
|-----|------|-----------|------|
| **任务监控** | `TaskMonitor.xaml` | `TaskMonitorViewModel` | 任务列表、创建、派发、取消、重试、人工确认、审计时间线 |
| **AGV 通讯** | `AgvCommunication.xaml` | `AgvCommunicationViewModel` | 车队状态、AGV 控制（暂停/恢复/取消） |
| **批量导入** | `BatchImport.xaml` | `BatchImportViewModel` | CSV/XLSX 批量导入、预览、优先级排序 |
| **KPI 看板** | `KpiDashboard.xaml` | `KpiDashboardViewModel` | 今日任务统计、完成率、趋势图、设备状态 |
| **工作流设计** | `WorkflowEditor.xaml` | `WorkflowEditorViewModel` | 节点拖拽、连线、参数配置、Draft/Validate/Publish |
| **地图视图** | `MapView.xaml` | `MapViewModel` | `.smap` 地图渲染、AGV 实时位置、路径动画、站点详情 |
| **就绪状态** | `Readiness.xaml` | `ReadinessViewModel` | Profile 元数据、地图指纹、物理预检、阻断原因 |

### 8.3 模块注册机制

```csharp
public sealed class ControlCenterModuleRegistry {
    public void Register(IControlCenterModule module);
    public IReadOnlyList<ControlCenterModuleDescriptor> Modules { get; }
    
    // 标准模块
    public static ControlCenterModuleRegistry CreateStandard() {
        // TaskMonitor, AgvCommunication, BatchImport, 
        // KpiDashboard, WorkflowDesigner
    }
}
```

### 8.4 地图渲染

- **数据源**: `.smap` XML (RoboshopPro 1.0.6)
- **渲染内容**: 
  - 特征墙线 (`advancedLineList`)
  - 路线 (`advancedCurveList` Bezier 曲线)
  - 站点标记 (`LocationMarks`)
  - 障碍扫描点 (`normalPosList` → Indexed8 位图)
  - AGV 实时位置与路径动画
- **交互**: 缩放、平移、站点点击详情、图层开关
- **导出**: 当前视口 PNG / 完整地图 PNG

### 8.5 批量导入

- **格式**: CSV / XLSX
- **列支持**: 任务 ID、源站、目标站、描述、优先级、计划时间
- **中英文列名**: `源站点/Source Station`, `目标站点/Target Station`, etc.
- **排序规则**: 优先级降序 → 计划时间升序 → 行号升序
- **验证**: 站点启用检查、格式验证、问题汇总

---

## 9. 物理验收边界

### 9.1 运行模式

```csharp
public sealed class AdapterRunMode {
    public string Value { get; }  // "standard" | "read-only-preflight"
    public bool IsReadOnlyPreflight { get; }
}
```

- **standard**: 正常运行模式，允许派发/控制/取消
- **read-only-preflight**: 只读预检模式，只允许 `GET`/`HEAD`，拒绝所有状态变更

### 9.2 物理预检 (Physical Preflight)

```csharp
public sealed record PhysicalAgvPreflightResponse {
    public AgvSnapshotResponse Snapshot { get; init; }
    public AgvSafetyReadinessResponse? Readiness { get; init; }
    public bool DispatchPermitted { get; init; }
    public IReadOnlyList<string> BlockingReasons { get; init; }
    public ControllerMapEvidenceResponse? MapEvidence { get; init; }
    public string? VehicleOperatingModePolicy { get; init; }
}
```

**预检项**:
1. **控制权**: Adapter 是否持有控制权
2. **地图一致性**: 控制器地图名称/版本/MD5 与 Profile 匹配
3. **站点一致性**: 控制器站点列表与 Profile 匹配
4. **有向边一致性**: 控制器直接有向边与 Profile 匹配
5. **定位状态**: `reloc_status=1`, 置信度 ≥ 配置阈值
6. **自动模式**: `vehicleOperatingMode` 符合策略要求
7. **安全状态**: 无急停、无阻塞、无致命错误
8. **活动任务**: 无其他活动任务

### 9.3 控制器地图证据

```csharp
public sealed record ControllerMapEvidenceResponse {
    public bool IsControllerAuthoritative { get; init; }
    public string? MapName { get; init; }
    public string? Version { get; init; }
    public string? Md5 { get; init; }
    public IReadOnlyList<string>? StationIds { get; init; }
    public IReadOnlyList<ControllerDirectedEdgeResponse>? DirectedEdges { get; init; }
}
```

**读取 API**:
- `1300`: 地图列表
- `1301`: 当前地图元数据
- `1302`: 下载 `.smap` 文件
- `4011`: 解析站点与路线

### 9.4 控制权生命周期

```
┌────────────────────────────────────────────────────────────┐
│  1. 预检（无控制权）                                          │
│     └─ 检查地图/定位/安全                                     │
│                                                              │
│  2. 获取控制权 (4005)                                         │
│     └─ 记录 AcquiredByThisSession                            │
│                                                              │
│  3. 二次预检（有控制权）                                      │
│     └─ 再次检查安全门禁                                       │
│                                                              │
│  4. 派发 (3066) 或失败                                        │
│     ├─ 成功 → 保留控制权                                      │
│     └─ 失败 → 自动释放控制权 (4006) 如果本次获取               │
│                                                              │
│  5. 人工释放 (POST /agv/control/release)                     │
│     └─ 调用 4006 + 1060 确认                                 │
└────────────────────────────────────────────────────────────┘
```

### 9.5 变更审计 (Mutation Audit)

**允许记录的字段** (allowlisted):
- `3066` 请求: 任务 ID、源站、目标站、`max_speed`
- `3066` 响应: `ret_code`, `err_msg`, `create_on`
- `4005/4006`: 控制权请求/响应
- `3067`: 取消请求/响应

**排除的字段**:
- 控制器 IP/主机名
- 完整负载 (payload)
- 任意未过滤的响应内容

### 9.6 现场验收记录

**文档位置**: `docs/physical-acceptance/FIELD-ACCEPTANCE-RECORD.md`

**记录内容**:
- 验收 ID、日期、操作员
- 预检结果（地图/定位/安全）
- 控制权获取结果
- 导航尝试结果
- 根因分析
- 修复措施
- 结论与下一步

---

## 10. 技术特性

### 10.1 幂等性保证

| 层级 | 实现 | 机制 |
|-----|------|------|
| **MES 任务创建** | `TaskId = Guid` | 外部 `ExternalId` 可选映射 |
| **Adapter 派发** | `DispatchGate` + 数据库 | 同一 `taskId` 检查已有记录，返回现有状态 |
| **设备协议** | `1110` 查询 | 派发前查询任务状态，404 才允许首发 |
| **工作流执行** | `RequestId` | 同一 `RequestId` 返回原执行结果 |

### 10.2 超时对账 (Timeout Reconciliation)

```
┌─────────────────────────────────────────────────────────┐
│  1. Adapter 派发 3066                                    │
│     └─ 写入数据库: state=dispatching                      │
│                                                           │
│  2. 网络超时 or 无响应                                    │
│     └─ 捕获 TimeoutException                             │
│                                                           │
│  3. 对账: 调用 1110 查询真实状态                          │
│     ├─ 找到任务 → 更新为设备状态 (moving/arrived/etc.)    │
│     └─ 未找到 → state=unknown, reason=timeout            │
│                                                           │
│  4. MES 恢复服务定期轮询 unknown 任务                     │
│     └─ 再次查询设备状态，恢复或标记失败                    │
└─────────────────────────────────────────────────────────┘
```

### 10.3 恢复服务 (Recovery Service)

```csharp
public sealed class RecoveryService : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            await ReconcileIncompleteTasksAsync(ct);  // 启动时
            await ReconcileActiveTasksAsync(ct);      // 定期轮询
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
}
```

**恢复逻辑**:
- **启动时**: 恢复所有 `dispatching/moving/paused` 任务
- **定期**: 轮询活动任务，检查设备状态
- **状态同步**: `ReconciledMoving`, `ReconciledCompleted`, `ReconciledFailed`

### 10.4 并发控制

| 机制 | 实现 | 用途 |
|-----|------|------|
| **派发门锁** | `DispatchGate` (SemaphoreSlim per taskId) | 防止同一任务并发派发 |
| **物理会话门** | `PhysicalAgvSessionGate` | 序列化物理派发/取消/释放控制权 |
| **调度器锁** | `MultiAgvScheduler` (object lock) | 路径预留原子操作 |
| **Compound Task 锁** | `SemaphoreSlim` (1,1) | 一次只执行一个复合任务 |

### 10.5 路径规划

**算法**: Dijkstra 最短路径

**特性**:
- 支持单向边与双向边
- 支持阻塞站点
- 连续路径规划 (current → source → target)
- 路径验证与成本计算

**冲突检测**:
- 活动路线预留反向路段
- 避免"相向而行"冲突
- 资源不足时闭环失败

---

## 11. 扩展性设计

### 11.1 驱动工厂模式

```csharp
public interface IAgvDriverFactory {
    string DriverId { get; }
    IAgvDriver Create(AgvDriverOptions options);
}

public sealed class DriverRegistry {
    public void Register(IAgvDriverFactory factory);
    public IAgvDriver Create(string driverId, AgvDriverOptions? options);
}
```

**使用示例**:
```csharp
// 注册驱动
services.AddSingleton<IAgvDriverFactory, SimulatorDriverFactory>();
services.AddSingleton<IAgvDriverFactory, VendorTcpDriverFactory>();
services.AddSingleton<DriverRegistry>();

// 选择驱动
var driver = registry.Create("simulator", new AgvDriverOptions("AGV-01"));
```

### 11.2 模块化架构

```csharp
public interface IControlCenterModule {
    ControlCenterModuleDescriptor Descriptor { get; }
    ControlCenterModuleRegistrations Registrations { get; }
}

public sealed record ControlCenterModuleDescriptor {
    public string Id { get; init; }
    public string DisplayName { get; init; }
    public int Order { get; init; }
    public bool Enabled { get; init; }
}
```

**标准模块**:
- `task-monitor`: 任务监控
- `agv-communication`: AGV 通讯
- `batch-import`: 批量导入
- `kpi-dashboard`: KPI 看板
- `workflow-designer`: 工作流设计

### 11.3 Profile 驱动配置

```json
{
  "Profile": {
    "Product": {
      "ProductId": "agv-mes-mvp",
      "Version": "1.0.0"
    },
    "Agvs": [
      {
        "AgvId": "AGV-01",
        "Enabled": true,
        "HomeStationId": "CHARGE_01"
      }
    ],
    "Stations": [
      {
        "Code": 0,
        "Name": "充电桩",
        "AgvStationId": "CHARGE_01",
        "Enabled": true,
        "Type": "Charge"
      }
    ],
    "Map": {
      "StationIds": ["CHARGE_01", "PICK_01", ...],
      "DirectedEdges": [
        { "From": "CHARGE_01", "To": "PICK_01", "Cost": 10 }
      ]
    },
    "Features": {
      "EnableAutomaticDispatch": false,
      "EnableTaskCancellation": true,
      "EnableFieldNavigationAcceptance": false
    },
    "Timeouts": {
      "TaskPollingInterval": "00:00:02",
      "DeviceStatusCheckTimeout": "00:00:10",
      "AgvNavigationTimeout": "00:05:00"
    }
  }
}
```

### 11.4 复合任务编排 (V2)

```csharp
public sealed class CompoundTaskServiceV2 {
    // 特性
    // - 设备预检（AGV/机械臂/视觉）
    // - 并发控制（SemaphoreSlim 1,1）
    // - 自动回滚（失败时返回源站放置物体）
    // - 可配置超时
    // - 进度报告
    
    public async Task<CompoundTaskResult> ExecuteAsync(
        CompoundTransportTask task,
        CancellationToken ct);
}
```

**执行流程**:
1. 设备预检
2. AGV 导航到源站
3. 视觉识别（可选）
4. 机械臂抓取
5. AGV 导航到目标站
6. 机械臂放置
7. 机械臂归位

**失败回滚**:
- 检测机械臂持有物体
- 导航回源站
- 放置物体
- 机械臂归位

---

## 12. 部署架构

### 12.1 本地开发模式

```
┌────────────────────────────────────────────────┐
│          开发机 (Windows 10/11)                 │
│  ┌──────────────────────────────────────────┐  │
│  │  WPF Launcher.exe                        │  │
│  │    └─ 自动启动 Simulator/Adapter/MES      │  │
│  └──────────────────────────────────────────┘  │
│                                                 │
│  Simulator:  http://localhost:5183             │
│  Adapter:    http://localhost:5041             │
│  MES:        http://localhost:5045             │
│                                                 │
│  数据: %LOCALAPPDATA%\MesControlAgv\            │
│        └─ local-simulator\simulator.db         │
│                                                 │
│  配置: appsettings.Development.json             │
└────────────────────────────────────────────────┘
```

### 12.2 隔离验收模式

```
┌────────────────────────────────────────────────┐
│      临时进程 (独立端口、临时数据库)              │
│                                                 │
│  Simulator:  http://localhost:5361             │
│  Adapter:    http://localhost:5362             │
│  MES:        http://localhost:5363             │
│                                                 │
│  数据: %TEMP%\MesControlAgv-{RunId}\            │
│        ├─ mes.db                                │
│        └─ adapter.db                            │
│                                                 │
│  配置: -RunId, -SimulatorUrl, -MesUrl, etc.    │
│                                                 │
│  脚本: scripts\run-local.ps1                    │
│        scripts\verify-local.ps1                 │
│        scripts\stop-local.ps1                   │
└────────────────────────────────────────────────┘
```

### 12.3 物理验收模式

```
┌────────────────────────────────────────────────┐
│     现场隔离环境 (只读预检 → 标准模式)            │
│                                                 │
│  WPF:        http://localhost:5045 (MES)       │
│  Adapter:    read-only-preflight               │
│              └─ 只允许 GET/HEAD                 │
│                                                 │
│  配置: appsettings.PhysicalAcceptance.json      │
│        ASPNETCORE_ENVIRONMENT=PhysicalAcceptance│
│                                                 │
│  Profile:    PhysicalAcceptanceProfile          │
│              ├─ ExpectedControlOwner            │
│              ├─ MapSnapshot (name/version/md5)  │
│              ├─ Safety (min confidence, etc.)   │
│              └─ VehicleOperatingModePolicy      │
│                                                 │
│  步骤:                                           │
│    1. 启动 read-only-preflight                  │
│    2. 调用 /physical/preflight                  │
│    3. 检查 DispatchPermitted 和 BlockingReasons │
│    4. 停止进程                                   │
│    5. 获得授权后重启为 standard 模式             │
│    6. 执行单次受监督路线                         │
└────────────────────────────────────────────────┘
```

### 12.4 生产部署 (规划)

```
┌────────────────────────────────────────────────┐
│            生产环境 (Windows Server)             │
│                                                 │
│  MES:        IIS / Kestrel (HTTPS)             │
│  Adapter:    IIS / Kestrel (HTTPS)             │
│  WPF:        发布 EXE (操作员工作站)             │
│                                                 │
│  数据库:     SQLite → SQL Server / PostgreSQL   │
│  配置:       appsettings.Production.json        │
│  日志:       Serilog → 文件 / ElasticSearch     │
│  监控:       Health Check / Prometheus          │
│                                                 │
│  网络:       千兆交换机                          │
│              ├─ 中控 PC: 192.168.1.10           │
│              ├─ AGV: 192.168.1.100              │
│              ├─ 机械臂: 192.168.1.101            │
│              └─ 视觉: 192.168.1.102             │
└────────────────────────────────────────────────┘
```

---

## 附录 A: 关键指标

### 测试覆盖

- **总测试数**: 338/338 通过 (截至 2026-08-11)
  - Domain: 35
  - MES: 45
  - Adapter: 82
  - WPF: 150
  - E2E: 12
  - Simulator: 5
  - Workflow Contract: 9

### 代码质量

- **编译警告**: 0
- **编译错误**: 0
- **代码覆盖**: 重点模块已覆盖

### 性能指标

- **任务创建**: < 100ms
- **任务派发**: < 500ms (不含 AGV 移动时间)
- **状态查询**: < 50ms
- **地图渲染**: < 1s (初次加载)
- **批量导入**: 1000 条 < 5s

---

## 附录 B: 参考文档

| 文档 | 位置 | 说明 |
|-----|------|------|
| **README.md** | 根目录 | 项目概述、快速开始、验证场景 |
| **PROGRESS.md** | `docs/` | 开发进度与交接记录 |
| **AGV-TCP-ADAPTER.md** | `docs/` | Vendor TCP 协议实现 |
| **LOCAL-VERIFICATION.md** | `docs/` | 本地隔离进程验证 |
| **DEVELOPMENT-ROADMAP.md** | `docs/` | 机械臂与视觉集成路线图 |
| **物理验收文档** | `docs/physical-acceptance/` | 现场验收边界与记录 |
| **架构决策** | `docs/ARCHITECTURE-DECISION.md` | 技术选型与架构决策 |

---

## 附录 C: 架构图说明

### C.1 系统架构图

**已生成**: `docs/agv-mes-architecture.png`

- 展示 WPF ↔ MES ↔ Adapter ↔ AGV 的完整调用链
- 包含 SQLite 数据库位置
- 标注默认端口与协议

### C.2 任务流程图

**已生成**: `docs/agv-mes-task-flow.png`

- 展示任务从 Created 到 Completed 的完整生命周期
- 包含人工确认节点
- 标注异常路径（Failed/Unknown/Cancelled）

---

## 附录 D: 技术栈总结

| 类别 | 技术 | 用途 |
|-----|------|------|
| **运行时** | .NET 8 | 跨平台框架 |
| **前端** | WPF (Windows Presentation Foundation) | Windows 桌面 UI |
| **后端** | ASP.NET Core Minimal API | HTTP REST 服务 |
| **数据库** | SQLite | 轻量级嵌入式数据库 |
| **ORM** | Entity Framework Core | 数据访问 |
| **测试** | xUnit + FluentAssertions | 单元测试 + 集成测试 |
| **架构模式** | MVVM (WPF), Clean Architecture | 分层与解耦 |
| **设备协议** | TCP Binary Frame (16-byte header) | AGV 厂商协议 |
| **地图格式** | .smap XML (RoboshopPro 1.0.6) | 地图数据 |
| **版本控制** | Git | 源码管理 |
| **构建工具** | dotnet CLI, MSBuild | 编译与打包 |

---

**文档结束**
