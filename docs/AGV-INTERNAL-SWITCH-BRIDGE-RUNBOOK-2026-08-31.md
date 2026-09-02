# AGV 内部交换机与控制网桥接现场步骤

更新时间：2026-08-31  
当前目的：在不启动机械臂运动、不移动 AGV、不触发 DI/DO 的前提下，确认
AGV 内部网络是否能够把总控电脑、AGV 控制器、AUBO 控制器和视觉设备放到同一
个二层控制网，随后为 AUBO 只读联通测试做准备。

本文件是网络布线和只读验证手册，不是运动调试手册。任何一步出现设备身份、
端口、地址或拓扑不确定，都停在该步并保持 NO-GO，不要靠猜测改 IP 或换端口。

## 1. 先理解现场可能遇到的设备

### 1.1 真正的 AGV 内部交换机

AGV 内部交换机通常是安装在车体/电控箱内的小型工业以太网交换机，常见特征：

- 5 口或 8 口 RJ45，部分为 DIN 导轨安装；
- 每个端口有 Link/Activity 指示灯；
- 可能使用 24 V DC 供电；
- 非网管型号没有 WAN/LAN 分区、没有 DHCP 配置页、没有 NAT；
- 网线插在哪个普通端口通常不影响转发，但必须保留厂家已确认的上联和设备端口。

也可能遇到以下两类设备，不能按普通交换机处理：

| 外观/标识 | 实际设备 | 处理原则 |
|---|---|---|
| 有 Console、Web、VLAN、Managed 标识 | 网管交换机 | 不恢复出厂、不改 VLAN；由厂家确认端口配置 |
| 有 WAN/LAN、Internet、DHCP、路由、防火墙标识 | 路由器/NAT | 不能只改 IP 代替桥接；必须切换为 AP/Bridge，或换成独立交换机 |
| Wi‑Fi 天线、无线桥、CPE、AP 标识 | 无线桥/AP | 必须确认工作在透明桥/AP 模式；NAT 客户端会隔离 AUBO |

不要因为设备有多个 RJ45 口就认定它是交换机；先拍照记录型号、供电方式、端口
标签和现有网线去向，必要时让厂家确认。

### 1.2 本次现场照片的初步判读（2026-08-31）

以下结论只根据照片外观，不能替代型号铭牌、接线图或厂家确认：

- **照片 1 的黑色鳍片盒**：外观更像散热型工业计算机/控制器或其他车载电子设备，
  可见绿色可插拔端子、疑似 USB/外设接口和粗电缆；照片中没有看到交换机常见的
  多个 RJ45 端口阵列及 Link/Activity 灯。不能把它当作交换机，也不能假设它的
  两个网络口具备硬件桥接能力。
- **照片 2 的黑色箱体和绿色端子排**：可见 `16XT3`、`16XT4`、`S010…S017`、
  `24V` 等标识，外观符合控制柜数字 I/O/端子区域的特征，不是以太网交换机。
  这些端子不要插网线、不要接 RJ45 转接头、不要把中控电脑地线或未知信号接入。
  精确的 DI/DO 针脚功能必须以 AUBO 电气图和厂家确认结果为准。

**当前照片结论：尚未找到可确认的 AGV 内部以太网交换机。** 在找到带有明确
`Ethernet/LAN`、型号和端口标识的交换机，或厂家确认使用独立外部工业交换机之前，
不得进行网络桥接。若照片 1 实际是车载工控机，除非厂家明确批准并说明其网络拓扑，
也不要在 Windows 中开启 Network Bridge/ICS 来“凑”出交换机功能。

建议补拍以下内容后再继续：

1. 黑色鳍片盒六面中带铭牌、型号和所有 RJ45/光口的一面；
2. AGV 电控箱内所有带 `LAN`、`ETH`、`Ethernet`、`SW`、`UPLINK` 标识的设备；
3. 每根网线两端和端口标签的近照，能看清去向但不要拔线；
4. 若存在独立交换机，拍全景、型号铭牌、端口编号和 Link 灯状态；
5. AUBO 控制柜的网口位置应单独拍照，避免与 `16XT*` I/O 端子混淆。

### 1.3 新增现场照片的初步判读（2026-08-31）

新增照片显示：

- **中上部黑色带铭牌模块**：连接了多束黄色/紫色/黑色线缆，外观更接近 PLC、
  远程 I/O 或车载控制/通信模块。照片没有清晰显示成排 RJ45 端口、端口编号或
  Link/Activity 灯，因此目前不能认定它是以太网交换机。即使它有两个网络口，
  也不能假设两个口是硬件桥接口。
- **白色端子/旋钮组件及大量红黄线**：属于电气配线、继电器或 I/O 分配区域的
  可能性较高，不是网络交换机；不要插网线或改变端子接线。
