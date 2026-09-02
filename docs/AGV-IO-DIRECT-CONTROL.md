# AGV → 机械臂 I/O 直连验证记录

更新时间：2026-08-27

## 已确认的事实

注意：现场存在两套不同的 I/O 命名空间。Roboshop API `1013/6001` 返回/写入的是
AGV 底盘控制器的 DI/DO（当前控制器 `192.168.1.2`）；厂商 `AUBO-CB-AGV-V2`
手册中的 DI00…DI17/DO00…DO17 则是 AUBO 机械臂控制箱的本地 I/O。两者不能
按相同编号直接对应，必须由实体线束和端子表连接起来。

现场 RoboshopPro 的当前 AGV 控制器是 `192.168.1.2`（型号
`W500-SZ`，版本 `v3.4.8.0011`）。笔记本通过 AGV 外部网口配置为
`192.168.1.106/24` 时，可以访问控制器的 TCP 服务。

报告 `report_20260825_16_16_18.zip` 还显示 AUBO 控制器的
`enp1s0=192.168.1.102`，另有一个内部/非托管的 `enp2s0` 接口。这解释了
“从 AGV 外部口能看到 AGV、却看不到机械臂 IP”：两台设备可以通过实体 I/O
线互触发，并不要求两个 TCP 网段互相路由。

报告中的 AUBO 配置同时记录 `rpc_tcp_port=30004`。若要在能直连机械臂
`.102` 的那条网线上启用只读状态检查，可在启动 Adapter 时临时注入：

```powershell
$env:Devices__AuboArm__Enabled = 'true'
$env:Devices__AuboArm__Host = '192.168.1.102'
$env:Devices__AuboArm__Port = '30004'
```

报告随附的当前工程配置还给出了 I/O 名称与物理针脚的对应关系：
`default_0.ins` 中标准输入 `DI17` 的 `pin="15"`，标准输出 `DO02`/`DO03`
分别标为“**小车DI7**”/“**小车DI8**”。这只是机械臂端的命名/针脚证据，
不能把 AGV 的 `DO6` 直接推断成 `DI17`；必须在脉冲期间观察示教器 I/O 页，
以实际变化的 DI 为准。该文件中的所有标准输入 `action="0"`，说明当前工程
没有配置输入沿触发动作，确认点位后仍需在驻留 Lua 中轮询 DI，或由厂家在
示教器中配置输入动作。

这只打开 `status/readiness/handshake/variables` 四个 GET；机械臂写入和
运动接口仍未注册。AGV DO 测试不需要这条 TCP 路由。

报告里的旧 Lua 模板仍有 `modbusAddSignal("192.168.1.11,502", ...)`，那是
“中控作为 Modbus 服务端”的历史方案；现场目前没有该服务时不要按旧模板
判断 AGV 是否连通。新的 6001 路径直接由 MES/Adapter 调用 AGV 控制器，
与这两个旧 Modbus 信号相互独立。

`Input/机器人API2023(5).pdf` 的 API 定义和控制器本地
`appInfo/setting/Config.ini` 一致：

| 用途 | 端口 | 请求 API | 响应 API | 请求体 |
| --- | ---: | ---: | ---: | --- |
| 读取 I/O | 19204 | 1013 | 11013 | 无 |
| 控制权查询 | 19204 | 1060 | 11060 | 无 |
| 获取控制权 | 19207 | 4005 | 14005 | `{"nick_name":"..."}` |
| 释放控制权 | 19207 | 4006 | 14006 | 无 |
| 设置单个 DO | 19210 | 6001 | 16001 | `{"id":6,"status":true}` |

报文仍是现有 `TcpAgvClient` 使用的 16 字节 Robokit 头，JSON 数据区为
UTF-8。厂商 PDF 第 353 页给出的 6001 示例头为：

```text
5A 01 00 01 00 00 00 17 17 71 00 00 00 00 00 00
{"id":3,"status":false}
```

## 代码落点

- `TcpAgvOptions.OtherPort` 默认值为 `19210`。
- `TcpAgvClient.GetIoAsync()` 调用 1013 并解析 `DI`/`DO`。
- `TcpAgvClient.SetDoAsync(id, status, ...)` 调用 6001；写入前重新确认
  1060 控制权，不会在未知控制权时抢占 Roboshop。
