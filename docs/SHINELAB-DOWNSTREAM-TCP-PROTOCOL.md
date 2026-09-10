# ShineLab 下游表 TCP 协议（最小直连结论）

`下游表协议.docx` 明确：下游仪表为 TCP Client，平台/中控为 Server；双方以换行作为 JSON 粘包/半包 framing，重连后需发送 `Certification`。请求字段为 `strID`、`strMethod`、`equipmentCode`、`body`，响应另含 `result`（Success/Failed）和 `msg`。

已覆盖的方法：`Certification`、`UpdateInfo`、`Config`、`Command`（action 0 启动进样、1 停止、2 启动检测等）、上报 `TaskError`、`SampleFinish`、`TaskFinish`、`Result`。示例设备代码包括 `Ge`、`B`、`IC_A`、`IC_C`。

## 2026-09-09 厂商确认：`BindModule`

厂商已确认 ShineLab 连接时使用的方法名为 **`BindModule`**（大小写和拼写按此记录）。该名称与现场 `res/ShineLab/ShDeviceFamily.dll` 中发现的 `IRAYCmdBindModule` 类及 `Send_bindModule` 符号一致，因此不是此前误记的 `bindmoudle`/`BindMoudle`。

当前已确认的是方法名和其存在于 ShineLab 二进制中的证据；完整 JSON 首帧仍需以现场原始报文或厂商示例确认，尤其是 `equipmentCode`、`strCode`、`body.chan`、换行分帧以及服务端响应格式。在这些字段确认前，MES 不主动向真实仪器发送猜测性的 `BindModule` 报文。

结论：MES 可以按协议作为 Client 连接下游表，但“直接下发、进样、启动”只在现场确认端口、认证内容、任务/方法参数和服务端实现后成立。文档没有给出端口、认证 body 的约束，也没有证明 ShineLab 当前进程开放该 TCP 服务；因此当前生产路径仍必须经过中控/代理，禁止凭示例报文连接真机。仓库提供的 `ShineLabTcpClient.Real(..., allowReal:true)` 是最小实现，默认无真实调用；测试应注入 transport 模拟响应。
