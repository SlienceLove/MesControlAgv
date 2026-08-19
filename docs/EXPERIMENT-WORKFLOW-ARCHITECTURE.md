# 实验流程编排目标架构

> 状态：规划基线，不代表现有功能已经实现
> 基线日期：2026-08-19
> 适用范围：AGV、机械臂、离子色谱仪、前处理工作站及后续实验设备的可配置标准流程

## 1. 文档目的

当前项目已经具备工作流草稿、校验、发布、版本固定、运行准入、执行持久化、审计和少量节点执行能力，也已经有两个 WPF 流程编辑界面原型。下一阶段不应继续直接增加设备按钮或节点，而应先确定能够长期承载实验编排的统一模型。

本文解决以下问题：

1. 哪些现有架构可以保留。
2. 当前流程契约和运行时为什么不足以支撑真实实验。
3. 实验方案、排程、流程运行和设备操作应该如何分层。
4. AGV、机械臂和离子色谱仪如何以统一方式接入流程，同时保留各自安全边界。
5. 如何在不推翻现有代码的前提下分阶段迁移。

本文不授权任何实体设备写操作，不改变 CIC-D160+ 当前只读和默认拒绝控制策略。

## 2. 参考资料中的可借鉴内容

`res/流程图.png` 给出的业务分层可概括为：

```text
LIMS / 订单与结果
        |
        v
WCS / MES
  - 任务标准化、任务池、排程、策略、异常、日志、报表、权限
        |
        v
RCS / 设备协调
  - 机器人、地图、路径、工作站、设备任务
        |
        v
协议与状态转换
  - 命令转换、状态采集、设备监控
        |
        v
AGV、机械臂、前处理设备、检测仪器
```

`res/排程系统介绍.pdf` 展示了七类业务入口：

| 参考模块 | 可借鉴内容 | 在本项目中的落点 |
| --- | --- | --- |
| 实验方案管理 | 方案总览、批量管理、配置完整性检查 | 实验方案与流程版本管理 |
| 流程编辑 | 图形化编排、节点独立配置、物料齐套检查 | 统一流程设计器和发布校验 |
| 实验运行看板 | 排程时间轴、样本、日志和流程追溯 | 排程看板与流程运行监控 |
| 设备运行看板 | 状态、故障、负载和长期运行监控 | 设备中心和资源占用视图 |
| 模块调试看板 | 初始化、归零、单设备调试 | 受权限和安全门禁保护的维护模块 |
| 日志管理 | 设备状态、实验数据和操作路径串联 | 统一审计时间线和检索 |
| 权限管理 | 用户、角色、权限约束 | 后续的操作授权和发布/执行权限 |

需要借鉴的是职责和交互，不是直接复制其内部实现。当前项目已经形成 MES、Adapter、InstrumentGateway 和设备安全门禁，这些边界应继续保留。

## 3. 当前实现评估

### 3.1 可以保留的基础

以下部分方向正确，应在其上演进：

- WPF 只通过 MES HTTP 契约处理业务，不直接访问设备协议。
- MES 负责工作流版本、运行状态、审计和恢复决策。
- Adapter、InstrumentGateway 等边界负责设备协议、设备真实状态和安全策略。
- 发布版本不可变，执行请求固定到明确版本。
- `RequestId` 幂等、`TransportOperationId` 对账和 `Unknown` 状态已经形成基础。
- AGV 的超时对账、重启恢复、物理预检和控制权管理可以作为其他设备任务的参考。
- CIC-D160+ 保持读写模型分离，生产网关默认只读，未知写结果禁止自动重试。

### 3.2 当前流程模型的限制

当前 `WorkflowNode` 主要包含：

```text
Id, Type, Name, Description, TargetStation,
X, Y, Order, Parameters, NextNodeIds
```

它适合线性流程验证，但存在以下缺口：

