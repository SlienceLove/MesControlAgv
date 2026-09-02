# AGV + AUBO 中控 WPF 快速落地交接说明

更新时间：2026-08-31  
交接目标：在新会话中继续完成“中控 WPF 操作 AGV 移动，并控制 AUBO 执行指定工程”，
优先跑通单台现场 AGV（`AGV-01`）和单个已确认工程（当前为 `测试.pro`）。

## 1. 新会话第一条指令

```text
继续 AGV + AUBO 中控 WPF 快速落地。先读取：
docs/FAST-TRACK-AGV-AUBO-WPF-IMPLEMENTATION-PLAN-2026-08-31.md
docs/AUBO-MODBUS-ROLE-AND-NEXT-STEP-PLAN-2026-08-31.md
docs/PROGRESS.md
然后优先把 AUBO C# Adapter 从未通过真机的 TCP 行协议切换到现场已验证的
WebSocket JSON-RPC 9012，再增加 MES 的工程加载/启动/停止接口和 WPF 机械臂操作页。
AGV WPF 单段派发随后接入。不要重复使用裸 TCP 30004、不要把 AUBO 从站表
399/400 直接当作当前工程 HR0/HR1，也不要重复启动现场工程。
```

## 2. 用户目标与范围

### 目标

```text
WPF 选择 AGV 起点/终点
  → MES 创建并派发 AGV 任务
  → AGV 到站
  → WPF/MES 调用 AUBO 加载/启动指定工程
  → WPF 显示 AGV 与 AUBO 状态/结果
```

### 第一阶段不做

- 视觉坐标计算和标定自动化；
- 夹爪独立控制；
- 多 AGV 调度；
- 自动批量派单；
- Modbus 地址表全面实现；
- 复杂审批页面和生产级权限体系。

用户要求先快速跑通。可以不新增流程型审批门禁，但不能删除设备级停止、超时、
防重复启动和未知结果处理。

## 3. 已完成的现场实测

### AUBO 网络和 RPC

现场 AUBO：

```text
IP：192.168.1.102
机器人名：rob1
型号：aubo_C5
subtype：10
控制箱：cb_i
控制软件版本码：31001
接口版本码：24000
```

已验证：

- WebSocket RPC：`ws://192.168.1.102:9012/` 可用；
- `getRobotNames` 返回 `rob1`；
- `RobotState.getRobotModeType`、`getSafetyModeType`、
  `RuntimeMachine.getStatus`、`RobotManage.getOperationalMode` 可读取；
- `RuntimeMachine.loadProgram("测试")` 返回 0；
- `RuntimeMachine.runProgram` 曾返回 0，并启动过一次现场工程；
- 成功启动使用的是 WebSocket JSON-RPC，不是 Modbus。

### 当前工程

当前工程名称由现场确认：`测试.pro`，RPC 调用时使用无扩展名 `测试`。

归档同名 Lua 工程包含：

- `moveJoint`；
- `moveLine`；
- 视觉 TCP Socket；
- Modbus 信号初始化；
- 弹窗 `trr`。

因此不能猜测其他工程名，也不能重复点击运行。

### 最近一次启动后的状态

最近一次启动前现场确认了：自动模式、路径已单步确认、空载、作业区无人、急停监护
在位。启动后读取到：

```text
Runtime：Running
Robot：Running
Safety：Normal
Operational：Automatic
计划上下文：第 16 行附近，弹窗消息 trr
```

新会话开始时必须重新读取状态，不要假设工程仍在运行或仍停在弹窗。

## 4. 当前通信决策

### 主控制面：AUBO WebSocket JSON-RPC

中控不需要物理直连 AGV，只要通过厂区控制交换机/无线网络能够访问 AUBO IP：

```text
ws://192.168.1.102:9012/
```

用于状态、加载工程、启动工程、停止工程和结果读取。

### AGV 控制面：Vendor TCP

AGV 控制器：

```text
192.168.1.2
状态：19204
命令：19206
控制权：19207
其他/I/O：19210
Push：19301（当前关闭）
```

WPF 通过 MES，MES 通过 Adapter 调用 AGV，WPF 不直接拼厂商报文。

### Modbus 的定位

当前“测试”工程中的：

```lua
modbusAddSignal("192.168.1.11,502", 1, 0, 3, "总控输入", false)
modbusAddSignal("192.168.1.11,502", 1, 1, 3, "总控输出", false)
```

表示 AUBO 是 Modbus TCP 主站，总控电脑是 `192.168.1.11:502` Server。

项目已有 `src/MesControlAgv.AuboModbusBridge`，但它只是独立最小 Server，默认
不允许非零 HR0 写入，尚未接入 MES/WPF。它只用于旧 Lua 兼容验证，不作为第一阶段
WPF 主控制通道。

`res/modbus从站地址表_v1.15.xlsx` 描述的是反向方案：中控/PLC 为主站、AUBO 为从站，
其中 300~555、399/400 等地址不能与当前工程 HR0/HR1 混用。

## 5. 当前代码状态

### WPF 已有