- **左下方黑色鳍片设备和蓝色外设**：仍不能仅凭外观确认型号/功能，不能把它当作
  交换机或 Windows 网络桥接主机。

因此，这张照片中也没有出现“可确认的交换机”。当前最值得补拍的是中上部黑色
模块的正面/侧面：必须看清铭牌、完整接口、是否为 RJ45、端口数量和 Link 灯；
在此之前不得拔插黄色线缆，不得把总控电脑接到该模块的未知接口。

### 1.4 推荐的目标拓扑

如果 AGV 的外部/上装网络口确实接入了车内二层交换机，推荐拓扑如下：

```text
                         ┌──────────────────────┐
                         │ 总控电脑（有线）      │
                         │ 192.168.1.11/24      │
                         └──────────┬───────────┘
                                    │
                         ┌──────────┴───────────┐
                         │ AGV 内部二层交换机    │
                         │ 无 DHCP/NAT/VLAN 改动 │
                         └──────┬──────┬─────────┘
                                │      │
                 AGV 控制器 192.168.1.2   AUBO 192.168.1.102:9012
                                │      │
                                └──┬───┘
                                   │
                         视觉设备地址待现场确认
```

如果 AGV 外部/上装口只能访问 `192.168.1.2`，而 AUBO 仍无法访问，说明这个口
可能不是内部交换机的上联。此时不要继续改 IP，应改为：

```text
总控电脑 ─┐
AUBO ─────┼── 独立 5/8 口二层交换机 ─── AGV 外部/上装口
视觉 ─────┘          （地址待确认）
```

只有在厂家确认 AUBO、视觉和 AGV 三者确实位于同一个二层广播域后，才进入 AUBO
只读测试。

本手册所说的“桥接”是交换机或无线设备的二层透明转发，不是 Windows 的“网络
桥接”、Internet Connection Sharing（ICS）或 NAT。不要在总控电脑上把 WLAN 和
以太网创建 Windows Network Bridge，也不要开启 Internet 共享；总控电脑应当是
控制网的一个普通终端，而不是路由器或 DHCP 服务器。

## 2. 本轮的地址和边界

| 设备 | 现场确认/目标地址 | 本轮用途 |
|---|---|---|
| 总控电脑有线网卡 | `192.168.1.11/24` | 控制网客户端；无默认网关 |
| 总控电脑旧地址 | `192.168.1.106/24` | 仅作改址前记录，不要与 `.11` 同时使用 |
| AGV 控制器 | `192.168.1.2` | 只做 ping/状态端口验证，不派单、不取控制权 |
| AUBO SDK TCP RPC | `192.168.1.102:30004` | TCP 可达；裸行协议不适用 |
| AUBO WebSocket RPC | `192.168.1.102:9012` | 已现场验证的只读 JSON-RPC 入口 |
| 视觉模块 | 地址待现场确认 | 不扫描未知地址或端口 |
| AGV 上装独立网段 | `192.168.192.5` | 不混入 `192.168.1.0/24` 控制网 |

硬边界：

- 不启动机械臂程序，不写变量，不调用 `setInt32`/`setString`；
- 不调用 `load`、`run`、`stop`，不执行握手派发和运动命令；
- 不获取 AGV 控制权，不连接 AGV 命令端口 `19206`，不发送取消/恢复/移动；
- 不触发 AGV DO、AUBO DI/DO 或夹爪；
- 不连接 D160+、SHA-18i 或其他仪器；
- 不打开控制柜、不拆 16XT7/16XT8，不接未知“上装2”线。

## 3. 阶段 A：接线前记录和安全确认

在拔插任何网线前完成以下清单：

- [ ] AGV 已停止，机械臂当前工程/程序已停止；
- [ ] 急停、人工接管和停止路径有人负责，作业区已隔离；
- [ ] 本轮只纳入一台 AGV；
- [ ] 自动派单、Push、任务取消和所有 I/O 触发均关闭；
- [ ] 拍照记录 AGV 内部交换机/路由器的型号、供电、端口标签和现有线缆；
- [ ] 给每根线贴临时标签：`PC`、`AGV-CONTROL`、`AUBO`、`VISION`、`UPLINK`；
- [ ] 记录网线原始插口，确保可以按照片回退；
- [ ] 厂家/现场负责人确认允许进行网络跳线。

网络跳线不等于控制柜改线。若必须拆开电控箱、断开 24 V 或触碰端子，暂停本
手册，交由有资质的电气人员和厂家执行。

## 4. 阶段 B：识别并选择桥接方式

### 4.1 已有内部二层交换机

按下表逐项确认，不要改变交换机配置：

