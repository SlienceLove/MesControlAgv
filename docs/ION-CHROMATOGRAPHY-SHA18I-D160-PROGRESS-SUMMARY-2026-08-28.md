# D160+ / SHA-18i 阶段进度总结

更新时间：2026-08-28
状态：现场动作暂停，等待下一次受控验证

## 1. 本阶段目标

确认 CIC-D160+ 与 SHA-18i 双通道自动进样器的真实通讯路径、ShineLab
样品/方法配置位置，并为后续中控仪器网关建立可审计的协议基础。

本阶段不把未经现场验证的写帧接入 MES，也不向真实设备重放猜测报文。

## 2. 已完成事项

### 2.1 D160+ 只读验证

- 现场串口：COM4，115200 8N1，从站地址 1。
- 连续 3 轮、4 组查询，共 12/12 成功。
- 功能码、CRC、长度、从站地址均通过，响应逐轮一致。
- 设备标识：`YA7261078`。
- 证据：`artifacts/ion-chromatography/d160-protocol-read-validation-20260828.json`
- 证据 SHA-256：
  `B2F883A4C2C14637FCF890FFC776274CECACE55BB3556CF363E9287F55C11F44`

### 2.2 SHA-18i 现场抓包

- 现场串口：COM3/FTDI，USBPcap dev8。
- 2026-08-27 抓包中已确认 ShineLab 真实发送并收到回显：
  - 状态读取 `0x04 @ 0x076C/6`；
  - 阀状态读取 `0x04 @ 0x0772/2`；
  - 方法块 `0x10 @ 0x0640/27`；
  - 方法尾块 `0x10 @ 0x065B/3`；
  - 自动进样触发 `0x06 @ 0x0709 = 1`。
- 2026-08-28 的另一轮只点击 D160+ 运行/暂停/恢复/终止，COM3 只有状态轮询，
  没有进样器动作帧；原因是当时打开的是 D160+ 采集生命周期窗口，并非明确调度
  SHA-18A 的样品方法。
- 原始证据：
  `artifacts/ion-chromatography/SHA18iA-capture-20260827-165207.zip`，
  `artifacts/ion-chromatography/SHA18iA-capture-20260828-132249.zip`。

### 2.3 厂商协议资料

- 文件：`res/18i双通道自动进样器通讯协议.xlsx`
- SHA-256：
  `674FDAFD7FC45925D44FD7CE48F0415CA1F6AB28E0570B95293F5F0DEEAC073C`
- 原始 xlsx 位于被 `.gitignore` 排除的 `res/` 目录，本次不直接发布到 GitHub；完整
  寄存器/功能码摘录和交叉核对已写入本总结与协议报告，后续可用哈希复核原件。
- 四个工作表分别覆盖：普通运行、调试与校准、位置微调、用户程序。
- 普通运行表与 2026-08-27 PCAP 逐字节一致，已补齐方法块字段语义：
  - `0x064A`：通道选择（1=A，2=B）；
  - `0x064B`：进样位置/样品瓶位置；
  - `0x064C`：进样体积；
  - `0x0640`：进样模式；
  - `0x0641/0x0642`：洗针模式/次数；
  - `0x0648/0x0649`：左右托盘规格。
- 控制命令表定义了初始化、自动进样、洗针、推盘、清除空瓶标志和抑菌清洗地址。
- 表格存在两个需要保留的版本/文档边界：
  1. `0x044C/11` 属于“用户程序”状态，不是普通运行状态；
  2. 运行表把 `0x0708` 命名为初始化，但 ShineLab 静态符号中的 `CmdStop` 指向
     同一地址，故“终止”是否为硬件停止/复位尚未定论。
- 完整交叉核对：
  `artifacts/ion-chromatography/SHA18I-PROTOCOL-CROSSCHECK-20260828.md`。

### 2.4 ShineLab UI 与手册

- 仪器配置已确认：D160+ 树中存在 `SHA-18iA-SHA-18`，右侧勾选 `SHA-18iA`，
  通讯方式 COM3。
