# SHA-18i 通讯协议取证阶段报告

更新日期：2026-08-28

## 1. 当前结论

对 `res/ShineLab` 安装包进行只读 PE 字符串、导入表、导出符号和局部反汇编检查后，可以确认：

- ShineLab 为 SHA-18 系列加载了独立的设备实现，不是把它当作 D160+ 的 Modbus 子模块处理；
- 设备类包括 `ShDevice_AS18`、`ShSubDevice_AS18`、`ShSubDevice_AS18B`；
- 协议类包括 `ShAS18Protocol`、`ShSubDeviceProtocol_AS18` 以及 AS18A/AS18B/AS18I/AS18IA/AS18IB/AS18L/AS18LPlus 等变体；
- 底层使用 `CSerialPipe`/`CSerialPipeConfig`，因此传输层是串口；
- 静态反汇编现已证明 AS18 命令路径使用 `ShFormatModbus` 的 Modbus RTU 功能码和 CRC16；
- 已取得厂商《18i双通道自动进样器通讯协议.xlsx》，并与现场 COM3 抓包逐字节交叉核对；
  普通运行状态、方法块和自动进样触发的寄存器语义已基本确定；
- “终止”是否复用初始化寄存器 `0x0708`、不同固件的校准/用户程序路径以及实际互锁行为，
  仍需现场动态验证，不能只凭符号名称或协议表命名。

## 2. 已发现的协议能力

`ShDeviceFamily.dll` 导出符号显示 AS18 协议至少包含以下业务操作：

| 符号 | 推断能力 | 证据级别 |
|---|---|---|
| `CmdAutoDetect@ShAS18Protocol` | 自动识别/探测设备 | 静态符号+反汇编 |
| `Init_New@ShSubDeviceProtocol_AS18` | 初始化 | 静态符号 |
| `CmdLoadState@ShSubDeviceProtocol_AS18` | 读取或装载状态 | 静态符号 |
| `CmdSendMethod@ShSubDeviceProtocol_AS18` | 下发进样方法 | 静态符号 |
| `CmdPerformInjection@ShSubDeviceProtocol_AS18` | 执行进样 | 静态符号 |
| `CmdStart2@ShSubDeviceProtocol_AS18` | 启动流程 | 静态符号 |
| `CmdStop@ShSubDeviceProtocol_AS18` | 停止流程 | 静态符号 |
| `Wash_New@ShSubDeviceProtocol_AS18` | 清洗 | 静态符号 |
| `ActionTray@ShSubDeviceProtocol_AS18` | 托盘动作 | 静态符号 |
| `ActionLight@ShSubDeviceProtocol_AS18` | 灯光/指示动作 | 静态符号 |
| `CmdClearMissFlag@ShSubDeviceProtocol_AS18` | 清除漏样/缺瓶标志 | 静态符号 |
| `GetState@ShSubDevice_AS18` | 获取规范化状态对象 | 静态符号 |
| `GetStatusInfo@ShSubDeviceState_AS18` | 获取状态文本 | 静态符号 |

`AutoSampler.exe` 的导入表还直接引用了 `ShDevice_AS18::OpenCommunication`，并包含 `PerformInjection`、`StopRun`、`GetState`、`GetProtocol` 等调用入口。

在 `ShDeviceFamily.dll` 的 AS18 实现反汇编中，多个操作会调用
`ShSubDeviceProtocol::SendCommand3`，并将 `0x0708`、`0x070B`、`0x070C`、`0x070F`
等数值直接作为 `WriteWord_06` 的寄存器地址传入。因此它们在线上对应 Modbus
`0x06` 写单寄存器请求的地址字段。厂商运行协议将这些地址分别命名为初始化、托盘、
缺瓶标志清零和灯光；静态符号 `CmdStop` 曾指向 `0x0708`，所以目前只能说 ShineLab
的终止路径可能复用初始化/复位命令，不能把 `0x0708` 直接标成硬件急停。

## 3. 串口配置证据

`AutoSampler.exe` 中存在以下配置字段字符串：

```text
COM
Pipe0
BaudRate
Parity
ByteSize
StopBits
```

