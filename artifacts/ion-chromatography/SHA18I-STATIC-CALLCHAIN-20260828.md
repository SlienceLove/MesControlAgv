# SHA-18i 静态调用链分析（2026-08-28）

## 结论

可以通过现有 ShineLab 安装包继续逆向 SHA-18i 的操作路径，但当前拿到的是
原生 C++ 二进制、导出符号、资源和日志，不是可直接阅读的 C++ 源码。静态分析
已经能把“操作入口 → AS18 设备对象 → 协议函数 → Modbus formatter → 串口”
串起来；普通运行 payload 的字段含义现已由厂商协议表补齐，并与现场方法帧逐项一致；
停止、校准和用户程序等变体仍需按对应工作表和固件分别验证。

## 已核实的文件证据

### AutoSampler.exe

`res/ShineLab/AutoSampler.exe` 的导入表直接引用 `ShDeviceFamily.dll` 中的 AS18
设备接口：

- `ShDevice_AS18::OpenCommunication`
- `ShSubDevice_AS18::PerformInjection`
- `ShSubDevice_AS18::StopRun`
- `ShSubDevice_AS18::GetState`
- `ShSubDevice_AS18::GetProtocol`
- `ShSubDevice_AS18::GetMethod`
- `ShSubDevice_AS18::CanRun`
- `ShSubDevice_AS18::AddRow`
- `ShSubDeviceProtocol_AS18::Init_New`
- `ShSubDeviceProtocol_AS18::Wash_New`
- `ShMethod_AS18::GetInjectPos`
- `ShMethod_AS18::GetSyringVol`
- `ShMethod_AS18::GetWashMode`

同一可执行文件还保留 `ShAS12State_Start`、`ShAS12State_WaitStart`、
`ShAS12State_PerformInjection`、`ShAS12State_Finish` 等状态机 RTTI 字符串。
这说明进样不是由 CSV 导入本身完成，而是由后续序列状态机调用设备对象。

### ShDeviceFamilyUI.dll

`res/ShineLab/ShDeviceFamilyUI.dll` 保留了 AS18 专用界面对象和方法：

- `ShSubDevice_AS18Page`
- `ShSubDevice_AS18Page_SubBase`
- `ShTrayDlgAS18`
- `ShAS18RWConfigDlg`
- `CreateTrayWnd` / `CreateTrayWndDlg`
- `UpdateTray` / `FillLeftRightTray`
- `FillSyrVolCmb`
- `OnSelChangeInjectMode`
- `OnSelChangeWashMode`
- `OnClickUserProg`
- `OnClickSwitch`

这证明安装包包含自动进样器配置、托盘和进样参数页面的实现。它不证明这些页面
在当前用户、当前权限或当前 ShineLab 主窗口中一定可见。

### ShDevice.dll

`res/ShineLab/ShDevice.dll` 还包含通用的流程上下文类型：

- `Context_Inject`
- `Context_MoveSamp`
- `Context_Wash`
- `Context_WashPushSampler`

这些类型表明软件有“移动样品/进样/清洗”的流程编排层，但其中部分类名带有
其他 AS 设备系列后缀，不能仅凭类名断言它们全部用于当前 SHA-18i 型号。

### 日志和配置

- `ShineLab` 日志记录了“SHA-18i-打开设备通讯”“SHA-18iA初始化”等实际操作；
- 数据库日志将当前子设备记录为 `SHA-18iA`，设备类型为 `SHA-18i`；
- `language/zh.xml` 包含“进样器-AS18”“洗针”“托盘弹出”“托盘复位”“进样盘编号”
  等界面词条；
- `Config.ini` 中的 `5556/5557/5558` 是 ShineLab 内部 ZMQ 配置，不是已证实的
  中控下发 TCP 端口。

## 已能静态恢复的协议层

厂商协议表 `res/18i双通道自动进样器通讯协议.xlsx`（SHA-256
`674FDAFD7FC45925D44FD7CE48F0415CA1F6AB28E0570B95293F5F0DEEAC073C`）现已提供
普通运行寄存器语义。它与现场 PCAP 的 `0x076C/6`、`0x0772/2`、`0x0640/27`、
`0x065B/3` 和 `0x0709=1` 全部一致。

