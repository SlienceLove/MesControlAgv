# 物理执行统一准入门禁设计

日期：2026-09-09
状态：设计已固化，等待用户书面复核

## 决策摘要

采用“MES 保持在线、物理执行失败关闭”的方案：物理模式下，
`PhysicalReadinessSupervisor` 未启用时，WPF、MES 健康检查和所有只读接口继续可用，
但任何会启动、继续或改变 AGV/AUBO 工艺状态的入口都必须在调用 Adapter 前拒绝。

保留三类关联明确的安全收尾：取消已知 AGV 任务、释放 Adapter 已持有的 AGV 控制权、
停止已知 AUBO 程序。AGV cancel 和 AUBO stop 只能由操作员显式请求；已授权且未跨进程
恢复的工作流可在确认最终 Move 到达后执行现有的一次性 release。它们不构成新任务授权，
不能由启动恢复流程自动触发，仍受既有配置、操作员、关联记录和“写入不自动重试”规则
约束。AGV `pause` 不列入例外。

监督器仍默认关闭；本设计不会自动打开物理 Probe、worker 或批量执行开关，也不会改变
Simulator 行为。

## 问题

当前 epoch 门禁仅在 `IPhysicalReadinessState.Enabled=true` 时执行。若将来只打开现场
worker、通用任务派发或直接设备控制入口，而遗漏监督器开关，部分旧路径会继续按历史
语义调用 Adapter，授权中不会绑定当前 `SupervisorInstanceId` 和 `DeviceEpoch`。

现有 `WorkflowPhysicalBatchAdmissionGate` 只检查现场导航、自动许可和 AUBO worker，
没有把监督器纳入同一启动快照。直接 AGV 命令、DO、AUBO handshake/program 和人工
field-navigation 路径也各自使用条件式判断，容易再次出现遗漏。

当前 `appsettings.PhysicalAcceptance.json` 中监督器、物理 worker、自动派发和现场导航
均为关闭状态，因此现状不会自动写设备。本设计用于防止下一次配置切换时形成组合漏洞。

## 目标

1. 物理模式的启动/继续类写入必须同时经过统一监督器准入和现有 epoch 授权校验。
2. 监督器关闭、未观察到设备、实例不匹配、epoch 不匹配或状态非 Ready 时，在任何
   Adapter 调用之前失败。
3. 保留 WPF/MES 只读运行能力，使设备断电时中控仍可常开并显示明确阻断原因。
4. 保留最小安全收尾能力，只开放明确的关联和审计入口，不允许恢复 worker 复用、不自动重试、
   不把收尾成功解释为流程成功。
5. 使用稳定错误码和审计字段，让 WPF、脚本和现场证据能够区分“配置未启用”与“设备故障”。

## 非目标

- 不把 `PhysicalReadinessSupervisor.Enabled` 改为默认 `true`。
- 不自动打开 `EnableAutomaticDispatch`、`EnableFieldNavigationAcceptance`、AUBO worker、
  field-navigation worker 或 WPF 物理批量开关。
- 不修改 AGV/AUBO 协议、IP、SSID、Bridge、NAT、网关、端口或地图事实。
- 不为 Adapter 引入跨进程令牌、密码或网络认证协议；Adapter 继续作为本机受信驱动边界，
  依靠回环监听、`Adapter:RunMode`、设备 `ControlEnabled` 和物理预检防护。
- 不激活当前未注册的旧 `CompoundTaskService`/`CompoundTaskServiceV2`。未来若注册，必须
  显式接入统一准入策略后才能用于物理模式。
- 不在本轮启动 MES、Adapter、WPF 或访问任何现场设备。

## 范围边界

首期覆盖 MES 内所有能够到达 AGV 导航/命令/DO/控制权及 AUBO handshake/program 写入的
路径，也覆盖会创建或继续这些节点的工作流、工作流控制和复合实验子流程。工作流定义编辑、
排程、资源预留等纯本地写入不属于设备写入，可以继续使用；但它们最终启动子工作流时必须
经过本门禁。

ShineLab 命令链不使用当前 AGV/AUBO readiness epoch，离子色谱和工作站当前只有只读入口，
因此本轮不把这些协议并入本门禁，也不宣称它们已受本门禁保护。未来新增或启用任何物理设备
写入口时，必须先注册 readiness probe、epoch 绑定和统一准入测试，不能仅靠配置开关上线。

## 方案比较

### A. 只修改批量开关组合

让 `physicalBatchEnabled` 额外依赖监督器。改动最少，但人工 field-navigation、通用任务、
直接设备 API 和 worker 内部仍可能绕过，不能满足“统一门禁”。不采用。

### B. 统一 MES 准入策略并在写入边界复核

