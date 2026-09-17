# 2026-09-17 工作站单步骤正式业务链验证

## 结论

**通过。** 已完成一次由 WPF 操作员 Run admission 触发的样品工作站单步骤正式业务链。它在运行中被观察到，随后在 `2026-09-17T02:14:18.307331Z` 完成：Job、Schedule 和 WorkflowRun 为 Completed；节点、设备操作和唯一排程活动为 Succeeded；工作站租约已释放。无 Failed、Unknown、Retry、Cancel 或 TimedOut 终态/事件。

本归档只记录已发生的验收和只读复核，不新增运行时或产品/测试代码变更。

## 业务对象与运行身份

| 对象 | 身份 / 最终状态 |
| --- | --- |
| Plan | `c0841566-6040-40b1-9409-200a4bf36f15` v`1`，Published |
| Job | `3bfa15d6-77d4-4de9-9b5f-05db3c621b4e`，Completed |
| Schedule | `404aef92-bad4-4c3a-a164-e9bc17ee0606`，Completed |
| WPF admission request | `0a1ca92c-7a19-46fd-b501-600fccccdab7` |
| WorkflowRun | `eef3ad56-dead-4a35-a8e9-662d38c4e780`，Completed |
| Node | `98f78aa5-edce-4992-8f4a-88c5be8472d7`，Succeeded，attempt `1` |
| Device operation / activity | `202170a0-eb2a-94f9-d646-c2f6f5ede152`，Succeeded，attempt `1` |

工作流为已发布 v`2`，节点为 `sample-workstation.execute-existing-task`，设备为 `SAMPLE-WORKSTATION-01`，厂家已有任务为 `TEST-001`。

## 生命周期证据

- WPF admission 审计 `ExperimentJobAdmitted / Admitted`：`2026-09-17T02:11:49.0921907Z`；同一事务已建立一个工作站租约。
- Workflow 接受：`02:11:49.016073Z`；节点认领/Running：`02:11:49.620286Z`；设备操作 Accepted：`02:11:50.7563748Z`；Running 已观察：`02:11:52.8433461Z`。
- 在 `2026-09-17T02:13:24Z` 的只读观察中，Job、Workflow、节点和设备操作为 Running，Schedule 为 Admitted，活动租约存在；设备 `Running/1`、`TEST-001` Running、厂家码 `3 / ExperimentStarted`。这不是由旧 Completed 状态推断。
- 厂家完成观察：`02:14:18.2928729Z`；节点和操作成功完成：`02:14:18.3036037Z`；`WorkflowStepCompleted`：`02:14:18.3056354Z`。
- Job/Schedule 完成：`2026-09-17T02:14:18.307331Z`；完成审计 `02:14:18.312017Z` 记录 `releasedLeaseCount=1`。最终排程 GET 为 `activeLeases=[]`，且仅一条目标活动，状态 Succeeded。

设备操作列表 `Count=1`；时间线只有一次 `WorkflowStepClaimed` 和一次 Accepted→Running 序列。MES 进程日志对该 Run 的 Adapter 出站 `POST .../tasks/TEST-001/start` 只有一条（line `133232`）；Initialize、工作站 Stop 和 Cancel 匹配均为零。因此本次是恰好一次启动，而非依赖较长生命周期 Adapter 历史日志的推断。

## 自动化与构建证据

新增跨边界业务链用例的提交为 `055a966`；其测试宿主隔离修复为 `951ebbb`。两者均为测试改动：本次验收未发现、也未实施产品代码修复。

| 验证 | 结果 |
| --- | --- |
| 跨边界聚焦用例 | `1/1` 通过 |
| MES 全量测试 | `321/321` 通过 |
| 回归（WorkflowContract 73 + MES 321 + Adapter 38 + WPF 定向 43） | `475/475` 通过 |
| 隔离构建 | 20 个项目，0 警告，0 错误 |

## 边界与后续

- 本次只验证一个已有厂家任务的单步骤运行；没有把 Unknown 重写为 Failed 或 Completed。
- 多步骤实验实体运行、厂家任务表导入、动态任务创建和远程停止仍未实现。
- 当前样品条码结论仅为设计、尚未实施：扫码枪在仪器工作站 PC，中控 WPF 在另一台 PC；不使用扫码枪或 last-scan HTTP 回传。实时 `TrajectoryParameterDetails` 显示 `SourceBarCode` / `TargetData` 配置而 `SMTBarCode=null`。中控将采用页面级人工目视比较与“核对完成”（无弹窗），审计及 revision/hash 锁定，编辑即失效。厂家任务表上传格式/API 待交付；导入/准备不得隐式启动。详见 [设计](../superpowers/specs/2026-09-17-sample-task-manual-barcode-verification-design.md)（提交 `0d60bfe`）。

## 归档范围与可复核来源

已提交的本文档和交接文档保留了本次现场验收的主体 ID、状态和结果。详细的 Task 3–5 报告及原始 GET/日志材料保留在本次现场会话的本地、已忽略 SDD 工作区中；它们不会出现在新的检出中，也不是仓库已发布的工件。

- `.superpowers/sdd/2026-09-17-sample-workstation-single-step-business-chain/task-1-report.md`
- `.superpowers/sdd/2026-09-17-sample-workstation-single-step-business-chain/task-3-report.md`
- `.superpowers/sdd/2026-09-17-sample-workstation-single-step-business-chain/task-4-report.md`
- `.superpowers/sdd/2026-09-17-sample-workstation-single-step-business-chain/task-5-report.md`
- `.superpowers/sdd/2026-09-17-sample-workstation-single-step-business-chain/task-5-raw-evidence.md`
