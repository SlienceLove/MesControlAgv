# 现场一键全流程：无线调试验证交接

更新时间：2026-09-07（Asia/Shanghai）  
当前阶段：无线网络通信只读验证准备  
现场边界：本文件只准备下一会话；本会话未连接 AGV/机械臂、未申请现场授权、未发送设备写入或运动命令。

## 1. 当前软件与代码基线

- 分支：`feature/wpf-ui-layout-optimization`。
- 远端已同步到 `fb743fa`：
  - `c3610cb`：复合子流程设备活动与步骤关联；
  - `fccd190`：模拟器复合子流程生命周期协调；
  - `fb743fa`：进度、门禁和部署包记录。
- Release 构建：0 警告、0 错误。
- 最新离线门禁：[`artifacts/mes-offline-release-gate-20260905-composite-worker.json`](../artifacts/mes-offline-release-gate-20260905-composite-worker.json)，`1033 passed / 5 allowed skipped / 0 failed`，`releaseEligible=true`。
- 部署包：[`bin/Verify/PhysicalOneClickCompositeRuntimeWorker-final-20260905-145900.zip`](../bin/Verify/PhysicalOneClickCompositeRuntimeWorker-final-20260905-145900.zip)，校验值见同名 `.zip.sha256`。
- 复合模拟器 Worker 仅在 `Profile.Features.UseSimulator=true` 且显式配置启用时运行；`PhysicalAcceptance` 继续关闭该 Worker，不能把模拟器包当作现场包启用。

## 2. 下一会话目标

先证明“控制电脑通过无线网络能够分别、稳定、只读访问 AGV 和机械臂”，再决定是否进入现场一键流程。无线可达不等于设备可运行，也不等于现场 GO。

目标分三层：

1. 无线链路和二层/三层拓扑事实可解释：SSID、无线 AP/Bridge/Client 模式、控制电脑接口、地址、掩码、路由、AGV 地址和 AUBO 地址均由现场新鲜采集。
2. AGV 状态端口与 AUBO WebSocket JSON-RPC 只读预检通过，身份、模式、运行状态和错误状态前后一致。
3. MES/Adapter/WPF 只读代理显示与直读证据一致；只有随后取得新的明确授权，才允许另行执行一次完整流程。

## 3. 必须保留的边界

- 不得 `git reset`、`checkout`、`clean`、stash 或覆盖既有现场证据。
- 不复用上一轮 RunId、RequestId、验收单、许可前缀或旧的现场授权。
- 不启用 Windows Bridge/ICS，不临时改设备 IP，不猜测无线设备是透明 Bridge。
- 无线调试验证阶段不调用 AGV 命令/控制权端口，不发送移动、取消、恢复或 I/O；AGV 只读状态端口优先。
- AUBO 只读阶段不调用 `load`、`run`、`stop`、`abort`、`setInt32`、`setString` 或任何 DI/DO/Modbus 写入。
- 不因 ping 成功、TCP 端口打开或 WebSocket 建连成功而自动判定 GO。
- 任一身份、拓扑、模式、路由、端口或安全状态不确定，立即停在 NO-GO 并记录证据。

## 4. 下一会话启动顺序

### A. 离线与工作区检查

1. 重新阅读本文件、`docs/PROGRESS.md`、`docs/AGV-INTERNAL-SWITCH-BRIDGE-RUNBOOK-2026-08-31.md`、`docs/AUBO-FIELD-READONLY-RUNBOOK-2026-08-31.md`。
2. 执行新鲜只读 `git status --short --branch`、`git log -5 --oneline`；确认现有删除项、未跟踪证据和用户改动仍在。
3. 使用当前 Release 包和独立的新数据库/日志目录；不要覆盖上一轮部署目录或现场数据库。
4. 确认 MES/Adapter/WPF 当前均未运行，记录 `5141/5145` 及本次启动所需端口；无线预检期间不启动设备写入 Worker。

### B. 现场无线拓扑采集

由现场人员填写以下事实，不能沿用历史值：

| 项目 | 本次现场值 | 证据 |
|---|---|---|
| 控制电脑无线接口/SSID/BSSID | 待采集 | `Get-NetIPConfiguration` / 现场照片 |
| 控制电脑无线 IPv4/掩码/网关 | 待采集 | `Get-NetIPConfiguration` |
| 无线设备模式 | 待确认：透明 Bridge/AP 或 Router/NAT | 厂家/现场确认 |
| AGV 控制器 IPv4 | 待确认 | 设备只读页面/现场记录 |
| AGV 状态端口 | 待确认，历史批准值为 `19204` | 配置/只读预检 |
| AUBO IPv4 | 待确认，历史记录为 `192.168.1.102` | WebSocket 只读预检 |
| AUBO WebSocket 端口/路径 | 历史为 `9012 / /`，本次必须复核 | WebSocket 证据 |
| 是否存在第二条同网段路径 | 必须明确为否，或记录路由优先级 | `Get-NetRoute` |