增加一个无设备通信能力的准入策略，集中判断运行模式、监督器开关和入口类别；批量入口、
服务层、worker 和直接 MES API 都使用它。MES 不因配置不完整而退出，只读能力继续工作。
这是本设计采用的方案。

### C. 配置不完整时拒绝 MES 启动

最严格，但设备断电或配置分阶段切换时会同时失去中控只读状态面和诊断入口，不符合 MES
常开目标。不采用。

## 架构

新增名称固定为 `PhysicalExecutionAdmissionPolicy` 的 MES 单例。它读取
`ProfileConfiguration`、`IPhysicalReadinessState` 和结构化 logger，不持有 Adapter、AGV 或
AUBO 客户端。拒绝时抛出固定类型 `PhysicalExecutionAdmissionException`，并由调用边界映射
响应；不得依赖匹配异常消息做控制流。

安全例外集中到 `PhysicalSafetyActionService`，而不是在各 endpoint 内散落 bypass。release 和
stop 每次先写入 `PhysicalSafetyActionRecord`，至少持久化 request/operation ID、动作类型、
device、operator、reason、可选 workflow run、状态、结果摘要和时间；request/operation ID
建立唯一约束。正常终态 release 使用 run/node/action 派生的确定性 ID。记录状态只允许
`Prepared -> Succeeded/Rejected/Unknown`，进入写调用后任何异常都记为 `Unknown`；同 ID 重放
只返回已存结果，不再调用 Adapter；同 ID 但 fingerprint 不同则返回 `409`。进程启动时残留的
`Prepared` 一律提升为 `Unknown` 并等待人工核销，不恢复执行。AGV Task/acceptance cancel 继续
使用现有任务和 acceptance 审计，不复制第二套状态机。

策略提供两种明确判定：

- `RequireSupervisedExecution(operation)`：Simulator 直接保持现有行为；物理模式必须确认
  监督器已启用，否则抛出统一准入异常。
- `RejectUnboundPhysicalWrite(operation)`：Simulator 保持现有行为；物理模式的直接写入口
  因没有 workflow/field-acceptance epoch 授权而拒绝，即使监督器已启用也不能绕过。

统一异常携带稳定原因码 `physical_readiness_supervisor_disabled` 或
`physical_epoch_authorization_required`。工作流 API 继续使用现有
`WORKFLOW_PHYSICAL_EXECUTION_DISABLED` 作为顶层拒绝码，并在详细原因中保留上述稳定码。
普通 HTTP 写入口返回 `409 Conflict` 和 `{ code, detail }`；不得把配置阻断伪装成设备离线、
超时或 `500`。

原因优先级固定：Simulator 直接旁路；物理模式且监督器关闭时返回
`physical_readiness_supervisor_disabled`；监督器开启但入口没有 epoch 关联时返回
`physical_epoch_authorization_required`；已有绑定但实例、epoch 或 Ready 状态无效时返回现有
状态层原因。相同条件不得因入口不同返回不同类别。

策略只负责“是否允许进入物理执行链”。进入后仍由现有状态存储、工作流授权和 worker 在
真正写入前校验 `SupervisorInstanceId`、`DeviceEpoch`、Ready、许可有效期和操作关联。
对已持久化请求的完全一致幂等重放只返回旧结果，不创建节点、不唤醒 worker、不访问 Adapter，
因此即使当前门禁关闭也允许读取；任何不同 payload 或可能继续执行的重放都按新请求门禁。

## 入口分类

| 入口/操作 | 监督器关闭 | 监督器开启但无 epoch 关联 | 当前 epoch 授权有效 | 说明 |
| --- | --- | --- | --- | --- |
| 无线采集、health、状态、地图、任务查询、AUBO readiness/catalog | 允许只读 | 允许只读 | 允许只读 | 不申请控制权、不写设备 |
| 工作流物理 Execute | 拒绝 | 拒绝 | 按现有模板及批量门禁继续 | 批量启动快照必须包含监督器 |
| 工作流 resume、成功核销 Unknown、可推进节点的 signal/manual-confirmation | 拒绝继续物理节点 | 拒绝继续物理节点 | 按原运行绑定复核后继续 | pause 和纯本地 cancel 仍允许；AGV pause 不是该语义 |
| field-navigation 授权/派发 | 拒绝 | 拒绝 | 按当前 acceptance/epoch 校验继续 | 授权与派发双重防御 |
| 通用 Task 派发、确认后续导航、Retry | 拒绝 | 拒绝 | 仍拒绝物理直派 | 物理模式必须走受监督工作流路径 |
| 通用 Task Recover、AGV/AUBO 状态核对 | 允许只读核销 | 允许只读核销 | 允许只读核销 | 可更新 MES 观测状态，不发设备命令 |
| AGV 直接 command、DO、AUBO handshake | 拒绝 | 拒绝 | 仍拒绝物理直写 | 这些 API 不承载 epoch 授权 |
| AUBO load/run | 拒绝 | 拒绝 | 仅允许关联的 workflow worker 路径 | 每个写入边界重新核验 |
| AGV cancel 已知 Task/acceptance | 允许显式安全收尾 | 允许显式安全收尾 | 允许显式安全收尾 | 保持既有配置、活动记录和 operator；零自动重试 |
| AGV release 已持有控制权 | 允许显式安全收尾 | 允许显式安全收尾 | 允许显式安全收尾或当前 run 终态一次性 release | 先只读确认 owner 且无活动任务；不主动获取控制权 |
| AUBO stop 已知程序 | 允许显式安全收尾 | 允许显式安全收尾 | 允许显式安全收尾 | fresh status 必须确认非终态程序；必须有 operator/operation 审计 |
| 跨进程启动恢复/worker 自动收尾 | 拒绝写入并转 Unknown | 拒绝写入并转 Unknown | 只读核对且不发新命令 | 恢复路径不得自动 release/stop/cancel |

