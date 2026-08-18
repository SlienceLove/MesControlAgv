# ShineLab 与 CIC-D160+ 通讯分析

分析日期：2026-08-17 至 2026-08-18

分析范围：`res/ShineLab`、CIC-D160+ 用户手册、ShineLab 工作站使用说明书

分析方式：只读检查配置、日志、数据库结构、PE 导出表和手册；未启动 ShineLab，未连接或发送仪器指令。

## 1. 结论

现有材料已经足以纠正原来的验证方向：样例中的 CIC-D160+ 不是通过局域网 TCP 与控制电脑通讯，而是通过仪器 DB 数据口连接 USB 数据线，在 Windows 中使用虚拟串口。ShineLab 再通过串口发送带 CRC16 的 Modbus RTU 报文。

因此：

- 控制电脑与笔记本处在同一局域网，不能抓到 CIC-D160+ 的底层仪器报文；
- 控制电脑上的 Wireshark 网卡捕获也看不到这条虚拟串口通讯；
- 正确验证手段是串口监控软件、USBPcap，或者经过确认引脚和电气标准后的硬件串口分析仪；
- 中控正式实现应增加一个独立的 `CIC-D160+ Serial/Modbus RTU Driver`，不能继续按 TCP/HTTP 仪器驱动假设推进。

需要注意：`res/ShineLab` 是已编译的程序安装目录，不是完整 C/C++ 源码仓库。目录中有 EXE、DLL、SQL、配置、日志和少量 Python 脚本，但没有 `.cpp/.h/.sln/.vcxproj`。日志保留了源文件名和行号，DLL 也保留了大量导出符号，所以可以分析协议结构；若要可靠获得全部寄存器表，仍应向内部研发索取真正的 `ShProtocol`、`ShPipe`、`ShDevice`、`ShDevicePlus`、`ShDeviceFamily` 源码和协议头文件。

## 2. 已确认的物理连接

CIC-D160+ 用户手册明确说明：

- 仪器与电脑之间通过数据线连接；
- 数据线 DB 接头接仪器后面板 DB 插口；
- USB 端接控制电脑 USB 插口；
- 软件配置时手动选择端口并执行“自动连接”。

同一手册还说明 SHA-18 自动进样器使用 RS-232 与电脑连接。现有 ShineLab 日志中的样例配置包含 CIC-D160+ 和 SHA-18i，它们是两个独立串口设备，不应合并为一条通讯链路。

## 3. 已确认的串口参数

ShineLab 日志记录了以下数据库写入：

```text
insert into tb_serialconfig values(<device-guid>, 3, 115200, 0, 8, 0, 0)
insert into tb_serialconfig values(<device-guid>, 4, 115200, 0, 8, 0, 0)
```

数据库结构定义了字段顺序：

```text
DeviceGuid, Com, Baud, Parity, ByteSize, StopBits, Pipe
```

字段注释说明 `Parity=0` 为无校验，`StopBits=0` 为 1 个停止位。因此材料中的串口参数为：

| 参数 | 值 |
|---|---|
| Windows 端口 | 样例曾选择 COM3、COM4；现场以设备管理器和 ShineLab 配置为准 |
| 波特率 | 115200 |
| 数据位 | 8 |
| 校验 | None |
| 停止位 | 1 |
| 简写 | 115200 8N1 |

日志还显示配置 D160+ 时删除了对应 `tb_tcpconfig`，随后写入 `tb_serialconfig`，进一步排除了该样例使用 TCP 的可能。

## 4. 已确认的协议格式

日志记录了一条失败请求：

```text
01 04 19 00 00 14 F7 59
```

按 Modbus RTU 解析：

| 字段 | 值 | 含义 |
|---|---:|---|
| Slave | `01` | 从站地址 1 |
| Function | `04` | 读取输入寄存器 |
| Start | `1900` | 起始地址 `0x1900` |
| Quantity | `0014` | 读取 20 个寄存器 |
| CRC | `F7 59` | Modbus CRC16，小端发送 |

对前 6 字节重新计算 CRC 得到 `0x59F7`，线上顺序正是 `F7 59`，因此这不是格式猜测，而是完整通过校验的 Modbus RTU 帧。该帧发生在“打开设备通讯/自动连接”阶段，较可能属于设备识别或基础状态读取；在看到成功响应前，不能把 `0x1900` 直接命名为某个具体业务状态。

## 5. DLL 提供的协议能力证据

`ShProtocol.dll` 和当前设备 DLL 都是 32 位 x86 PE。导出符号及导入函数确认：

- 通过 Windows `CreateFile` 打开 COM 口；
- 使用 `SetCommState`、`SetCommTimeouts`、`ReadFile`、`WriteFile` 和 `ClearCommError`；
- 实现 Modbus CRC/LRC；
- 支持功能码 `01/03/04/05/06/15/16`，当前框架还存在自定义/扩展 `07`；
- 有串口发送、等待响应、接收长度校验、CRC 校验和超时处理；
- 上层能力包括连接、设备识别、读取状态、发送方法、启动、停止、进样、复位、泵、温控、抑制器、淋洗液、阀和检测器控制。