- 样品编辑表的“选择列”中确实存在独立的“进样盘编号”；默认未勾选。
- “处理方法”是谱图处理/积分方法，不是样品瓶位置。
- “进样盘编号”与“样品编号”也不是同一字段：前者是物理位置，后者是业务标识。
- 手册中方法级的进样模式、定量环、清洗等参数位于
  `仪器 → 色谱方法管理 → AS/SHA-18A`；样品级位置/体积位于
  `分析控制 → 样品编辑`。
- 手册分析：`artifacts/ion-chromatography/SHINELAB-MANUAL-ANALYSIS-20260828.md`。

### 2.5 离线代码与测试

- `src/MesControlAgv.InstrumentGateway/Sha18iProtocolCodec.cs` 已更新：
  - 普通运行状态、阀状态、用户程序状态分开建模；
  - 设备身份和方法块解析；
  - 0x06/0x10 离线帧构造与回显校验；
  - 不包含串口写入或现场发送路径。
- 对应测试：
  `tests/MesControlAgv.InstrumentGateway.Tests/Sha18iProtocolCodecTests.cs`。
- 结果：协议专项 32/32 通过；InstrumentGateway 全量 80/80 通过；
  本阶段改动未打开现场 COM。

## 3. 当前可确认的结论

1. SHA-18i 是与 D160+ 分离的独立串口设备，不是 D160+ 的子寄存器。
2. ShineLab/AutoSampler 确实包含 SHA-18i 的方法、样品位置和进样状态机路径。
3. 普通方法下发和自动进样命令已经同时得到“厂商协议表 + 现场 PCAP”双重证据。
4. 通过显示“进样盘编号”列即可在 ShineLab 样品表中定位物理样品瓶位置；不应使用
   “处理方法”承载该信息。
5. 终止、硬件急停、校准地址和用户程序地址仍不能混为一谈。

## 4. 暂停原因与安全边界

- 调试工具与现场 ShineLab DLL ABI 不匹配，不能通过替换单个 DLL 修复，也不能把
  2023 校准包直接混入现场目录。
- 上一轮 D160+ PCAP 末尾仍需现场确认泵/压力是否真正回到安全等待状态；不能仅凭
  ShineLab 显示“终止”判断硬件已停。
- 未确认现场设备身份、固件、串口参数和终止互锁前：
  - 不手工发送 SHA-18i 写帧；
  - 不让第二个程序占用 COM3/COM4；
  - 不把写入接口注册到 MES；
  - 不把 `0x0708` 命名为硬件急停。

## 5. 下次继续顺序

1. 现场确认 D160+ 安全空闲，并记录 SHA-18i 设备身份 `0x06A4`、固件版本和 COM3
   实际串口参数（只读）。
2. 在 ShineLab 样品表确认“进样盘编号”列已显示，核对现有值；上一轮方法帧中
   `0x064B=11`，不要未经核对改写。
3. 打开真正被样品行引用的色谱方法，确认 `AS/SHA-18A`、通道、托盘规格、进样模式、
   进样体积和洗针参数。
4. 只选一行、循环一次，在 USBPcap 被动捕获下运行一次；预期看到方法块、
   `0x0709` 和状态从空闲→进样→完成。
5. 进样成功后，再分别验证自动洗针、托盘动作和缺瓶标志清除。
6. 最后单独验证“终止”；记录是否发送 `0x0708` 以及状态/硬件实际结果，不能预设
   其语义。
7. 将原始 PCAP、动作时间表、方法截图和设备信息成套归档，再讨论中控写入接口。

## 6. 相关文件索引

- 协议交叉核对：`artifacts/ion-chromatography/SHA18I-PROTOCOL-CROSSCHECK-20260828.md`
- 现场手册：`artifacts/ion-chromatography/FIELD-RUNBOOK-SHA18I-PROTOCOL-V1-20260828.txt`
- 静态调用链：`artifacts/ion-chromatography/SHA18I-STATIC-CALLCHAIN-20260828.md`
- ShineLab 手册分析：`artifacts/ion-chromatography/SHINELAB-MANUAL-ANALYSIS-20260828.md`
- 协议编解码器：`src/MesControlAgv.InstrumentGateway/Sha18iProtocolCodec.cs`
- 专项测试：`tests/MesControlAgv.InstrumentGateway.Tests/Sha18iProtocolCodecTests.cs`