- `AgvCommunicationView`：AGV 状态、任务和暂停/恢复/取消；
- `TaskMonitorView`：任务创建、路线预览、人工派发；
- `MainViewModel`：MES 刷新、任务派发和 AGV 状态绑定；
- WPF 只通过 MES HTTP，不直接访问设备协议。

### MES 已有

- `AdapterClient`：AGV 任务和状态；
- `AdapterAuboArmClient`：AUBO 只读/握手抽象；
- `/api/tasks/...` 任务接口；
- `/api/robot-arms/{id}/status`、`readiness`、`handshake` 等基础接口。

### Adapter 已有

- `TcpAgvClient`：现场 AGV Vendor TCP；
- `AuboArmReadOnlyDriver`：AUBO 只读抽象；
- `AuboArmJsonRpcClient`：当前仍是 TCP 行协议，现场 `30004` 裸请求超时；
- `AuboArmControlledHandshakeSession`：环回测试通过，但尚未接入现场 WebSocket；
- AUBO 模块默认控制关闭。

## 6. 最快实施阶段

### 阶段 1：AUBO WebSocket Adapter

优先修改：

```text
src/MesControlAgv.Adapter/Modules/AuboArm/
```

建议新增 `AuboArmWebSocketClient`，或将现有传输抽象改为 WebSocket，支持：

- 单请求/单响应；
- WebSocket 消息完整接收；
- JSON-RPC response ID 校验；
- RPC error 解析；
- 超时和连接重建；
- 字符串状态保留，不再假设状态只能是整数。

首批方法：

```text
getRobotNames
rob1.RobotState.getRobotModeType
rob1.RobotState.getSafetyModeType
RuntimeMachine.getStatus
rob1.RobotManage.getOperationalMode
RuntimeMachine.getPreloadProgram
RuntimeMachine.loadProgram
RuntimeMachine.runProgram
RuntimeMachine.abort
```

`runProgram` 只能运行已确认/已加载工程；不要隐式加载未知工程。

### 阶段 2：MES AUBO 程序接口

增加独立的程序控制接口和 HTTP 路由：

```text
GET  /api/robot-arms/{id}/status
GET  /api/robot-arms/{id}/readiness
GET  /api/robot-arms/{id}/program
POST /api/robot-arms/{id}/program/load
POST /api/robot-arms/{id}/program/run
POST /api/robot-arms/{id}/program/stop
```

快速版本只支持工程 `测试`，请求中显式传入工程名和操作员名称。

### 阶段 3：WPF 机械臂操作页

新增轻量视图/视图模型，显示：

- RobotMode；
- SafetyMode；
- OperationalMode；
- Runtime；
- 当前工程；
- 加载、启动、停止按钮；
- 最后响应和错误信息。

第一版不要让 WPF 直接连接 AUBO，也不要暴露原始寄存器地址。

### 阶段 4：现场 AGV 标准模式

现场配置需要切换为：

```text
AGV：AGV-01 / W500-SZ / vendor-tcp / 192.168.1.2
Adapter：standard
AcquireControl：true
EnablePush：false
速度上限：0.3 m/s
```

WPF 先跑单段：

```text
LM1 → LM2
```

使用新任务 ID，人工点击一次派发，轮询 accepted/moving/arrived/failed/unknown。

### 阶段 5：顺序联动

第一版只实现：

```text
AGV 到站
→ AUBO load("测试")（必要时）
→ AUBO runProgram()
→ 轮询 Runtime
→ WPF 显示完成/失败
```

不做并行导航、视觉和夹爪闭环。

## 7. 已知问题和注意事项

1. 原 `scripts/Invoke-AuboRpc.ps1` 是 TCP 行协议工具，不要把它当作当前真机通道；
   真机已验证的是 `scripts/Invoke-AuboWsReadOnlyPreflight.ps1` 的 WebSocket 路径。
2. C# Adapter 的 AUBO TCP 实现尚未通过真机，Adapter 现场 AUBO 路由暂不作为已验证能力。
3. `测试` 工程的 Modbus Server 地址固定为 `192.168.1.11:502`，当前总控 502 曾无监听；
   Modbus 数据面和 JSON-RPC 控制面要分开验证。
4. 不要重复启动“测试”工程；如果 Runtime 已经 Running，先只读确认，不再发送启动。
5. 现场工程可能弹出 `trr`，不要通过自动点击或盲目清除弹窗推进。
6. 示教器后续可能拔除；拔除前必须让 Runtime=Stopped，并重新验证安全状态、模式和
   WebSocket 可达性。
7. 保持当前操作模式密码只在授权现场使用，不写入代码或自动化参数。

## 8. 关键文件