关键类/符号包括：

```text
CSerialPipe
ShSerialPipe
ShFormatModbus
ShDevice_Modbus
ShSubDeviceProtocol
ShFactoryD160Plus
ShDevice_D160
```

这说明正式驱动可以按“串口传输层 → Modbus RTU 编解码 → D160+ 寄存器映射 → 仪器能力接口”四层实现。

## 6. 从旧版通用协议 DLL 提取的寄存器线索

以下地址来自 `ShProtocol.dll` 反汇编。它们能指导源码检索和现场对照，但该 DLL 可能是兼容旧型号的通用模块，不能在尚未确认适用版本时直接向真实 D160+ 写入。

### 6.1 读取线索

| 操作符号 | 功能码 | 起始寄存器 | 数量 |
|---|---:|---:|---:|
| `ReadVersion` | 04 | `0x0FA0` | 3 |
| `ReadDetectorConst` | 04 | `0x1778` | 1 |
| `ReadDetectorData` | 04 | `0x1770` | 12 |
| `ReadTempDatas` | 04 | `0x17D4` | 12 |
| `ReadSuppDatas` | 04 | `0x1838` | 4 |
| `ReadSuppEluentDatas_180` | 04 | `0x1838` | 9 |

### 6.2 写入线索（禁止未经确认直接现场使用）

| 操作符号 | 功能码 | 寄存器 | 编码线索 |
|---|---:|---:|---|
| `WriteDetectorTemp` | 06 | `0x1388` | 温度 × 100 |
| `WriteColTemo` | 06 | `0x1389` | 温度 × 100 |
| `WriteDetectorConst` | 06 | `0x139A` | 数值 × 1000 |
| `WritePumpFlow` | 06 | `0x13DA` | 流量 × 1000 |
| `WriteMultiPositionValve` | 06 | `0x13E2` | 整数位置 |
| `WriteSupp` | 06 | `0x13FC` | 整数值 |
| `WriteEluentConcent` | 06 | `0x13FE` | 浓度 × 10 |
| `WriteTempOpen` | 06 | `0x157C` | 位标志 |
| `WritePumpOpen` | 06 | `0x157D` | 0/1 |
| `WriteValveMode` | 06 | `0x157E` | 模式值 |
| `WriteSuppEluentOpen` | 06 | `0x157F` | 位标志 |

这些地址必须通过当前版本源码、成功串口记录或公司内部寄存器表三者之一确认后，才能进入测试驱动。

## 7. ShineLab 的业务边界

工作站手册区分了两类接口：

1. **向下控制仪器**：串口/Modbus RTU，负责设备状态、方法、启动停止和实时数据；
2. **向上对接 LIMS**：文件导出、数据库查询视图、定制 TCP 或 HTTP，负责结果输出。

此前拿到的“文件协议”属于第二类，适合中控读取任务结果，但不能替代第一类来直接控制泵、阀、检测器或启动采集。

从交付风险看，可以分两阶段：

- 第一阶段通过 ShineLab 的数据库视图/文件导出读取结果，由原软件继续控制仪器；
- 第二阶段完成 D160+ 串口协议验收后，中控才直接控制仪器。

## 8. 现场抓取方案应调整为串口

### 8.1 首选：控制电脑软件串口监控

在控制电脑安装经过批准、支持现代 Windows 的串口监控工具，以只读方式附加到 ShineLab 使用的 COM 口，记录：

- 时间戳；
- TX/RX 方向；
- 原始十六进制；
- 每次 API 调用或 UI 动作；
- 串口打开/关闭和超时。

串口通常被 ShineLab 独占，不能同时打开 SSCOM 直接监听。不要用 SSCOM 与 ShineLab 抢占同一 COM 口。

### 8.2 备用：USBPcap

因为仪器通过 USB 数据线接电脑，可以在控制电脑用 USBPcap + Wireshark 捕获 USB URB，再按端点和方向还原串口字节。该方法不会依赖局域网，但分析难度高于串口监控工具。

### 8.3 硬件监听

只有在确认 DB 接头的引脚定义、电平标准（RS-232/RS-485/TTL）和方向后，才使用双向串口分析仪。不要仅凭 DB 外形直接并线，错误接线可能损坏接口。

### 8.4 如果能取得真正源码

最可靠的方法是在测试构建中给 `CSerialPipe::Send/Receive` 或 `ShSubDeviceProtocol::SendCommand/ProcessReceivedFrame` 增加十六进制审计日志。这样可以同时记录上层动作名、寄存器、原始报文、响应和解析结果，优于外部抓包。

## 9. 建议的只读现场动作

