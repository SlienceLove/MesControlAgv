# AUBO 机械臂现场只读联通运行手册

更新时间：2026-08-31  
适用范围：总控电脑、交换机、AUBO 控制器。此阶段不做 AGV 移动、I/O 触发、
变量写入、程序启动、夹爪动作或仪器连接。

## 当前结论与硬门禁

当前项目进度已将机械臂联通列为现场 P0。代码已经具备 AUBO JSON-RPC
只读驱动、运行/安全模式读取、预加载工程读取和握手快照读取；物理控制写入仍
默认关闭。现场结果不能由历史抓包或本文件自动判定为 GO，必须由现场负责人和
厂家共同审阅。

2026-08-31 现场实测已修正传输结论：`30004` 可建立 TCP，但裸“单行 JSON + 换行”
客户端未完成 SDK 的连接/登录处理而超时；控制器实际开放 WebSocket JSON-RPC
`9012`，已通过 `ws://192.168.1.102:9012/` 成功读取。后续现场只读命令使用
`Invoke-AuboWsReadOnlyPreflight.ps1`，不再使用原始 TCP 行协议脚本。

四项握手契约仍需现场确认后才能进入空载握手：

1. 命令变量键；
2. 整数动作编号与 Lua 分支的对应关系；
3. `ack`、`result`（以及可选 detail）的实际键名和结果码；
4. Lua 完成回复的时序/方式。

本手册中的 `mes_cmd`、`mes_seq`、`mes_ack`、`mes_result`、
`mes_result_detail` 只是代码默认候选，未经现场确认不得据此写入或触发动作。

## 现场凭据（受限记录）

- **操作模式密码（Manual ↔ Automatic）：`1`**；来源：现场操作人员于 2026-08-31
  确认。
- 该密码不是 MES 配置、RPC 参数或程序启动参数；不得写入脚本、环境变量、日志或
  自动化命令，也不要在非授权人员面前展示。
- 如厂家/管理员后续修改密码，应立即更新本节并废止旧值；密码不确定时不要反复尝试，
  交由授权人员按示教器密码管理流程处理。

## 已批准的网络参数

| 设备 | 地址/端口 | 本阶段用途 |
|---|---|---|
| 总控电脑有线网卡 | `192.168.1.11/24` | 交换机接入后的控制网地址；旧现场记录的 `.106` 仅作历史参考 |
| AUBO SDK TCP RPC | `192.168.1.102:30004` | TCP 可达；需要 SDK 连接/登录处理，不使用裸行协议 |
| AUBO WebSocket RPC | `192.168.1.102:9012` | 已现场验证的 JSON-RPC 只读入口 |
| AGV 控制器 | `192.168.1.2` | 本阶段不移动；仅在网络拓扑需要时确认在线 |
| 视觉模块 | 地址待现场确认 | 不扫描未知端口，不沿用旧记录的 `.101` |

子网掩码为 `255.255.255.0`，交换机不启用 DHCP/NAT。不要把 AGV 上装口
`192.168.192.5` 混入控制网，也不要把视觉或机械臂地址配置到总控电脑。

## 阶段 0：接线和安全

由现场人员完成以下确认后再通电/插线：

- AGV 停止、机械臂程序停止，急停和人工接管路径有人负责；
- 作业区隔离，机械臂工作空间内无人员和障碍物；
- 只纳入一台 AGV；不触发实体 DI/DO；
- AUBO 网线、AGV 外部/上装网口、中控网线分别接入交换机；
- 如果视觉设备只有一个 LAN 口，先让厂家确认桥接/AP 能力，不要盲插造成 NAT 隔离；
- 不拆控制柜，不改 16XT7/16XT8，不接未知“上装2”线。

## 阶段 1：总控电脑改址和网络确认

交换机和设备均接好后，在管理员 PowerShell 中先查看网卡和当前地址：

```powershell
Get-NetAdapter -Name '以太网' | Format-List Name,Status,LinkSpeed,MacAddress
Get-NetIPAddress -InterfaceAlias '以太网' -AddressFamily IPv4 |
  Format-Table InterfaceAlias,IPAddress,PrefixLength,AddressState
```

确认 `192.168.1.11` 未被其他设备使用后，再执行项目已有改址脚本。脚本只改
有线网卡地址，不改 WLAN、DNS 或有线默认网关：

