# AGV—AUBO—视觉—中控同网段恢复方案

## 2026-08-31 现场实测更新

交换网络已接通，AGV 与 AUBO 地址均可达。AUBO `30004` TCP 可建立连接，但原始
换行分帧 JSON 客户端未完成 SDK 的连接/登录流程而超时；控制器 WebSocket RPC
`9012` 已实际开放并成功返回 JSON-RPC 2.0。现场只读检查改用：

```powershell
.\scripts\Invoke-AuboWsReadOnlyPreflight.ps1 `
  -ControllerHost 192.168.1.102 -Port 9012 -RobotName rob1
```

首次实时状态为 `Running / Normal / Stopped / Manual`，预加载工程为空；当前不得
启动程序。C# Adapter 尚未切换到真机验证过的 WebSocket 传输，暂不用于现场 AUBO
控制。

## 目标拓扑

不改 AGV/AUBO 控制器程序，不拆实体 DI/DO。将机械臂、视觉模块、AGV 控制器
和中控接入同一个二层交换网络：

```text
                         ┌──────────────┐
                         │  中控 PC     │
                         │ 192.168.1.106│
                         └──────┬───────┘
                                │
                 ┌──────────────┴──────────────┐
                 │ 5/8 口千兆非网管交换机       │
                 │ 不启用 DHCP/NAT              │
                 └──────┬──────────┬────────────┘
                        │          │
                 AUBO Ethernet   视觉模块/工控机
                 192.168.1.102   （地址待现场确认）
                        │
                  AGV 外部/上装网口
                  192.168.1.2
```

如果视觉模块当前是 Wi-Fi 转以太网设备，且 AUBO 网线插在它唯一的 LAN 口，
可以把 AUBO 网线改插交换机，再把 Wi-Fi 模块的 LAN 口也接到交换机；只有在
Wi-Fi 模块工作于 AP/桥接模式时，接入该 Wi-Fi 的中控才会与 AUBO 同一网段。
若它是 NAT 客户端，必须让厂家改成桥接/AP，不能只改 IP。

## 地址规划

| 设备 | 当前/建议地址 | 备注 |
|---|---|---|
| AGV Roboshop 控制器 | `192.168.1.2` | 已由现场软件和 API 1013/6001 验证 |
| AUBO 控制器 | `192.168.1.102` | 示教器 LAN 页面和报告确认；JSON-RPC `30004` |
| 视觉模块 | 以设备页面为准（旧记录曾写 `.101`） | 不要直接沿用旧架构猜测 |
| 中控 PC | `192.168.1.106` | `/24`，隔离网段不配置默认网关 |

每个设备地址必须唯一，子网掩码统一 `255.255.255.0`。交换机本身不分配地址，
不要把它当路由器使用。

## 现场接线顺序

1. 停止 AGV 运动和机械臂程序，保留急停可用。
2. 用一根网线把交换机接到 AGV 当前已验证的外部/上装网络口。
3. 将 AUBO 当前接视觉模块的网线拔下，插入交换机；这只是网络跳线，不涉及
   控柜 DI/DO 端子。
4. 将视觉模块原来的 LAN 线接入交换机；如果视觉模块只有一个 LAN 且必须
   继续给 AUBO 提供网络，先让厂家确认其是否支持桥接，不能盲插造成 NAT 隔离。
5. 将中控网线接入交换机。AGV 移动场景下，中控可改为连接同一交换机上的
   Wi-Fi AP，但 AP 必须是桥接模式。

## 一次性连通性验收

中控配置 `192.168.1.106/24` 后，每项只测一次：

```powershell
ping 192.168.1.2 -n 1
ping 192.168.1.102 -n 1
Test-NetConnection 192.168.1.2 -Port 19204 -InformationLevel Quiet
Test-NetConnection 192.168.1.102 -Port 30004 -InformationLevel Quiet
```

预期是 AGV 和 AUBO 都可达。若只通 AGV，检查 AUBO 网线、交换机端口和 Wi-Fi
模块桥接模式；不要再尝试改 IP。视觉端口以其软件/厂家资料为准，不对未知端口
做扫描。

## 软件侧启动顺序

网络通过后先做只读验证：

```powershell
# AUBO JSON-RPC 状态
.\scripts\Invoke-AuboRpc.ps1 -Operation status -ControllerHost 192.168.1.102

# AGV I/O 状态
.\scripts\Invoke-AgvIoApi.ps1 -Operation read -ControllerHost 192.168.1.2
```

然后再以现场配置启动 Adapter/MES：

```powershell
$env:Agv__Driver = 'vendor-tcp'
$env:Agv__Tcp__Host = '192.168.1.2'
$env:Agv__Tcp__EnablePush = 'false'
$env:Devices__AuboArm__Enabled = 'true'
$env:Devices__AuboArm__ControlEnabled = 'true'
$env:Devices__AuboArm__Host = '192.168.1.102'
$env:Devices__AuboArm__Port = '30004'
```

AUBO 的 `ControlEnabled` 只在明确的现场标准模式下打开；默认配置仍关闭。
网络同通后，中控即可选择直接调用 AUBO `loadProgram/resume`，或写入已运行
Lua 工程的命名变量，不再依赖实体 I/O 触发。

## 失败分支

- **视觉必须直连 AUBO 且不能插交换机**：请厂家确认视觉模块是否有内部交换
  芯片/桥接功能；若没有，唯一可靠方式是在 AUBO—视觉链路中间增加小交换机。
- **AGV 外部口只能访问 `.2`**：说明 AGV 口不是内部交换机上联，需将 AGV、
  AUBO、视觉的实际以太网口一起接入交换机，或让厂家提供内部桥接。
- **ping 通但 30004 不通**：检查 AUBO 控制箱服务状态/防火墙和示教器运行模式；
  示教器连接本身通常不会影响 ping。