- `GET /agv/io`：读取当前控制器 I/O。
- `POST /agvs/{agvId}/io/do/{id}`，请求体
  `{"status":true|false}`：在标准模式下获取/确认控制权后写 DO。
- MES 已透传为 `GET /api/agvs/{agvId}/io` 和
  `POST /api/agvs/{agvId}/io/do/{id}`，所以业务侧不需要自行拼接厂商报文。
- `scripts/Invoke-AgvIoApi.ps1`：现场不启动 MES 也可以直接验证同一帧协议。

只读预检模式仍会拒绝 DO 写入；这是启动模式开关，不影响标准模式下的
现场验证。

## 现场验证顺序

1. 保持机械臂停止、示教器处于可观察 DI 页面；确认 AGV 不在运动任务中。
2. 先读取 AGV I/O：

   ```powershell
   .\scripts\Invoke-AgvIoApi.ps1 -Operation read
   ```

3. 确认允许接管 AGV 后，给一个已确认安全的测试点（当前暂用 DO6）发高、
   再发低。脚本会检查控制权、写 6001，并默认用 1013 回读：

   ```powershell
   .\scripts\Invoke-AgvIoApi.ps1 -Operation set -DoId 6 -Status true -AcquireControl
   .\scripts\Invoke-AgvIoApi.ps1 -Operation set -DoId 6 -Status false
   ```

4. 同时记录三件事：
   - AGV 1013 中 DO6 是否从 `false` 变为 `true` 再恢复；
   - AGV 外部端子/继电器是否有动作（用户已听到一次继电器声）；
   - 机械臂 DI0…DIx 中是否有且只有一个点同步变化。

AGV DO 回读成功只证明控制器逻辑输出已改变；只有机械臂 DI 同步变化，才
能证明“AGV 控制器 → 外部线缆/继电器 → 机械臂 DI”的物理链路和点位映射。
若 AGV DO 变化但机械臂 DI 不变，应记录端子号、有效电平（高/低）和线缆
标签，请厂家确认接线/点位表，不要继续尝试机械臂运动。

2026-08-27 现场短脉冲结果：`DO6=true` 与复位 `DO6=false` 的两次 6001 写入
均返回 `ret_code=0`，约 1 秒后 1013 回读恢复 `DO6=false`，并听到 AGV
继电器动作；当时机械臂未接入该 I/O 线束，因此示教器 DI 面板不会闪变。
当前网线验证到此结束，下一步是确认/接通标记为“上装2”的实体 I/O 线及公共端，
再重复同一 500 ms 脉冲。

同日将笔记本改接标记“上装2”的线进行网络探测：`192.168.1.2` 仍可 ping，
1013 仍可读；`192.168.1.102:30004` 仍不可达，ARP 表只有 AGV 控制器的
MAC。由此只能确认该线当前通向 AGV 侧，不能把“上装2”认定为 AUBO 网络口；
它是否承载实体 DI/DO 仍需按端子/针脚表确认。

## MES 流程落点

```
MES 下发导航 → TcpAgvClient 3066 → 1110 确认到站
    → SetDoAsync(测试/握手点)
    → 1013 回读 AGV DO
    → 机械臂 Lua 等待对应 DI/变量并执行既有流程
```

因此短期不需要在中控电脑上运行 Modbus 服务，也不需要新增 PLC。需要
厂家补充的只有最终 I/O 点位表（哪个 AGV DO 接到哪个机械臂 DI、脉冲/保持
时间、有效电平）以及机械臂程序完成/故障回报码定义。

### DI/DO 触发职责

DI 变化只是输入状态，不会凭空触发跨设备通讯。当前工程配置中的标准输入
`action=0` 也表明没有预置“DI 变化即启动程序”的自定义动作。目标链路应由
两端各自完成：MES 使用 AGV API `6001` 写 DO6；AGV 的实体 DO6 经“上装2”
或同类 I/O 线接到 AUBO 的 DI/COM；AUBO 端再由示教器输入动作或驻留 Lua
轮询 DI 并调用既有运动流程。AGV DI 仅在需要 AUBO→AGV 的完成/故障回执时
使用，不需要修改 AGV 控制器固件。