`ShDeviceFamily.dll` 的 AS18 协议函数已定位到以下入口（RVA）：

| 能力 | 符号 | RVA | 当前证据 |
| --- | --- | ---: | --- |
| 自动识别 | `ShAS18Protocol::CmdAutoDetect` | `0x11520` | `0x04 @ 0x06A4/1` |
| 普通运行状态读取 | `ShSubDeviceProtocol_AS18::CmdLoadState` | `0x35370` | `0x04 @ 0x076C/6 + 0x0772/2` |
| 用户程序状态读取 | `AS18IA::GetUserProgramState` | `0x219C0` | `0x04 @ 0x044C/11` |
| 方法/进样 | `ShSubDeviceProtocol_AS18::CmdPerformInjection` | `0x35670` | `0x10` 参数块 |
| AS18IA 方法发送 | `ShSubDeviceProtocol_AS18IA::CmdSendMethod` | `0x200F0` | `0x10` 参数块 |
| AS18IA 进样 | `ShSubDeviceProtocol_AS18IA::CmdPerformInjection` | `0x20170` | 状态条件后发送 |
| 启动 | `ShSubDeviceProtocol_AS18::CmdStart2` | `0x36270` | `0x10 @ 0x0640/27` 候选 |
| 停止 | `ShSubDeviceProtocol_AS18::CmdStop` | `0x36F30` | 调用写单寄存器 `0x0708` |
| 托盘 | `ShSubDeviceProtocol_AS18::ActionTray` | `0x37790` | 写单寄存器 `0x070B` |
| 缺瓶标志清除 | `ShSubDeviceProtocol_AS18::CmdClearMissFlag` | `0x36F70` | 写单寄存器 `0x070C` |
| 灯光 | `ShSubDeviceProtocol_AS18::ActionLight` | `0x378F0` | 写单寄存器 `0x070F` |
| 清洗/抑菌 | `ShSubDeviceProtocol_AS18::Wash_New` / `DoAntibacterial` | 已导出 | 具体动作需动态确认 |

2026-08-27 的 USBPcap 已对照确认：`0x10 @ 0x0640/27`、`0x10 @ 0x065B/3` 和
`0x06 @ 0x0709=1` 在 ShineLab 测试序列运行期间真实出现并得到回显；27-word
方法块的进样模式、洗针、样品位、体积和托盘规格均可按厂商表解码。停止、托盘和
清洗的线上帧仍未在现有 PCAP 中完整取得。

## 建议的逆向工作顺序

1. 在 `AutoSampler.exe` 中以导入函数和虚表调用为锚点，定位状态机进入
   `PerformInjection`、`StopRun`、`Wash_New` 的调用位置。
2. 对 `ShMethod_AS18` 的字段读取做结构化标注：样品位、左右托盘、注射体积、
   洗针模式、注射泵参数和用户程序命令号。
3. 对每个调用点向下跟踪 `SendCommand3/SendCommand16` 和 `ShFormatModbus`，
   生成“函数 → 寄存器块 → 参数偏移”的静态表。
4. 在控制电脑保持 ShineLab 正常运行，用 USBPcap 被动采集一个动作一组的
   TX/RX；按动作时间把静态调用点与实际帧对应起来。
5. 先在离线 fake transport 中回放和校验帧，再考虑任何独立控制程序；不从静态
   推断直接向真实设备发送写帧。

## 不可由静态分析单独确定的内容

- 当前现场固件与厂商运行工作表的完整兼容性；
- `0x0709` 在不同序列状态下是单次进样还是启动序列触发；
- 停止/清洗命令的状态前置条件、响应状态和异常恢复规则；
- ShineLab 中哪个菜单、序列节点或维护页面最终触发这些调用。

因此，静态分析可以把后续抓包范围缩小到明确的函数和候选寄存器，但不能替代
现场动作取证，也不能据此开放 MES 写入或无人值守控制。
