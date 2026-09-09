# AGV MES MVP Progress

Last updated: 2026-09-09

> 本文件只保留当前交接、关键分支和最近一周主线。更早的阶段记录、逐次会话
> 日志和完整验收细节保留在 Git 历史及下方链接的文档/证据中。

## 当前结论

- 当前分支 `feature/wpf-ui-layout-optimization`；Task 5 离线验证基线为
  `e29289c`，它是当前 HEAD `e4e1993` 的祖先；`e4e1993` 仅记录该轮
  文档交接，不应被误写成测试执行基线。早期交接中的 `794127f`/`fb743fa`
  不作为当前基线。
- 既有现场证据、用户删除项和无关工作树修改均保留，未被清理或覆盖。
- 默认桌面启动已改为 `physical`：WPF/Launcher 默认连接外部
  `127.0.0.1:5141`（Adapter）和 `127.0.0.1:5145`（MES），不托管或启动
  Simulator。显式 `WPF_RUNTIME_MODE=simulator` 仍保留离线回归路径
  `5041/5045/5183`。
- Task 5 最新离线验证：MES `224/224`、WPF（`--no-build`）`411/411`、
  WorkflowContract `71/71`、Simulator `5/5`；解决方案全量测试实际为
  `690 passed / 5 existing skipped / 0 failed`，但因并发 WPF Host 锁定 DLL
  退出 `1`，详见 Task 5 报告。Release 构建 `0` 警告、`0` 错误。
- 现场实体设备仍按 **NO-GO** 管理：无线可达、Ping、TCP 或 WebSocket 成功
  均不等于可以派发或运行。

## 关键分支

### 1. P0 现场无线 AGV/AUBO

- 网络源表：[广州盛瀚复合机器人.xls](../res/广州盛瀚复合机器人.xls)。已确认
  SSID `AMR`、总控 `192.168.1.11`、AGV `192.168.1.2`、AUBO
  `192.168.1.102`；现场实际使用 `/24`，未确认的网关/Bridge/NAT 不作推断。
- 最新无线直读证据：
  [`wireless-readonly-20260908-140540-db5c224c`](../artifacts/physical-acceptance/wireless-readonly-20260908-140540-db5c224c/wireless-readonly-evidence.json)。
  `WLAN 3=AMR`、`192.168.1.11/24`、无默认网关，AGV `19204` 和 AUBO
  WebSocket `9012` 均通过。
- 最新 Adapter 完整只读证据：
  [`adapter-readonly-20260908-continue-1445`](../artifacts/physical-acceptance/adapter-readonly-20260908-continue-1445/adapter-readonly-evidence.json)。
  AGV 最终观测为 `LM1`、无活动任务、控制权 `none`、定位状态 `1`、置信度
  `0.9566`、无急停/阻挡/故障；权威地图为 `guangzhou606 / 1.0.6 /
  9bd67a8b01f4da2617ce67e5f8a8d6b1`。AUBO 为
  `Running / Normal / Automatic / Stopped`，当前工程“运行模板”。
- 本次 `dispatchPermitted=false`，只剩 `adapter_does_not_hold_control` 和
  `automatic_dispatch_disabled` 两个预期写入门禁；仍不能据此直接派发。
- 另发现 AGV 空闲 TCP 连接被控制器主动关闭后出现 `SocketException 10054`；详见
  [`transport-reliability-issue.md`](../artifacts/physical-acceptance/adapter-readonly-20260908-continue-1445/transport-reliability-issue.md)。
- 随后 3 轮只读稳定性复核通过、未再出现 `10054`，证据见
  [`stability-evidence.json`](../artifacts/physical-acceptance/adapter-readonly-stability-20260908-continue-1540/stability-evidence.json)。离线代码已增加幂等只读请求的单次重连重读与审计；所有写请求保持零自动重试。
- standard 上线前验证发现 P0：AGV 完整/普通观测的身份字段完整度不同，导致
  `DeviceEpoch` 无真实变化仍持续递增并永久阻断调度；现场会话已在零写入状态停止，详见
  [`offline-hardening-checkpoint.md`](../artifacts/physical-acceptance/wireless-full-flow-20260908-continue-1615/offline-hardening-checkpoint.md)。
- 该 P0 已离线修复：只有完整预检更新权威身份/地图指纹，普通轮询不再造成 epoch
  抖动；真实完整预检差异仍只推进一次 epoch 并要求重新授权。
- 断电/MES 重启恢复也已加固：旧物理 Move 的监督器实例或 AGV epoch 缺失/不匹配时，
  节点和运行标记为 `Unknown` 并要求人工核销，不释放控制权、不重新派发。