```text
docs/FAST-TRACK-AGV-AUBO-WPF-IMPLEMENTATION-PLAN-2026-08-31.md
docs/AUBO-MODBUS-ROLE-AND-NEXT-STEP-PLAN-2026-08-31.md
docs/AUBO-FIELD-READONLY-RUNBOOK-2026-08-31.md
docs/AGV-INTERNAL-SWITCH-BRIDGE-RUNBOOK-2026-08-31.md
docs/PROGRESS.md

scripts/Invoke-AuboWsReadOnlyPreflight.ps1
scripts/Invoke-AuboRpc.ps1
scripts/Invoke-AgvIoApi.ps1

src/MesControlAgv.Adapter/Modules/AuboArm/
src/MesControlAgv.Adapter/Services/TcpAgvClient.cs
src/MesControlAgv.Mes/Services/AdapterAuboArmClient.cs
src/MesControlAgv.Wpf/Views/AgvCommunicationView.xaml
src/MesControlAgv.Wpf/ViewModels/MainViewModel.cs
src/MesControlAgv.AuboModbusBridge/

artifacts/aubo-field-readonly-20260831-103237.json
artifacts/aubo-field-program-load-20260831-134401.json
artifacts/aubo-field-program-run-20260831-140544.json
```

## 9. 当前停止点

本会话已完成一次现场 AUBO 工程启动实测，状态正常；现在停止在“规划/交接”阶段。
新会话开始后，先重新读取现场 Runtime，不要默认工程仍在运行。开发工作从阶段 1
WebSocket Adapter 开始，完成本地 loopback 测试后再接 MES/WPF，最后才执行新的现场
启动或 AGV 移动测试。

## 10. 工作区注意事项

当前工作区包含前序现场联调、离线优化和 AUBO/AGV 改动，存在大量已修改和未跟踪
文件。新会话必须保留这些用户变更，不执行 `git reset --hard`、批量清理或覆盖现场
证据；只在目标模块范围内增量修改，并在每个阶段运行相应构建/测试。

## 11. 本次软件执行检查点

- 阶段 1 已完成：Adapter 默认使用 WebSocket JSON-RPC `9012`，实现完整消息接收、
  响应 ID/RPC error 校验、超时和连接重建；旧 `30004` 行协议仅保留离线兼容测试。
- 阶段 2 已完成：MES 增加工程查询、显式加载、启动和停止接口，工程名由请求传入，
  快速白名单默认仅为 `测试`，操作员名称必填。
- 阶段 3 已完成主体：WPF 通过 MES 显示四类状态和当前工程，并提供加载/启动/停止
  三个按钮；WPF 不直接访问 AUBO。
- 阶段 4 已准备模板：`src/MesControlAgv.Adapter/appsettings.FieldStandard.json` 固定
  单台 `AGV-01`、`W500-SZ`、`192.168.1.2`、`AcquireControl=true`、Push 关闭、
  `0.3 m/s` 和 `LM1 → LM2`；模板不会被默认启动自动选用。
- 阶段 5 已加入人工触发的顺序协调 API 和 loopback 验证：只接受已到站且可关联的
  任务，必要时加载已批准工程，最多启动一次；通信中断/超时进入人工处置，不自动重试。
- 截至本检查点未执行新的真实 AUBO/AGV 写入、启动或移动。现场调试必须重新完成只读
  状态确认并获得独立授权；不要把本地构建/loopback 通过当作现场 GO。
- 16:37 已按上述要求完成一次新的现场只读预检，证据为
  `artifacts/aubo-field-readonly-20260831-163700.json`。结果为 WebSocket 可达、
  RobotMode=`Running`、SafetyMode=`Normal`、Runtime=`Stopped`，但
  OperationalMode=`Manual` 且没有预加载工程；因此本次严格停在 **NO-GO**，没有发送
  load/run 或任何 AGV 命令。

## 12. 2026-09-01 继续调试结论（以本节为准）

- 地图已切换到 `guangzhou606.smap` 的 LM 标识：LM1 备注“充电原点”，LM7 备注“站点1”，其余按 LMn 展示；起点站和终点站均来自 MES 站点目录，不再依赖旧的 `CHARGE_01` 等应用界面硬编码。
- 中控窗口标题已统一为“中控运营中心”。流程编辑器提供 LM1→LM4 AUBO 到站模板；两个机械臂节点默认不写入任何真实程序名，必须从当前控制器目录刷新后分别选择。
- 机械臂程序目录刷新是显式只读动作，使用预加载槽位和允许列表；流程节点在目录可用时显示下拉选择。目录读取失败不会自动重试，也不会触发加载/启动。
- 本地 FieldSimulation 已验证服务链路、地图和流程节点；解决方案构建通过，当前回归为 899 个通过、5 个跳过（跳过项是需要完整 AGV 模拟器的旧 E2E 场景）。
- 现场 AUBO 最新只读证据 `artifacts/field-debug-aubo-readonly-20260901-104707.json` 仍为 Manual、Runtime Stopped、LM/工程预加载槽位为空，真实程序执行保持 NO-GO。切换到现场写入前必须由现场人员确认 Automatic、安全状态、实际 `.pro` 名称和独立授权。

### 13:35 现场复核更新

网线已恢复，`192.168.1.102:9012` 可达；新只读预检 `artifacts/aubo-field-readonly-20260901-133334.json` 已确认 `Running / Normal / Automatic / Stopped`。随后扫描 0–99 预加载槽位仍为空，见 `artifacts/aubo-field-program-catalog-20260901-133552.json`。下一步只需现场确认两个真实程序名并预加载/批准，才能开始单步写入测试。