| 缺口 | 当前表现 | 真实实验中的影响 |
| --- | --- | --- |
| 边没有独立模型 | 只有 `NextNodeIds` | 无法稳定表达成功、失败、超时和条件分支 |
| 参数均为字符串 | `Name/Value/DataType` 由界面自由填写 | 缺少范围、枚举、设备能力和版本校验 |
| 节点类型是固定枚举 | 新设备能力需要修改多层枚举和映射 | 扩展成本高，容易出现前后端不一致 |
| 无输入输出数据模型 | 节点只能读取执行参数 | 无法把仪器读数、识别结果传给后续判断节点 |
| 无资源需求 | 节点不知道站点、机械臂、仪器是否被占用 | 多实验运行时会争用设备和工作站 |
| 无显式执行策略 | 超时、重试、未知结果散落在 Worker 中 | 不同设备的风险策略无法统一审计 |
| 无节点配置版本 | 节点参数含义变化后难以兼容旧流程 | 发布版本虽然不可变，解释器仍可能改变语义 |

### 3.3 当前运行时的限制

当前运行时是单令牌、单待执行节点模型：

- 一次执行只保存一个 `CurrentNodeId` 和一个 `PendingStepJson`。
- 一次只允许一个 `TransportOperationId`。
- 多出口节点会返回 `WORKFLOW_BRANCH_UNSUPPORTED`。
- 启动、结束节点之外，按单一路径逐节点推进。
- 当前只有模拟环境的 AGV Move 和定时 Wait 能自动推进。
- `InstrumentOperation` 只作为元数据停留在 `Prepared`，不会执行设备 I/O。
- 没有独立的逐节点执行记录，节点尝试历史主要依赖通用审计事件还原。
- 没有资源租约、信号订阅、人工任务、条件求值和并行令牌。

这些限制是合理的第一阶段安全边界，但在实现条件分支、设备等待、人工确认和并行流程前必须调整。

### 3.4 当前 WPF 的模型分裂

仓库中存在两套流程编辑实现：

1. `ExperimentFlowEditorViewModel` + Nodify
   - 画布、连接、条件标签和自动布局体验更完整。
   - 使用 `ExperimentFlowNode`、`ExperimentFlowConnection` 和本地导入导出 DTO。
   - 没有接入 MES 版本、发布、执行和审计契约。

2. `WorkflowEditorViewModel` + 主窗口 Canvas
   - 已接入 MES 的 Draft、Validate、Publish 和 DryRun。
   - 本地 `WorkflowModels.cs` 与 Contracts 之间存在人工映射。
   - 画布连接和节点配置能力较弱。

长期维护两套业务模型会导致节点类型、参数、连接和版本语义逐渐分叉。当前候选方案是保留 Nodify 画布能力，但必须先通过独立技术 Spike；无论最终使用 Nodify 还是其他 Diagram 控件，画布都只能编辑唯一的共享工作流模型。

## 4. 架构决策

### 4.1 保留总体服务边界

目标调用链保持为：

```text
WPF
  |
  | HTTP contracts
  v
MES / Workflow Orchestrator
  |
  | application ports
  v
Device gateways / Adapter modules
  |
  | vendor protocol
  v
AGV / Robot arm / Instruments / Workstations
```

不新增由 WPF 直接连接串口、机械臂 TCP 或 AGV 控制器的路径。流程节点也不能携带寄存器地址、原始报文或厂商命令文本。

### 4.2 分离实验方案、排程和流程运行

三个概念必须独立：

```text
ExperimentPlan
  描述做什么：流程版本、物料要求、默认参数、适用布局

ExperimentJob / ScheduleEntry
  描述何时做：样品批次、优先级、计划时间、资源选择

WorkflowRun
  描述实际怎么执行：节点状态、设备操作、结果、异常和审计
```

排程器不得直接推进节点，流程执行器也不得自行决定全局任务优先级。

### 4.3 设备能力优先于设备协议

流程编辑器选择的是设备能力：

```text
agv.navigate-to-station
robot.execute-program
robot.pick-sample
instrument.read-status
instrument.wait-until-stable
instrument.start-analysis
workstation.open-cap
```