这证明 AS18 使用可配置串口管道。安装包中的默认字符串附近出现 `115200`、`Parity=0`、`ByteSize=8`、`StopBits` 等值，但它们不能直接视为现场 SHA-18i 的最终参数，必须以现场 `setinfo.ini`、设备管理器和动态通讯为准。

## 4. 校验和协议层证据

AS18 代码依赖通用的：

- `CSerialPipe`；
- `CPipeProtocol`；
- `CProtocolCommand`；
- `VerifyTool` / `VerifyTool_Sh0D0A`。

`VerifyTool` 暴露 `GetCrc`、`GetLrc` 和 `Verify`。AS18 的 `InitFormat` 实际实例化通用 `VerifyTool`，并由 `ShFormatModbus::BuildMsgCRC` 追加 CRC16；因此其帧层与 D160+ 同属 Modbus RTU 风格，但寄存器表是 AS18 专用的。

## 5. 日志证据

现有日志包含：

```text
ShSubDeviceProtocol_AS18.cpp:444] Init 指令成功
```

这证明 ShineLab 确实执行过 AS18 初始化命令，但日志没有记录原始 TX/RX 字节，不能据此恢复报文。

## 6. 厂商协议表与现场报文交叉核对（2026-08-28）

输入文件：`res/18i双通道自动进样器通讯协议.xlsx`，SHA-256
`674FDAFD7FC45925D44FD7CE48F0415CA1F6AB28E0570B95293F5F0DEEAC073C`。

运行工作表定义：

- 设备地址默认 `0x01`，Modbus-RTU、CRC、单播；默认 115200、无校验；
- 设备信息 `0x06A4`–`0x06A6`，设备序号 `0xC2` 表示 18i；
- 方法块 `0x0640/27` 和尾块 `0x065B/3`；
- 普通状态块 `0x076C/6` 与阀状态块 `0x0772/2`；
- 控制地址 `0x0708`（初始化）、`0x0709`（自动进样）、`0x070A`（洗针）、
  `0x070B`（推盘）、`0x070C`（清空瓶标志）、`0x070F`（灯）、
  `0x0710`（在线稀释）、`0x0711`（抑菌清洗）。

与 `SHA18iA-capture-20260827-165207.zip` 的 COM3/dev8 报文完全一致：

| 现场报文 | 协议定义 | 结论 |
|---|---|---|
| `01 04 07 6C 00 06 B1 61` | 读取普通状态 6 words | 一致 |
| `01 04 07 72 00 02 D0 A4` | 读取 A/B 阀状态 2 words | 一致 |
| `01 10 06 40 00 1B ...` | 27-word 方法块 | 一致 |
| `01 06 07 09 00 01 99 7C` | 自动进样=1 | 一致 |
| `01 10 06 5B 00 03 ...` | 1627–1629 尾部设置 | 一致 |

早期静态表中的 `0x044C/11` 来源于协议第四工作表“用户程序”的命令完成状态，
不是普通运行状态；代码和后续验证应分别命名为 `UserProgramState` 与
`RuntimeState`，避免误读。

协议表中 1613 的十六进制单元格写成 `0x065D`，按十进制地址和连续地址关系应为
`0x064D`，这是表格笔误。第二、三、四工作表属于调试/校准/用户程序路径，设备序号
和地址布局与运行工作表不同，不能混用。

## 7. 为什么仍需动态验证

AS18 的高层函数通过协议对象和虚函数调用构造命令，参数来自 `ShMethod_AS18`、托盘/样品位置和运行时状态。协议表已经补齐了大部分字段，但仍需动态确认：

- 现场设备序号、固件和串口参数是否与运行工作表一致；
- 终止路径是否将 `CmdStop` 映射为 `0x0708=1`，以及该写入的真实硬件语义；
- 响应状态码；
- 超时、重试和异步完成规则。

因此协议表足以支持离线编解码和仿真，但不能让我们跳过一次受控现场验证，
也不能直接向真实 SHA-18i 重放写帧。

## 8. 下一步动态取证

在隔离测试环境中执行以下动作，每次只执行一个动作：

1. 打开通讯/自动识别；
2. 初始化或回零；
3. 读取状态；
4. 选择样品位；
5. 下发一个最小进样方法；
6. 执行进样；
7. 清洗；
8. 停止；
9. 清除缺瓶标志。

