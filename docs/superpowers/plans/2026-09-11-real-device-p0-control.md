# 四类实体设备真实控制 P0 实施计划

> 对应设计：[2026-09-11-real-device-p0-control-design.md](../specs/2026-09-11-real-device-p0-control-design.md)
>
> 目标：两周内在 WPF 上完成一次真实单样本流程。AGV、AUBO 机械臂、开盖分液工作站、两台离子色谱均必须产生真实执行或真实任务启动证据，并由 MES 回传任务号、状态、错误和结果报表。
>
> 范围：单样本、串行调度、人工安全确认、可查询异常和结果归档。UI 视觉重构、连续批量、长期无人值守和自动恢复不属于本轮验收。

## 0. 约束、基线和完成定义

### 不可突破的约束

- 链路固定为 `WPF -> MES -> Adapter/仪器网关 -> 实体设备`。WPF 不连接厂家 TCP、WebSocket、HTTP 或串口。
- 每个可能改变设备状态的动作先在 MES 持久化唯一 `OperationId`，随后只发送一次；请求超时、断线、响应无法解析、厂家任务号无法关联均进入 `Unknown`，禁止自动重发。
- `Unknown` 只允许查询、人工核对和审计确认，不允许通过刷新页面、重启服务或重复点击隐式重试。
- AGV/AUBO 的历史现场证据不能代替本次授权；每次现场运行都要重新做只读预检、记录操作员/安全监护人和设备 epoch。
- 离子色谱未拿到厂家正式模块、字段和动作映射之前，不实现也不发送猜测性的 `Config`、`Command`、进样、停止或复位报文。
- 物理开关只能在隔离的 `PhysicalAcceptance` 配置中开启，开发默认和 Simulator 配置继续关闭。

### 当前基线（实施前复核）

- 代码基线可构建，现有测试为 1177 通过、5 跳过、0 失败；实施结束必须回归并解释新增/变更用例。
- AGV 已有 `vendor-tcp`、状态/地图/控制权/派发/暂停/恢复/取消/I/O 和 WPF 调度页，但物理配置仍为 `read-only-preflight`。
- AUBO 已有 WebSocket JSON-RPC `9012`、程序目录、load/run/stop 和 WPF 页，但 `Enabled`、`ControlEnabled`、`WorkflowAuboWorker` 默认关闭。
- 开盖分液目前只有只读 HTTP；需要按 `res` 中的接口文档核实真实 URL、端口、`EquipmentNo`、字段和返回数据。
- 两台离子色谱目前只有 D160+ Modbus 只读和 ShineLab/YLoop 连接层；正式写模块、结果关联和双设备隔离尚未完成。

### P0 完成定义

一份新的 WPF 运行记录必须同时满足：

1. 使用明确版本的已发布流程和新的 `RunId`；
2. AGV 真实到达目标站并返回设备任务号/最终状态；
3. AUBO 真实加载并运行批准程序，返回运行状态；
4. 开盖分液真实初始化、建任务、启动并查询到完成或明确人工收口状态；
5. 离子色谱 `CIC-D160-01` 和 `CIC-D160-02` 各自独立真实执行一次，返回厂家任务号/结果文件或厂家确认的结果载荷；
6. WPF 能显示完整时间线、`OperationId`、厂家任务号、错误/阻断原因，并导出 JSON/CSV 报表；
7. 任一不确定写入均显示 `Unknown`，流程停在人工处理，不得被显示为成功或空闲。

## 1. Task 0：现场输入冻结和隔离发布配置（D0-D1）

**目标**：在发送任何真实写入前，把五个物理设备身份、网络和授权证据固化为可审查输入。

**修改/新增文件**

- `src/MesControlAgv.Adapter/appsettings.PhysicalAcceptance.json`
- `src/MesControlAgv.Mes/appsettings.PhysicalAcceptance.json`
- `src/MesControlAgv.Adapter/PhysicalAcceptanceConfiguration.cs`
- `src/MesControlAgv.Adapter/Modules/SampleWorkstation/SampleWorkstationOptions.cs`
- `src/MesControlAgv.InstrumentGateway/CicD160PlusOptions.cs`
- `docs/physical-acceptance/README.md`
- 新增 `docs/physical-acceptance/2026-09-11-four-device-input-freeze.md`

**实施内容**

