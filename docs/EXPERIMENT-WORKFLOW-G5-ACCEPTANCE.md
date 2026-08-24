# 实验流程 G5 验收记录

> 状态：G5-A、G5-B、G5-C、G5-D 及 G5 总体已通过项目方验收；项目方已授权进入 G6。

日期：2026-08-24

## 1. 阶段边界

| 切片 | 责任 | 当前状态 |
| --- | --- | --- |
| G5-A | 方案、任务、排程条目、计划预留和运行租约的契约、持久化及只读投影 | 已通过验收 |
| G5-B | 方案/任务生命周期、人工排程、确定性预留冲突、阻塞原因和审计 | 已通过验收 |
| G5-C | 运行准入取租约及完成、取消、恢复失败后的释放 | 已通过验收 |
| G5-D | 独立 WPF 方案页、排程页、资源泳道和人工验收 | 已通过项目验收 |

## 2. G5-A 基线

实现提交：`b690289 feat(workflows): add G5 planning record foundation`

- 建立方案、任务、排程条目、计划预留和运行租约的独立契约与 SQLite 表。
- 任务同时固定方案版本和已发布流程版本；计划预留与真实运行租约保持分离。
- 提供计划、任务和排程只读 API，不接管旧任务或直接工作流准入。

项目方 G5-A 验收结论：**通过**。

## 3. G5-B 交付

实现提交：`2e5ef8f feat(workflows): add G5 manual experiment scheduling`

### 3.1 方案与任务生命周期

- 支持方案草稿创建/更新、显式校验、发布及从已发布版本复制下一草稿。
- 更新已校验草稿会使旧校验失效；已发布版本不可原位修改。
- 仅允许从已发布方案创建任务。任务固定方案版本和流程版本，并合并方案默认参数与任务覆盖值。
- Profile、资源和流程版本问题使用稳定错误码；发布时重新校验，避免使用过期校验结果。

### 3.2 人工排程与资源可用性

- 支持排程、改期、撤排和取消，并保存计划时间、优先级及请求的具体资源。
- 冲突窗口使用半开区间 `[start, end)`；相邻窗口不冲突。
- 容量按查询窗口内的峰值并发预约计算，不把彼此错开的预约误算为同时占用。
- 阻塞排程保留请求资源与稳定阻塞原因，但不创建 `Planned` 预留；改期先释放旧预留。
- 资源目录只投影当前 Profile 中的 AGV、站点、仪器、机械臂和工作站身份、能力、启用状态及容量。
- 活动运行租约在可用性中可见，但 G5-B 不获取、释放或把它转换成计划预留。

### 3.3 幂等与审计

- 每个写请求必须携带 `RequestId`、操作者和理由。
- 相同 `RequestId` 与相同规范化载荷返回首次提交的结果；改变动作或载荷会得到冲突响应。
- 单 MES 进程内的写命令由统一互斥门串行化，使幂等检查、冲突检查、状态更新和审计一起提交。
- 追加式审计保存请求指纹、结果快照、操作者、理由、关联实体和可读资源键；业务 API 不提供更新或删除入口。

新增写入/查询 API：

```text
POST /api/experiment-plans
PUT  /api/experiment-plans/{planId}/versions/{version}/draft
POST /api/experiment-plans/{planId}/versions/{version}/validate
POST /api/experiment-plans/{planId}/versions/{version}/publish
POST /api/experiment-plans/{planId}/versions/{sourceVersion}/next-draft
POST /api/experiment-jobs
PUT  /api/experiment-jobs/{jobId}/schedule
POST /api/experiment-jobs/{jobId}/unschedule
POST /api/experiment-jobs/{jobId}/cancel
GET  /api/resources/availability
GET  /api/experiment-scheduling/audits
```

## 4. G5-C 交付

实现提交：`75655cd feat(workflows): add G5 runtime resource admission`

### 4.1 原子运行准入

- 新增 `POST /api/experiment-jobs/{jobId}/admit`，只接受具有唯一 `Scheduled` 排程条目、
  完整 `Planned` 预留、已发布方案版本和已发布固定流程版本的任务。
- 准入在同一 SQLite 事务内创建 `WorkflowExecution`、把计划预留转换为运行租约、固定
  `ExperimentJob.WorkflowRunId`、更新任务/排程状态并追加审计。
- `WorkflowResourceLeases.ActiveResourceKey` 唯一索引是最终数据库互斥点；同一具体资源同时
  只能存在一个活动租约。运行拒绝或租约冲突会回滚本次运行、节点记录和租约写入。
- 请求 ID、操作者和理由必填；同一规范化请求返回首次结果，改变任务、操作者或理由会返回
  `EXP-ADMISSION-REQUEST-ID-REUSED`。

### 4.2 租约生命周期

- 首次认领节点时任务从 `Admitted` 投影为 `Running`；流程完成、失败或安全取消时，任务、
  排程、租约和追加审计在同一次保存中更新。
- `Paused` 和未决 `Unknown` 均继续持有租约。`ExpiresAt` 是恢复/告警证据，不会在可能仍由
  非终态运行控制资源时触发自动释放。
