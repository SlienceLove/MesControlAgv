# 离子色谱仪直连验证分支

分支：`feat/ion-chromatography-direct-control-validation`

## 结论先行

当前仓库没有离子色谱仪的厂商协议、设备型号、IP/端口、串口参数、命令表或响应码。已有的 `InstrumentOperation` 只是工作流节点名称，不能证明中控可以直接控制仪器；之前“文件协议”描述的是“控制软件与仪器之间”的交互，也不能直接假定中控可以复用。

本分支先提供一个独立的 `MesControlAgv.DeviceProtocolTester` 验证器。它不依赖 MES/WPF，默认只读，支持：

- TCP：发送厂商给出的文本或十六进制报文，捕获原始响应；
- HTTP/HTTPS：发送 GET 或经过明确授权的 JSON/文本请求，捕获状态码和响应；
- JSON 会话记录：请求、响应均保留 Base64，另附 UTF-8 解码文本，便于后续实现正式驱动和审计。

这一步验证的是“中控 PC 能否直接到达仪器端点，以及端点实际返回什么”，不是凭空实现某个未知厂商协议。

## 推荐落地架构

```text
WPF / MES 工作流
        │ 稳定业务能力（连接、状态、开始分析、停止、结果）
        ▼
IonChromatography Adapter（独立进程/模块）
        │ 厂商协议、重连、超时、幂等、错误映射、审计
        ▼
离子色谱仪（TCP / HTTP / 串口 / 厂商 SDK）
```

不要让 MES 直接拼接厂商报文。仪器驱动应实现专用 `IInstrumentDriver`，并把能力和状态转换成中控的通用契约。只有在直连验证、命令幂等规则和现场安全授权完成后，才把驱动注册到生产 Adapter。

## 使用验证器

先编译：

```powershell
dotnet restore src/MesControlAgv.DeviceProtocolTester/MesControlAgv.DeviceProtocolTester.csproj
dotnet build src/MesControlAgv.DeviceProtocolTester/MesControlAgv.DeviceProtocolTester.csproj --no-restore -p:UseSharedCompilation=false
```

TCP 文本只读探测（把报文替换成厂商文档中的“查询状态”命令）：

```powershell
dotnet run --project src/MesControlAgv.DeviceProtocolTester -- tcp `
  --host 192.168.1.50 --port 9000 `
  --request-text "STATUS\r\n" --stop-at-newline `
  --timeout-ms 3000 --output artifacts/ion-chromatography/status-session.json
```

TCP 二进制探测：

```powershell
dotnet run --project src/MesControlAgv.DeviceProtocolTester -- tcp `
  --host 192.168.1.50 --port 9000 `
  --request-hex "AA 55 01 00 00 00" `
  --timeout-ms 3000 --output artifacts/ion-chromatography/status-session.json
```

HTTP/JSON 查询：

```powershell
dotnet run --project src/MesControlAgv.DeviceProtocolTester -- http `
  --url http://192.168.1.50:8080/api/status `
  --method GET --timeout-ms 3000 `
  --output artifacts/ion-chromatography/status-session.json
```

任何会改变仪器状态的请求都必须显式使用：

```text
--mode write --confirm-write I-UNDERSTAND
```

现场第一次只做连通性和状态查询。启动分析、停止、复位、方法下载等写操作必须由仪器负责人单独授权，并在空载/废液和急停可用的条件下进行。

## 必须向厂商/现场补齐的信息