- 入口脚本：先用 [`Invoke-FieldWirelessReadOnlyCapture.ps1`](../scripts/Invoke-FieldWirelessReadOnlyCapture.ps1)
  核验无线直读，再用 [`Invoke-PhysicalReadOnlyPreflight.ps1`](../scripts/Invoke-PhysicalReadOnlyPreflight.ps1)
  完成权威预检和自动收尾。两者都不授权派发。

### 2. 一键物理料盘流程

- WPF 已支持已发布标准模板的一次性现场授权和批量执行；UI 仍要求操作员、
  安全监护人、唯一许可前缀和有效分钟数，不把“凭证”降级为绕过安全门的密码字段。
- 默认 `WPF_ENABLE_PHYSICAL_BATCH=false`；MES 现场 worker、自动许可、自动派发和
  常规取消保持关闭，必须由受监督的新会话显式开启。
- 2026-09-04 曾完成一次历史标准流程，证据见
  [`phase2-field-acceptance-evidence-20260904.md`](../artifacts/physical-acceptance/phase2-std-20260904-093754/phase2-field-acceptance-evidence-20260904.md)。
  该 RunId、RequestId、验收单和许可不可复用。

### 3. WPF/MES/Adapter 主线

- WPF 启动诊断、运行监控、工作流导入/发布、物理与模拟器运行来源标识已完成；
  最近增加计划/实际/当前三层时间轴展示。
- 物理 Adapter 使用 `vendor-tcp`，读写边界、控制权、超时、Unknown 和审计均
  fail-closed；Simulator 只作为显式 `FieldSimulation` 离线工具。
- 本地回归入口：[`LOCAL-VERIFICATION.md`](LOCAL-VERIFICATION.md)；不要用
  `run-local.ps1` 代替 physical 现场会话。

### 4. 实验流程 / 复合运行时

- G5/G6 工作流、计划/排程和复合运行时的状态、持久化、子流程关联与恢复能力已在
  离线环境推进；复合 worker 只在显式 Simulator 配置下启用。
- PhysicalAcceptance 不自动创建或重试设备流程，Unknown 必须人工核销；现场 AGV、
  AUBO 未因该分支重新连接。
- 详见 [`EXPERIMENT-WORKFLOW-G6-ACCEPTANCE.md`](EXPERIMENT-WORKFLOW-G6-ACCEPTANCE.md)
  和 [`EXPERIMENT-WORKFLOW-ARCHITECTURE.md`](EXPERIMENT-WORKFLOW-ARCHITECTURE.md)。

### 5. ShineLab / D160+

- ShineLab CSV 追加导入已有历史真机成功记录；确定性 `AppendDelta`/`Verified`
  仍需在控制电脑完成新批次的只读基线和一次性验证，当前分支暂停。
- CIC-D160+ 及相关串口/Modbus 写入继续关闭；不得把 ShineLab 导入成功扩展为
  仪器运行授权。
- 详见 [`ION-CHROMATOGRAPHY-RPA-HANDOFF-2026-08-24.md`](ION-CHROMATOGRAPHY-RPA-HANDOFF-2026-08-24.md)
  和 [`ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md`](ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md)。

## 最近一周主线（2026-09-03 至 2026-09-09）

| 日期 | 推进节点 | 结果/边界 |
| --- | --- | --- |
| 08-31 | AUBO 现场协议分层确认 | 实际只读控制面为 WebSocket `9012`；确认 `rob1 / aubo_C5` 和状态读取路径，旧 `30004` 行协议不作为默认路径。 |
| 09-01 | AGV/AUBO/WPF 只读与流程接入 | 完成程序目录、状态投影、LM 站点映射和物理/模拟器边界；现场状态仍须以新鲜证据为准。 |
| 09-02 | 料盘标准模板与一键执行能力 | 完成一次性授权、Move/AUBO 节点关联和运行监控；默认物理写入门禁保持关闭。 |
| 09-03 | 可靠性与异常边界 | 加固控制权、超时、断线、Unknown、取消和不重复派发规则；设备写入结果不明时停止并人工核销。 |
| 09-04 | 现场标准流程与工具链 | 历史新授权流程完成；随后补齐 AUBO 目录缓存、UTF-8 导入工具、监控回放和写入 correlation，服务已收尾停止。 |
| 09-05 | 复合运行时离线推进 | 完成外层状态、持久化、子流程证据关联和 Simulator-only worker；未连接现场设备。 |
| 09-07 | 无线只读 + 默认启动调整 | AMR 无线接口、路由、AGV `19204`、AUBO `9012` 只读通信通过；完整 AGV 快照仍缺失。WPF/Launcher 默认切为 physical。 |
| 09-08 | 就绪监督门禁、现场预检与离线加固 | 双无线网卡完整只读通过；修复 epoch 抖动、只读 TCP 单次恢复、重启恢复门禁和预检进程收尾，`1069` 测试通过，现场全程零写入。 |
| 09-09 | Task 5 离线验证与交接 | 统一 admission、恢复路径 fail-closed、一次性安全收尾完成离线回归；WPF 并发 Host 锁导致解决方案全量命令未能完成，未停止 Host，未访问设备。 |

