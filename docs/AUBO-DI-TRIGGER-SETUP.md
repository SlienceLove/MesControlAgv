# AUBO DI 触发机械臂流程

## 最快落地：示教器内置 StartProgram

实体接线由厂家完成：AUBO-CB-AGV-V2 手册中的 `16XT7` 是通用 DI 端子，
`16XT8` 是通用 DO 端子；DI 按 NPN 方式工作，外部设备必须与控制柜共地。
不要由中控人员自行拆柜或将未知“上装2”线接入电脑。

这一方案不要求中控能访问 AUBO 网络，也不需要中控电脑运行 Modbus：

```text
MES → AGV API 6001 设置 DO6 高/低脉冲
   → AGV 实体 DO6 → AUBO DI17（待现场确认）
   → AUBO DI17 的 StartProgram(3) 上升沿动作
   → 当前已加载工程从头运行
```

官方 AUBO API 定义 `getStandardDigitalInput(index)` 读取标准 DI，
`StandardInputAction.StartProgram` 的值为 `3`，含义是“开始工程，上升沿触发”。
因此在示教器的 I/O 配置中，把实际接线的 DI（当前报告中的候选是 DI17，pin 15）
动作设置为 `StartProgram`，保存；再加载要执行的工程。一次只测试一个 DI 和一个工程。

也可以运行 `scripts/aubo/MesDiStartProgramSetup.lua` 做同样的设置，但不同固件对
输入动作的持久化方式可能不同，最终应以示教器 I/O 配置页面保存结果为准。

## 多流程：常驻 Lua 轮询

当同一个 DI 需要根据任务类型执行不同流程，使用
`scripts/aubo/MesDiTrigger.lua`：

1. 在文件顶部确认 `DI_PIN`、`ACTIVE_HIGH` 和可选 `ACK_DO_PIN`。
2. 把已经在现场验证过的抓取/进样/放置动作粘贴进 `run_flow()`；不要把旧工程
   中的 `modbusAddSignal`/`modbusGetSignalStatus` 代码带过来。
3. 在 AuboStudio/AuboScope 中新建 Script File，粘贴文件内容并保存；官方操作路径是
   New project → Script node → File → Save As → Edit → Play。也可以用 U 盘把 `.lua`
   文件复制到示教器后，在程序界面打开并保存为 Script File。
4. 运行该工程，让 `MES DI listener online` 出现在控制器日志；以后中控只需要发
   AGV DO6 脉冲，监听器会按上升沿执行一次。

监听器默认只打印日志，不会自行移动机械臂；必须先把一段已验证的运动代码放入
`run_flow()`，并在空载、低速、急停可用的条件下做第一次动作测试。

## 中控调用

AGV 侧已提供直接验证工具：

```powershell
.\scripts\Invoke-AgvIoApi.ps1 -Operation set `
  -ControllerHost 192.168.1.2 -DoId 6 -Status true `
  -PulseMs 500 -AcquireControl -ReleaseControl
```

MES/Adapter 也已提供 `POST /agvs/{agvId}/io/do/{id}`；这条路径只写 AGV DO，
不要求 AUBO TCP 连接。

## 回执建议

报告的 `default_0.ins` 将 AUBO `DO02` 命名为“小车DI7”、`DO03` 命名为“小车DI8”。
若现场线束确实把它们接回 AGV DI7/DI8，可用一个输出表示 `busy/done`，另一个表示
`fault`，MES 继续通过 AGV `1013` 读回。当前名称不等于已确认接线，仍需厂商针脚表。
