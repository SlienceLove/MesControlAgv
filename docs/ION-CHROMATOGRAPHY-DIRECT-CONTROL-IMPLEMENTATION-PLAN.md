# CIC-D160+ 中控直控实施计划（历史方案，已停止）

> 本计划记录的中控直接打开 D160+/SHA-18i 串口方案已停止采用。当前以
> `docs/SHINELAB-SHA18I-INTERFACE-REQUIREMENTS-2026-09-01.md` 为准：ShineLab
> 负责串口和设备动作，ShineLab 作为 TCP Client 连接我方 TCP Server，中控只
> 管理任务、发送业务命令和接收状态/结果。

## 目标

最终由中控任务系统直接控制 CIC-D160+，不需要操作员在 ShineLab 工作站上逐步点击。ShineLab 只保留为调试、维护和故障回退工具，不参与正常自动任务。

```text
中控界面 → 中控任务服务 → 仪器网关 → COM 虚拟串口/Modbus RTU → CIC-D160+
                                      └→ 独立 COM/RS-232 → SHA-18i
```

仪器网关不是“控制 ShineLab 的程序”，而是安装在物理连接仪器的控制电脑或工业电脑上的本地服务，负责独占串口和执行已审核的设备驱动命令。

## 当前基线

- 设备型号：CIC-D160+。
- 已从 `res/ShineLab` 静态材料确认：设备通过 USB 虚拟串口通信，样例参数为 115200 8N1。
- 已发现样例 Modbus RTU 请求：`01 04 19 00 00 14 F7 59`，CRC 正确。
- `SHA-18i` 是独立 RS-232/USB-串口设备，不能和 D160+ 共用一个串口实例；其 AS18I/AS18IA 软件路径已通过反汇编确认使用 `ShFormatModbus`（功能码 `0x03/04/05/06/07/10` + CRC16）。
- `MesControlAgv.DeviceProtocolTester` 已支持串口只读探测、CRC 检查、超时留存和写功能码保护。
- D160+ 只读寄存器和少量无动作写回已完成验证；厂商协议表与现场 PCAP 已确认 SHA-18iA 普通运行的自动识别 `0x04@0x06A4`、状态轮询 `0x04@0x076C/6 + 0x0772/2`、方法块 `0x10@0x0640/27 + 0x065B/3`、自动进样 `0x06@0x0709=1`，以及洗针/托盘/缺瓶清零等控制地址。`0x044C/11` 仅属于用户程序状态；`0x0708` 在协议表中是初始化，但静态 `CmdStop` 指向该地址，终止语义仍需现场验证。

## 不可突破的安全边界

1. 默认只读；未确认的寄存器地址不能写入真实设备。
2. ShineLab 和仪器网关不能同时占用同一个 COM 口。
3. 启动、进样、复位等非幂等操作禁止自动盲目重试。
4. 通讯中断后必须先重新读取设备状态，不能假定任务未执行。
5. 压力、泄漏、废液、耗材和硬件急停仍由现场安全规程负责。
6. `res/` 中的日志、数据库配置和凭据不提交 Git。

## 分阶段执行路线

### 阶段 0：资料和环境冻结

**输入：** CIC-D160+、SHA-18i、控制电脑、ShineLab、设备手册。

**人工确认：**

- 固件版本和设备序列号；
- D160+、SHA-18i 实际 COM 号；
- 串口参数和从站地址；
- ShineLab 是否有授权/校准/方法依赖；
- 是否允许停止 ShineLab 并由中控独占设备；
- 正常停止、异常停止和硬件急停流程。

**交付物：** 设备清单、接线图、协议证据包、回退方案。

### 阶段 1：只读串口验收（当前执行）

先不接入生产任务，只确认中控能稳定读取设备。

```powershell
dotnet restore src/MesControlAgv.DeviceProtocolTester/MesControlAgv.DeviceProtocolTester.csproj
dotnet build src/MesControlAgv.DeviceProtocolTester/MesControlAgv.DeviceProtocolTester.csproj --no-restore

dotnet run --project src/MesControlAgv.DeviceProtocolTester -- serial `
  --com COM3 `
  --baud 115200 --data-bits 8 --parity none --stop-bits 1 `
  --request-hex "01 04 19 00 00 14 F7 59" `
  --timeout-ms 3000 `
  --output artifacts/ion-chromatography/d160-readonly.json
