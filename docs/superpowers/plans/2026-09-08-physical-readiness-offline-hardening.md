# 物理就绪与只读传输离线加固实施计划

> 对应规格：`docs/superpowers/specs/2026-09-08-physical-readiness-offline-hardening-design.md`
>
> 范围：设备保持断电，仅修改和验证本地代码、测试与文档；不访问现场 IP，
> 不连接 AGV 命令端口，不申请控制权，不执行 AUBO/Modbus/DI/DO 写入。

## 1. 状态指纹生命周期

- [x] 修改 `src/MesControlAgv.Mes/Services/PhysicalReadinessStateStore.cs`：只有
  `IsFullPreflight` 观测才计算、比较和保存身份/地图指纹；普通轮询保留最近一次
  完整预检的权威指纹。
- [x] 保持完整预检首次建指纹、真实变化只推进一次 `DeviceEpoch`、离线/探针失败/
  过期观测的现有 fail-closed 语义。
- [x] 在 `tests/MesControlAgv.Mes.Tests/PhysicalReadinessSupervisorTests.cs` 增加
  现场形状的普通快照回归：完整预检后多次普通轮询 epoch 和授权不变；下一次真实
  地图/身份变化只使 epoch 递增一次并要求重新授权。
- [x] 覆盖普通观测不清空地图/身份投影，以及普通观测中的活动任务/临时障碍仍为
  临时调度阻断而非身份变化。

## 2. AGV 幂等只读 TCP 恢复

- [x] 在 `TcpApiChannel` 增加显式 `RequestReadOnlyAsync` 路径；原始
  `RequestAsync` 继续表示单次请求，默认不重试。
- [x] 为 API `1000/1013/1021/1060/1101/1110/1300/1301/1302/4011` 的调用点
  显式使用只读路径；`4005/4006/3066/3001/3002/3067/6001/9300` 保持单次路径。
- [x] 只读路径在 IOException/SocketException/EOF 后清理连接并最多重连重读一次，
  共用同一个总超时和取消令牌；超时、取消、协议/API/JSON 错误不扩大重试范围。
- [x] 记录首次断线、重读次数和最终结果；成功重读不隐藏首次断线事实。
- [x] 在 `tests/MesControlAgv.Adapter.Tests/TcpAgvClientTests.cs` 增加：只读首次
  EOF 后成功重读、重读耗尽、DO/控制权/导航写入不重试及取消/总超时边界回归。

## 3. 现场预检脚本与离线回放

- [x] 检查 `scripts/Invoke-FieldWirelessReadOnlyCapture.ps1` 的默认路由判定继续
  基于 `Get-NetRoute`，不引入 `$null` 网关计数判断。
- [x] 检查/修复 `scripts/Invoke-FieldWirelessReadOnlyCycle.ps1` 的启动、采集、收尾
  分层和 `finally` 进程清理；失败结果标记为 aborted/failed，不伪造设备失败。
- [x] 仅使用本地 fake HTTP/TCP 端点回放脚本与证据写入逻辑，确认现场目标未被访问。

## 4. 验证与交接

- [x] 运行定向 MES/Adapter 测试。
- [x] 运行 `dotnet test MesControlAgv.sln -m:1`，记录新增失败与既有允许跳过项。
- [x] 运行 Release 构建并确认无警告/错误。
- [x] 更新 `docs/PROGRESS.md`、离线检查点和必要的现场交接说明，明确下次上电需
  新 RunId/新授权/新监督器实例；不提交任何凭证。
- [x] 只提交本次加固涉及的文件，保留工作树中其他用户修改、证据和删除项。

## 完成判据

1. 完整预检→普通轮询→完整预检周期中，无真实身份/地图变化时 epoch 不抖动。
2. 只读连接重置最多产生一次安全重读，并有可检索审计；所有写请求零自动重试。
3. 本地测试与 Release 门禁通过；设备继续保持断电，未产生任何现场写入。