- 建立显式设备清单：`AGV-01`、`AUBO-01`、`SAMPLE-WORKSTATION-01`、`CIC-D160-01`、`CIC-D160-02`；真实序列号、IP/端口、传输方式、驱动版本和设备 epoch 来源必须逐项填写。
- AGV 只在现场批准后从 `read-only-preflight` 切到标准物理模式；保留 `AcquireControl=false`、自动派发关闭的默认配置，切换通过重启和配置校验完成。
- AUBO 固定批准工程目录和程序白名单，记录控制器地址 `192.168.1.102:9012` 是否仍有效；不接受 WPF 任意工程名或原始 RPC。
- 开盖分液记录 PDF 同时出现的 `8082`/`8808` 哪一个是现场端口、基础路径 `/Service/`、认证/防火墙要求、`EquipmentNo` 和厂家版本；缺一项则只读，不开放写入。
- 离子色谱分别记录正式 `equipmentCode/strCode`、设备序列号、连接会话、厂商模块版本、`UpdateInfo.status=99` 含义、Config/Command/结果样例和停止语义。空编号或仅有 fallback 编号时阻断。
- 配置文件不提交密码、令牌或临时抓包；敏感值通过现场环境变量/受控部署注入。

**验证**

- `GET /health`、`GET /api/adapter/devices`、`GET /api/physical/readiness` 均能显示正确运行模式和设备清单。
- 只读模式下对每个写端点执行一次负向检查，确认返回明确的 409/503，不触达实体动作。
- 现场输入冻结文档由操作员和安全监护人签字后，才允许进入 Task 2。

## 2. Task 1：统一设备操作、结果和 Unknown 契约（D0-D2）

**目标**：让四类设备共用可持久化的操作生命周期，而不改变现有 Simulator 行为。

**修改/新增文件**

- `src/MesControlAgv.Contracts/Workflows/WorkflowContracts.cs`
- `src/MesControlAgv.Contracts/Workflows/WorkflowGraphContracts.cs`
- `src/MesControlAgv.Contracts/Workflows/WorkflowCatalogContracts.cs`
- `src/MesControlAgv.Contracts/Devices/DeviceContracts.cs`
- `src/MesControlAgv.Contracts/Devices/SampleWorkstationContracts.cs`
- 新增 `src/MesControlAgv.Contracts/Devices/DeviceOperationContracts.cs`
- `src/MesControlAgv.Mes/Entities/WorkflowDeviceOperationRecord.cs`
- `src/MesControlAgv.Mes/Entities/WorkflowNodeExecutionRecord.cs`
- `src/MesControlAgv.Mes/Data/MesDbContext.cs`
- `src/MesControlAgv.Mes/Program.cs`（仅在确有新表/索引时增加幂等 schema）

**实施内容**

- 统一 `Queued -> Preparing -> StartPending -> Running -> AcquiringResult -> Completed` 状态，并支持 `Failed/Cancelled/Unknown/ManualInterventionRequired` 终态。
- 扩展现有 `WorkflowDeviceOperationRecord` 的 JSON 摘要约定：写入前保存设备 ID、能力、请求摘要、幂等键、运行/节点关联；完成后保存厂家任务号、原始响应摘要、结果文件引用、解析结果和时间戳。
- 定义稳定的 `UnknownReason`（超时、写后断线、响应不完整、任务号缺失、结果缺失、身份不匹配等）和人工收口请求；所有人工决定写入 `WorkflowAuditRecord`。
- 让 `OperationId`、`RunId`、`NodeExecutionId`、可选厂家任务号贯穿请求、轮询、报表和查询 API；禁止根据页面时间或设备当前空闲状态推断成功。
- 将现有 `WorkflowStepCompletionRequest.Outputs` 作为规范化结果入口，保留受限长度的原始响应摘要，不把完整现场凭据写入数据库。

**验证**

- Domain/MES 单元测试覆盖合法状态迁移、重复完成幂等、Unknown 不可重发、不同设备 ID 不可交叉关联。
- 数据库启动两次不会重复建表/索引；旧运行记录可读取为 `Unknown` 或兼容状态，不被迁移成成功。

## 3. Task 2：Adapter/仪器网关单次写入和错误边界（D1-D4）

**目标**：把“只发一次”和“无法证明结果即 Unknown”落实到传输层。

**修改/新增文件**