1. 找到交换机的普通 LAN 口和电源指示灯；
2. 按线缆标签确认 AGV 控制器、AUBO、视觉和外部/上装口的去向；
3. 将总控电脑网线接入同一交换机的空闲普通端口；
4. 如果 AUBO 当前网线只插在视觉设备的唯一 LAN 口，先确认视觉设备是否透明桥接；
5. 不要同时保留两条互相连接的上联线，否则可能形成二层环路；
6. 接线后观察对应端口 Link 灯，记录亮灯端口和速率。

### 4.2 只有 Wi‑Fi 转以太网/NAT 设备

向厂家确认以下问题并记录答案：

- 工作模式是 `Bridge/AP/透明桥`，还是 `Router/NAT/Client`？
- 是否启用 DHCP？是否存在 WAN/LAN 隔离？
- AUBO 网线和视觉 LAN 是否共享同一个二层广播域？
- 是否支持多个有线客户端同时互通？

只有在 `Bridge/AP` 且 DHCP/NAT 不会隔离控制网时，才可把设备 LAN 接入交换机。
若设备是 NAT 客户端，不能只把它的地址改成 `192.168.1.x`；应让厂家改成桥接
模式，或在 AUBO、视觉和 AGV 外部口之间增加独立二层交换机。

### 4.3 不确定内部拓扑

使用“独立交换机方案”：总控电脑、AUBO、视觉和 AGV 外部/上装口全部接入同一个
已确认的非网管二层交换机。此方案只改变网线拓扑，不改 AGV/AUBO 控制器地址。

不要通过端口扫描来推断设备；每个地址/端口都必须来自厂商资料或示教器/控制器
页面的现场确认。

## 5. 阶段 C：总控电脑配置

### 5.1 记录当前网卡

在现场控制电脑上执行：

```powershell
Get-NetAdapter -Name '以太网' |
  Format-List Name,Status,LinkSpeed,MacAddress

Get-NetIPAddress -InterfaceAlias '以太网' -AddressFamily IPv4 |
  Format-Table InterfaceAlias,IPAddress,PrefixLength,AddressState

Get-NetIPConfiguration -InterfaceAlias '以太网' |
  Format-List InterfaceAlias,IPv4Address,IPv4DefaultGateway,DNSServer
```

记录当前有线地址通常为 `192.168.1.106/24`。WLAN 应保持关闭，或使用不同网段；
不要让 WLAN 也配置 `192.168.1.0/24`，以免路由选择不确定。

### 5.2 确认 `.11` 没有冲突

交换机和设备接好后，先由现场负责人确认 `.11` 没有分配给其他设备。可以辅助
执行：

```powershell
ping 192.168.1.11 -n 2
arp -a
```

无 ping 响应不是地址空闲的充分证明；若 ARP 或厂商记录显示 `.11` 已在使用，
立即停止，不运行改址脚本。

### 5.3 执行项目改址脚本

确认无冲突后，在管理员 PowerShell 执行：

```powershell
.\scripts\Set-CentralControllerIp.ps1 `
  -InterfaceAlias '以太网' `
  -TargetAddress '192.168.1.11' `
  -PrefixLength 24 `
  -PreviousAddress '192.168.1.106'
```

该脚本只切换有线 IPv4 地址，不设置有线默认网关，不修改 WLAN/DNS。执行后再次
确认：

```powershell
Get-NetIPAddress -InterfaceAlias '以太网' -AddressFamily IPv4 |
  Format-Table InterfaceAlias,IPAddress,PrefixLength,AddressState
```

若脚本提示 Duplicate、网卡不是 Up 或地址异常，停止网络测试并保留错误信息。

## 6. 阶段 D：逐项只读验证

### 6.1 先验证二层/三层基本连通

```powershell
ping 192.168.1.2 -n 1
ping 192.168.1.102 -n 1

Test-NetConnection 192.168.1.2 -Port 19204 -InformationLevel Quiet
Test-NetConnection 192.168.1.102 -Port 30004 -InformationLevel Quiet
Test-NetConnection 192.168.1.102 -Port 9012 -InformationLevel Quiet
```

记录每项结果和时间。不要把 ping 通解释为应用协议已通过；还必须验证端口。

### 6.2 按现象定位，不要盲目改址

| 现象 | 含义/处理 |
|---|---|
| AGV 与 AUBO 都不通 | 检查 PC 网卡、交换机供电、线缆、子网掩码和是否插错交换机；不要改设备 IP |
| AGV 通、AUBO 不通 | AGV 外部口可能未桥接 AUBO；检查 AUBO 线、交换机端口和视觉/NAT 拓扑 |
| AUBO ping 通、30004 不通 | 检查示教器网络页、控制箱服务、防火墙和端口资料；不要扫描其他端口 |
| 30004 可达但裸 JSON 超时 | 当前控制器的 SDK TCP 需要专用连接/登录处理；现场只读改走已验证的 WebSocket `9012` |
| 9012 不通 | 检查 AUBO WebSocket RPC 服务、控制器配置和防火墙 |
| 地址出现 Duplicate/ARP MAC 变化 | 立即停止，恢复原接线/地址并排查地址冲突 |
| Link 灯反复闪断 | 更换已确认的网线/端口；不要在设备运行中反复插拔 |

