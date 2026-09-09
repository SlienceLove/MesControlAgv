# AGV MES MVP Progress

Last updated: 2026-09-09

## 当前结论

- 分支：`feature/wpf-ui-layout-optimization`；当前 HEAD 为 `52c64ca`（物理准入拒绝持久化）。
- 最新离线验证：定向 `60/60`、MES `269 passed / 0 failed`、解决方案 `1151 total / 1146 passed / 5 skipped / 0 failed`；Release 构建 `0` 警告、`0` 错误，`git diff --check` 通过。
- 默认桌面启动为 `physical`，连接外部本地 Adapter/MES；Simulator 仍须显式设置 `WPF_RUNTIME_MODE=simulator`，不会被默认启动。
- 现场实体设备当前仍按 **NO-GO** 管理。此前只读网络/状态证据不等于本次上电后的运行授权；本轮离线验证未访问现场 IP/端口，也未执行 AGV/AUBO/Modbus/DI/DO 写入。

## 关键分支

### 1. 现场无线与设备就绪

- 网络源表：[广州盛瀚复合机器人.xls](../res/广州盛瀚复合机器人.xls)。历史现场记录确认过 `AMR`、总控 `192.168.1.11`、AGV `192.168.1.2`、AUBO `192.168.1.102`；网关、Bridge、NAT 不作猜测。
- 入口顺序：人工切换到 AMR，执行 [`Invoke-FieldWirelessReadOnlyCapture.ps1`](../scripts/Invoke-FieldWirelessReadOnlyCapture.ps1)，再执行 [`Invoke-PhysicalReadOnlyPreflight.ps1`](../scripts/Invoke-PhysicalReadOnlyPreflight.ps1)，完成后可切回 SHINE。
- `PhysicalReadinessSupervisor` 默认关闭；重新上电必须使用新 RunId、隔离数据库、`SupervisorInstanceId`/设备 epoch 和新授权。断电、身份/地图变化或观测失效时，旧 Ready 和授权失效。
- 最近一次现场只读证据：[`wireless-readonly-20260908-140540-db5c224c`](../artifacts/physical-acceptance/wireless-readonly-20260908-140540-db5c224c/wireless-readonly-evidence.json)；它不能替代下一次新鲜预检。

### 2. 一键物理流程与恢复

- WPF 已具备标准模板的一次性授权、Move/AUBO 节点关联和批量执行入口；操作员、安全监护人、唯一许可前缀和有效期仍为必填条件。
- 物理批量执行、自动许可、自动派发和常规取消默认关闭；重启恢复只读核对，无法确认时落 `Unknown` 并人工核销，不自动 release、重派或继续写入。
- AGV 只读断线最多在同一总超时内重读一次；任何写请求不自动重试。AUBO `load/run` 前重新核对当前授权、监督器实例和设备 epoch；`stop` 只接受持久化关联明确的 `Running/Unknown` 操作。

### 3. WPF/MES/Adapter 主线

- WPF 启动诊断、运行监控、工作流导入/发布、物理/模拟器来源标识和多设备就绪状态页已接入 MES 只读投影。
- Adapter 使用 `vendor-tcp`，控制权、超时、`Unknown`、地图指纹和审计均 fail-closed；Simulator 仅用于显式离线回归。
- 本地回归入口：[`LOCAL-VERIFICATION.md`](LOCAL-VERIFICATION.md)。不要用 `run-local.ps1` 代替物理现场会话。

### 4. ShineLab / D160+

- ShineLab 状态仅通过 MES 只读投影展示；D160+/SHA-18i 的串口、Modbus 和仪器写入继续关闭。
- `AppendDelta`/`Verified` 新批次仍需在控制电脑完成新鲜只读基线和一次性验证；历史证据、RunId、RequestId、许可和程序名不可复用。

## 最近一周主线（2026-09-03 至 2026-09-09）

| 日期 | 推进节点 | 结果/边界 |
| --- | --- | --- |
| 09-03 | 可靠性与异常边界 | 加固控制权、超时、断线、Unknown、取消和不重复派发；写入结果不明即停止并人工核销。 |
| 09-04 | 现场标准流程与工具链 | 完成历史新授权流程及只读工具链；服务已收尾停止，历史授权不可复用。 |
| 09-05 | 复合运行时离线推进 | 完成状态、持久化、子流程证据关联和 Simulator-only worker；未连接现场设备。 |
| 09-07 | 无线只读与默认启动 | 完成 AMR 接口/路由、AGV `19204`、AUBO `9012` 只读核验；默认切为 physical。 |
| 09-08 | 就绪监督与断电恢复加固 | 修复 epoch 抖动、只读 TCP 单次恢复、重启恢复门禁和预检收尾；现场全程零写入。 |
| 09-09 | 物理准入离线收尾 | 统一 admission、恢复路径 fail-closed、最终 Move release 审计及 epoch/实例绑定通过全量回归；未访问设备。 |

## 下一步

1. 推送当前已验证提交；现场上电后重新执行无线直读和 Adapter 权威只读预检。
2. 启动新的 MES/监督器实例，观察“完整预检→普通轮询→完整预检”周期，确认 epoch 稳定且设备达到 Ready。
3. 仅在现场重新输入管理员、安全监护人和新许可后，另行进行一次 standard 全流程；每一步保留新证据，异常或结果不明立即停止。

## 不可越过的边界

- 不猜测旧 IP、SSID、Bridge、NAT 或网关；不启用 Windows Bridge、ICS 或 NAT。
- 无线直读只访问现场明确核验的 AGV 状态端口 `19204` 和 AUBO WebSocket `9012`；不连接 AGV 命令端口 `19206`，不申请控制权、不派发、不移动、不写 I/O。
- 不执行 AUBO `load`、`run`、`stop`、`abort`、变量写入、DI/DO 或 Modbus；不连接 D160+/SHA-18i 写入面。
- 凭证不写入代码、配置库或证据；任何历史授权和执行记录不得直接复用。

## 关键入口

- [现场无线交接](FIELD-ONE-CLICK-WIRELESS-DEBUG-HANDOFF-2026-09-07.md)
- [AGV 网络/桥接手册](AGV-INTERNAL-SWITCH-BRIDGE-RUNBOOK-2026-08-31.md)
- [AUBO 只读手册](AUBO-FIELD-READONLY-RUNBOOK-2026-08-31.md)
- [物理设备就绪监督](PHYSICAL-DEVICE-READINESS-SUPERVISOR.md)
- [物理验收索引](physical-acceptance/README.md)