- `src/MesControlAgv.Adapter/Services/AdapterService.cs`
- `src/MesControlAgv.Adapter/Modules/AgvAdapterModule.cs`
- `src/MesControlAgv.Adapter/Modules/AuboArm/AuboArmAdapterModule.cs`
- `src/MesControlAgv.Adapter/Modules/SampleWorkstation/SampleWorkstationAdapterModule.cs`
- `src/MesControlAgv.Adapter/Modules/SampleWorkstation/VendorSampleWorkstationHttpClient.cs`
- `src/MesControlAgv.InstrumentGateway/CicD160PlusControlledWriteSession.cs`
- `src/MesControlAgv.Mes/Services/PhysicalExecutionAdmissionPolicy.cs`
- `src/MesControlAgv.Mes/Services/PhysicalSafetyActionService.cs`
- 新增 `src/MesControlAgv.Adapter/Services/DeviceWriteOutcome.cs`（或并入现有共享契约）

**实施内容**

- 所有控制端点先校验设备 ID、运行关联、当前 readiness/epoch、控制开关和操作员，再进入串行写入队列；同一设备同时只有一个物理写操作。
- 请求超时、连接断开、响应解析失败、写回显缺失或厂家任务号为空时抛出统一 Unknown 结果；调用方只能查询，不得由 HTTP 客户端/worker 自动重发。
- 响应日志使用脱敏摘要、长度、方向、哈希和任务号；禁止记录令牌、完整样品信息或任意原始坐标。
- 保留现有 AGV `IPhysicalAgvControlGateway`、AUBO 受控 handshake、D160+ 只读 preflight 的 fail-closed 语义。
- 物理取消、释放控制权、AUBO stop 通过 `PhysicalSafetyActionService` 统一审计；结果不确定同样进入 Unknown。

**验证**

- Adapter 测试模拟成功、超时、半包、断线、重复请求，断言物理写调用次数始终为 0 或 1，绝不为 2。
- MES API 测试断言无关联 `OperationId`、旧 epoch、错误设备 ID 和 supervisor 未启用均返回明确冲突码。

## 4. Task 3：AGV 真实控制收口（D2-D5）

**目标**：复用已验证的 `vendor-tcp` 能力，完成一次受监护的单车、单段真实导航。

**修改/新增文件**

- `src/MesControlAgv.Adapter/Services/TcpAgvClient.cs`
- `src/MesControlAgv.Adapter/Modules/AgvAdapterModule.cs`
- `src/MesControlAgv.Mes/Services/WorkflowFieldNavigationWorker.cs`
- `src/MesControlAgv.Mes/Services/FieldNavigationAcceptanceService.cs`
- `src/MesControlAgv.Mes/Services/WorkflowNodeExecutionOrchestration.cs`
- `src/MesControlAgv.Mes/Services/WorkflowRuntimeRecordPersistence.cs`
- `src/MesControlAgv.Mes/Endpoints/DeviceGatewayEndpointRouteBuilderExtensions.cs`
- `src/MesControlAgv.Wpf/ViewModels/AgvCommunicationViewModel.cs`
- `src/MesControlAgv.Wpf/ViewModels/TaskMonitorViewModel.cs`
- `src/MesControlAgv.Adapter/appsettings.PhysicalAcceptance.json`

**实施内容**

- 先取得新鲜只读预检：在线、车辆模式、地图名/version/MD5、定位置信度、急停/阻挡/故障、当前任务和控制权。
- 记录本次授权的 `AgvId`、操作员、安全监护人、permit 前缀和 device epoch；通过现有受控接口单次 acquire/dispatch，轮询设备任务到 `arrived` 或明确失败。
- 到站后确认站点和任务号，再释放控制权；释放失败不伪造完成，保留安全告警和人工处理入口。
- WPF 只补最小字段绑定：目标站、预检状态、派发按钮、设备任务号、OperationId、最终状态和错误。

**验证/验收**

- 离线使用现有 `TcpAgvClientTests`、`PhysicalAcceptancePreflightServiceTests`、workflow worker 测试覆盖状态和 Unknown。
- 现场按 `docs/physical-acceptance/README.md` 的新鲜预检步骤执行一次短距离低速单段；保存请求/响应摘要、控制权前后快照和到站照片/日志。
- WPF 运行记录必须显示真实 `AGV-01` 任务号，不能只显示 MES 任务已创建。

## 5. Task 4：AUBO 真实程序执行收口（D3-D6）

**目标**：使用现有 WebSocket JSON-RPC 9012 和批准工程目录，完成一次 load/run/stop 或自然完成。

**修改/新增文件**

