# 实验流程 G5 验收记录

> 状态：G5-A 已通过项目方验收；项目方已授权进入 G5-B。G5-C、G5-D 尚未开始。

日期：2026-08-21

## 1. 阶段边界

| 切片 | 责任 | 当前状态 |
| --- | --- | --- |
| G5-A | 方案、任务、排程条目、计划预留和运行租约的契约、持久化及只读投影 | 已通过验收 |
| G5-B | 方案/任务生命周期、人工排程、确定性预留冲突、阻塞原因和审计 | 进行中 |
| G5-C | 运行准入取租约及完成、取消、恢复失败后的释放 | 未开始 |
| G5-D | 独立 WPF 方案页、排程页、资源泳道和人工验收 | 未开始 |

## 2. G5-A 交付

提交：`b690289 feat(workflows): add G5 planning record foundation`

- 新增 `ExperimentPlan`、`ExperimentJob`、`ScheduleEntry`、
  `ResourceReservation`、`ResourceLease` 公共契约。
- `ExperimentPlan` 固定流程 ID/版本；`ExperimentJob` 同时固定方案版本和流程版本，
  后续方案修改不能改变既有任务引用。
- 计划预留与运行租约使用不同契约和表。预留只表达可调整的计划窗口，
  租约才表达某个 `WorkflowRun` 的真实互斥占用。
- 资源类型使用可扩展字符串标识；资源键按类型和 ID 做大小写无关规范化。
- 新增五张 SQLite 表，并为活动租约的规范化资源键建立唯一索引。
- 新增只读计划、任务和排程查询；排程投影包含计划预留与当前活动租约。

只读 API：

```text
GET /api/experiment-plans
GET /api/experiment-plans/{planId}/versions
GET /api/experiment-plans/{planId}/versions/{version}
GET /api/experiment-jobs?status={status}
GET /api/experiment-jobs/{jobId}
GET /api/schedule?from={time}&to={time}
```

## 3. 兼容与安全

- 升级只增加 `ExperimentPlans`、`ExperimentJobs`、`ScheduleEntries`、
  `ResourceReservations` 和 `WorkflowResourceLeases`，不修改既有表的语义。
- 不把 `TransportTask`、批量导入记录或既有 `WorkflowExecution` 回填为实验任务。
- 旧 `/api/tasks` 和 `/api/workflows/execute` 行为保持不变；排程器尚未接管运行准入。
- G5-A 没有创建、调整、排程、取租约或释放租约的公共写接口。
- 唯一索引提供最终数据库互斥边界，但租约获取事务和生命周期策略属于 G5-C。
- 未新增设备控制、串口访问、协议字段、实体设备命令或 CIC-D160+ 写入路径。

## 4. 自动化验证

Release 门禁于 2026-08-21 执行：

| 检查 | 结果 |
| --- | --- |
| `dotnet build MesControlAgv.sln --configuration Release --nologo` | 0 警告，0 错误 |
| Domain | 39/39 通过 |
| Workflow Contract | 57/57 通过 |
| MES | 97/97 通过 |
| WPF | 212/212 通过 |
| Adapter | 176/176 通过 |
| Instrument Gateway | 50/50 通过 |
| Simulator | 5/5 通过 |
| E2E | 19 通过，5 个既有用例跳过，0 失败 |
| 全方案合计 | **655 通过，5 跳过，0 失败** |

新增 7 个测试覆盖版本固定、预留/租约分离、资源键规范化、旧 G4 数据库原位升级、
只读 API 投影、时间范围校验，以及同一资源活动租约的数据库级双占用拒绝。

## 5. 验收与下一门禁

G5-A 没有新增可操作 UI 或运行行为，因此无需项目方执行实体设备或人工流程验收。
本阶段的确认点是契约边界、增量兼容策略和自动化证据。

项目方 G5-A 验收结论：**通过**。

项目方已授权进入 G5-B。G5-B 不得提前接入运行租约，不得由排程器推进流程节点，
并继续保持所有实体设备控制和 D160 写入路径关闭。
