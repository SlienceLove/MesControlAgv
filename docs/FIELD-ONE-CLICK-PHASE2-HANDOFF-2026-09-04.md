# 现场一键全流程：第二阶段交接

更新时间：2026-09-04（Asia/Shanghai）

## 第二阶段目标

使用第一阶段修复后的最新 Release，在新的现场授权下完成一次完整九节点流程（Start、4 个 Move、3 个 AUBO Program、End），并证明取消/断线收口、程序目录读取和最终控制权释放符合预期。

目标顺序：

`Start（AGV 位于 LM1） -> LM7 -> 取料盘 -> LM2 -> 放料盘 -> LM7 -> 回收料盘 -> LM1 -> End（Completed）`

## 会话起点

- 控制器和设备已断电。
- 现场网线已拔。
- WPF、MES、Adapter 已停止；`5141/5145` 无监听。
- 上一运行 `a6a6e311-616a-466e-9dbf-bb208d057973` 已终止为 `Cancelled`，不得恢复或重用。
- 上一验收单、request/correlation ID 和 `material-batch` 许可不得重用。
- 第一阶段 Release 门禁记录的 Git 基线为 `b178a71`；工作树仍含第一阶段实现及用户现场证据的未提交改动，不得 reset、checkout 或清理。
- Release 门禁已通过：963 passed / 5 allowed skipped / 0 failed。

## 新会话首先阅读

1. [第一阶段收尾记录](FIELD-ONE-CLICK-PHASE1-CLOSEOUT-2026-09-04.md)
2. [当前进度](PROGRESS.md)
3. [现场一键韧性记录](../artifacts/physical-one-click-resilience-20260903.md)
4. [现场标准流程检查单](../artifacts/field-standard-workflow-runbook-20260902.md)
5. [上一运行取消说明](../artifacts/physical-acceptance/material-wpf-20260903-cancelled.md)
6. [第一阶段 Release 门禁](../artifacts/mes-offline-release-gate-20260904-phase1.json)

## 第二阶段执行顺序

### A. 仍在断电断网时

1. 检查 `git status`，保留所有现有改动和现场证据。
2. 再执行一次 Release 构建、定向测试和离线门禁。
3. 从当前工作树输出新的隔离部署目录；不要覆盖上一现场包。
4. 确认 PhysicalAcceptance 仍为单车、通用自动派发关闭、Push 关闭、写权限只能由显式标准会话开启。

### B. 取得新现场授权后

1. 由现场人员上电、接网线；不要修改 IP、启用 Windows Bridge/ICS 或猜测设备地址。
2. 仅启动 `read-only-preflight`，采集新的 AGV/AUBO 证据。
3. AGV 必须在线、无活动任务、控制权为 `none`、地图指纹/站点/有向边匹配、无急停/阻挡/故障、重定位状态为 1、置信度不低于 0.90。
4. 标准模板要求 AGV 从 `LM1` 开始；若实际不在 LM1，由现场授权人员人工处理，不能用未确认的自动重定位接口。
5. AUBO 必须为 `Running / Normal / Automatic / Stopped`；15 秒目录扫描必须完整返回，并逐字确认 `取料盘`、`放料盘`、`回收料盘`。
6. 停止只读进程，再用新数据库、新日志、新 RunId 启动 standard Adapter、MES worker 和 physical WPF。
7. 填写真实操作员、安全监护人、新许可前缀和有效期；确认作业区无人、急停监护到位。
8. 只点击一次“一键现场执行”，立即记录 workflow execution ID；禁止再次点击或复用请求 ID。
9. 持续监控节点、验收单、设备操作和时间线，任何 `Unknown`、断线、急停或硬故障均停止并人工核销，不自动重发。

### C. 收尾验收

1. 九个节点全部成功，四段 Move 均有 arrived 证据，三个 AUBO 程序均有 load/run/terminal 证据。
2. AGV 回到 LM1，无活动任务；AUBO Runtime 为 Stopped。
3. 最终 Move 完成前只尝试一次 AGV 控制权释放；只读复核必须显示 owner=`none`。
4. 关闭 WPF、MES、Adapter，确认现场端口无监听并归档完整 JSON/日志/截图。

## 第二阶段验收标准

- Workflow 最终为 `Completed`，Start、4 个 Move、3 个 AUBO Program、End 共 9/9 节点终态一致。
- 4/4 AGV 验收单到站，3/3 AUBO 程序成功。
- 无重复 3066、无重复 load/run、无自动重放未知写入。
- 最终控制权释放已确认，而不是仅停止进程或记录 Unknown。
- 新增目录超时、释放异常边界和取消释放修复均取得现场证据。

## 新会话启动提示词

```text
继续现场一键全流程第二阶段。先完整阅读：
docs/FIELD-ONE-CLICK-PHASE2-HANDOFF-2026-09-04.md
docs/FIELD-ONE-CLICK-PHASE1-CLOSEOUT-2026-09-04.md
docs/PROGRESS.md

当前设备和控制器断电、网线已拔。先只做离线构建、测试和新部署包；不要连接设备。
保留脏工作树和所有现场证据，不要 reset/checkout/清理。
需要重新上电接线时再明确提醒我；现场阶段先只读预检，再申请新授权并只执行一次完整流程。
```
