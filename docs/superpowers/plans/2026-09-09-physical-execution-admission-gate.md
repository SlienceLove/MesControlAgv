# 物理执行统一准入门禁实施计划

> 对应规格：`docs/superpowers/specs/2026-09-09-physical-execution-admission-gate-design.md`
>
> 范围：设备保持断电，仅修改和验证本地代码、测试与文档；不访问现场 IP，不启动
> PhysicalAcceptance 服务，不连接 AGV 命令端口，不执行 AUBO/Modbus/DI/DO 写入。

## Task 1: 统一准入策略与启动组合

- [ ] 新增 `PhysicalExecutionAdmissionPolicy`、固定异常类型和
  `physical_epoch_authorization_required` 原因码；Simulator 保持现有行为。
- [ ] 将 `PhysicalReadinessSupervisor.Enabled` 纳入
  `WorkflowPhysicalBatchAdmissionGate` 启动快照，配置缺项只拒绝物理 Execute，不阻止 MES。
- [ ] 增加配置矩阵和异常映射测试，确认监督器关闭优先返回
  `physical_readiness_supervisor_disabled`，且不会访问 Adapter。

## Task 2: 启动、继续与直接写入口

- [ ] 在 `WorkflowApplicationService` 的新物理 Execute、run resume、成功核销 Unknown、
  可推进设备节点的 signal/manual-confirmation 前接入策略；完全一致的只读幂等重放保持可用。
- [ ] 在 `FieldNavigationAcceptanceService` 的 Authorize/Dispatch 接入策略；physical cancel
  要求 operator，且只允许已有活动或 Unknown acceptance。
- [ ] 在 `TaskService` 的 Dispatch/Retry/ConfirmPickup 及其他导航入口于本地状态迁移前拒绝
  physical 直派；Recover 和状态核对保持只读。
- [ ] 在 AGV command/DO、AUBO handshake、旧 `AgvAuboSequenceService` 入口拒绝未绑定的
  physical 写；AUBO load/run 继续只接受当前 workflow correlation/epoch。
- [ ] 为所有准入异常统一返回 `409 { code, detail }`，工作流 Execute 保留现有顶层拒绝码。

## Task 3: Worker 与重启恢复

- [ ] `WorkflowFieldNavigationWorker` 和 `WorkflowAuboProgramWorker` 在 physical supervisor
  关闭时不再视为通过；新节点不 claim、不 dispatch/load/run。
- [ ] 跨进程 recoverable 节点只做只读核对并进入 `Unknown`，不得自动
  cancel/release/stop；现有只读状态重试不扩大为写重试。
- [ ] 覆盖当前实例正向执行、旧实例恢复、设备 epoch 变化和 supervisor off 的调用计数测试。

## Task 4: 一次性安全收尾

- [ ] 新增 `PhysicalSafetyActionRecord`、唯一 fingerprint/request ID、状态机和启动时
  `Prepared -> Unknown` 核销。
- [ ] 新增 `PhysicalSafetyActionService` 与
  `POST /api/agvs/{agvId}/control/release`；先只读核对身份、owner 和活动任务，再单次 release。
- [ ] 将当前 run 最终 Move release 接入同一去重审计服务；仅正向当前实例允许，恢复路径禁用。
- [ ] 将 AUBO stop 接入安全动作服务：fresh status、显式 operation ID、operator、可选 Unknown
  correlation；写调用后异常记 Unknown，永不自动重发。
- [ ] 保持 AGV Task/acceptance cancel 的既有配置和审计，补齐 operator 校验与零重试测试。

## Task 5: 离线验证与交接

- [ ] 运行新增及受影响的 MES/WPF/Adapter 定向测试。
- [ ] 运行 `dotnet test MesControlAgv.sln -m:1`，确认无失败。
- [ ] 运行 Release 构建并确认零警告、零错误；运行 `git diff --check`。
- [ ] 更新必要的进度/交接说明，只提交本轮代码和文档，保留所有用户证据及删除项。

## 完成判据

1. supervisor off 时 MES/WPF 与只读 API 可用，所有 AGV/AUBO 启动/继续写入口在 Adapter 前拒绝。
2. 新物理执行只使用当前 supervisor instance/device epoch；重启恢复不发送任何收尾或继续命令。
3. cancel/release/stop 仅通过关联明确、一次性、可审计路径执行，任何不确定结果均为 Unknown。
4. Simulator 回归、全量测试和 Release 构建通过，现场设备始终未被访问。