```powershell
.\scripts\Set-CentralControllerIp.ps1 -InterfaceAlias '以太网' `
  -TargetAddress '192.168.1.11' -PrefixLength 24 -PreviousAddress '192.168.1.106'
```

改址后确认有线网卡为 `192.168.1.11/24`，且该网卡没有默认网关；WLAN 如需保留
必须使用不同网段。

## 阶段 2：只读 TCP 联通

每项只测一次，不对视觉或其他未知端口做扫描：

```powershell
ping 192.168.1.102 -n 1
Test-NetConnection 192.168.1.102 -Port 30004 -InformationLevel Quiet
Test-NetConnection 192.168.1.102 -Port 9012 -InformationLevel Quiet
```

可选地确认 AGV 网络存在，但不要连接 AGV 命令/控制权端口：

```powershell
ping 192.168.1.2 -n 1
Test-NetConnection 192.168.1.2 -Port 19204 -InformationLevel Quiet
```

若 AUBO 不通，先检查交换机端口、AUBO 控制箱网口、示教器网络页和防火墙；
不要在现场随意改 AUBO 地址或端口。

## 阶段 3：AUBO JSON-RPC 只读预检

项目新增并现场验证脚本 `scripts/Invoke-AuboWsReadOnlyPreflight.ps1`。它会：

- 只建立到指定 `Host:Port` 的 WebSocket JSON-RPC 连接；
- 只读取机器人模式、安全模式、运行时状态、操作模式和预加载工程；
- 只有显式传入 `-VariableKey` 时才读取变量，绝不自动猜测变量键；
- 默认同时读取现有 Modbus 信号名称、索引、类型、值和原始错误码；
- 输出 `writesAttempted=false`、`canDetermineGo=false`，不做 GO 判定；
- 通过 `-OutputPath` 保存一次性证据文件，已存在的文件不会被覆盖。

先执行不带变量的只读预检：

```powershell
$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
.\scripts\Invoke-AuboWsReadOnlyPreflight.ps1 `
  -ControllerHost '192.168.1.102' -Port 9012 -RobotName 'rob1' `
  -OutputPath ".\artifacts\aubo-readonly-$runId.json"
```

审阅输出中的字符串状态。现场首轮实测为 RobotMode=`Running`、SafetyMode=`Normal`、
Runtime=`Stopped`、OperationalMode=`Manual`、预加载工程为空，因此
`readyForProgramStart=false`。出现 Manual/Teach、保护停机、急停、错误或工程不匹配
时保持 NO-GO。

只有厂家/现场已经确认变量键后，才追加变量只读核对。例如确认的键确实是下列
候选时：

```powershell
$keys = 'mes_cmd','mes_seq','mes_ack','mes_result','mes_result_detail'
.\scripts\Invoke-AuboWsReadOnlyPreflight.ps1 `
  -ControllerHost '192.168.1.102' -Port 9012 -RobotName 'rob1' `
  -VariableKey $keys `
  -OutputPath ".\artifacts\aubo-readonly-vars-$runId.json"
```

变量读数的 `exists`、类型和值只作为现场证据；不存在、类型不符、结果残留或
键名不一致都应记录并暂停，不得通过写入“修正”。

本次实测五个 `mes_*` 候选键均不存在；控制器现有变量契约是 Modbus 信号
`总控输入`/`总控输出`，索引 `0/1`、类型 `3`、当前值均为 `0`。在工程和动作映射
未确认前，不写入这些信号。

## 阶段 4：通过 Adapter 复核（可选）

当前 C# Adapter 的 AUBO 传输仍基于未通过真机的 TCP 行协议，**现场暂不使用本节
启动 Adapter 读取 AUBO**。应先把驱动切换为已验证的 WebSocket `9012` 并完成回归。
以下命令仅保留为后续实现目标，不在本轮现场执行：

```powershell
dotnet build .\MesControlAgv.sln -c Release
$runId = "aubo-ro-$(Get-Date -Format yyyyMMdd-HHmmss)"
$db = Join-Path $env:TEMP "$runId-adapter.db"
.\scripts\start-physical-acceptance-adapter.ps1 `
  -ExpectedRunMode 'read-only-preflight' `
  -ControllerHost '192.168.1.2' `
  -EnableAuboReadOnly -AuboHost '192.168.1.102' -AuboPort 30004 -AuboRobotName 'rob1' `
  -AdapterDatabasePath $db -RunId $runId
```

