# 现场一键全流程第二阶段验收证据

日期：2026-09-04（Asia/Shanghai）

## 运行结论

现场操作员确认：本次实验验证现场运作正常，无中途错误，可作为本阶段现场证据。

本次唯一受理的完整运行：

- Workflow execution：`937e061e-82c8-434e-ba38-8b9ac320a0c1`
- Request：`b572ebf6-a38e-4140-aad3-d1d24a71b529`
- Correlation：`wpf-physical-batch-16b6ae9899274f46a5ed9ae6ae10262e`
- Workflow：`65d9bd5e-73e8-49ae-9510-8541842637fb` / v1
- 操作员：`admin`
- 安全监护人：`admin`
- 许可前缀：`一键启动流程-0904`
- 许可有效期：60 分钟（`2026-09-04T03:07:09.3033613+00:00` 到期）
- MES 受理响应：HTTP 202，`isAccepted=true`
- Workflow 最终状态：`Completed`
- 运行时间：`2026-09-04T02:07:09.4374109+00:00` 至 `2026-09-04T02:19:12.2401645+00:00`

## 节点与设备证据

工作流定义为 9 个语义节点（Start、4 个 Move、3 个 AUBO Program、End）。运行 API 持久化 7 个可执行节点记录；Start/End 为运行边界节点，由受理和 Completed 终态表示。

| 顺序 | 节点 | 结果 | 现场结果 |
| --- | --- | --- | --- |
| 1 | `LM1 → LM7` | Succeeded | 验收单 `arrived` |
| 2 | `取料盘.pro` | Succeeded | load/run 后 Runtime `Stopped` |
| 3 | `LM7 → LM2` | Succeeded | 验收单 `arrived` |
| 4 | `放料盘.pro` | Succeeded | load/run 后 Runtime `Stopped` |
| 5 | `LM2 → LM7` | Succeeded | 验收单 `arrived` |
| 6 | `回收料盘.pro` | Succeeded | load/run 后 Runtime `Stopped` |
| 7 | `LM7 → LM1` | Succeeded | 验收单 `arrived` |

机器计数：

- 节点执行：7/7 `status=Succeeded`，全部 `attempt=1`，无错误。
- 设备操作：7/7 `status=Succeeded`，4 个 `agv.navigate-to-station`、3 个 `robot.execute-program`。
- AGV 验收单：4/4 `arrived`，许可后缀均为 `-a1`。
- 时间线：1 次 `WorkflowExecutionAccepted`、7 次节点准备、7 次认领、7 次设备操作更新、7 次步骤完成。

## 最终设备状态

- AGV：`online=true`、站点 `LM1`、`currentTaskId=null`、控制权 `none`。
- 最终 AGV 只读复核：无急停、无阻挡、故障计数 `0/0`、重定位 `1`、定位置信度 `0.9247`；地图权威证据仍为 `guangzhou606 / 1.0.6 / 9bd67a8b01f4da2617ce67e5f8a8d6b1`。
- AUBO：RobotMode `Running`、Safety `Normal`、Operational `Automatic`、Runtime `Stopped`。
- 最终 Move 前后仅有一次控制权释放请求；MES 日志记录 Adapter `/agv/control/release` HTTP 200，随后记录“Released Adapter AGV control”，最终 owner=`none`。

## 原始机器记录

- [最终后台验收 JSON](backend-final-verification-v2.json)
- [最终执行请求](execution-final/workflow-execution-request.json)
- [执行响应](execution-final/workflow-execution-response.json)
- [执行前最终只读复核](execution-attempt-3b/final-preflight.json)
- [工作流发布证据](workflow-correction-utf8-3/workflow-publication-evidence.json)
- [离线问题集中修复清单](phase2-followup-fixes.md)

## 偏差与后续修复

- 本次最终请求使用与 WPF“一键现场执行”相同的 MES 执行合同，并保证只提交一次；WPF 进程未自动收到该请求的 `LastExecution` 回调，因此界面未自动显示运行 ID。该 UI/操作入口问题已记录在 `phase2-followup-fixes.md`，不影响 MES worker 的现场执行和后台证据。
- 两次入口拒绝请求（HTTP 400、HTTP 422）均未受理运行、未触发设备动作，request ID 不复用。
- 现场运行期间未发现中途错误；严格的“整个会话无任何额外 AUBO load”仍需结合控制器侧审计核对，详见修复清单第 9 项。
