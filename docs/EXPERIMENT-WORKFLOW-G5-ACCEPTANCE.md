# 实验流程 G5 验收记录

> 状态：G5-A、G5-B、G5-C 已通过项目方验收；G5-D 已获准开始。

日期：2026-08-24

## 1. 阶段边界

| 切片 | 责任 | 当前状态 |
| --- | --- | --- |
| G5-A | 方案、任务、排程条目、计划预留和运行租约的契约、持久化及只读投影 | 已通过验收 |
| G5-B | 方案/任务生命周期、人工排程、确定性预留冲突、阻塞原因和审计 | 已通过验收 |
| G5-C | 运行准入取租约及完成、取消、恢复失败后的释放 | 已通过验收 |
| G5-D | 独立 WPF 方案页、排程页、资源泳道和人工验收 | 进行中 |

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

## 5. 兼容与安全

- G5-A 数据库原位升级先建表、再补列、最后建索引；保留既有方案数据和非唯一任务排程索引。
- 新增校验快照、请求资源和排程审计存储，不回填或改写 `TransportTask`、批量导入记录及既有运行记录。
- 旧 `/api/tasks`、`/api/workflows/execute` 和工作流定义/发布行为保持不变。
- G5-B 排程命令仍不会创建 `WorkflowExecution`、`NodeExecution`、`DeviceOperation` 或
  `ResourceLease`；只有显式 G5-C 准入端点会创建运行与租约。
- G5-C 没有自动调度、WPF 排程页面、新设备适配器调用、串口访问、协议/寄存器字段、实体设备
  命令或 CIC-D160+ 写入路径。准入只准备现有运行记录，节点执行仍遵守既有 G4 边界。
- 回退到 G5-A 代码不会删除新增列和审计表；旧代码会忽略这些增量结构，已有 G5-B 数据应保留待恢复。

## 6. 自动化验证

Release 门禁于 2026-08-22 执行：

| 检查 | 结果 |
| --- | --- |
| `dotnet build MesControlAgv.sln --configuration Release --nologo` | 0 警告，0 错误 |
| G5-C 运行准入筛选测试 | 6/6 通过 |
| G5 调度筛选测试 | 9/9 通过 |
| G4 运行控制/持久化/恢复筛选测试 | 31/31 通过 |
| G5/G4 数据库升级测试 | 3/3 通过 |
| Domain | 39/39 通过 |
| Workflow Contract | 58/58 通过 |
| MES | 110/110 通过 |
| WPF | 212/212 通过 |
| Adapter | 176/176 通过 |
| Instrument Gateway | 50/50 通过 |
| Simulator | 5/5 通过 |
| E2E | 19 通过，5 个既有用例跳过，0 失败 |
| 全方案合计 | **669 通过，5 跳过，0 失败** |

G5-C 新增 7 个测试，覆盖成功准入与不可变关联、幂等重放、同资源竞争仅一个成功、运行拒绝
事务回滚、Paused/Unknown 保持租约、完成/失败/安全取消释放、恢复失败/终态残留释放、过期非终态
保留租约，以及旧直接运行不依赖实验任务。

## 7. 验收与下一门禁

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

项目方已授权进入 G5-D。G5-D 仅实现独立 WPF 方案/排程页面及人工排程操作；自动调度、
自动推进流程节点、实体设备控制和 D160 写入继续保持关闭。
