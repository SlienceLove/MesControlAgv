# 物理安全审查问题修复设计

日期：2026-09-09  
状态：设计已按现场联调约束确认，待书面复核

## 1. 背景与目标

当前基线已经具备物理就绪监督、统一物理执行准入和断电后失效机制，但代码审查仍发现四个需要在下一次现场联调前修复的问题：MES 直接启动时仍可能从默认配置进入 Simulator；最终 Move 的 release 结果可能被误判为工作流成功；授权确认只绑定设备 epoch 而未绑定监督器实例；运行时 descriptor 或监督器配置变化不会使旧授权失效。

本设计将这些问题收敛为一次离线、可测试的安全修复，目标是：

1. 物理模式成为 MES 直接启动的默认模式，Simulator 只能通过明确的 `FieldSimulation` 环境选择。
2. 只有最终 Move 明确得到 `Succeeded` 才能把工作流标记为成功；任何拒绝、超时、异常或无法判断的结果都进入 `Unknown` 并停止后续自动写入。
3. 授权同时绑定当前 `SupervisorInstanceId` 和设备 epoch，旧实例或旧 epoch 不能确认新授权。
4. descriptor 或监督器配置发生有效变化时，清除旧观测、预检状态和授权，并推进设备 epoch；设备移除后重新加入也不得复用旧的 epoch 数字。

## 2. 范围与明确边界

### 包含

- `src/MesControlAgv.Mes/appsettings.json` 的安全默认配置及相关环境选择契约。
- `WorkflowFieldNavigationWorker` 最终 Move、release 结果分类、持久化审计和工作流终态。
- `IPhysicalReadinessState`、监督器、状态存储、服务调用方及测试 fake 的授权签名和实例绑定。
- 状态存储对 descriptor/监督器配置指纹变化的处理。
- 配置契约、最终 release、授权绑定、重配置失效和 epoch 单调性的自动化测试。
- 必要的设计/进度文档和尾随空格清理。

### 不包含

- 不连接或扫描现场设备，不访问现场 IP 或端口。
- 不修改真实 IP、SSID、Bridge、NAT、网关或路由。
- 不连接 AGV 命令端口，不执行 AGV 移动/派发/控制权写入，不执行 AUBO `load`、`run`、`stop` 或其他写入。
- 不执行 Modbus、DI/DO、串口或仪器写入。
- 不改变已确认的物理批量执行、自动派发和监督器默认关闭策略。
- 不重构无关协议、UI 或数据库功能。

## 3. 设计决策

### 3.1 安全默认配置与环境隔离

MES 根配置改为物理安全默认值：`features.useSimulator=false`，AGV 默认驱动为 `vendor-tcp`，控制器地址只保留不可路由的占位符，不写入任何真实现场地址。物理监督器、自动派发、批量 worker 及其他会主动改变设备状态的开关继续按现有安全默认值关闭。

`FieldSimulation` 是唯一的 Simulator 选择入口；该环境显式加载 Simulator 配置并保留现有 Simulator 测试/回归行为。Production、Development 以及未指定环境不得因默认配置自动切入 Simulator。Adapter 层已有的 Simulator 默认值不在本次修改范围内，最终运行模式仍由 MES profile 和环境契约决定。

### 3.2 最终 Move 的三态结果

最终 Move 的判断结果采用三态：`Succeeded`、`Rejected`、`Unknown`。只有能明确证明设备已接受并完成最终 release 的 `Succeeded` 才允许工作流进入 `Succeeded`。

- release 明确拒绝、超时、抛出异常、返回无法识别的状态，或无法可靠判断当前节点是否为最终节点：结果为 `Unknown`（拒绝原因按审计上下文保留）。
- `Rejected` 仅表示 release 调用明确返回拒绝；它不能被转换成工作流成功。
- `Unknown`、`Rejected`、超时和异常均不自动重试、不继续后续设备写入；由现有人工核验/安全收尾流程处理。
- 记录实际 release 返回值、终态、异常/超时分类、workflow/run/node 上下文和关联标识，确保工作流状态与设备动作结果可独立审计。

实现可继续使用现有 release 审计模型；不新增第二套工作流状态机。`TryReleaseBatchControlOnceAsync` 等调用链必须显式返回或传递分类结果，调用方不得忽略 release 结果。

