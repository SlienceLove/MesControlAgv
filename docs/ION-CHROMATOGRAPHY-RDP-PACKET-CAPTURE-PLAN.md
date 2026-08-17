# 离子色谱控制电脑远程抓包执行计划

适用场景：控制电脑与仪器已经能够通过原厂/公司控制软件正常通信；操作人员通过 Windows 远程桌面进入控制电脑，在控制电脑本地被动采集通信报文。

本计划的第一目标是获取真实协议证据，不是现场替换控制软件。第一轮不得从中控向仪器注入报文。

> **CIC-D160+ 适用性更正**：取得设备手册和 ShineLab 日志后，已确认样例中的 CIC-D160+ 使用“DB 接头数据线 → USB 虚拟串口”，协议为带 CRC 的 Modbus RTU。它的底层报文不会出现在局域网 Wireshark 抓包中。对于该型号，应优先执行 [ShineLab 与 CIC-D160+ 通讯分析](ION-CHROMATOGRAPHY-SHINELAB-ANALYSIS.md) 中的串口监控方案。本文件后续 Wireshark/pktmon 步骤仅适用于确认走 Ethernet TCP/UDP 的其他型号或上层 LIMS 接口。

## 1. 执行范围和停止条件

### 1.1 第一轮允许执行

- 查看控制电脑网络配置和现有连接；
- 安装或启动已批准的 Wireshark/Npcap；
- 被动捕获原控制软件产生的网络流量；
- 在控制软件中执行已批准的状态查询；
- 如现场负责人批准，在测试样品/空载条件下执行一次完整分析；
- 保存 pcapng、动作时间表、原始 TCP Stream 和结果文件样例。

### 1.2 第一轮禁止执行

- 端口扫描、网段扫描、ARP 欺骗或中间人代理；
- 修改仪器 IP、控制电脑 IP、防火墙或路由；
- 从中控重放启动、停止、复位、方法下载报文；
- 在正式样品运行过程中安装驱动、重启或退出控制软件；
- 将原始 pcap、样品信息、账号/token 上传到公共网盘或公共聊天工具。

### 1.3 立即停止条件

- 控制软件离线、仪器状态异常或正在执行正式样品；
- 安装 Npcap 提示必须重启，而现场没有批准停机；
- 捕获动作导致控制软件卡顿、通信中断或仪器报警；
- 无法确认当前操作是否会改变流路、压力、进样或废液状态；
- 现场负责人要求停止。

停止时只停止抓包，不退出控制软件、不重启控制电脑、不修改网络。

## 2. 现场准备清单

执行前由现场负责人确认：

- [ ] 控制电脑和仪器均为公司设备，抓包活动已经批准；
- [ ] 已安排测试窗口，当前没有正式样品任务；
- [ ] 有熟悉仪器的操作员在场或在线；
- [ ] 确认仪器型号、序列号、固件版本、控制软件名称和版本；
- [ ] 获得控制电脑远程桌面账号；
- [ ] 如需安装软件，获得本机管理员权限；
- [ ] 确认控制软件是否支持远程桌面会话；
- [ ] 准备至少 2 GB 的受控磁盘空间；
- [ ] 准备测试方法、测试样品或空载验证步骤；
- [ ] 明确异常时由谁操作停止、急停和恢复。

推荐由两人配合：一人负责仪器和控制软件，一人负责抓包和记录。

## 3. 远程桌面连接注意事项

1. 远程连接前让现场操作员确认当前仪器处于 `Idle`、`Ready` 或厂商定义的安全状态。
2. 连接后不要选择“注销”、不要重启控制电脑。任务结束时通常只关闭远程桌面窗口，让会话保持运行；具体以控制软件要求为准。
3. 如果控制软件只允许控制台会话、RDP 后黑屏、界面丢失或设备断开，立即退出本次验证，改由现场操作员本机执行 Wireshark。
4. 关闭不必要的磁盘、打印机和剪贴板重定向，证据通过公司批准的受控位置传递。
5. RDP 本身会产生大量网络流量。后续必须按“仪器 IP”过滤，不能按整张网卡流量判断协议。
6. 记录远程连接开始和结束时间，便于把 RDP 流量与仪器流量区分。

## 4. 建立现场记录目录

在控制电脑选择经过批准的数据盘，例如：

```powershell
$captureRoot = 'D:\IonCapture\2026-08-17'
New-Item -ItemType Directory -Path $captureRoot -Force
New-Item -ItemType Directory -Path (Join-Path $captureRoot 'streams') -Force
```

日期应替换为现场日期。不要使用系统根目录或控制软件安装目录。

建议最终结构：