- `src/MesControlAgv.Adapter/Modules/AuboArm/AuboArmAdapterModule.cs`
- `src/MesControlAgv.Adapter/Modules/AuboArm/AuboArmProgramDriver.cs`
- `src/MesControlAgv.Adapter/Modules/AuboArm/AuboArmControlledHandshakeSession.cs`
- `src/MesControlAgv.Mes/Services/WorkflowAuboProgramWorker.cs`
- `src/MesControlAgv.Mes/Services/AuboArmWorkflowCorrelationValidator.cs`
- `src/MesControlAgv.Mes/Endpoints/DeviceGatewayEndpointRouteBuilderExtensions.cs`
- `src/MesControlAgv.Wpf/Services/IMesClient.cs`
- `src/MesControlAgv.Wpf/Services/MesClient.cs`
- `src/MesControlAgv.Wpf/ViewModels/AuboArmControlViewModel.cs`
- `src/MesControlAgv.Wpf/Views/AuboArmControlView.xaml(.cs)`
- `src/MesControlAgv.Adapter/appsettings.PhysicalAcceptance.json`
- `src/MesControlAgv.Mes/appsettings.PhysicalAcceptance.json`

**实施内容**

- 读取新鲜状态、模式、安全模式、当前工程和批准目录；不允许工程名脱离白名单。
- 为 load、run、stop 分别创建并持久化 `OperationId`；响应无关联或状态不稳定时进入 Unknown，通过只读查询补证据。
- 启用仅限现场配置的 `AuboArm:Enabled`、`ControlEnabled` 和 `WorkflowAuboWorker.Enabled`；开发/Simulator 仍关闭。
- worker 只领取当前 supervisor instance/epoch 的节点；重启恢复只读核对，不自动继续 run。
- WPF 最小操作包括选择批准程序、显示 readiness、load/run/stop、显示运行状态和人工 Unknown 收口。

**验证/验收**

- Adapter 现有 AUBO JSON-RPC、handshake、program driver 测试全部通过，并新增一次写入/超时/状态不一致测试。
- 现场在安全模式和人工监护下完成一次批准程序，保存 catalog、load/run 响应摘要和结束状态。

## 6. Task 5：开盖分液 HTTP 真实驱动（D2-D7）

**目标**：在不把厂家 HTTP 暴露给 WPF 的前提下，把只读模块扩展为最小真实任务闭环。

**修改/新增文件**

- `src/MesControlAgv.Adapter/Modules/SampleWorkstation/SampleWorkstationAdapterModule.cs`
- `src/MesControlAgv.Adapter/Modules/SampleWorkstation/SampleWorkstationReadOnlyDriver.cs`
- 新增 `src/MesControlAgv.Adapter/Modules/SampleWorkstation/SampleWorkstationControlledDriver.cs`
- `src/MesControlAgv.Adapter/Modules/SampleWorkstation/VendorSampleWorkstationHttpClient.cs`
- `src/MesControlAgv.Adapter/Modules/SampleWorkstation/SampleWorkstationOptions.cs`
- `src/MesControlAgv.Application/Ports/SampleWorkstationGateway.cs`（若接口尚不存在则新增）
- `src/MesControlAgv.Mes/Services/SampleWorkstationAdapterClient.cs`
- `src/MesControlAgv.Mes/Services/WorkflowSampleWorkstationWorker.cs`（新增）
- `src/MesControlAgv.Mes/Endpoints/DeviceGatewayEndpointRouteBuilderExtensions.cs`
- `src/MesControlAgv.Contracts/Devices/SampleWorkstationContracts.cs`
- `src/MesControlAgv.Wpf/Services/IMesClient.cs`
- `src/MesControlAgv.Wpf/Services/MesClient.cs`
- 新增 `src/MesControlAgv.Wpf/ViewModels/SampleWorkstationControlViewModel.cs` 及最小视图绑定

**实施内容**

- 依据 `res/开盖分液工作站Http接口文档V1.0.pdf` 只实现厂商确认的 `/Init`、`/AddExperimentalTask`、`/AddTaskTrajectoryParameter`、`/StartExperiment`、`/GetTaskState`、`/GetTaskDetails`。
- 继续复用 `Code/Data` 信封校验、HTTP 超时和业务错误映射；POST 请求使用固定 DTO，不开放任意 URL、原始坐标、速度或夹爪参数。
- 任务创建参数必须来自批准模板/物料批次；持久化 `TaskNo`、`EquipmentNo`、模板版本和结果字段。创建或启动响应无法确认任务号时立即 Unknown。
- 状态机采用 `Offline -> Connecting -> Uninitialized -> Initializing -> Idle -> Preparing -> Ready -> StartPending -> Running -> Completed`，任何非预期状态进入 `Faulted/Unknown/ManualInterventionRequired`。
- 在 Adapter 和 MES 两层增加 `ControlEnabled`/supervisor gate；WPF 只提交样品批次、模板和操作员信息。