每个动作必须同时记录：

- COM 号和串口参数；
- TX/RX 原始十六进制；
- 时间戳和帧间隔；
- 动作前后 ShineLab 状态；
- 样品位、体积和方法参数；
- 错误码、重试和超时。

首选在测试构建中给 `CSerialPipe::Send/Receive` 或 `ShSubDeviceProtocol::SendCommand/ProcessReceivedFrame` 增加十六进制审计日志。若不能修改程序，则使用经批准的双向串口监听器或硬件串口分析仪；不要让第二个程序直接抢占 ShineLab 正在使用的 COM 口。

## 9. Static disassembly results (2026-08-26, updated with vendor table)

Direct disassembly establishes that the AS18 family uses the Modbus formatter
implemented by `ShFormatModbus` in `ShPipe.dll`:

- `SendCommand1` calls `ReadWords_04` (function `0x04`).
- `SendCommand_03` calls `ReadWords_03` (function `0x03`).
- `SendCommand3` calls `WriteWord_06` (function `0x06`).
- `SendCommand5` calls `WriteBit_05` (function `0x05`).
- `SendCommand7` calls `WriteWord_07` (function `0x07`).
- `SendCommand16` calls `WriteBits_16` (function `0x10`).
- `InitFormat` installs the generic `VerifyTool`; `BuildMsgCRC` appends CRC16.

Concrete AS18 call sites recovered from `ShDeviceFamily.dll`:

The relevant function RVAs are `CmdAutoDetect=0x11520`, `CmdStart2=0x36270`,
`CmdPerformInjection=0x35670`, `CmdStop=0x36F30`, `ActionTray=0x37790`,
`ActionLight=0x378F0`, `CmdClearMissFlag=0x36F70`, and
`AS18IA::GetUserProgramState=0x219C0`.

| Operation | Formatter | Start/register | Payload/value | Wire register |
|---|---|---:|---:|---:|
| AutoDetect | `ReadWords_04` | `0x06A4` | 1 word | - |
| SHA-18iA runtime state poll | `ReadWords_04` | `0x076C` | 6 words | - |
| SHA-18iA runtime valve state | `ReadWords_04` | `0x0772` | 2 words | - |
| User-program completion state | `ReadWords_04` | `0x044C` | 11 words | - |
| Start2 | `WriteBits_16` | `0x0640` | 27 words | `0x0709` or `0x0710` |
| PerformInjection | `WriteBits_16` | `0x0640` | 19 words | `0x0709` |
| Init/reset path (static `CmdStop` target) | `WriteWord_06` | `0x0708` | value `1` | `0x0708` |
| Tray action | `WriteWord_06` | `0x070B` | value `1` | `0x070B` |
| Clear missing-vial flag | `WriteWord_06` | `0x070C` | value `1` | `0x070C` |
| Light action | `WriteWord_06` | caller argument | light argument | `0x070F` |
| Antibacterial wash | `WriteWord_06` | `0x0711` | value `1` | `0x0711` |

`WriteWord_06` emits the standard eight-byte Modbus request layout (slave, `06`,
 register high/low, value high/low, CRC high/low). For example, with slave address
 `1` and value `1`, the init/reset target is `01 06 07 08 00 01 CRC_lo CRC_hi`.
The AS18 field at protocol offset `0x7C` is passed as the send timeout/context;
it is not part of the wire payload.

With slave address `1`, the statically recoverable request prefixes are:

```text
AutoDetect: 01 04 06 A4 00 01 CRC_lo CRC_hi
RuntimeState: 01 04 07 6C 00 06 CRC_lo CRC_hi
ValveState:   01 04 07 72 00 02 CRC_lo CRC_hi
UserProgramState: 01 04 04 4C 00 0B CRC_lo CRC_hi
Init/reset:  01 06 07 08 00 01 CRC_lo CRC_hi
Tray:       01 06 07 0B 00 01 CRC_lo CRC_hi
ClearMiss:  01 06 07 0C 00 01 CRC_lo CRC_hi
Light(n):   01 06 07 0F 00 n  CRC_lo CRC_hi
Wash:       01 06 07 11 00 01 CRC_lo CRC_hi
```

