# 物理就绪与现场只读可靠性离线加固设计

日期：2026-09-08
状态：已获现场离线优化授权，等待实施计划执行

## 目标

在下一次设备上电前，消除现场验证发现的三类可靠性问题：AGV
`DeviceEpoch` 因部分观测反复变化、AGV 空闲 TCP 连接被控制器关闭后的只读
恢复，以及最终预检中容易出现的本地网关判定误报。加固后，监督器能够在
“完整预检→普通轮询→完整预检”周期内保持同一设备代次；真实身份/地图变化、
断电、离线和过期观测仍然会严格失效并要求重新授权。

## 已确认的现场事实

- `WLAN 3=AMR`，控制电脑 `192.168.1.11/24`，WLAN 3 没有
  `0.0.0.0/0` 路由；公司 WLAN 的默认路由不属于 AMR 接口。
- AGV `192.168.1.2` 的只读状态和地图预检成功，AUBO
  `192.168.1.102:9012` 的只读状态/工程目录成功。
- 现场只读 Adapter 日志曾出现两次 AGV `SocketException (10054)`；随后三轮
  只读稳定性复核未再出现该错误。
- 启用 MES 物理就绪监督器后，AGV 在没有真实身份或地图变化时出现
  `DeviceEpoch 4→10`，随后采样为 `19,19,20`，原因均为
  `device_identity_or_map_changed`。
- 根因是完整预检含 `VehicleModel/ControllerVersion/MapName/MapVersion/MapMd5`，
  普通轮询只含模型；状态存储用普通部分观测重建并覆盖了权威指纹。

## 设计

### 1. 权威身份/地图指纹生命周期

修改 `src/MesControlAgv.Mes/Services/PhysicalReadinessStateStore.cs` 的
`Apply` 逻辑：

- 只有 `PhysicalDeviceReadinessObservation.IsFullPreflight=true` 时，才从
  `VehicleModel`、`ControllerVersion`、`MapName`、`MapVersion`、`MapMd5`
  生成候选指纹并与已保存指纹比较。
- 普通轮询观测不重新生成、不清空、不覆盖已保存的权威指纹；因此状态更新
  位置、活动任务、控制权和安全事实时，不会把“未观测到地图字段”解释成地图
  变化。
- 首次完整预检建立指纹；后续完整预检发现真实差异时只推进一个新 epoch，
  保留 `device_identity_or_map_changed` 和重新授权要求。
- 离线、探针失败、观测过期、真实地图/型号/版本变化的现有 fail-closed
  语义保持不变；活动任务和临时障碍仍不推进 epoch。

### 2. 幂等只读 TCP 断线恢复

修改 `src/MesControlAgv.Adapter/Services/TcpAgvClient.cs` 及其内部
`TcpApiChannel`：

- 增加显式的只读请求路径（或等价的显式 `readOnly` 请求语义）；调用方必须
  明确标记查询，不根据“没有回调”或 API 编号隐式推断。
- 只读请求在 `IOException`、`SocketException` 或远端 EOF 导致的连接重置后，
  清理旧连接并最多重连重读一次；使用同一取消令牌和有界总超时，不建立无限
  重试循环。
- 只读重试记录 API、尝试次数和最终结果，便于现场证据核对；成功重读不掩盖
  首次断线事实。
- 控制权申请/释放、导航、暂停、恢复、取消、Push 配置、DO 写入以及任何
  其他可能改变设备状态的请求保持单次尝试，结果不明时继续进入人工核销，
  绝不自动重发。

### 3. 预检判定与现场启动边界

- 现场无线脚本继续以 `Get-NetRoute` 的 `0.0.0.0/0` 路由记录判断接口默认
  路由，不使用 `@($null).Count` 判断 `IPv4DefaultGateway`。
- 预检编排必须分离“启动/health”“只读采集”“收尾停止”三个阶段，并在
  `finally` 中按状态文件停止本次进程；失败证据保留为 aborted/failed，不冒充
  设备 NO-GO。
- 任何上电、MES 重启或监督器实例变化都会要求新的 `SupervisorInstanceId`
  和设备 epoch 授权；未执行的许可也不能跨断电直接复用。
- 标准会话仍由现场人员明确开启；离线优化不改变默认物理批量执行关闭、
  Simulator 显式启用和所有写入边界的人工授权要求。

## 测试设计

### 状态存储回归

在 `tests/MesControlAgv.Mes.Tests/PhysicalReadinessSupervisorTests.cs` 增加：

1. 完整健康观测建立指纹后，连续普通部分观测不推进 epoch、不添加
   `device_identity_or_map_changed`，稳定窗口后能够进入 `Ready`。
2. 普通观测期间授权保持绑定当前 epoch；下一次完整预检的真实地图差异只推进
   一个 epoch 并要求重新授权。
3. 真实离线/重新上线、过期观测和探针失败仍各自推进 epoch并清除旧 Ready。

在 Probe 测试中使用现场形状的“普通快照无地图字段、完整预检有地图字段”输入，
避免只用所有字段都填满的理想化 fixture。

### TCP 只读恢复回归

在 `tests/MesControlAgv.Adapter.Tests/TcpAgvClientTests.cs` 增加：

1. 第一次只读请求由测试服务器关闭连接，第二次连接返回合法响应，断言查询
   成功且 API 只发送一次重读。
2. 只读重连耗尽后返回明确异常并重置连接。
3. 控制权申请、导航和 DO 写入在连接关闭后均只收到一次写请求，不能因共享
   重试逻辑而重复发送。
4. 取消令牌和总超时不会被重试逻辑吞掉。

### 离线门禁与文档

- 运行定向 Adapter/MES 测试、完整 `dotnet test MesControlAgv.sln -m:1` 和
  Release 构建。
- 用本地 fake HTTP/TCP 端点回放最终预检脚本，验证无默认路由判断、失败时
  `finally` 清理和证据文件不可覆盖；回放不访问现场 IP。
- 更新 `docs/PROGRESS.md` 和现场交接文档，明确新问题、修复状态和下次上电
  必须重新授权的边界。

## 验收标准

- 监督器连续运行至少一个完整预检周期时，AGV epoch 不因部分字段缺失而变化。
- 真实地图/身份变化、断线和断电仍能失效旧授权，且没有任何写入自动重试。
- 只读 TCP 空闲连接重置最多产生一次安全重读，并在证据中可见。
- 离线预检回放无误报默认网关，所有启动失败路径都停止由本次创建的进程。
- Release 构建无警告/错误，全量测试无新增失败；现场设备保持断电或未连接，
  直到下一轮由现场重新执行只读预检。

## 非目标

- 不修改现场 IP、SSID、Bridge、NAT、网关或无线配置。
- 不实现或启用 AGV 自动派发、AUBO 自动运行、Modbus、DI/DO 写入或命令端口
  探测。
- 不跨断电恢复旧的 workflow、permit、request、correlation 或设备 epoch。