记录启动脚本输出的 `StatePath` 后，在另一个 PowerShell 只读调用：

```powershell
Invoke-RestMethod 'http://127.0.0.1:5041/health'
Invoke-RestMethod 'http://127.0.0.1:5041/api/adapter/devices'
Invoke-RestMethod 'http://127.0.0.1:5041/api/robot-arms/ARM-01/status'
Invoke-RestMethod 'http://127.0.0.1:5041/api/robot-arms/ARM-01/readiness'
```

此阶段不要调用 `/handshake` 或 `/handshake/dispatch`。前者会读取默认握手键，
后者是写入/触发路径；四项契约未确认前两者都不属于本轮测试。

结束只读复核后，使用启动脚本输出的状态文件停止 Adapter：

```powershell
.\scripts\stop-local.ps1 -StatePath '<启动输出的StatePath>'
```

## 停止条件和回传材料

出现以下任一情况立即停止本轮并保持 NO-GO：端口不通、响应不是 JSON-RPC 2.0、
响应 ID 不匹配、模式/安全状态未知、示教器处于人工/急停/保护停机、预加载工程
不明、变量键未经确认、现场有人/障碍物进入工作区。

请回传以下信息即可进入下一轮评审：

1. `Invoke-AuboWsReadOnlyPreflight.ps1` 的终端输出或证据文件路径；
2. AUBO 示教器显示的 IP、端口、机器人名和当前工程名；
3. 厂家确认的命令/ack/result 键名、结果码和完成回复方式；
4. 任何错误码、超时或模式异常。

在这些材料审阅通过、并取得单独的空载握手书面授权前，不执行 `-AllowWrite`，
不启用 `ControlEnabled`，不调用 `set-int32`/`set-string`、`load`、`run`、`stop`，
不发送运动、夹爪、AGV I/O 或仪器命令。

## 示教器拔除前后的影响与验证

示教器是操作界面，不等同于 AUBO 控制器本身的 Ethernet/RPC 服务。若 AUBO
控制箱保持上电、`enp1s0` 网口仍接入控制交换机，且示教器没有承载唯一的网络桥，
拔除示教器通常不会让 `192.168.1.102:9012` 或 AUBO→中控的 Modbus TCP 路径
自动消失。但这不能替代现场验证。

拔除示教器可能影响：

- 操作模式切换、工程选择、弹窗确认和故障查看；
- 三档位使能、Enable Device、急停或其他安全 I/O 回路；
- 当前工程是否继续运行，以及是否进入 Manual/Disabled/Protective Stop；
- 如果示教器或无线设备实际承担网络桥接，AUBO 的 IP 可达性本身也会丢失。

官方说明：自动/手动/联动模式由状态栏模式切换，若配置了 Operational Mode 安全
输入，模式只能由 I/O 切换；自动模式必须有完整安全防护，不能仅凭“RPC 还能连上”
判定可以运行。

只有在厂家确认允许拔除，并完成以下步骤后才可做一次断开试验：

1. 先让工程进入 `Stopped`，不要在 `Running`、运动中或 `trr` 弹窗等待时拔线；
2. 记录拔除前的 RobotMode、SafetyMode、OperationalMode、Runtime、工程名和
   `9012/30004` 端口结果；
3. 保持控制箱供电和 AUBO Ethernet 网线不动，由有资质人员拔除示教器连接；
4. 立即确认本地急停、外部安全回路和人工停止路径仍可用；
5. 重新执行 `Test-NetConnection 192.168.1.102 -Port 9012` 和 WebSocket 只读预检；
6. 只有在网络仍通、Safety=`Normal`、模式与工程符合厂家要求、Runtime=`Stopped`
   时，才记录为“通讯保持”；模式变为 Manual/Disabled 或安全状态异常时保持 NO-GO；
7. 首次断开试验不要同时启动工程、写 Modbus HR0/HR1 或触发 I/O。

若拔除后网络不通，先恢复示教器和原始网络拓扑，不要创建 Windows Bridge/ICS，
也不要修改 AUBO IP、端口或安全输入配置。