**验证/验收**

- 用固定 HTTP stub 覆盖端口、编码、Code/Data、空 Data、业务错误、超时和重复启动；断言 POST 次数和 Unknown 语义。
- 现场先只读 `status/errors/tasks`，再由厂家确认空闲和安全条件后执行一次初始化、建任务、启动和任务详情查询。
- WPF 显示厂家 `TaskNo`、原始状态、完成时间和结果字段；未确认端口时页面显示“协议待确认”，不可点击启动。

## 7. Task 6：两台离子色谱正式模块接入（D0-D9，并行泳道）

**目标**：在厂家模块交付后，分别对 `CIC-D160-01` 与 `CIC-D160-02` 完成一次真实方法/样品/结果闭环。

**外部依赖闸门（不阻塞其他任务）**

- 厂家必须提供可运行模块或正式 SDK/协议说明、两个设备的真实 `equipmentCode/strCode`、Config/Command/结果/停止样例、状态码（含 99）和结果文件落盘规则。
- 在闸门关闭期间，只能完成双设备只读身份/状态、报文捕获分析和适配器接口桩；不得把 fallback 编号或推测字段接入生产控制。
- D2 结束仍未交付时，将离子色谱泳道标记为 `protocol_pending`，冻结其真实写入入口，但继续执行工作站、AGV、AUBO、MES 编排、WPF 报表、离线测试和发布准备，不等待厂家模块。
- 厂家模块到位后，只补齐该泳道的正式 codec/driver、双设备配置和现场执行证据；不得为了赶进度修改已完成设备的安全门禁或引入猜测协议。

**修改/新增文件**

- `src/MesControlAgv.Application/Ports/IonChromatographyGateway.cs`
- `src/MesControlAgv.InstrumentGateway/CicD160PlusOptions.cs`
- `src/MesControlAgv.InstrumentGateway/CicD160PlusReadOnlyDriver.cs`
- `src/MesControlAgv.InstrumentGateway/CicD160PlusControlledWriteSession.cs`
- `src/MesControlAgv.InstrumentGateway/Sha18iProtocolCodec.cs`
- 新增 `src/MesControlAgv.InstrumentGateway/ShineLabControlledDriver.cs` 或厂家模块适配器
- `src/MesControlAgv.Mes/Services/ShineLabCommandService.cs`
- `src/MesControlAgv.Mes/Services/ShineLabConnectionManager.cs`
- `src/MesControlAgv.Mes/Services/IonChromatographyGatewayClient.cs`
- `src/MesControlAgv.Mes/Services/WorkflowIonChromatographyWorker.cs`（新增）
- `src/MesControlAgv.Mes/Services/WorkflowNodeExecutionOrchestration.cs`
- `src/MesControlAgv.Mes/Services/WorkflowRuntimeRecordPersistence.cs`
- `src/MesControlAgv.Mes/Endpoints/DeviceGatewayEndpointRouteBuilderExtensions.cs`
- `src/MesControlAgv.Mes/appsettings.PhysicalAcceptance.json`
- `src/MesControlAgv.InstrumentGateway/appsettings.json`
- `src/MesControlAgv.Contracts/Devices/DeviceContracts.cs`
- `src/MesControlAgv.Wpf/Services/IMesClient.cs`
- `src/MesControlAgv.Wpf/Services/MesClient.cs`
- `src/MesControlAgv.Wpf/ViewModels/IonChromatographyViewModel.cs`

**实施内容**

- 把 `IIonChromatographyGateway` 的 `Identify/LoadApprovedMethod/StartRun/PauseRun/ResumeRun/StopRun/GetResult` 映射到厂家正式动作；保留现有 D160+ Modbus 只读作为 preflight，不把只读驱动伪装成控制驱动。
- 用 keyed/device-id 配置注册两套独立连接会话、端口/锁、任务状态、轮询器和结果目录；任何一台的任务号、状态或结果不得串到另一台。
- `Config` 和 `Command` 请求从批准方法/样品 DTO 构造，固定字段顺序和序列化策略；每个动作只写一次，回包不完整或 `equipmentCode/strCode` 不匹配即 Unknown。
- 运行完成后同时保存厂家任务号、样品批次、方法版本、通道、原始结果引用、解析值、单位、时间戳和错误码；无法得到结果文件/载荷时状态为 `AcquiringResult` 或 `Unknown`，不写 Completed。
- 在 ShineLab 连接层按设备代码路由 `UpdateInfo/Result/TaskFinish/TaskError/EndMission`，保留当前空设备码和 status 99 的诊断告警直到厂家定义明确。
- WPF 仪器页扩展设备选择、批准方法/样品参数、开始/停止、结果和错误显示；默认设备 ID 不再硬编码为单台。