能力目录由设备模块声明，至少包含：

```text
CapabilityId
DisplayName
DeviceFamily
SchemaVersion
ConfigurationSchema
ResultSchema
ExecutionMode
SafetyClassification
SupportedProfiles
Enabled / ControlEnabled
```

流程定义引用 `CapabilityId` 和逻辑设备选择，不引用 Modbus 地址或厂商报文。Adapter 或 InstrumentGateway 将能力转换为真实协议调用。

### 4.4 显式边模型取代单纯 NextNodeIds

目标边模型至少包含：

```csharp
public sealed record WorkflowEdgeDefinition
{
    public Guid Id { get; init; }
    public Guid SourceNodeId { get; init; }
    public string SourcePort { get; init; } = "success";
    public Guid TargetNodeId { get; init; }
    public WorkflowEdgeKind Kind { get; init; }
    public ConditionExpression? Condition { get; init; }
    public int Priority { get; init; }
}
```

首批边语义：

- `Success`
- `Failure`
- `Timeout`
- `Cancelled`
- `ConditionTrue`
- `ConditionFalse`
- `Compensation`

第一阶段条件表达式只允许结构化比较，不允许在流程中执行任意脚本：

```text
左值绑定 + 比较操作符 + 右值
```

例如：

```text
instrument.pressureMpa <= 12.0
robot.result == "Completed"
agv.stationId == "IC_01"
```

### 4.5 节点定义采用类型目录和配置版本

目标节点基础结构：

```csharp
public sealed record WorkflowNodeDefinition
{
    public Guid Id { get; init; }
    public string NodeTypeId { get; init; } = string.Empty;
    public int SchemaVersion { get; init; }
    public string Name { get; init; } = string.Empty;
    public NodeConfiguration Configuration { get; init; } = new();
    public ExecutionPolicy ExecutionPolicy { get; init; } = new();
    public IReadOnlyList<ResourceRequirement> Resources { get; init; } = [];
    public CanvasPosition Position { get; init; } = new();
}
```

节点配置必须通过对应类型的解析器和校验器转换为强类型配置，例如：

```text
AgvMoveNodeConfiguration
RobotCommandNodeConfiguration
InstrumentReadNodeConfiguration
InstrumentWaitConditionNodeConfiguration
ManualApprovalNodeConfiguration
TimedWaitNodeConfiguration
```

`SchemaVersion` 用于保证旧流程版本仍按原含义解释。升级节点配置时必须提供迁移器或继续保留旧版本执行器。

### 4.6 节点输入输出使用结构化绑定

节点运行结果需要成为后续节点的可引用数据：

```text
run.parameters.sampleId
nodes.readInstrument.outputs.pressureMpa
nodes.vision.outputs.pose
nodes.robot.outputs.programResult
```

绑定模型应保存源路径、目标配置项和期望类型，不应把表达式混在普通字符串中。运行时在节点开始前解析绑定并形成不可变输入快照。

## 5. 目标领域模型

### 5.1 定义侧

| 聚合/实体 | 职责 |
| --- | --- |
| `ExperimentPlan` | 方案名称、业务分类、物料要求、布局要求、流程版本引用和默认参数 |
| `WorkflowDefinition` | 流程逻辑身份，不直接执行 |
| `WorkflowVersion` | 不可变的节点、边、配置和能力需求快照 |
| `WorkflowNodeDefinition` | 节点类型、配置版本、资源需求和执行策略 |
| `WorkflowEdgeDefinition` | 节点之间的结果、条件、异常和补偿路径 |
| `MaterialRequirement` | 物料、数量、批次规则和齐套要求 |
| `LayoutRequirement` | 站点、工作站或设备布局约束 |

### 5.2 排程侧

| 聚合/实体 | 职责 |
| --- | --- |
| `ExperimentJob` | 一次待执行实验，固定方案和流程版本 |
| `ScheduleEntry` | 计划开始、优先级、资源分配和排程状态 |
| `ResourceReservation` | 计划阶段的设备/站点时间窗口预留 |