| 项目 | 必须确认的事实 | 未确认时的处理 |
|---|---|---|
| 设备型号/固件 | 精确型号、固件版本、选件 | 不实现正式驱动 |
| 物理接口 | Ethernet TCP、HTTP、RS-232/485、USB、SDK 或仅文件交换 | 只做对应传输探测 |
| 端点 | IP、端口、TLS/认证、服务端/客户端角色 | 不扫描、不猜端口 |
| 帧格式 | 编码、长度、大小端、校验、分包、结束符 | 原始报文保留 Base64 |
| 命令 | 状态、方法/序列、开始、暂停、停止、复位、结果读取 | 先只读命令 |
| 状态机 | Idle/Ready/Running/Paused/Error/Completed 及转移条件 | 不能直接映射工作流 |
| 结果 | 结果文件/JSON/数据库/推送事件、样品 ID 关联 | 先保存原始响应 |
| 并发/锁 | 控制软件是否独占设备、是否有登录/租约/控制权 | 不与原控制软件并行写入 |
| 安全 | 急停、流路、废液、压力、方法校验和权限 | 写操作保持关闭 |

## 现场验收顺序

1. **隔离网络**：中控 PC 与仪器接入同一隔离交换机；记录 IP、端口和防火墙规则，不修改生产网。
2. **只读连通**：用验证器完成 TCP/HTTP 连接、状态查询、重复查询和超时测试；保存 JSON 会话记录。
3. **协议确认**：对照厂商文档核对请求/响应字节、编码、校验和错误码；必要时在原控制软件运行期间只抓包，不注入报文。
4. **非破坏写入**：厂商确认后，在空载或测试方法下验证登录/获取控制权、启动、状态轮询和停止；每步保留操作员、时间和原始报文。
5. **异常与恢复**：拔网线、重启仪器、重复命令、错误参数、超时，确认驱动不会重复启动或把未知状态误判为完成。
6. **结果闭环**：以唯一 `sampleId`/批次号启动一次分析，读取完成状态和结果文件，证明结果可回溯到 MES 任务。
7. **再接入工作流**：只有以上证据齐全，才把驱动接入 `InstrumentOperation` 节点；先提供 dry-run 和人工确认，再开放自动执行。

## 验收证据最低要求

- 一份厂商协议或抓包说明，明确设备型号/固件和端点；
- 一份只读会话 JSON（成功、超时、错误响应各至少一条）；
- 一份写操作授权记录和启动/停止/恢复原始报文；
- 状态机映射表，以及重复命令和断线恢复结果；
- 结果文件样例，包含样品/批次关联字段；
- 现场回退方案：原控制软件如何恢复、如何释放中控控制权。

在这些证据缺失前，本分支不会声称“已实现离子色谱仪控制”，也不会把模拟响应接入生产 KPI。

## 没有协议时的现场抓包方法

抓包的目标是观察“原控制软件已经成功完成的动作”，不是猜测后向仪器重放写指令。第一次现场验证只做被动采集，不启用中控写入。

### 1. 先判定通信介质

| 现场连接 | 抓取方式 | 备注 |
|---|---|---|
| 控制 PC ↔ 仪器网线 | 交换机端口镜像 + Wireshark | 优先方案，不改变两端配置 |
| 控制 PC 直连仪器网口 | 在控制 PC 安装 Npcap/Wireshark | 只能在控制 PC 上抓，不能把网线拔下来“串接”普通电脑 |
| 控制软件和仪器服务在同一 PC | Wireshark 选 `Npcap Loopback Adapter` | 可能只看到 `127.0.0.1` 或本机命名管道 |
| RS-232/RS-485 | 双向串口监听器/串口分析仪 | 网卡抓包看不到串口字节；RS-485 必须同时记录方向 |
| USB 仪器 | USBPcap + Wireshark 或 USB 协议分析仪 | 需确认驱动是否加密/封装 |
| 文件交换协议 | Process Monitor + 目录只读监控 | 重点记录文件名、创建时间、内容、归档/删除动作 |

不要在生产交换机上临时改端口配置。端口镜像应由网络管理员在隔离交换机上完成，镜像目标端口只接抓包电脑。

### 2. 以太网抓包操作顺序