```text
D:\IonCapture\2026-08-17\
├── 00-environment.txt
├── 01-startup-and-status.pcapng
├── 01-actions.txt
├── 02-test-analysis.pcapng
├── 02-actions.txt
├── 03-result-read.pcapng
├── 03-actions.txt
├── streams\
│   ├── stream-0-client-to-device.bin
│   └── stream-0-device-to-client.bin
└── hashes.txt
```

## 5. 记录环境基线

在 PowerShell 中执行以下只读命令，把输出复制到 `00-environment.txt`：

```powershell
Get-Date -Format 'yyyy-MM-dd HH:mm:ss K'
Get-ComputerInfo | Select-Object WindowsProductName, WindowsVersion, OsBuildNumber
Get-NetAdapter | Format-Table Name, Status, LinkSpeed, MacAddress, InterfaceDescription
Get-NetIPConfiguration
Get-Process | Sort-Object ProcessName | Select-Object ProcessName, Id, Path
```

记录但不要在公开文档中保留真实密码、token 或敏感样品编号。

如果知道控制软件进程名，查看它的 TCP 连接：

```powershell
$controlProcess = Get-Process '<控制软件进程名>'
Get-NetTCPConnection |
  Where-Object { $controlProcess.Id -contains $_.OwningProcess } |
  Format-Table LocalAddress, LocalPort, RemoteAddress, RemotePort, State
```

如果存在多个同名进程，逐个记录进程 ID，不要强制结束任何进程。

## 6. Wireshark 方案（首选）

### 6.1 安装与启动

1. 优先使用公司批准、提前下载并校验过的 Wireshark 安装包。
2. 安装 Npcap 时使用默认网络捕获选项即可；不需要无线监听模式，也不要启用不理解的兼容选项。
3. 如果安装程序要求重启，停止安装并申请停机窗口。不得在仪器运行中重启。
4. 打开 Wireshark，选择控制电脑实际连接仪器网络的以太网卡。
5. 如无法捕获且提示权限不足，再以管理员身份运行 Wireshark。

可在每张网卡旁观察实时流量曲线。不要选择 RDP 虚拟网卡、VPN 网卡或未连接网卡。

### 6.2 确认仪器 IP

优先从控制软件配置、设备设置页面或现有连接中确认仪器 IP。不要扫描网段。

如果仪器 IP 暂时未知：

1. Wireshark 不加捕获过滤器开始抓包；
2. 在控制软件中只执行一次“刷新状态/重新连接”；
3. 观察新出现的 TCP/UDP 连接；
4. 结合设备 MAC、连接时间和 `Get-NetTCPConnection` 确认目标；
5. 确认后停止捕获，重新开始正式采集。

### 6.3 设置显示过滤器

假设仪器 IP 为 `192.168.1.50`：

```text
ip.addr == 192.168.1.50
```

只看 TCP/UDP：

```text
ip.addr == 192.168.1.50 && (tcp || udp)
```

确认端口后，例如 TCP 9000：

```text
ip.addr == 192.168.1.50 && tcp.port == 9000
```

过滤器只影响显示，不会删除已捕获的原始数据。正式保存时保留完整 pcapng。

## 7. 分阶段捕获

不要把所有操作放进一份无法定位的大文件。至少分成以下三段。

### 7.1 捕获一：软件启动、连接和状态查询

文件名：`01-startup-and-status.pcapng`

执行顺序：

1. 确认仪器安全、控制软件未执行正式任务；
2. Wireshark 开始捕获；
3. 如控制软件尚未启动，由仪器操作员正常启动；如已启动，只执行批准的“刷新/状态查询”；
4. 查询状态一次，等待 5 秒；
5. 再查询相同状态一次，等待 5 秒；
6. 不执行其他按钮；
7. 停止捕获并保存 pcapng。

同步填写 `01-actions.txt`：

```text
2026-08-17 10:00:00 +08:00 开始捕获
2026-08-17 10:00:12 +08:00 启动控制软件/刷新连接
2026-08-17 10:00:30 +08:00 第一次查询状态
2026-08-17 10:00:40 +08:00 第二次查询状态
2026-08-17 10:00:50 +08:00 停止捕获
仪器状态：Ready
操作员：<姓名或工号>
异常：无
```

这份捕获用于识别连接建立、登录、心跳、状态请求和动态字段。

### 7.2 捕获二：测试分析生命周期

文件名：`02-test-analysis.pcapng`

只有现场负责人明确批准后执行。使用测试样品、测试方法或仪器厂商允许的空载模式。

每一步之间等待并记录时间：

1. 开始捕获；
2. 选择或载入测试方法；
3. 设置唯一测试样品号，例如 `CAPTURE-20260817-001`；
4. 启动一次测试分析；
5. 分别记录 Ready、Running、Completed 状态查询时间；
6. 正常等待完成；如必须验证停止，单独安排另一轮，不与首次成功分析混在一起；
7. 停止捕获，保存 pcapng 和动作表。