## 下一步

1. 下次上电后先用新 RunId、新隔离数据库重新执行无线直读和 Adapter 权威只读预检。
2. 启动新的 MES/监督器实例，观察至少一个“完整预检→普通轮询→完整预检”周期，确认
   epoch 稳定；任何真实断线、地图/身份变化仍须失效旧授权。
3. 只有设备 Ready、现场重新输入管理员/安全监护与新许可后，才另行开启一次 standard
   全流程；旧许可、请求和执行记录不得复用。

## 不可越过的边界

- 不猜测旧 IP、SSID、Bridge、NAT、网关或设备拓扑；不启用 Windows Bridge、ICS 或 NAT。
- 无线直读脚本只访问 AGV 状态端口 `19204` 和 AUBO WebSocket `9012`；完整
  Adapter 预检另使用厂商只读地图 API `1300/1301/1302/4011`。两者均不连接 AGV
  命令端口 `19206`，不申请控制权，不派发、不移动、不写 I/O。
- 不执行 AUBO `load`、`run`、`stop`、`abort`、变量写入、DI/DO 或 Modbus；不连接
  D160+/SHA-18i 的写入面。
- 任何历史 RunId、RequestId、验收单、许可和程序名都不能直接复用；凭证不写入代码、
  配置库或证据文件。

## 关键入口与证据

- 无线交接：[`FIELD-ONE-CLICK-WIRELESS-DEBUG-HANDOFF-2026-09-07.md`](FIELD-ONE-CLICK-WIRELESS-DEBUG-HANDOFF-2026-09-07.md)
- AGV 网络/桥接手册：[`AGV-INTERNAL-SWITCH-BRIDGE-RUNBOOK-2026-08-31.md`](AGV-INTERNAL-SWITCH-BRIDGE-RUNBOOK-2026-08-31.md)
- AUBO 只读手册：[`AUBO-FIELD-READONLY-RUNBOOK-2026-08-31.md`](AUBO-FIELD-READONLY-RUNBOOK-2026-08-31.md)
- 物理验收索引：[`physical-acceptance/README.md`](physical-acceptance/README.md)
- 最新离线门禁报告（本轮生成于系统临时目录）：`MesControlAgv-default-physical-gate-20260907.json`

## 2026-09-07/08 续接：物理设备就绪监督

- 新增只读 `PhysicalReadinessSupervisor`、线程安全状态存储和可扩展 Probe 边界；默认关闭，Simulator 不触发物理 Probe。
- 新增 `/api/physical/readiness` 与只读刷新接口；WPF 显示多设备状态、位置、活动任务、地图指纹、阻断原因和 `DeviceEpoch`。
- 离线/重新上线、可信观测丢失或身份/地图变化会切换代次并清除旧 Ready；重新上线需稳定窗口和完整预检，授权同时绑定监督器实例 ID，MES 重启后也不会误用旧 epoch。
- 物理工作流授权、现场导航许可和 AUBO/AGV 工作节点接入当前代次校验；旧代次不得继续写入，结果不明仍不得自动重发。
- AGV 活动任务和临时障碍只暂停新调度，不再误废弃当前工作流授权；安全物理配置中的“自动派发关闭/尚未持有控制权”不再阻止设备达到只读 Ready。
- AUBO 在 `load`、`run` 两个写入边界前分别重查授权和设备代次，避免等待期间断电或模式变化后继续下发。
- 离线加固后，普通轮询不再覆盖完整预检指纹；AGV 幂等只读断线最多自动重读一次，
  写请求仍不重发；新增权威只读预检脚本保证成功和失败路径都停止本次 Adapter。
- 现场导航 worker 在进程重启恢复旧记录前重新校验 `SupervisorInstanceId` 与
  `DeviceEpoch`；不匹配时只落 `Unknown`/人工核销，不触发 release 或导航写入。
- 当前仍保持 `PhysicalReadinessSupervisor.Enabled=false`、物理批量执行关闭；9 月 8 日仅完成
  Adapter 只读现场连接，未启动标准会话或执行物理流程。详见
  [`PHYSICAL-DEVICE-READINESS-SUPERVISOR.md`](PHYSICAL-DEVICE-READINESS-SUPERVISOR.md)。
