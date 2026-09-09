# 物理设备就绪监督模块

## 目的

MES 进程常开时，监督器持续对已配置的物理设备执行只读观测。它把 AGV、AUBO 及未来设备的在线状态、位置、活动任务、控制权、安全事实和地图指纹汇总为一个状态面，并处理设备断电/重新上电后的重新验证。

监督器不包含任何设备写入能力：不申请 AGV 控制权、不派发导航、不写 DI/DO、不执行 AUBO `load/run/stop/abort`，也不触碰 Modbus。

## 状态与代次

每台设备有一个单调递增的 `DeviceEpoch`：

- 首次观测、在线/离线切换、可信观测丢失、设备身份或地图指纹变化都会产生新的代次。
- 离线或重新上线后，旧的 Ready 状态和旧代次授权立即失效。
- 已授权流程中的活动任务或传感器临时阻挡只会暂时进入 `Blocked`，不会切换代次；阻挡清除并重新稳定后，同一有效授权可以继续后续步骤，不会重发已经越过写入边界的命令。
- 重新上线先进入 `Stabilizing`，连续满足稳定窗口并完成完整只读预检后才可进入 `Ready`。
- 新授权必须同时绑定当前 `SupervisorInstanceId` 和设备代次；MES 进程重启或设备代次变化后，旧授权都不会被自动迁移或续期。

状态值为 `Unknown`、`Offline`、`Stabilizing`、`Ready`、`Blocked`、`Degraded`。`Ready` 只表示当前只读事实满足监督器条件；`SchedulingPermitted` 还要求当前代次已经被一次新的授权明确确认。

已有写入结果不明的操作仍按原规则处理：只读对账，标记 `Unknown`，不自动重发。

## 默认与启用

配置节为 `PhysicalReadinessSupervisor`，默认 `Enabled=false`。这是有意的安全默认值，避免无线只读采集或 Simulator 会话自动连接现场控制器。Simulator 配置即使把该开关设为 true，也不会启动物理 Probe。

在现场明确进入标准物理会话后，才在 MES 的配置或进程环境中显式打开：

```powershell
$env:PhysicalReadinessSupervisor__Enabled = 'true'
```

也可以在本次物理服务使用的 `appsettings.PhysicalAcceptance.json` 中设置：

```json
"PhysicalReadinessSupervisor": {
  "Enabled": true,
  "PollInterval": "00:00:02",
  "ReadyStabilityWindow": "00:00:05",
  "FullPreflightInterval": "00:00:30",
  "ObservationStaleAfter": "00:00:10",
  "RequireFullPreflightForReady": true
}
```

启用前仍须使用现场最新网络和设备证据核对 Adapter 配置。配置本身不推断 SSID、IP、Bridge、NAT 或端口，也不会替代现场授权。

## 只读 API

```text
GET  /api/physical/readiness
POST /api/physical/readiness/refresh?forceFull=true
```

两个接口都只返回监督器快照。`POST` 只是要求立即重新执行只读 Probe，不是控制命令。

关键字段：

- `devices[].state`、`devices[].deviceEpoch`
- `supervisorInstanceId`（与授权中的实例身份一并校验）
- `devices[].currentStationId`、`devices[].activeTaskId`、`devices[].controlOwner`
- `devices[].mapName`、`devices[].mapVersion`、`devices[].mapMd5`
- `devices[].blockingReasons`、`devices[].lastFullPreflightAtUtc`
- `schedulingPermitted` 和聚合 `blockingReasons`

WPF 就绪页会显示设备列表、代次、位置、活动任务、重新授权标记和阻断原因。主界面周期刷新只读取 MES 聚合快照，不直接扫描现场端口。

## 轮询策略

- 启动时先做一次完整只读观测。
- 常规轮询读取在线、位置、活动任务、控制权和已知安全事实。
- 启动、离线→在线、身份/地图变化或完整预检过期时执行完整预检。
- `automatic_dispatch_disabled` 和预控制阶段尚未持有 Adapter 控制权属于独立写入策略，不会被误判为设备故障；实际派发仍必须通过工作流、验收单和当前代次门禁。
- 任一 Probe 异常会使对应设备进入 `Degraded`/`Unknown`，不保留可写入的旧 Ready。
- 超过 `ObservationStaleAfter` 未得到新观测时，即使最后一次结果为 Ready，也会自动失效并切换代次。
- 多设备按设备身份隔离；没有对应 Probe 的设备保持不可调度，直到注册经过审核的只读 Probe。
- AUBO 工作节点在 `load` 和 `run` 写入边界前分别重新核对授权有效期、`SupervisorInstanceId` 和设备代次；任一变化都停止且不发送该次写入。

## 现场验证边界

无线只读采集仍按现场交接文档执行：人工切换到 AMR，采集完成后切回 SHINE 继续对话。监督器是 MES 运行期的状态与恢复门禁，不替代首次网络拓扑、AGV `19204` 状态端口或 AUBO WebSocket `9012` 的独立证据，也不授权标准流程自动启动。

## 2026-09-08 离线加固检查点

- 身份和地图指纹现在只由 `IsFullPreflight=true` 的权威观测更新；普通轮询继续更新位置、活动任务、控制权和安全事实，但不会因缺少地图字段造成 `DeviceEpoch` 抖动或清除授权。
- AGV 幂等只读 API 遇到 EOF、`IOException` 或 `SocketException` 时，在同一个总超时内最多重连重读一次并记录审计；控制权、导航、暂停/恢复/取消、Push 和 I/O 写入仍为零自动重试。
- `Invoke-PhysicalReadOnlyPreflight.ps1` 已把“启动 Adapter→核对 health→完整只读采集→按本次状态文件停止”固化为单次会话。成功和注入失败的本机假 TCP 回放均确认命令/Other 端口零请求且无遗留 PID。
- 现场导航 worker 在进程重启恢复旧 Move 前重新校验监督器实例和 AGV 设备代次；绑定过期时只记录 `Unknown` 并要求人工核销，不释放控制权或重新派发。
- Task 5 的已完成定向离线回归为 MES `224/224`、WPF（`--no-build`）`411/411`、WorkflowContract `71/71`、Simulator `5/5`；原始解决方案全量命令实际因 WPF Host 锁定 Debug DLL 退出 `1`，不能宣称全量通过。Release 构建 `0` 警告、`0` 错误。该结果不替代下次上电实证。
- 下次设备上电必须使用新 RunId、新隔离数据库、新 `SupervisorInstanceId`/设备 epoch 和新授权；断电前作废的许可、旧工作流执行、请求 ID 与证据不得复用。

## 2026-09-09 Task 5 离线交接

本轮仅完成离线加固与回归验证，设备保持断电且未访问。下次上电仍须执行新鲜的只读预检，并取得新的明确授权；本轮验证结果不构成现场运行或无线全流程证据。