1. 记录控制 PC、仪器 IP、网卡名称、交换机端口和现场时区；不要先扫描未知设备或修改仪器 IP。
2. 在 Wireshark 选择镜像口对应网卡，先不加过滤器开始捕获；若知道仪器 IP，可使用显示过滤器：

   ```text
   ip.addr == 192.168.1.50
   ```

   若只知道控制 PC 和仪器之间的端口，再缩小为：

   ```text
   ip.addr == 192.168.1.50 && (tcp || udp)
   tcp.port == 9000
   ```

3. 点击“开始捕获”后，再启动原控制软件。每次只做一个动作，并在记录表写下精确时间：

   ```text
   10:00:00 软件启动/连接
   10:01:00 查询状态（重复两次）
   10:02:00 登录或获取控制权
   10:03:00 载入测试方法（如有）
   10:04:00 启动一次空载/测试分析
   10:05:00 停止或等待完成
   10:06:00 读取结果
   ```

4. 每个动作完成后等待 3–5 秒再做下一个动作，便于按时间切分会话。不要在同一段捕获里连续点击多个按钮。
5. 停止捕获，保存原始 `.pcapng`；同时导出一份脱敏副本。原始包只存放在受控目录，不上传公共网盘。

Windows 没有 Wireshark 时，可以用管理员 PowerShell 做底层采集：

```powershell
pktmon filter remove
pktmon filter add IonInstrument -i 192.168.1.50
pktmon start --capture --pkt-size 0 --file-name ion-control.etl
# 在原控制软件中只执行一个已记录的动作
pktmon stop
pktmon etl2pcap ion-control.etl -o ion-control.pcapng
```

`pktmon` 适合确认是否有流量；协议分析优先使用 Wireshark。不要把 `pktmon` 过滤器中的地址替换成生产网段范围。

### 3. 从捕获中提取协议事实

在 Wireshark 中对单条连接右键 **Follow → TCP Stream**，分别保存客户端到仪器、仪器到客户端的原始字节。重点记录：

- 服务端端口、客户端临时端口、连接建立/关闭方式；
- 是否为 ASCII、UTF-8、JSON、XML 或二进制；是否有 `CR/LF`、STX/ETX、长度字段；
- 请求与响应是否一一对应，是否有心跳、推送、重连；
- 同一“查询状态”重复两次时，哪些字段变化、哪些字段固定；
- 错误输入时的返回码和连接行为；
- 启动/停止是否需要会话 token、控制权、序号或校验和；
- 结果是响应报文返回，还是另写入 CSV/XML/数据库/共享目录。

可先用本分支验证器对确认过的**只读**原始报文做回环或隔离设备验证：

```powershell
dotnet run --project src/MesControlAgv.DeviceProtocolTester -- tcp `
  --host <测试仪器IP> --port <确认端口> `
  --request-hex "<从 Follow TCP Stream 提取的只读请求>" `
  --timeout-ms 3000 --output artifacts/ion-chromatography/read-only-session.json
```

不要直接重放从 pcap 提取的“启动、停止、复位、方法下载”报文。先确认其中的动态 token、序号、样品号和时间戳，并取得设备负责人书面授权。

### 4. 加密或抓不到明文时

- 若看到 TLS，只记录握手、证书、端口和时序；不要在生产环境做中间人解密。
- 优先向公司设备开发人员索取测试固件、协议调试日志或会话密钥日志（例如应用层 debug log），而不是破解加密。
- 若控制软件使用本机服务、命名管道或文件目录，网络 pcap 为空并不代表没有协议；改用 Process Monitor、API 日志或串口/USB 分析仪。
- 只有在控制软件和仪器均为测试环境时，才考虑将中控接入同一隔离网络做只读探测。

### 5. 抓包完成后的交付物

现场回来至少应有：原始 pcapng、脱敏 pcapng、动作时间表、TCP stream 原始字节、端口/协议判断、状态查询样例、错误样例、结果文件样例，以及“哪些报文禁止重放”的清单。凭这些证据才能开始编写离子色谱 Adapter，而不是从控制软件的文件名反推仪器协议。