`read-only-preflight` Adapter 模式继续阻断包括安全收尾在内的所有写入；上述例外只描述
standard Adapter 会话中 MES 的显式人工安全动作，以及当前关联 run 正向完成时的一次性 release。

## 数据流

启动或继续类操作按以下顺序执行：

1. WPF 或 API 提交请求。
2. 先识别完全一致且不会唤醒执行的持久化幂等重放；命中时只返回原结果。
3. MES 统一准入策略判断 Simulator/physical、监督器是否启用以及入口是否承载 epoch 关联。
4. 不满足时返回稳定拒绝码，Adapter 调用计数必须保持为零；会触发派发的 Task/工作流操作还
   必须在改变本地任务状态、消费许可或增加 retry/attempt 前拒绝。
5. 满足时，MES 将授权绑定到当前 `SupervisorInstanceId` 和所有相关设备 epoch。
6. worker 在 AGV 导航、AUBO `load`、AUBO `run` 等每个写入边界前重新读取当前状态并复核。
7. 任一状态变化都停止后续写入；已越过写入边界而结果不明时进入 `Unknown`，不自动重发。

显式安全收尾先完成只读状态/owner 核对，再进入既有任务或操作关联检查，不创建新授权、
不让设备进入 Ready，也不解除工作流的 `Unknown`；收尾结果仍需单独核销。正常运行的最终
Move release 只有在当前 `SupervisorInstanceId`/epoch 仍匹配且该到达由本进程正向执行观察到
时才允许一次，尝试和结果写入工作流审计；重启恢复路径即使观察到终态也不得复用该分支。
人工 release 通过独立的安全收尾请求进入，不伪造成工作流自动完成。

## 接入点

### MES 启动组合

`Program.cs` 计算 `WorkflowPhysicalBatchAdmissionGate` 时加入监督器启用条件，并给出精确的
缺失开关原因。该门禁只拒绝物理 Execute，不阻止 MES 启动。

### 服务层

- `WorkflowApplicationService`：物理执行绑定前检查统一策略；只有当前监督器可以创建新绑定。
- `WorkflowRunControlService`/`WorkflowAdvancedRuntime`：物理 run 的 resume、
  `ConfirmedSucceeded` Unknown 核销，以及会推进到后续设备节点的 signal/manual-confirmation，
  必须复核原 run 的 supervisor/epoch；pause 和不会发设备命令的本地 cancel 保持可用。
- `FieldNavigationAcceptanceService`：Authorize 与 Dispatch 两处检查；监督器关闭时不得产生
  无 epoch 的 Authorized 记录。physical 下 cancel 必须提供非空 operator，并只允许取消已有
  活动/Unknown acceptance。
- `TaskService`：物理模式下通用 Dispatch、Retry 和确认后触发的下一段导航统一拒绝；取消
  已知活动任务保持安全收尾语义且要求非空 operator；门禁必须早于 Task 状态迁移。
- `AgvAuboSequenceService`：该旧入口不承载 epoch 关联，物理模式拒绝启动；Simulator 不变。
- `ExperimentCompositeRuntimeService`：纯本地准备/核销不受影响；若未来允许物理复合 worker，
  其每个子工作流仍调用相同 Execute 门禁，不能复制或伪造 epoch。

### HTTP 直接入口

- AGV command、DO 和 AUBO handshake 在物理模式统一拒绝未绑定写入。
- AUBO load/run 必须具备有效 workflow correlation 和当前设备 epoch。
- AUBO stop 作为显式安全收尾：先 fresh GET 确认设备在线、loaded program 非空且 runtime
  是 `Running/Retracting/Pausing/Paused/Stepping/Aborting` 之一，物理模式还必须由调用方显式
  提供非空 operation ID；再保留 operator/operation/correlation 结构化审计，未提供 reason 时
  记录固定原因 `operator_requested_safety_stop`。若带 workflow correlation，只校验它确实指向
  同设备的已有运行中/Unknown 操作，不复用会拒绝 Unknown 的 load/run 校验器。任何读失败都
  不写，且后台恢复不得调用。