排程状态建议：

```text
Draft -> Ready -> Scheduled -> Admitted -> Running
      -> Blocked / Cancelled / Completed
```

### 5.3 运行侧

| 聚合/实体 | 职责 |
| --- | --- |
| `WorkflowRun` | 一次运行的总体状态、固定版本和上下文 |
| `NodeExecution` | 一个节点的一次或多次尝试及输入输出快照 |
| `DeviceOperation` | 设备命令、查询、对账和未知结果 |
| `ResourceLease` | 运行期间对 AGV、站点、机械臂、仪器的互斥占用 |
| `WorkflowSignal` | 人工确认、设备事件或外部系统回调 |
| `WorkflowAuditEvent` | 追加式生命周期和操作审计 |

建议的节点执行状态：

```text
Pending
Ready
WaitingForResource
Claimed
Running
WaitingForSignal
Succeeded
Failed
TimedOut
Unknown
Blocked
Cancelled
Skipped
```

`Unknown` 是重要的非终态决策点。它表示无法确认真实设备结果，不能被自动转换为 `Failed` 后盲目重试。

## 6. 运行时设计

### 6.1 准入流程

启动一次实验运行前，MES 应执行：

1. 固定已发布的流程版本和实验方案版本。
2. 校验流程图、节点配置和数据绑定。
3. 校验当前 Profile 是否提供全部设备能力。
4. 校验设备控制策略和安全分类是否允许本次运行。
5. 校验物料齐套、样品参数和布局要求。
6. 检查必要资源是否存在，但不要求所有资源立即空闲。
7. 保存运行上下文、能力目录版本和安全策略摘要。
8. 创建首批 `NodeExecution`，记录准入审计。

### 6.2 节点调度循环

```text
读取 Ready 节点
    |
    v
获取 ResourceLease
    | 获取失败
    +--------------> WaitingForResource
    |
    v
创建/声明 NodeExecution attempt
    |
    v
创建 DeviceOperation 或 Wait/Manual subscription
    |
    v
执行、轮询或接收信号
    |
    v
保存输出和证据
    |
    v
按显式 Edge 选择后续节点
```

运行时本身不实现厂商协议。它只调用能力执行端口，并根据规范化结果推进节点。

### 6.3 设备操作契约

所有外部设备操作至少携带：

```text
WorkflowRunId
NodeExecutionId
Attempt
OperationId
IdempotencyKey
CorrelationId
CapabilityId
RequestedAt
```

设备结果至少区分：

```text
Accepted
Running
Succeeded
Rejected
Failed
Cancelled
Unknown
```

查询和写入应有不同策略：

- 只读查询可以按明确退避策略重试。
- 幂等命令只有在设备或 Adapter 能证明幂等时才允许重试。
- 非幂等写入超时后进入 `Unknown`，先对账，禁止自动重发。
- AGV 使用任务 ID 查询真实状态。
- 机械臂使用消息/程序运行 ID 查询或监听确认。
- 离子色谱仪写入必须遵循单独的安全门禁和人工授权，不因流程节点存在而自动开放。

### 6.4 等待节点

等待应拆分成明确类型：

| 类型 | 示例 | 运行方式 |
| --- | --- | --- |
| 定时等待 | 等待 30 秒 | 持久化截止时间，不占 Worker 线程 |
| 设备条件等待 | 压力稳定、机械臂空闲 | 周期采样或设备事件，要求稳定窗口 |
| 外部信号等待 | LIMS 返回结果 | 订阅带关联 ID 的信号 |
| 人工确认等待 | 确认样品已放置 | 创建人工任务并记录操作者 |
| 资源等待 | 等待 IC 工作站释放 | 由资源租约协调器唤醒 |

设备稳定条件需要包含：

```text
采样间隔
条件表达式
连续满足时长
最大等待时长
数据过期阈值
超时路径
```

### 6.5 条件分支

条件分支只能读取已保存的运行上下文和节点输出。首期要求：