```

现场运行前关闭 ShineLab 或确认它已释放串口。每次只执行一个已确认的只读请求，保存 JSON、时间、COM 号和设备状态。

**通过条件：**

- 连续至少 30 次请求无 CRC 错误；
- 响应地址和功能码符合协议；
- 超时、断线和串口占用能安全失败；
- 中控读取的状态与 ShineLab 显示一致。

### 阶段 2：建立本地仪器网关骨架

建议新增独立网关进程或 Windows Service，分层如下：

```text
SerialPortTransport
  → ModbusRtuCodec
  → CicD160PlusDriver / Sha18iDriver
  → InstrumentGateway API
```

网关只暴露业务能力，不暴露任意寄存器写接口：

```text
Identify
ReadStatus
ReadPressure
ReadTemperature
ReadPumpStatus
ReadDetectorStatus
ReadAutosamplerStatus
LoadApprovedMethod
StartRun
PauseRun
ResumeRun
StopRun
GetResult
```

阶段 2 仍只注册 `Identify`、`ReadStatus`、温度、压力和设备健康检查。

### 阶段 3：中控任务模型和界面

新增或扩展以下业务对象：

- `Instrument`：设备、型号、序列号、COM 配置、当前控制者；
- `InstrumentMethod`：方法版本、参数范围、审核状态；
- `InstrumentTask`：样品、方法、优先级、幂等键和任务状态；
- `InstrumentTaskStep`：预检查、平衡、进样、运行、取结果等步骤；
- `InstrumentCommandLog`：请求、响应、CRC、耗时和错误；
- `InstrumentResultArtifact`：结果文件、哈希、样品批次和任务关联；
- `InstrumentAlarm`：报警、恢复动作和人工处理记录。

任务状态建议为：

```text
Queued → Connecting → Identifying → Preflight → Equilibrating
       → PreparingSample → Injecting → Running → AcquiringResult
       → SavingResult → Completed
```

异常状态统一进入 `ManualInterventionRequired`，由操作员决定继续、停止、取消或回退。

### 阶段 4：空载控制验证

在设备负责人授权和安全状态下，逐项验证：

1. 加载审核方法；
2. 设置温度和流速；
3. 阀和抑制器控制；
4. 自动进样器定位；
5. 进样；
6. 启动；
7. 正常停止；
8. 复位和异常恢复。

每个动作单独留存原始报文、响应、设备实际状态和人工见证记录。未确认命令不能放入自动流程。

### 阶段 5：标准样自动流程

采用固定方法和标准样完成：

```text
中控创建任务 → 自动预检查 → 人工允许开始
→ 自动进样/运行 → 自动取结果 → 结果审核
```

先运行半自动模式，再切换全自动模式。

### 阶段 6：生产接入

- 网关注册为唯一仪器控制者；
- ShineLab 只作为人工维护和回退工具；
- 中控通过 API 派发任务；
- 操作员不再通过 ShineLab 点击日常流程；
- 异常任务必须转人工处理；
- 全部操作和原始报文可追溯。

## 人工确认点设计

### 一次性确认

设备型号、固件、COM 口、协议、方法范围、停止流程、急停流程、ShineLab 释放串口和回退方案。

### 每次任务确认

样品批次、样品位置、方法版本、耗材和废液状态。上线初期增加“允许开始”确认，稳定运行后可配置为自动通过。

### 异常时确认

通讯中断、压力/温度异常、进样器故障、未知错误码、状态不确定、结果文件不完整、需要复位或重新进样。

## API 边界

中控使用业务 API，例如：

```text
POST /api/instruments/CIC-D160-01/tasks
GET  /api/instruments/CIC-D160-01/status
POST /api/instruments/CIC-D160-01/operations/start
POST /api/instruments/CIC-D160-01/operations/stop
GET  /api/instruments/CIC-D160-01/tasks/{taskId}/result
```

不提供以下高风险接口：

```text
POST /write-register
POST /send-arbitrary-frame
```

寄存器和 Modbus 功能码只能存在于设备驱动内部。

## 当前落地顺序

1. 完成阶段 1 的真实 COM 只读验收；
2. 从官方协议、源码或抓包补齐寄存器和响应表；
3. 建立网关接口和模拟串口测试；
4. 接入中控仪器状态页面；
5. 接入任务状态机，但先禁止写操作；
6. 在空载环境逐项开放控制命令；
7. 完成标准样半自动流程；
8. 最后再开放全自动任务。

## 当前完成定义

只有同时满足以下条件，才可以宣称“中控直接控制 CIC-D160+”：

- 网关可独占并稳定连接 D160+ 和 SHA-18i；
- 只读、启动、停止、进样和结果读取均有协议证据；
- 任务状态在断线、超时和重启后可恢复；
- 任务和原始报文均可审计；
- ShineLab 不参与正常任务且有明确回退方案；
- 标准样自动流程通过现场验收。