`CRC_lo/CRC_hi` are the low/high bytes returned by `VerifyTool::GetCrc` over all
preceding bytes. Examples: AutoDetect `70 A1`, Init/reset `C8 BC`, Tray `38 BC`,
RuntimeState `B1 61`, ValveState `D0 A4`, UserProgramState `71 2A`,
ClearMiss `89 7D`, Wash `19 7B`.
Multi-register requests use
the literal register block and word counts above; payload words are assembled
from `ShMethod_AS18` and subdevice state fields. Static assignments in `Start2`
read AS18B offsets `0x568, 0x554, 0x55C, 0x5B4, 0x598, 0x5A0, 0x5A4, 0x5A8,
0x5C0, 0x5C4, 0x5E4, 0x574, 0x570, 0x5BC, 0x5B8, 0x57C, 0x5D0, 0x578,
0x5B0, 0x59C, 0x5AC, 0x564`, plus computed syringe/volume and trigger values.

Therefore framing, function codes, register block, word counts, CRC layer, and
the ordinary method/injection path are confirmed by both the vendor table and
the field capture. A single approved TX/RX capture of each maintenance action is
still required to confirm firmware-specific interlocks and the meaning of the
ShineLab "terminate" path; no guessed write frame should be used for production
control.

The `AS18IA` user-program path adds one important detail: `CmdDoUserProgramCmd`
converts the method command number to a register address and the command text to
an integer value, then sends it through `SendCommand3`/`WriteWord_06` with slave
address `1`. Therefore user-program injection frames have the form
`01 06 <method-command-hi> <method-command-lo> <value-hi> <value-lo> CRC`,
while the method command number and value are data-driven rather than hard-coded
in the binary.

## 10. 网关实现边界

协议完成取证前，SHA-18i 网关只注册：

```text
Identify
ReadStatus
ReadPosition
ReadAlarm
```

确认报文和安全规则后，才逐项开放：

```text
Home
MoveTo
LoadMethod
PerformInjection
Wash
Stop
Reset
```

网关对 MES 暴露业务接口，不暴露 COM、原始报文、命令码或任意写帧。

## 11. 现场抓包执行版

### 11.1 机器分工

- **控制电脑（必须现场执行）**：连接 SHA-18i、安装/运行 ShineLab、运行抓包工具并产生原始采集文件。
- **分析电脑（本机即可）**：接收脱敏后的 `pcapng`、串口分析仪导出文件或十六进制日志，离线解析，不连接设备、不打开现场 COM。
- 通过远程桌面操作时，远程桌面会话必须进入控制电脑；在本机启动 Wireshark 看不到控制电脑的串口流量。

### 11.2 先确认连接类型

1. 在控制电脑设备管理器和 ShineLab 配置中记录 SHA-18iA 的 COM 号、USB VID/PID、驱动名称、波特率、校验、数据位和停止位。
2. 若是 USB 虚拟串口或 USB 转串口，优先使用控制电脑上的 `USBPcap + Wireshark`，它不会抢占 COM 口。
3. 若是原生 RS-232（非 USB），使用高阻抗双向串口监听器/硬件分析仪；普通串口调试助手不能与 ShineLab 同时打开同一 COM。
4. RS-232 必须分别记录 PC->SHA 和 SHA->PC 两个方向。DB9 的 2/3/5 只作为常见参考，现场以设备手册和测量结果为准，不要盲接。

### 11.3 USBPcap 操作步骤（SHA-18i 为 USB 串口时）

在控制电脑执行：

1. 安装 USBPcap 和 Wireshark，重启后以管理员权限打开 Wireshark。
2. 选择 `USBPcap1`（或包含 SHA-18i USB 设备的总线），先不加过滤器开始捕获。
3. 记录当前时间、COM 号和设备序列号，然后启动 ShineLab；不要关闭或重新插拔仪器。
4. 每次只做一个动作，动作之间间隔 5 秒，并在记录表写下时间：连接/自动识别、状态查询、初始化/回零、托盘动作、最小测试方法下发、测试进样、清洗、停止。
5. 停止捕获，保存原始文件，例如 `SHA18iA_20260826_raw.pcapng`；复制一份作为分析副本，不修改原始文件。
6. 在 Wireshark 用 `usb.capdata` 过滤 USB 数据；若使用命令行，可在控制电脑导出：