- 同一条件网关按优先级逐条求值。
- 必须有默认分支。
- 求值输入缺失时进入明确错误或等待状态，不能按 `false` 静默处理。
- 每次求值保存输入快照和命中边 ID。

### 6.6 并行流程

并行分叉和汇合不应在现有单 `PendingStep` 模型上补丁实现。必须先具备：

- 独立 `NodeExecution` 表。
- 多个同时处于 Ready/Running 的节点。
- 资源租约。
- Join 到达令牌或前驱完成集合。
- 取消、失败和补偿对其他并行分支的传播规则。

因此并行能力放在单路径、条件和人工节点稳定之后。

### 6.7 异常和补偿

实验设备操作通常不可完全回滚。目标模型使用显式补偿路径，而不是通用自动回滚：

```text
主路径失败
  -> 进入故障处理节点
  -> 判断设备真实状态
  -> 人工确认或执行已批准的补偿动作
  -> 恢复、终止或标记 Unknown
```

例如机械臂持有样品时，可配置“返回安全站点并放置”的补偿流程；离子色谱仪写入结果未知时，只能先读取和人工对账，不能自动发送相反命令。

## 7. 资源与排程

### 7.1 资源类型

首批资源建议：

- AGV 实体。
- 站点和路段。
- 机械臂。
- 仪器。
- 前处理工作站。
- 样品载具或托盘。
- 操作员确认席位。

### 7.2 计划预留与运行租约

两种占用不能混为一谈：

- `ResourceReservation`：排程阶段预计使用，允许调整。
- `ResourceLease`：运行阶段真实互斥占用，必须有持有者和过期/释放规则。

排程器根据计划时间、优先级、预计时长和资源日历安排任务；运行时在节点开始前获取真实租约。排程成功不代表设备现场条件一定允许执行。

### 7.3 调度器边界

调度器负责：

- 任务池和优先级。
- 计划时间和依赖。
- 设备/站点预计负载。
- 资源冲突检测。
- 阻塞原因和重新排程。

流程运行时负责：

- 节点推进。
- 真实资源租约。
- 设备操作和对账。
- 运行异常、补偿和审计。

## 8. 设备接入方式

### 8.1 AGV

建议能力：

```text
agv.navigate-to-station
agv.wait-arrival
agv.pause-task
agv.resume-task
agv.cancel-task
```

短期可以把“导航并等待到站”作为一个节点能力，继续复用现有稳定任务 ID、对账和地图预检。以后再拆分调度和等待状态。

### 8.2 机械臂

建议能力：

```text
robot.execute-program
robot.move-home
robot.pick-sample
robot.place-sample
robot.read-state
```

机械臂节点应引用经过批准的程序或动作模板 ID，不允许从流程界面输入任意脚本。执行前至少检查：

- 设备在线和安全状态。
- 工位/AGV 已到位。
- 夹具、样品和占用资源一致。
- 请求与确认消息具有关联 ID。

### 8.3 离子色谱仪

当前只允许：

```text
instrument.identify
instrument.read-status
instrument.wait-until-stable  // 只读条件满足后才可实现
```

以下能力即使出现在能力目录设计中，也必须保持不可用，直到对应安全验证完成：

```text
instrument.set-flow
instrument.enable-pump
instrument.set-temperature
instrument.load-method
instrument.inject
instrument.start-analysis
instrument.stop-analysis
```

流程发布校验必须同时检查 `Enabled` 和 `ControlEnabled`。一个节点类型存在不代表当前 Profile 允许发布或执行。

## 9. 持久化演进

现有三张表可以保留：

- `WorkflowVersions`
- `WorkflowExecutions`
- `WorkflowAudits`

建议分阶段增加：

| 表 | 用途 |
| --- | --- |
| `WorkflowNodeExecutions` | 每个节点和每次尝试的状态、输入、输出、时间和错误 |
| `WorkflowDeviceOperations` | 设备操作 ID、能力、请求摘要、设备结果和对账状态 |
| `WorkflowSignals` | 人工、设备和外部系统信号 |
| `WorkflowResourceLeases` | 资源持有、期限、释放和冲突原因 |
| `ExperimentPlans` | 实验方案元数据、物料和流程版本引用 |
| `ExperimentJobs` | 样品/批次对应的待执行实验 |
| `ScheduleEntries` | 排程时间、优先级和计划资源 |