**验证/验收**

- 协议 codec、payload builder、状态映射、结果解析、双会话隔离和 Unknown 测试先通过；测试不得连接现场 IP。
- 分别对两台设备执行“身份/状态 -> 加载方法 -> 启动 -> 轮询 -> 结果”一次，现场保存每台独立证据包和哈希。
- 任一设备失败不影响另一台记录，但整条跨设备工作流停在明确的 `Failed/Unknown/ManualInterventionRequired`，不自动切换到另一台冒充完成。
- 模块未到位时，自动化和 WPF 必须明确显示“离子色谱协议待厂商交付/只读”，该状态不阻止其他三类设备和报表链路验收，但不能计入“四类真实控制完成”。

## 8. Task 7：MES 串行工作流、联锁和结果归档（D6-D11）

**目标**：把四类设备串成一次单样本流程，并保证前后置条件和设备资源互斥。

**修改/新增文件**

- `src/MesControlAgv.Mes/Services/WorkflowApplicationService.cs`
- `src/MesControlAgv.Mes/Services/WorkflowNodeExecutionOrchestration.cs`
- `src/MesControlAgv.Mes/Services/WorkflowRuntimeRecordPersistence.cs`
- `src/MesControlAgv.Mes/Services/WorkflowFieldNavigationWorker.cs`
- `src/MesControlAgv.Mes/Services/WorkflowAuboProgramWorker.cs`
- 新增 `src/MesControlAgv.Mes/Services/WorkflowSampleWorkstationWorker.cs`
- 新增 `src/MesControlAgv.Mes/Services/WorkflowIonChromatographyWorker.cs`
- 新增 `src/MesControlAgv.Mes/Services/WorkflowDeviceOperationCoordinator.cs`
- `src/MesControlAgv.Mes/Services/PhysicalReadinessSupervisor.cs`
- `src/MesControlAgv.Mes/Services/WorkflowRecoveryService.cs`
- `src/MesControlAgv.Mes/Endpoints/WorkflowEndpointRouteBuilderExtensions.cs`
- `src/MesControlAgv.Mes/Endpoints/DeviceGatewayEndpointRouteBuilderExtensions.cs`
- `src/MesControlAgv.Contracts/Workflows/WorkflowCatalogContracts.cs`
- `src/MesControlAgv.Domain/Workflows/BuiltInWorkflowCatalog.cs`
- `src/MesControlAgv.Domain/Workflows/WorkflowValidator.cs`

**实施内容**

- 扩展节点执行和能力目录，使 `agv.move`、`robot.execute-program`、工作站控制节点和离子色谱动作节点都能生成同一类 `WorkflowDeviceOperation`；旧 `instrument.operation` 只读兼容路径继续可用。
- 每个 worker 遵循“校验发布版本/能力/资源/授权 -> claim 节点 -> 单次真实写入 -> 只读轮询/结果查询 -> 完成或 Unknown”的顺序。
- 联锁：AGV 未到目标站不可启动工作站/AUBO；工作站/AUBO 未回安全状态不可派 AGV 离站；离子色谱不在允许进样状态不可启动；任何外部任务、未知状态、任务号不一致或结果缺失都停止后续节点。
- 跨进程恢复只做只读核对和人工确认；不自动发送 stop/cancel/release/run。`Unknown` 通过既有 `ResolveUnknown`/manual confirmation API 收口。
- 结果归档服务按 `RunId` 聚合所有设备步骤，生成可追溯 JSON/CSV，保留设备 ID、OperationId、厂家任务号、样品批次、方法版本、错误和结果引用。

**验证**

- MES workflow/domain 测试覆盖节点 claim、资源互斥、联锁阻断、暂停/取消、重启恢复、Unknown 收口和结果聚合。
- 使用隔离 SQLite 和 stub Adapter 做一次全流程，不得把 stub/Simulator 证据标记为 physical acceptance。

## 9. Task 8：WPF 最小控制和报表闭环（D8-D12）