若无线设备是 NAT/Client 隔离模式，不能仅把控制电脑改成 `192.168.1.x` 解决；必须先由现场/厂家确认透明 Bridge/AP 或提供明确、受控的路由方案。

### C. 新鲜只读网络预检

只在现场明确允许只读检查后执行：

1. `Get-NetAdapter`、`Get-NetIPConfiguration`、`Get-NetRoute`：记录活动接口、地址、掩码、默认路由和到 AGV/AUBO 的具体路由。
2. 对现场确认的 AGV 地址仅探测批准的状态端口；不探测/连接命令、控制权或 I/O 端口作为“连通性测试”。
3. 对现场确认的 AUBO 地址执行 `scripts/Invoke-AuboWsReadOnlyPreflight.ps1` 或带退避的只读脚本；只采集设备身份、RobotMode、SafetyMode、OperationalMode、Runtime、当前工程和 RPC 错误。
4. 记录每个目标的本地接口、远端地址、端口、时间、响应摘要和 `writesAttempted=false`；失败不自动重试写请求。
5. 物理控制网、无线网或路由发生变化时，重新执行整套预检，不沿用旧的“已连通”结论。

## 5. 无线只读验证通过条件

必须同时满足：

- 控制电脑无线接口和路由唯一、可解释，没有 Windows Bridge/ICS/NAT 猜测配置；
- AGV 身份、当前位置、活动任务、控制权、急停/阻挡/故障和定位状态可读，且控制权为 `none`、无活动任务；
- AUBO 身份与目标设备一致，`SafetyMode=Normal`，运行模式符合现场要求，验证阶段 `Runtime=Stopped`；
- AGV 状态证据、AUBO WebSocket 证据、Adapter/MES 代理证据和 WPF 显示一致；
- 没有任何 AGV 派发/控制权、AUBO load/run/stop、Modbus/DI/DO 或机械运动证据。

任何一项不满足，都只输出 NO-GO 及证据，不进入流程执行。

## 6. 取得新授权后的下一步

无线只读验证通过后，必须由用户在新会话中明确提供并确认：

- 操作员姓名；
- 安全监护人姓名；
- 新的许可前缀；
- 有效期；
- 本次唯一目标流程和允许的 AGV/AUBO 设备；
- 是否允许从只读验证进入一次完整流程。

授权后仍遵循：新数据库、新日志、新 RunId；只执行一次完整流程；不重复点击、不复用 RequestId；任何 `Unknown`、断线、急停、模式变化、控制权异常或结果不可证实时立即停止并人工核销。

## 7. 预期证据目录

下一会话建议使用新的目录，不覆盖历史文件：

```text
artifacts/physical-acceptance/wireless-debug-20260907/
  network-interface-preflight.json
  route-and-topology.json
  agv-readonly-preflight.json
  aubo-ws-readonly-preflight.json
  adapter-mes-readonly-proxy.json
  wpf-readonly-screen.png
  session-notes.md
```

若进入一次完整流程，再在同一新目录下增加 execution ID、请求/关联 ID、节点/设备操作时间线、最终状态和关闭验证；未执行的项目不要创建伪证据。

## 8. 新会话启动提示词

```text
继续现场一键全流程无线调试验证。先完整阅读：
docs/FIELD-ONE-CLICK-WIRELESS-DEBUG-HANDOFF-2026-09-07.md
docs/PROGRESS.md
docs/AGV-INTERNAL-SWITCH-BRIDGE-RUNBOOK-2026-08-31.md
docs/AUBO-FIELD-READONLY-RUNBOOK-2026-08-31.md

当前代码基线为远端 feature/wpf-ui-layout-optimization 的 fb743fa。
先做新鲜只读无线接口/路由/拓扑预检，再做 AGV 状态端口和 AUBO WebSocket 9012 只读预检；不要猜测旧 IP、SSID、Bridge 或 NAT，不要连接 AGV 命令端口，不要执行 AUBO load/run/stop，不要申请现场授权。
只有我明确提供新的操作员、监护人、许可前缀、有效期并确认进入一次完整流程后，才继续现场执行。
保留工作树、既有现场证据和未跟踪文件，禁止 reset/checkout/clean。
```