不建议为新运行时立即引入完整事件溯源。当前 EF Core + SQLite 可以继续使用：核心状态采用结构化表，审计采用追加记录，定义快照继续使用 JSON。生产规模和并发证明 SQLite 不足后再评估数据库迁移。

## 10. API 边界演进

### 10.1 定义与版本

```text
GET    /api/workflows
POST   /api/workflows
PUT    /api/workflows/{id}/versions/{version}
POST   /api/workflows/{id}/versions/{version}/validate
POST   /api/workflows/{id}/versions/{version}/publish
GET    /api/workflows/{id}/versions/{version}/capability-check
```

### 10.2 运行

```text
POST   /api/workflow-runs
GET    /api/workflow-runs/{runId}
GET    /api/workflow-runs/{runId}/nodes
GET    /api/workflow-runs/{runId}/timeline
POST   /api/workflow-runs/{runId}/pause
POST   /api/workflow-runs/{runId}/resume
POST   /api/workflow-runs/{runId}/cancel
POST   /api/workflow-runs/{runId}/signals
POST   /api/workflow-runs/{runId}/unknown-resolution
```

所有状态变更接口需要操作者身份、请求 ID、理由和审计。设备 Worker 的 claim/complete 接口不应作为无认证的公共操作员 API。

### 10.3 能力和资源

```text
GET /api/device-capabilities
GET /api/devices
GET /api/resources/availability
GET /api/schedule
```

## 11. 安全和审计原则

1. 流程图不能绕过设备模块的安全策略。
2. 发布校验和运行准入是两道门，现场状态只能在运行准入时确认。
3. 原始设备写能力默认不可见或明确显示为不可用。
4. 非幂等写结果未知时禁止自动重试。
5. 人工确认必须保存用户、时间、理由和当时设备状态摘要。
6. 已发布版本、运行输入、能力目录版本和策略摘要必须可追溯。
7. 审计中只保留经过白名单过滤的请求/响应摘要，避免记录凭据和未过滤载荷。
8. 维护调试与生产流程执行分离，使用不同权限和明显的运行模式标识。

## 12. 迁移策略

### 阶段 0：术语和模型基线

目标：不改运行行为，先统一概念。

- 确认本文中的实验方案、排程任务、流程版本、流程运行、节点执行、设备操作和资源租约术语。
- 将现有 `EXPERIMENT-FLOW-EDITOR.md` 定位为历史实现记录。
- 为后续契约变更建立兼容性测试。

验收：团队在接口、数据库和界面中不再混用“方案、任务、流程实例”。

### 阶段 1：收敛编辑模型

目标：只有一套业务工作流模型。

- 先完成画布技术 Spike，再将候选画布改为共享 Contracts/ViewModel 适配层。
- 保留 `WorkflowEditorViewModel` 的 MES 生命周期命令。
- 停止新增 `ExperimentFlowConfigDto` 独立字段。
- 导入旧 Nodify JSON 时转换成统一模型，之后只写新格式。

验收：同一个流程可在画布编辑、保存草稿、校验、发布和重新加载，节点/边无信息丢失。

### 阶段 2：节点类型目录和结构化配置

目标：从自由文本参数转为按节点类型生成的配置。

- 引入 `NodeTypeId`、`SchemaVersion` 和节点类型目录。
- 首批实现 Start、End、AGV Move、Timed Wait、Manual Wait、Instrument Read。
- 属性面板由节点 schema 生成控件。
- 发布校验增加能力和 Profile 检查。

验收：用户不能输入无效站点、未知设备或协议地址；不可用能力无法发布。

### 阶段 3：逐节点运行记录

目标：替换单 `PendingStep` 的内部执行模型，但保持外部兼容读取。