### 3.3 授权的监督器实例绑定

将授权确认接口扩展为同时接收 `expectedSupervisorInstanceId` 和 `expectedEpoch`。确认时必须精确匹配：设备、监督器实例、设备 epoch、授权状态及有效期均满足当前规则；任一项不匹配都返回失败且不改变授权状态。

监督器、准入策略、field-navigation、workflow 服务和测试 fake 全部使用同一签名。既有只按 epoch 调用的路径不得保留兼容旁路；Simulator 分支继续保持现有行为，但不能借此放宽物理授权。

### 3.4 descriptor/config 变化与 epoch 生命周期

状态存储为每个设备保存当前有效 descriptor/监督器配置指纹及 epoch 分配信息：

- 首次建立有效身份/配置时创建当前 epoch。
- 完整 descriptor 或监督器配置与已保存指纹发生有效变化时，单调推进该设备 epoch，清除旧观测、完整预检状态、Ready 状态和授权，并记录稳定原因码。
- 普通轮询缺少地图字段或只更新可变运行状态时，不把“不完整观测”当作 descriptor 变化。
- 设备从当前集合移除后再次加入，不复用历史 epoch 数字；epoch 分配信息须保留到足以保证单调性的持久化边界。
- 重配置、进程重启、断电恢复、观测过期和探针失败继续遵循现有 fail-closed 语义；旧授权不能跨越新的实例或 epoch。

配置变化的判定必须是确定性的，并在测试中覆盖“相同配置不变化、有效变化只推进一次、移除后重新加入不复用、旧授权被清除”四类行为。

## 4. 数据流与失败处理

启动或运行时先读取环境/profile，建立物理或 Simulator 模式；物理模式下，所有需要写入设备的现有路径在调用 Adapter 前继续经过统一准入和当前实例/epoch 校验。状态存储接收完整预检或配置更新时先比较指纹，再决定是否推进 epoch 和清理授权。

最终 Move 到达后，worker 先确定节点分类，再执行至多一次 release，并把明确结果转换为三态审计记录。只有 `Succeeded` 结果才写入工作流成功终态；其余结果写入可恢复的 `Unknown`/失败审计并停止自动后续动作。任何异常路径都不得以“到达”或“release 调用已发起”推导工作流成功。

所有新增失败结果使用现有稳定原因码/审计字段，不记录管理员密码、无线凭证或其他敏感全文。离线测试使用内存 fake、回放或本地数据库，不访问现场设备。

## 5. 测试与验收

### 配置契约

- Production/Development 直接加载根配置时断言 `useSimulator=false`、`vendor-tcp` 和占位地址。
- 只有显式 `FieldSimulation` 环境加载 Simulator 配置；既有 Simulator 测试全部保持通过。
- 物理监督器、自动派发和批量 worker 的安全默认关闭策略不被改变。

### 最终 Move/release

- release 成功时工作流才进入 `Succeeded`。
- 明确拒绝、超时、异常、未知返回和最终节点无法分类时均不进入 `Succeeded`，且不触发自动重试或后续写入。
- 审计记录包含实际 release 结果、三态终态及 workflow/run/node 关联上下文。

### 授权与重配置

- 正确 supervisor instance + epoch 可确认；实例不匹配、epoch 不匹配和过期授权均失败。
- 相同 descriptor/配置的连续轮询不推进 epoch。
- 有效 descriptor/监督器配置变化只推进一次并清除旧授权/Ready；移除再加入不复用 epoch。
- 进程重启/断电恢复路径不复用旧实例授权。

完成定向测试后，运行相关 MES 测试、全量 `dotnet test MesControlAgv.sln -m:1` 和 Release 构建；所有结果必须为零失败，且全程保持现场设备断电/未访问状态。通过代码审查且无 Critical/Important 问题后，才允许普通推送；现场联调另行执行新鲜只读预检并重新获取授权。

## 6. 实施顺序

1. 先落地配置契约和环境隔离测试。
2. 再实现最终 Move/release 三态传递及审计测试。
3. 更新授权接口、全部调用方和 instance/epoch 测试。
4. 实现 descriptor/config 指纹变化和 epoch 单调性测试。
5. 清理文档尾随空格，运行分层验证并更新进度。