**目标**：只补真实控制所需的输入、状态和报表，不进行 UI 视觉重构。

**修改/新增文件**

- `src/MesControlAgv.Wpf/Services/IMesClient.cs`
- `src/MesControlAgv.Wpf/Services/MesClient.cs`
- `src/MesControlAgv.Wpf/ViewModels/WorkflowRunMonitorViewModel.cs`
- `src/MesControlAgv.Wpf/ViewModels/TaskMonitorViewModel.cs`
- `src/MesControlAgv.Wpf/ViewModels/AgvCommunicationViewModel.cs`
- `src/MesControlAgv.Wpf/ViewModels/AuboArmControlViewModel.cs`
- `src/MesControlAgv.Wpf/ViewModels/IonChromatographyViewModel.cs`
- 新增 `src/MesControlAgv.Wpf/ViewModels/SampleWorkstationControlViewModel.cs`
- `src/MesControlAgv.Wpf/Views/AuboArmControlView.xaml(.cs)`
- `src/MesControlAgv.Wpf/Views/ShineLabDeviceStatusView.xaml(.cs)`
- 新增或扩展 `src/MesControlAgv.Wpf/Views/SampleWorkstationControlView.xaml(.cs)`
- `src/MesControlAgv.Wpf/WorkflowCanvas/WorkflowRunMonitorView.xaml(.cs)`
- 新增 `src/MesControlAgv.Wpf/Services/RunReportExportService.cs`

**实施内容**

- 运行启动表单：样品批次/样品号、AGV 站点、批准 AUBO 程序、工作站模板/任务参数、两个离子色谱设备 ID/方法、操作员和安全监护人。
- 运行监控表格按设备显示 `OperationId`、厂家任务号、状态、开始/完成时间、错误、阻断原因和结果引用；显示“只读/未授权/协议待确认”而不是空闲。
- Unknown 卡片提供“查询设备”“人工确认已完成/失败/继续人工处理”入口；不提供自动重试按钮，也不把刷新当重试。
- 报表导出使用现有 WPF 运行目录/权限，输出 UTF-8 JSON 和 CSV；CSV 字段固定并包含结果单位和原始引用。
- WPF 所有控制调用仍只经过 MES HTTP；HTTP 错误显示服务端 `code/detail`，不吞掉冲突原因。

**验证**

- WPF binding/HTTP contract 测试覆盖双离子色谱选择、状态刷新、Unknown 按钮禁用自动重试、报表字段和错误呈现。
- 使用 `WPF_RUNTIME_MODE=simulator` 做离线 UI 回归，确认不改变默认 physical 连接和现场配置。

## 10. Task 9：定向自动化验证和发布门禁（D10-D13）

**目标**：在连接实体设备前完成可重复的离线质量门禁。

**测试范围**

- `tests/MesControlAgv.Contracts.Tests`（如项目存在则新增，否则放入对应 Domain/MES 测试项目）：设备操作/Unknown/报表契约。
- `tests/MesControlAgv.Domain.Tests`：目录能力、联锁、状态机和双设备隔离。
- `tests/MesControlAgv.Adapter.Tests`：AGV/AUBO/工作站 HTTP/离子色谱 codec、单次写入和错误映射。
- `tests/MesControlAgv.InstrumentGateway.Tests`：D160+/厂家模块、双会话、结果解析和读写边界。
- `tests/MesControlAgv.Mes.Tests`：workers、资源租约、持久化、HTTP endpoint、Unknown 收口和报表聚合。
- `tests/MesControlAgv.Wpf.Tests`：四类设备最小控件、WPF-MES HTTP 契约、报表导出。
- `tests/MesControlAgv.E2E.Tests`：隔离服务的单样本串行闭环和失败分支。

**必须执行的命令**

```powershell
dotnet test MesControlAgv.sln -m:1
dotnet build MesControlAgv.sln -c Release --no-restore
git diff --check
```

构建和测试输出写入本次独立证据目录；不得覆盖用户现有 `artifacts/`、现场抓包或 SQLite。

## 11. Task 10：现场四设备一次性验收（D12-D14）

**目标**：用新鲜设备状态和新运行 ID 取得唯一一份四设备真实执行证据包。

**执行顺序**

1. 断开旧服务和旧任务，确认设备处于安全/空闲状态；记录电源、网线、控制器版本和时间。
2. 启动隔离的 PhysicalAcceptance Adapter/MES，确认 `5141/5145` 健康、设备清单、supervisor instance/epoch 和 readiness 均为新值。
3. 启动 WPF（默认 physical）：

   ```powershell
   dotnet run --project src/MesControlAgv.Wpf -c Debug
   ```