- Unknown 被人工确认为成功后，仅在流程确实到达 `Completed` 时释放；确认为失败后按
  `Failed` 终态释放。任何路径都不会因 Unknown 自动重试或重发设备命令。

### 4.3 无设备恢复协调

- MES 启动时执行一次任务、运行和租约关联协调，不轮询、不调用 Adapter、不推进节点。
- 明确终态残留租约，以及缺失、拒绝、DryRun 或无有效准入证据的运行租约会释放并留审计；
  已接受的非终态运行即使超过 `ExpiresAt` 也保留租约。
- 旧 `/api/workflows/execute` 不要求实验任务，也不会为直接运行创建实验任务或资源租约。

## 5. G5-D 交付

实现提交：`27d63c8 feat(workflows): add G5 planning and scheduling UI`

### 5.1 独立 WPF 页面

- 新增独立“实验方案”页，提供方案目录、版本列表、结构化基础信息/物料/参数/资源要求编辑器、
  完整度问题列表，以及草稿保存、校验、发布和复制下一草稿版本操作。
- 完整度问题保留稳定代码、字段和资源；选中问题会切换到对应的基础信息、物料、参数或资源页签。
- 新增独立“任务排程”页，提供待排任务池、日期/时间窗口、资源负载、固定宽度时间轴、资源泳道、
  选中任务详情、阻塞原因和追加式审计。
- 流程设计、实验方案、任务排程和流程运行监控保持四个独立主导航页；计划时间没有进入流程画布。

### 5.2 人工作业闭环

- 方案页仅调用既有 G5 方案 API；排程页支持从已发布方案创建任务、人工排程/改期、撤排、取消和
  显式运行准入，所有写操作继续要求操作者和理由并使用独立请求 ID。
- 人工排程使用结构化日期、小时、分钟、预计时长、优先级和具体资源选择；阻塞结果仍保存稳定原因，
  不在 WPF 内猜测或覆盖 MES 的确定性冲突判断。
- 取消和运行准入具有明确确认；准入只调用既有 G5-C 端点，将计划预留转换为运行租约，不直接执行
  工作流、不推进节点，也不调用 AGV、Adapter 或仪器命令。

### 5.3 资源投影

- 计划排程、阻塞排程和活动运行租约使用不同的时间轴块；活动租约不会被显示成计划预留。
- 时间轴按所选窗口裁剪，泳道保持固定高度并支持重叠轨道；任务池仍保留窗口外任务的排程详情和
  阻塞解释，避免跨日期任务只显示“Blocked”而没有原因。
- 资源目录显示 Profile 名称、启用状态、容量、窗口峰值计划负载、可用容量及活动租约状态；未分配
  资源的排程使用独立泳道，不会从时间轴消失。

## 6. 兼容与安全

- G5-A 数据库原位升级先建表、再补列、最后建索引；保留既有方案数据和非唯一任务排程索引。
- 新增校验快照、请求资源和排程审计存储，不回填或改写 `TransportTask`、批量导入记录及既有运行记录。
- 旧 `/api/tasks`、`/api/workflows/execute` 和工作流定义/发布行为保持不变。
- G5-B 排程命令仍不会创建 `WorkflowExecution`、`NodeExecution`、`DeviceOperation` 或
  `ResourceLease`；只有显式 G5-C 准入端点会创建运行与租约。
- G5-C 没有自动调度、WPF 排程页面、新设备适配器调用、串口访问、协议/寄存器字段、实体设备
  命令或 CIC-D160+ 写入路径。准入只准备现有运行记录，节点执行仍遵守既有 G4 边界。
- G5-D 只扩展 WPF 和既有 G5 HTTP 客户端；没有修改 MES 排程、准入、数据库或设备执行语义，
  也没有新增自动优化、自动节点推进、设备适配器、串口、协议/寄存器字段或 D160 写入路径。
- 回退到 G5-A 代码不会删除新增列和审计表；旧代码会忽略这些增量结构，已有 G5-B 数据应保留待恢复。

## 7. 自动化验证

G5-D Release 门禁于 2026-08-24 执行：

| 检查 | 结果 |
| --- | --- |
| `dotnet build MesControlAgv.sln --configuration Release --nologo` | 0 警告，0 错误 |
| G5-D WPF/HTTP/投影/边界筛选测试 | 13/13 通过 |
| G5-C 运行准入筛选测试 | 6/6 通过 |
| G5 调度筛选测试 | 9/9 通过 |
| G4 运行控制/持久化/恢复筛选测试 | 31/31 通过 |
| G5/G4 数据库升级测试 | 3/3 通过 |
| Domain | 39/39 通过 |
| Workflow Contract | 58/58 通过 |
| MES | 110/110 通过 |
| WPF | 225/225 通过 |
| Adapter | 176/176 通过 |
| Instrument Gateway | 50/50 通过 |
| Simulator | 5/5 通过 |
| E2E | 19 通过，5 个既有用例跳过，0 失败 |
| 全方案合计 | **682 通过，5 跳过，0 失败** |