- 新增 `WorkflowNodeExecutions` 和 `WorkflowDeviceOperations`。
- 将现有 AGV Move/Timed Wait Worker 迁移为统一节点执行器。
- 提供运行节点列表和时间线 API。
- 完善暂停、恢复、取消和 Unknown 处理。

验收：重启后可以从节点执行记录恢复，能看到每次尝试和设备对账证据。

### 阶段 4：信号、人工任务和条件分支

目标：支持真实实验中的等待和判断。

- 人工确认任务。
- 设备条件等待和稳定窗口。
- 结构化条件表达式和默认边。
- 显式成功、失败、超时和取消边。

验收：流程可以安全表达“AGV 到站 -> 人工/机械臂确认 -> 仪器状态满足 -> 后续步骤”。

### 阶段 5：资源租约和排程

目标：支持多个实验任务共享设备。

- 引入运行资源租约。
- 建立实验任务池和排程条目。
- 提供资源负载、阻塞原因和时间轴。
- 排程器只启动满足准入条件的固定版本任务。

验收：两个流程不能同时占用同一机械臂或仪器，阻塞原因可见且可审计。

### 阶段 6：并行、子流程和受控设备写能力

目标：在前述基础稳定后扩展高级能力。

- 并行分叉和汇合。
- 可复用子流程。
- 显式补偿路径。
- 按设备安全验证逐项开放控制能力。

离子色谱写能力是否进入此阶段，仍由独立的现场安全证据决定，不由软件路线图自动授权。

## 13. 必须现在决定与可以后置的事项

### 必须现在决定

- 单一共享流程模型。
- 独立边模型。
- 节点类型/配置 schema 版本。
- 实验方案、排程、运行的边界。
- 设备能力而非协议命令作为节点配置入口。
- 逐节点执行记录是后续运行时基础。

### 可以后置

- 并行执行的完整语义。
- 子流程和跨流程依赖。
- 自动优化排程算法。
- 多人协同编辑。
- 完整事件溯源。
- 生产数据库选型。

## 14. 风险与约束

| 风险 | 应对 |
| --- | --- |
| 先做 UI 后改模型导致返工 | 阶段 1 先收敛共享模型和边语义 |
| 通用节点抽象掩盖设备差异 | 能力统一，安全策略和执行器保持设备专属 |
| 任意表达式带来安全和可维护性问题 | 首期只支持结构化白名单操作符 |
| 并行过早引入使恢复复杂化 | 先完成逐节点执行、租约和条件分支 |
| SQLite 并发边界 | 当前阶段保持单实例 MES，建立并发和事务测试 |
| 发布流程与现场能力不一致 | 发布校验 + 运行准入双重能力检查 |
| 操作员误将调试当生产运行 | 调试模块独立入口、权限和醒目的运行模式 |

## 15. 本阶段完成标准

在开始下一轮功能实现前，应完成以下评审：

- 认可本文的职责划分和术语。
- 确认首批节点类型及其配置字段。
- 确认边语义和条件表达式范围。
- 确认 `NodeExecution`、`DeviceOperation`、`ResourceLease` 的最小字段。
- 确认 WPF 只保留一套业务模型，画布通过适配层接入；Nodify 是否最终采用以 Spike 结果为准。
- 确认离子色谱仪继续保持只读能力边界。
- 将阶段 1 拆成独立、可测试且不改变实体设备行为的实现任务。

界面信息架构和交互规划见 [EXPERIMENT-WORKFLOW-UI-DESIGN.md](EXPERIMENT-WORKFLOW-UI-DESIGN.md)，画布技术评估见 [EXPERIMENT-WORKFLOW-CANVAS-EVALUATION.md](EXPERIMENT-WORKFLOW-CANVAS-EVALUATION.md)，逐阶段交付和验收见 [EXPERIMENT-WORKFLOW-IMPLEMENTATION-PLAN.md](EXPERIMENT-WORKFLOW-IMPLEMENTATION-PLAN.md)。