4. 在 WPF 创建新的单样本运行，依次执行 AGV 到站、AUBO 批准程序、开盖分液任务、`CIC-D160-01`、`CIC-D160-02`；每一步只点击一次并等待 MES 回传。
5. 对每台设备保存状态前后快照、请求/响应摘要、OperationId、厂家任务号、结果文件哈希和操作者确认；任一步 Unknown 时停止后续动作并按人工流程收口。
6. 从 WPF 导出 JSON/CSV，核对报表与 MES `GET /api/workflow-runs/{workflowRunId}/timeline`、`.../device-operations` 一致。
7. 结束后停止 WPF/MES/Adapter，确认 `5141/5145` 无监听，保留完整证据和配置哈希。

**现场验收命令（只在人员、设备和隔离配置确认后执行）**

```powershell
Invoke-RestMethod http://127.0.0.1:5141/health
Invoke-RestMethod http://127.0.0.1:5145/health
Invoke-RestMethod http://127.0.0.1:5145/api/physical/readiness
Invoke-RestMethod http://127.0.0.1:5145/api/workflow-runs/<runId>/timeline
Invoke-RestMethod http://127.0.0.1:5145/api/workflow-runs/<runId>/device-operations
```

以上命令只查询状态/证据；真实写入必须由 WPF/MES 受控业务端点发起，禁止用 `Invoke-RestMethod` 拼厂家报文。

## 12. 两周排期、依赖和升级规则

| 日期 | 交付物 | 进入条件 | 当日退出条件 |
| --- | --- | --- | --- |
| D0 | 现场输入清单、设备 ID、风险登记 | 设计已确认 | 所有缺口有责任人和截止时间 |
| D1 | 物理配置骨架、统一操作契约 | D0 清单 | 只读预检和写入门禁测试通过 |
| D2 | 单次写入/Unknown 边界、工作站 HTTP DTO | 工作站端口/字段确认；离子厂家闸门检查 | Adapter 离线测试通过；离子模块到位或升级项目风险 |
| D3-D4 | AGV 真实控制、AUBO 真实控制 | 新鲜现场授权 | 各完成一次单设备真实证据 |
| D5-D7 | 工作站真实任务闭环 | 厂家 HTTP 样例和空闲确认 | 完成一次真实初始化/建任务/启动/结果查询 |
| D6-D9 | 两台离子色谱控制和结果 | 厂家正式模块、双设备身份 | 两台各自完成一次真实结果闭环 |
| D8-D11 | MES 串行 worker、联锁、归档 | 四类单设备证据 | 隔离 stub 全流程和 Unknown 分支通过 |
| D10-D12 | WPF 最小控制、监控、报表 | MES API 稳定 | WPF 能创建、监控、导出一次运行 |
| D12-D13 | Release 构建和现场预演 | 所有定向测试通过 | 新运行 ID、配置哈希和证据目录就绪 |
| D14 | 四设备真实单样本验收 | 操作员/监护人现场确认 | 四设备结果报表签字或形成明确降级结论 |

**关键升级规则**：D2 离子色谱厂家闸门未关闭时只升级风险并标记 `protocol_pending`，不暂停其他泳道；D9 任一离子设备仍只有只读能力，不能宣称“四类设备真实控制完成”，但不阻止其余三类和软件闭环继续交付；D12 现场预演出现 Unknown 未完成对账，D14 不得重发同一动作，只能换新运行 ID 并由人工确认安全状态后决定是否继续。

## 13. 交付物清单

- 代码：Contracts、Adapter/仪器网关、MES workers/API、WPF 最小控制和报表。
- 配置：隔离 `PhysicalAcceptance` 配置、五设备身份、方法/模板白名单、配置哈希。
- 自动化证据：全量测试、Release 构建、`git diff --check`、双离子色谱协议/解析测试。
- 现场证据：每台设备只读预检、授权、单次写入、状态轮询、结果文件哈希和人工确认。
- 报表：按 `RunId` 导出的 JSON/CSV；包含设备 ID、OperationId、厂家任务号、状态、错误、结果引用和审计信息。
- 风险结论：若厂家协议未按时交付，单独列为 P0 未完成项并继续推进其他泳道；不使用 Simulator、历史截图或只读状态替代四类真实控制结论。