G5-C 新增 7 个测试，覆盖成功准入与不可变关联、幂等重放、同资源竞争仅一个成功、运行拒绝
事务回滚、Paused/Unknown 保持租约、完成/失败/安全取消释放、恢复失败/终态残留释放、过期非终态
保留租约，以及旧直接运行不依赖实验任务。

G5-D 新增 13 个测试，覆盖所有现有 G5 HTTP 路由与载荷、业务拒绝和稳定错误码、方案结构化草稿与
生命周期门禁、任务创建和人工排程/改期/撤排/取消/准入、阻塞原因、活动租约、泳道几何、页面绑定和
主导航分离。负向边界测试证明排程 ViewModel 不调用 `ExecuteWorkflowAsync` 或任何设备命令。

## 8. G5-D 验收与下一门禁

G5-D 可在 Simulator 模式验收，不需要连接实体设备：

1. 启动 WPF，进入“实验方案”；选择现有方案时，预期方案目录、版本、固定流程版本和完整度一致。
   新建草稿并填写操作者、理由、已发布流程版本及结构化物料/参数/资源要求，保存后预期出现 v1 草稿。
2. 点击“校验”；有问题时预期显示稳定代码、字段和原因，选择问题会切换到对应详情页签。修正并再次
   保存/校验后预期显示“校验通过”；点击“发布”后状态为“已发布”，且原版本编辑控件禁用。
3. 进入“任务排程”，从已发布方案创建样品任务；预期任务以“待排程”进入任务池。选择日期、时间、
   时长、优先级和资源后执行人工排程；预期任务离开待排池，并在对应资源泳道显示计划块和负载。
4. 创建第二个任务并把容量为 1 的同一资源排到重叠窗口；预期任务状态为“阻塞”，详情显示
   `EXP-RESOURCE-RESERVATION-CONFLICT`（或 MES 返回的其他确定性资源代码）、具体原因和冲突资源，
   时间轴显示阻塞块。改到空闲窗口后预期变为“已排程”；撤排后预期返回待排池。
5. 对“已排程”任务点击“运行准入”并确认；预期返回非空运行 ID，任务为“已准入”，计划预留被
   活动运行租约替代且时间轴使用独立租约样式。准入不会自动推进节点，也不会出现设备命令。
6. 切换“实验流程管理”“实验方案”“任务排程”“流程运行监控”；预期四页状态和职责独立，排程页
   不编辑流程图，运行监控页不修改方案或计划时间。

自动化复核命令：

```powershell
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj `
  --configuration Release --no-build --no-restore `
  --filter "FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentPlanManagementViewModelTests|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~ExperimentPlanningViewBindingTests" --nologo
dotnet test MesControlAgv.sln --configuration Release --no-build --no-restore --nologo
```

预期分别为 13/13 通过，以及全方案 682 通过、5 个既有 E2E 跳过、0 失败。

项目方于 2026-08-24 确认 G5-D 验收通过，并确认 G5 总体验收通过、授权进入 G6。

验收过程中追加的标题状态、任务池计数和资源图例对齐修正见提交
`858d3bd fix(workflows): align G5 planning page headers`；修正后 Release WPF 构建为 0 警告、0 错误，
`ExperimentPlanningViewBindingTests` 2/2 通过，并完成“实验方案”和“任务排程”两页截图复核。

进入 G6 不自动授权设备能力。自动调度、实体设备控制、串口访问、协议/寄存器字段和 CIC-D160+ 写入
继续保持关闭；G6 各子阶段仍需独立实现、自动化验证和项目验收。

### 8.1 已通过的 G5-C 验收留痕

G5-C 没有新增 WPF 操作面，无需连接实体设备。复核自动化证据可运行：

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj `
  --configuration Release --filter "FullyQualifiedName~ExperimentRuntimeAdmission" --nologo
dotnet test MesControlAgv.sln --configuration Release --no-build --no-restore --nologo
```

预期分别为 6/6 通过，以及全方案 669 通过、5 个既有 E2E 跳过、0 失败。

建议项目验收：

1. 通过 G5-B API 准备一个已发布方案、`Scheduled` 任务和具体资源预留，调用准入端点；预期
   HTTP 202、返回非空运行 GUID，任务/排程为 `Admitted`、计划预留为 `Released`、租约为 `Active`。
2. 使用相同请求 ID 和载荷重放；预期 HTTP 200 且 `isIdempotentReplay=true`。修改理由后重放；
   预期 HTTP 409 和 `EXP-ADMISSION-REQUEST-ID-REUSED`。
3. 预先排程两个不重叠但请求同一具体资源的任务，并在第一个仍运行时依次准入；预期仅一个
   成功，另一个返回 HTTP 409 和 `EXP-RESOURCE-LEASE-ACTIVE`，数据库只有一个活动资源键。
4. 对已准入的静止运行执行 Pause；预期租约保持活动。安全 Cancel 后预期任务/排程终止、
   `ActiveResourceKey` 清空并出现释放审计。
5. 将已认领节点记录为 Unknown；预期租约保持活动。人工确认失败，或确认成功并使流程真正
   完成后，预期租约释放；确认操作不产生第二条设备命令。

项目方 G5-C 验收结论：**通过**（2026-08-24）。