如果出现压力、流路、温度、废液或进样异常，立即按仪器操作规程处理，不要为了抓完整报文继续运行。

### 7.3 捕获三：结果读取

文件名：`03-result-read.pcapng`

1. 开始捕获；
2. 在控制软件中打开刚才的唯一测试样品；
3. 执行查看结果、刷新结果或导出结果；
4. 记录生成的文件名、目录、文件时间和样品 ID；
5. 停止捕获并保存。

如果没有明显网络响应，应进一步确认结果是否来自本机数据库、共享目录、CSV/XML 或控制软件内部缓存。

## 8. Windows 内置 pktmon 备用方案

不能安装 Wireshark 时，可在管理员 PowerShell 使用 `pktmon`。以下示例假设仪器 IP 为 `192.168.1.50`：

```powershell
$captureRoot = 'D:\IonCapture\2026-08-17'
pktmon filter remove
pktmon filter add IonInstrument -i 192.168.1.50
pktmon start --capture --pkt-size 0 --file-name (Join-Path $captureRoot '01-startup-and-status.etl')
```

然后只执行已批准的状态查询。完成后：

```powershell
pktmon stop
pktmon etl2pcap (Join-Path $captureRoot '01-startup-and-status.etl') `
  -o (Join-Path $captureRoot '01-startup-and-status.pcapng')
pktmon filter remove
```

不同 Windows 版本命令可能略有差异，执行前先查看：

```powershell
pktmon start help
pktmon etl2pcap help
```

`pktmon` 适合收集原始包；后续仍建议在分析电脑使用 Wireshark 打开 pcapng。

## 9. 导出 TCP Stream

在 Wireshark 中：

1. 选择一条仪器 TCP 数据包；
2. 右键选择 **Follow → TCP Stream**；
3. 记录 `tcp.stream` 编号；
4. 分别查看客户端到仪器、仪器到客户端两个方向；
5. 文本协议保存 ASCII/UTF-8；二进制协议选择 Raw 后保存；
6. 文件命名应包含 stream 编号和方向；
7. 返回主界面使用以下过滤器核对完整时序：

   ```text
   tcp.stream eq 0
   ```

重点标记：固定包头、长度、命令号、序号、时间戳、token、样品 ID、响应码、结束符和校验字段。

## 10. 结果校验和证据封存

在控制电脑计算文件哈希：

```powershell
$captureRoot = 'D:\IonCapture\2026-08-17'
Get-ChildItem -LiteralPath $captureRoot -File -Recurse |
  Get-FileHash -Algorithm SHA256 |
  Format-Table Path, Hash |
  Out-File -LiteralPath (Join-Path $captureRoot 'hashes.txt') -Encoding utf8
```

封存要求：

- 原始 pcapng 只读保留，不直接编辑；
- 另做脱敏副本用于开发分析；
- 脱敏至少覆盖账号、token、样品信息、人员信息和不应公开的 IP；
- pcapng、动作表、控制软件版本、结果样例必须成套保存；
- 通过公司批准的文件传输方式取回，不使用公共网盘。

## 11. 现场结果判断

### 情况 A：看到明文 TCP/UDP

按动作时间拆分请求和响应，先识别状态查询。下一步仅在隔离测试环境使用 `MesControlAgv.DeviceProtocolTester` 重放已确认的只读请求。

### 情况 B：看到 TLS

保留握手、证书、端口和连接时序。联系设备开发人员提供测试环境协议日志、调试固件或合法的 TLS 会话密钥日志；不要做生产环境中间人解密。

### 情况 C：只看到心跳，没有操作报文

检查是否选择了错误网卡、操作实际走了另一个端口、控制软件使用本机服务，或应用只写本机数据库/文件。

### 情况 D：完全没有仪器网络流量

确认仪器是否通过 RS-232/485、USB、本机服务、命名管道或文件目录连接。网络抓包不能覆盖这些介质。

### 情况 E：抓包后仪器通信异常

立即停止捕获，保持控制软件和仪器现状，记录时间和现象，由仪器负责人按原流程恢复。不要临时修改防火墙、网卡驱动或重启设备。

## 12. 抓包后给开发人员的材料

最低交付内容：

- `00-environment.txt`；
- 三段原始 pcapng 和对应动作时间表；
- 客户端到仪器、仪器到客户端的原始 TCP Stream；
- 仪器型号/固件、控制软件名称/版本；
- 测试样品号和结果文件样例；
- 哪些动作经过授权、哪些报文禁止重放；
- SHA-256 哈希文件。

开发阶段先完成“只读连接、状态查询、断线和错误响应”验证。启动分析、停止、复位和方法下发必须在协议字段、幂等规则、控制权和异常恢复均明确后再实现。
