# AUBO WebSocket Adapter 回归记录

更新时间：2026-08-31

本记录对应快速落地计划阶段 1。Adapter 的 AUBO 控制面固定为
`ws://192.168.1.102:9012/`；原 `30004` 裸 TCP 行协议客户端不再由默认模块注册。
现场工程名仍须由操作员显式确认，当前快速白名单只有 `测试`（RPC 发送时去掉
`.pro`/`.lua` 后缀）。

## 本地回归边界

- WebSocket 客户端按单请求/单响应发送 JSON-RPC 2.0，读取完整文本消息（包括分片），
  校验响应 `id`，解析 `error`，并在超时、关闭或协议错误后重建连接；不会自动重发
  可能已经到达控制器的变更请求。
- `AuboArmReadOnlyDriver` 保留只读状态接口，并保留控制器返回的字符串状态；程序控制
  通过独立 `AuboArmProgramDriver` 暴露，写入前检查机器人、安全、运行和操作模式。
- `loadProgram` 只接受显式白名单工程，`runProgram` 只运行当前已加载工程；运行中再次
  点击会在发送 RPC 前拒绝。停止已停止的运行时只返回幂等结果，不发送重复 abort。
- loopback 控制器覆盖加载、启动、停止、未加载工程、模式门禁和控制关闭路径。测试只
  使用内存/loopback，不访问真实 AUBO、AGV 或 Modbus 端口。

## 现场前置条件

代码回归通过不等于现场 GO。重新连接前必须由现场人员确认链路、`rob1` 身份、
Safety/Operational/Runtime 状态和工程内容；若 Runtime 已为 `Running`，禁止再次发送
`runProgram`。示教器拔除或工程停止前后都要重新执行只读核验。旧 Lua 工程中的视觉
Socket、Modbus 信号和立即运动仍不属于本 Adapter 的通用 Demo 能力。

