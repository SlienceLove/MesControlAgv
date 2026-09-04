# 现场料盘标准流程运行前检查单

该检查单对应 WPF “料盘四段标准模板”，仅用于后续受监督的现场运行。现场已确认站点1=`LM7`、站点2=`LM2`；模板已经建立，但本次会话没有启动物理流程。

## 固定流程

| 顺序 | 类型 | 目标/程序 | 通过条件 |
| --- | --- | --- | --- |
| 1 | 前置条件 | AGV 位于原点 `LM1` | 位置、定位、急停、故障和控制权证据新鲜 |
| 2 | AGV Move | 站点1 `LM7` | 只读预检通过后，由操作员单独授权该段 |
| 3 | AUBO Program | `取料盘.pro` | 目录/允许列表逐字匹配，现场确认空载/工位安全 |
| 4 | AGV Move | 站点2 `LM2` | 路径在最新地图快照中有效，单段授权 |
| 5 | AUBO Program | `放料盘.pro` | 与当前控制器目录逐字匹配，单次启动并观察终态 |
| 6 | AGV Move | 返回站点1 `LM7` | 单段授权，异常时暂停并人工处置 |
| 7 | AUBO Program | `回收料盘.pro` | 与当前控制器目录逐字匹配，单次启动并观察终态 |
| 8 | AGV Move | 返回原点 `LM1` | 单段授权，确认最终停稳 |
| 9 | End | 流程完成 | WPF/MES 运行快照、节点证据和设备结果一致 |

## 开始实际运行前的硬门槛

1. 有线接口为 Up，现场控制地址和路由已确认；TCP 可达不能替代应用层预检。
2. 对 AGV 执行一次新鲜只读预检：当前站点为 `LM1`、定位置信度达到当前配置门槛 `0.90`、无急停/阻挡/故障，地图站点和有向路径与当前快照一致，控制权状态明确。
3. 对 AUBO 执行一次新鲜只读预检：设备身份正确，`Automatic / Normal / Stopped`，读取当前加载工程和程序目录；三个目标程序必须逐字匹配。任何一个程序缺失都停止。
4. 操作员确认工作区、料盘状态、夹具和急停监护；每一段 Move 和每一个 Program 单独确认，不自动重试、不跳过失败节点。
5. 在 WPF 完成模板站点/程序复核、保存、校验和发布；运行时观察监控页的节点状态、设备结果、错误和时间线。

## 异常处置

- AGV 状态变为故障、急停、阻挡、定位不可信或到站不确定：立即暂停，保留证据，人工处置；不要重发同一段。
- AUBO 加载/启动后终态不确定：标记 Unknown，保持现场安全状态，禁止自动重试或继续下一节点。
- 任一节点失败或取消：不把后续节点当作已执行；先确认设备已停稳，再由操作员决定是否重新建立新的授权运行。

当前 `PhysicalAcceptance`、AGV 自动派发和 `WorkflowAuboWorker` 仍是签入关闭状态。本检查单不构成设备控制授权。AGV 定位置信度门槛已按现场确认调整为 `0.90`，仍需由现场操作员手动调整并确认后，才能重新进行只读预检。

## 2026-09-02 只读预检记录

- AGV 证据：`artifacts/physical-acceptance/agv-field-readonly-preflight-20260902-material-flow-release-sync.json`
  （Release 输出已与当前现场 Profile 同步后取得）。AGV-01 在线、当前位置 `LM1`、无活动任务、
  控制权 `none`；定位置信度 `0.7541`，低于 `0.95` 门槛，`dispatchPermitted=false`。
- AUBO 证据：`artifacts/aubo-field-readonly-20260902-material-flow.json`。身份为 `rob1 / aubo_C5`，
  状态 `Running / Normal / Automatic / Stopped`；Dashboard 当前加载 `回收料盘`，预加载槽位为空。
- 两份预检均为只读，未执行程序加载、启动、运动、控制权申请或 AGV 派发。上述状态未满足现场运行门槛。

### 标准流程启动前最新复核

- AGV 置信度已恢复为 `0.9625`，超过当前门槛 `0.90`；当前位置 `LM1`，地图快照为现场 `9bd...`。
- AUBO 当前状态为 `Running / Normal / Automatic / Stopped`，但可用目录只有 `回收料盘`；
  `取料盘`、`放料盘` 尚未在目录/允许列表中，标准流程首节点不具备可执行条件。
- 现场已确认三个实际程序名；PhysicalAcceptance 与标准运行配置的人工确认允许列表现已登记
  `取料盘.pro`、`放料盘.pro`、`回收料盘.pro`。这不等同于控制器文件系统读取，实际加载时仍需由
  AUBO 控制器返回成功结果。
- 综合证据见 `artifacts/physical-acceptance/agv-aubo-readonly-preflight-20260902-standard-flow.json`；
  只读会话已停止，结论仍为 **NO-GO**。

### 现场确认允许列表后的复核

- AUBO 接口目录现返回三项：`取料盘`、`放料盘`、`回收料盘`；其中三项来自现场确认允许列表，
  当前控制器实际加载工程仍为 `回收料盘`，预加载槽位为空。允许列表不等同于文件系统目录，
  每次 load/run 仍以控制器返回结果为准。
- AGV 最新置信度为 `0.9675`，当前位置 `LM1`，`reloc_status=1`；只读会话的 control owner 和
  automatic dispatch disabled 仍是预期阻断。
- 证据见 `artifacts/physical-acceptance/agv-aubo-readonly-preflight-20260902-catalog-confirmed.json`；
  只读会话已停止，尚未进入标准写入会话。

最新启动前复核证据为 `artifacts/physical-acceptance/agv-aubo-readonly-preflight-20260902-start.json`；
它确认 AGV 置信度 `0.9675`，并确认三项程序允许列表。下一步仍需现场提供本次操作员、安全监护人
和唯一许可编号，再进入标准写入会话。

本次 WPF 运行中发现的目录刷新卡顿和列表裁切问题先记录不修复，见
`artifacts/wpf-standard-run-issues-20260902.md`；不得在运行中通过重复刷新或改布局规避。

### 门槛调整后的复核

- 已将 Adapter PhysicalAcceptance、Adapter FieldStandard 和 MES PhysicalAcceptance 的定位置信度门槛同步为 `0.90`，并完成 Release 构建。
- 新配置只读复核的 AGV 定位置信度为 `0.8957`，仍低于 `0.90`；`dispatchPermitted=false`，不能开始第一段移动。
- 复核会话已停止，未申请控制权、未派发 AGV、未调用 AUBO 写接口。