```powershell
tshark -r SHA18iA_20260826_raw.pcapng -Y "usb.capdata" -T fields `
  -e frame.time_epoch -e usb.src -e usb.dst -e usb.capdata `
  > SHA18iA_20260826_usb-capdata.tsv
```

### 11.4 原生 RS-232 硬件监听步骤

1. 在不改变 ShineLab 接线的情况下接入双向监听器；监听器只接收，不向总线发送。
2. 按 ShineLab 实际串口参数设置监听器；若配置未知，先记录 ShineLab 配置，不要用猜测参数发测试字节。
3. 开始记录后按 10.3 的动作顺序逐项操作；导出带时间戳和方向的原始十六进制文件，例如 `SHA18iA_20260826_serial.csv`。
4. 同时保存动作时间表、设备状态截图和 ShineLab 日志。每个动作单独一段文件，便于将帧与动作对应。

### 11.5 交付给本机分析

至少传回：原始 `pcapng` 或串口分析仪文件、脱敏副本、动作时间表、COM 参数、SHA-18iA 型号/固件/序列号、ShineLab 日志和结果文件样例。本机只对这些文件做 CRC、寄存器和状态字段解析；在完成字段映射前，不从本机向现场设备重放任何写帧。

### 11.6 远程协作方式

可以通过现场电脑的 RDP/远程协助进行实时配合，但抓包程序必须运行在现场控制电脑上。本分析环境不能直接看到你现场电脑的 COM 口，也不能替你点击 ShineLab；实际流程是：

1. 你建立到控制电脑的远程桌面，并确认有管理员权限；
2. 你把控制电脑屏幕上的 COM 号、串口参数和 USB 设备信息发给我；
3. 我根据这些信息给出逐条命令和动作顺序；
4. 你在控制电脑启动被动监听后，在 ShineLab 中执行一个动作；
5. 你把生成的 `pcapng`/十六进制文件或命令输出发回，本机立即解析并给出下一步。

如果现场电脑已开放受控的 PowerShell/SSH 远程终端并且本工作区挂载在该电脑上，我可以直接运行采集脚本；否则不能仅凭“网口已接通”远程打开它的串口。首次会话先做连接、状态查询和初始化三组动作，确认捕获有效后再继续托盘、方法、进样、清洗和停止。

## 12. 2026-08-28 静态调用链补充

对安装包中的 `AutoSampler.exe`、`ShDeviceFamily.dll`、`ShDeviceFamilyUI.dll`
和 `ShDevice.dll` 进一步核对后，已确认：

- `AutoSampler.exe` 的导入表直接引用 AS18 的 `OpenCommunication`、
  `PerformInjection`、`StopRun`、`GetState`、`GetMethod`、`CanRun`、`AddRow`、
  `Init_New` 和 `Wash_New`；
- `ShDeviceFamilyUI.dll` 包含 `ShSubDevice_AS18Page`、`ShTrayDlgAS18`、
  `ShAS18RWConfigDlg` 以及托盘、进样模式、洗针模式和用户程序入口；
- `ShDevice.dll` 包含 `Context_Inject`、`Context_MoveSamp`、`Context_Wash` 等
  流程上下文类型；
- 中文资源中存在“进样器-AS18”“洗针”“托盘弹出”“托盘复位”等词条。

这说明当前版本的 ShineLab 安装包具备 AS18 控制和界面实现，但不证明这些入口
一定在当前主窗口、当前权限或当前设备配置下可见。静态函数、调用链和字段偏移的
完整清单见 [`SHA18I-STATIC-CALLCHAIN-20260828.md`](../artifacts/ion-chromatography/SHA18I-STATIC-CALLCHAIN-20260828.md)。

静态分析下一步主要用于缩小现场取证范围；普通进样方法块和 `0x0709` 触发已经得到
协议表与现场帧的双重支持。停止（静态 `CmdStop`/协议初始化地址）、托盘、清洗及其
互锁仍须由原控制软件在受控条件下执行并被动抓包确认，不能依据反汇编结果直接重放。