- 新增 MES `POST /api/agvs/{agvId}/control/release` 安全收尾入口，请求必须包含唯一
  `requestId`、`operatorName` 和 `reason`，可带 `workflowRunId`。入口先通过只读 preflight
  确认路由 AGV 与观测身份一致、控制 owner 是当前配置批准的 Adapter 且 AGV 无活动任务，再
  执行一次 release；读失败、身份/owner 不匹配或有活动任务均返回 `409` 且不写。相同
  `requestId` 只返回已记录结果，绝不重发 release。正常终态 release 可额外接受“观测任务就是
  当前 acceptance 且其终态已确认”的情形，但不得接受无关或非终态活动任务。
- AGV 正常终态 release 仍是 worker 内部的当前 run 关联动作，与人工 release 共用一次性执行
  和审计组件；二者都不提供获取控制权能力。
- 只读 GET 和 readiness refresh 不接入写入门禁。

### 后台 worker 与恢复

- `WorkflowFieldNavigationWorker` 和 `WorkflowAuboProgramWorker` 不再把“监督器不存在或关闭”
  解释为通过。
- 新节点保持 Ready/Blocked 并记录稳定阻断原因；进程重启恢复只可做只读核对，旧物理节点进入
  `Unknown` 和人工核销，不派发、不 load/run、不自动 release/stop/cancel。
- 监督器启用且 epoch 有效时，现有暂态重试窗口保持不变；不会新增写请求重试。

## 错误与审计

所有门禁拒绝至少记录：operation、runtime mode、supervisor enabled、workflow run/node/operation
标识（若有）、reason code、时间。不得记录管理员密码、无线凭证或许可全文。

稳定原因：

- `physical_readiness_supervisor_disabled`：物理执行需要监督器但当前关闭。
- `physical_epoch_authorization_required`：入口不承载当前监督器实例和设备 epoch。
- 现有 `readiness_supervisor_instance_mismatch`、`device_epoch_mismatch`、
  `device_not_ready` 等继续由状态层返回。

门禁异常必须发生在第一个 Adapter/设备调用之前。安全收尾出现超时或连接中断时保持
`Unknown`，不得自动再次发送。

## 测试设计

1. 配置矩阵：Simulator/physical × supervisor on/off × worker on/off；确认只读服务始终可用，
   物理批量仅在完整组合下启用。
2. 每个启动/继续入口在监督器关闭时返回稳定拒绝，并断言 fake Adapter 的写调用为零。
3. 监督器开启但缺少 correlation、实例 ID 或 epoch 时仍拒绝，写调用为零。
4. 当前实例和 epoch 有效的批准工作流仍能到达 fake Adapter；既有模板、许可和单飞规则不变。
5. 工作流 resume、成功核销 Unknown、signal/manual-confirmation 推进物理节点时复核原绑定；
   pause、本地 cancel 和完全一致的只读幂等重放不访问 Adapter。
6. 通用 Task 的 Dispatch/Retry/ConfirmPickup 被拒绝时不提前改变状态、许可、retry 或 attempt。
7. 断电、观测过期、身份/地图变化和 MES 重启后，旧授权不能继续写入。
8. 显式 AGV cancel/AUBO stop 与当前实例正常终态的一次性 release 保持可调用；启动恢复不能
   借用这些例外；pre-read 失败不写，所有写请求均验证零自动重试。
9. 通用 Task、直接 AGV/AUBO API 和旧 sequence 在 physical 下不能成为旁路；Simulator
   回归保持通过。
10. 运行定向 MES/WPF/Adapter 测试、全量 `dotnet test MesControlAgv.sln -m:1` 和 Release 构建。

所有测试只使用内存 fake、回环 HTTP/TCP 或本地数据库；设备保持断电，不访问
`192.168.1.2`、`192.168.1.102` 或任何现场端口。

## 完成判据

1. 监督器关闭时，MES/WPF 可启动且只读 API 可用；所有启动/继续类物理写入口在 Adapter
   调用前确定性拒绝。
2. 任何可执行的新物理流程都绑定当前监督器实例和相关设备 epoch，并在每个写入边界复核。
3. 只有显式人工 cancel/stop 和当前监督器实例内正常终态的一次性 release 保留安全收尾能力，
   后台恢复无法自动调用这些例外。
4. 门禁拒绝响应和对应审计均含稳定原因码，且不包含凭证。
5. 定向、全量和 Release 门禁通过，现场设备仍未被访问。
