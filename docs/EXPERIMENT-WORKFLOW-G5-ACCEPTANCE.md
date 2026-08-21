# 实验流程 G5 验收记录

> 状态：G5-A、G5-B 已通过项目方验收；项目方已授权进入 G5-C。G5-D 尚未开始。

日期：2026-08-21

## 1. 阶段边界

| 切片 | 责任 | 当前状态 |
| --- | --- | --- |
| G5-A | 方案、任务、排程条目、计划预留和运行租约的契约、持久化及只读投影 | 已通过验收 |
| G5-B | 方案/任务生命周期、人工排程、确定性预留冲突、阻塞原因和审计 | 已通过验收 |
| G5-C | 运行准入取租约及完成、取消、恢复失败后的释放 | 进行中 |
| G5-D | 独立 WPF 方案页、排程页、资源泳道和人工验收 | 未开始 |

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

## 4. 兼容与安全

- G5-A 数据库原位升级先建表、再补列、最后建索引；保留既有方案数据和非唯一任务排程索引。
- 新增校验快照、请求资源和排程审计存储，不回填或改写 `TransportTask`、批量导入记录及既有运行记录。
- 旧 `/api/tasks`、`/api/workflows/execute` 和工作流定义/发布行为保持不变。
- G5-B 自动化证明确认排程不会修改已发布流程，也不会创建 `WorkflowExecution`、
  `NodeExecution`、`DeviceOperation` 或 `ResourceLease`。
- 本切片没有运行准入、租约获取/释放、自动调度、WPF 排程页面、设备适配器调用、串口访问、
  协议字段、实体设备命令或 CIC-D160+ 写入路径。
- 回退到 G5-A 代码不会删除新增列和审计表；旧代码会忽略这些增量结构，已有 G5-B 数据应保留待恢复。

## 5. 自动化验证

Release 门禁于 2026-08-21 执行：

| 检查 | 结果 |
| --- | --- |
| `dotnet build MesControlAgv.sln --configuration Release --nologo` | 0 警告，0 错误 |
| G5 调度筛选测试 | 9/9 通过 |
| G5/G4 数据库升级测试 | 3/3 通过 |
| Domain | 39/39 通过 |
| Workflow Contract | 57/57 通过 |
| MES | 104/104 通过 |
| WPF | 212/212 通过 |
| Adapter | 176/176 通过 |
| Instrument Gateway | 50/50 通过 |
| Simulator | 5/5 通过 |
| E2E | 19 通过，5 个既有用例跳过，0 失败 |
| 全方案合计 | **662 通过，5 跳过，0 失败** |

G5-B 新增 7 个测试，覆盖完整方案/任务/排程生命周期、发布版本固定、幂等回放与请求 ID
复用拒绝、并发同资源冲突、半开边界、容量峰值、稳定阻塞原因、资源可用性、追加审计和旧库原位升级。

## 6. 验收与下一门禁

G5-B 没有新增 WPF 操作面或运行行为，无需连接实体设备。复核自动化证据可运行：

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj `
  --configuration Release --filter "FullyQualifiedName~ExperimentScheduling" --nologo
dotnet test MesControlAgv.sln --configuration Release --no-build --no-restore --nologo
```

预期分别为 9/9 通过，以及全方案 662 通过、5 个既有 E2E 跳过、0 失败。

项目方 G5-B 验收结论：**通过**。

项目方已授权进入 G5-C。G5-C 仅处理运行准入和租约生命周期；
WPF 方案/排程页面仍属于 G5-D，实体设备控制和 D160 写入仍保持关闭。