1. 设备管理器记录 D160+ 和 SHA-18i 对应 COM 号、USB VID/PID、驱动名称和版本；
2. 关闭正式序列，确认仪器处于安全等待状态；
3. 启动串口监控，然后由 ShineLab 正常“打开通讯”；
4. 仅观察自动识别和周期状态查询 30–60 秒；
5. 在 ShineLab 中执行一次手动“刷新/状态读取”，不改参数；
6. 停止监控并保存 TX/RX 原始日志；
7. 对每个帧校验 CRC、从站地址、功能码、寄存器和响应长度；
8. 重复一次，确认动态字段和固定字段；
9. 在完成寄存器映射审批前，不重放写功能码 `05/06/15/16`。

## 10. 下一步决策点

在编写 D160+ 串口验证器前，需要现场或设备研发确认：

- 真实设备是否确实为 CIC-D160+；
- 自动进样器是否为 SHA-18i；
- 当前 COM 号和 USB VID/PID；
- D160+ 与 SHA-18i 是否各占一个 COM 口；
- 是否能提供真正的 C++ 源码或 Modbus 寄存器表；
- 中控目标是“直接控制所有模块”，还是先通过 ShineLab 接收任务结果。

确认这些信息后，验证器应先实现：串口枚举、115200 8N1、Modbus CRC、只读帧发送、响应长度/从站/功能码/CRC 校验、原始会话归档和写入双重解锁。现场只读通过后，再按泵、温控、抑制器、淋洗液、阀、检测器、序列生命周期逐项开放能力。

## 11. 2026-08-18 安装包静态分析补充

本次只对 `res/ShineLab` 的离线副本执行了 PE 字符串/符号检查和日志分析，
没有启动任何 ShineLab 进程，也没有打开 COM4。

### 11.1 二进制边界

- `ShineDataAcquire-Normal.exe`、`ShineControl-Normal.exe`、
  `ShDevice.dll` 和 `ShDeviceFamily.dll` 是原生 MSVC C++/MFC 二进制，
  不是可直接用 ILSpy 还原的 .NET 程序。
- `ShDevice.dll` 保留了 `ShDevice_Modbus`、`ShSerialPort`、
  `Modbus_Tool` 和 `ShFormatModbus` 符号，明确包含 `ReadWords_03/04`、
  `WriteWord_06`、`WriteWords_16`、`SetInt`、`SetFloat`、串口超时和
  `CreateFile`/`ReadFile`/`WriteFile` 相关边界。
- `ShDeviceFamily.dll` 保留了 CIC-D160+ 专用工厂和协议类，包括
  `ShFactoryD160Plus`、`ShDevice_D160Plus`、
  `ShSubDeviceProtocol_D160PlusPump`、`...Temp`、`...Supp`、
  `...Eluent`、`...Detector` 和 `...Valve6`，以及 `SetFlow`、
  `SetPumpOpen`、`SetSupp`、`SetTemp`、`SetEluentOpen` 等写入能力符号。

这些符号证明软件具备对应能力，但不等价于当前固件的寄存器表，也不证明
任何一个写操作可以安全重放。

### 11.2 日志中的现场事实

- `ShDeviceDBTool` 记录 D160+ 曾配置 COM3 和 COM4，参数均为
  `115200, parity=0, byteSize=8, stopBits=0`，即 `115200 8N1`。
- `ShSubDeviceProtocol` 记录了请求
  `01 04 19 00 00 14 F7 59` 的 5 秒超时失败，这与当前只读验证器的
  请求和超时行为一致。
- 2026-08-10 的采集日志记录了泵流量 `300.01/500.01/600.01/700.01/1000.01`、
  泵开关、柱温/电导池温度 `35.00`、抑制器电流 `65`、淋洗液发生器开关和
  浓度 `15/50` 等动作；同时记录了多次淋洗器流量设置失败。

这些是上层业务日志，未包含对应的 TX/RX 十六进制报文。它们只能用于设计
后续“单动作、单抓包”的验证矩阵，不能直接转换成 Modbus 写帧。

### 11.3 与 USBPcap 的关联结论

2026-08-17 的 USBPcap 会话只包含周期性 `0x04` 读取和未知语义的
`0x06 0x13E4=0x5AA5` 写回显，没有包含上述 2026-08-10 手动动作的写帧。
因此目前仍无法把“设置流量/温度/抑制器/淋洗液”映射到已确认的地址和编码。
下一次现场抓包必须在明确授权的空载条件下，每次只做一个动作，并同时记录
动作前后状态、原始 TX/RX、响应 CRC 和失败行为。

## 12. 资料安全提醒

`res/ShineLab` 包含运行日志、数据库配置、设备标识以及明文服务凭据。该目录当前未被 Git 跟踪，不应直接提交到仓库。若需要保留分析样例，应先脱敏并仅提交最小必要的协议证据。