### 6.3 AUBO 只读 JSON-RPC 检查

只有 `192.168.1.102:9012` 可达后，才执行：

```powershell
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'

.\scripts\Invoke-AuboWsReadOnlyPreflight.ps1 `
  -ControllerHost '192.168.1.102' `
  -Port 9012 `
  -RobotName 'rob1' `
  -OutputPath ".\artifacts\aubo-readonly-$runId.json"
```

脚本只读取设备身份、机器人模式、安全模式、运行时状态、操作模式、预加载工程
和现有 Modbus 信号，并明确输出 `writesAttempted=false`、`canDetermineGo=false`。
首轮现场结果为 `Running / Normal / Stopped / Manual`，预加载工程为空；因此不能
启动程序。

第一轮不要传 `-VariableKey`。只有厂家确认了 Lua 工程的实际键名，才可用该参数
读取变量；不得根据 `mes_cmd` 等代码默认值直接触发握手。

## 7. 阶段 E：可选的 Adapter 只读复核

当前 MES Adapter 的 AUBO 传输仍基于未通过真机的 TCP 行协议，现场暂不启动本节。
应先切换为已验证的 WebSocket `9012` 并完成回归；以下命令仅保留为后续实现目标：

```powershell
dotnet build .\MesControlAgv.sln -c Release

$runId = "aubo-ro-$(Get-Date -Format yyyyMMdd-HHmmss)"
$db = Join-Path $env:TEMP "$runId-adapter.db"

.\scripts\start-physical-acceptance-adapter.ps1 `
  -ExpectedRunMode 'read-only-preflight' `
  -ControllerHost '192.168.1.2' `
  -EnableAuboReadOnly `
  -AuboHost '192.168.1.102' `
  -AuboPort 30004 `
  -AuboRobotName 'rob1' `
  -AdapterDatabasePath $db `
  -RunId $runId
```

随后只调用 GET：

```powershell
Invoke-RestMethod 'http://127.0.0.1:5041/health'
Invoke-RestMethod 'http://127.0.0.1:5041/api/adapter/devices'
Invoke-RestMethod 'http://127.0.0.1:5041/api/robot-arms/ARM-01/status'
Invoke-RestMethod 'http://127.0.0.1:5041/api/robot-arms/ARM-01/readiness'
```

确认输出中的 `ARM-01` 为 `enabled=true`、`controlEnabled=false` 后，使用启动
脚本输出的 `StatePath` 停止 Adapter：

```powershell
.\scripts\stop-local.ps1 -StatePath '<启动输出的 StatePath>'
```

不要调用 `/handshake` 或 `/handshake/dispatch`；后者属于写入/触发路径。

## 8. 回退和异常处理

出现以下任一情况，立即停在现场并保持 NO-GO：

- 设备地址冲突、交换机形成环路、Link 灯异常或设备重启；
- 视觉/NAT 拓扑无法确认；
- AUBO 处于人工/示教、急停、保护停机、错误或未知模式；
- JSON-RPC 响应格式与厂家资料不一致；
- 现场有人进入工作区，或 AGV/机械臂出现任何非预期动作。

回退顺序：

1. 停止所有只读脚本和本地 Adapter；
2. 记录当前端口、Link 灯、地址和错误信息；
3. 按接线照片恢复原始网线拓扑；
4. 如需恢复总控电脑旧地址，由管理员按现场网络方案恢复，确认无地址冲突后再操作；
5. 不通过重启控制器、恢复出厂或修改 AUBO/AGV 地址来“试错”；
6. 将现场照片、端口表、ping/端口结果和只读 JSON 一起交给厂家/负责人审阅。

## 9. 本轮完成标准

本轮网络桥接只有在以下证据齐全时才算完成：

- [ ] 交换机/桥接设备型号和工作模式已确认；
- [ ] PC、AGV、AUBO、视觉的线缆去向和端口已记录；
- [ ] 总控有线地址为 `192.168.1.11/24`，无有线默认网关；
- [ ] `192.168.1.102:30004`（SDK TCP）和 `:9012`（WebSocket RPC）可达；
- [ ] AUBO 只读 JSON-RPC 返回有效响应，工程/模式结果已保存；
- [ ] 未写变量、未启动程序、未运动、未触发 I/O、未获取 AGV 控制权；
- [ ] 本轮未使用尚未完成 WebSocket 适配的 C# Adapter 控制 AUBO；
- [ ] 所有现场证据已保存并由现场负责人/厂家审阅。

完成以上项目后，仍不能自动进入运动测试。下一阶段必须另行确认 Lua 四项握手
契约并取得空载、低速、急停可用的书面授权。
