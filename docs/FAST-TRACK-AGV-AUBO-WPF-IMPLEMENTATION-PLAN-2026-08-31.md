# AGV + AUBO 中控 WPF 快速落地计划

更新时间：2026-08-31  
目标：尽快在中控 WPF 界面完成“单台 AGV 移动 + AUBO 执行指定工程”，再扩展
AGV 到站后的机械臂联动。当前只针对一台现场 AGV（`AGV-01`）和一个已由现场
单步确认的 AUBO 工程（当前为 `测试.pro`）。

## 1. 现状盘点

### 已有能力

- WPF 已有 AGV 任务创建、路线规划、任务派发、状态刷新和暂停/恢复/取消界面；
- MES 已有 `POST /api/tasks/{id}/dispatch`、任务状态和 AGV 状态查询；
- Adapter 已有 AGV Vendor TCP 驱动、控制权管理、单段路线派发和状态轮询；
- AUBO WebSocket JSON-RPC `192.168.1.102:9012` 已现场验证；
- 已验证 `RuntimeMachine.loadProgram("测试")` 返回 0，
  `RuntimeMachine.runProgram` 可以启动工程；
- 现场工程当前使用 Modbus 信号 `总控输入/总控输出`，但这不是中控控制面的首选。

### 关键缺口

- C# AUBO Adapter 仍使用未通过真机的 TCP 行协议，尚未接入 WebSocket `9012`；
- MES 只有 AUBO 只读/握手抽象，没有“加载/启动/停止指定工程”的业务接口；
- WPF 没有机械臂控制页，也没有工程选择、启动、停止和状态绑定；
- 物理验收配置仍默认 `read-only-preflight`，AGV 标准派发配置尚未形成现场专用
  配置文件；
- Modbus Bridge 是独立控制台程序，尚未接入 MES/WPF；
- 当前“测试”工程包含视觉 Socket、Modbus 初始化和立即运动，不能作为通用无风险
  Demo 工程。

## 2. 快速路径的协议决策

### 推荐控制面：AUBO WebSocket JSON-RPC

中控 → Adapter → AUBO `ws://192.168.1.102:9012/`，统一承载：

```text
状态读取 → 工程加载 → 工程启动 → 工程停止 → 结果读取
```

这条链路不要求中控物理直连 AGV，只要求厂区交换机/无线网络提供稳定的 IP 路由。

### Modbus 的定位

- 只为当前旧 Lua 工程保留 `AUBO Master → 中控 .11:502 Server` 兼容层；
- 不把 HR0/HR1 直接暴露为 WPF 任意写寄存器按钮；
- `res/modbus从站地址表_v1.15.xlsx` 的 AUBO 从站 300~555、399/400 方案暂不作为
  第一条落地路径；
- 如果后续必须采用 PLC 风格控制，再独立实现“中控 Master → AUBO Slave”。

## 3. 阶段计划

### 阶段 1：AUBO WebSocket Adapter（最快优先）

目标：让 MES 后端能够调用已验证的 WebSocket RPC，而不是直接让 WPF 连接设备。

实施项：

1. 新增 `AuboArmWebSocketClient`，支持单请求/单响应、超时、响应 ID 校验、JSON-RPC
   error 解析和断线重连；
2. 只开放明确方法：
   `getRobotNames`、机器人/安全/操作模式读取、`RuntimeMachine.getStatus`、
   `loadProgram`、`runProgram`、`abort`、已批准的工程状态读取；
3. `loadProgram` 要求工程名由业务请求显式传入，去掉 `.pro/.lua` 后缀由 Adapter
   统一处理；
4. `runProgram` 只能运行当前已加载工程，不隐式加载未知工程；
5. 返回统一的 `accepted/running/completed/failed/unknown` 状态；
6. 保留现有 `IAuboArmReader`，新增独立的程序控制接口，避免读写混在一个类型里；
7. 增加 WebSocket loopback 测试和现场协议回归记录。

本阶段不做：视觉坐标、夹爪控制、Modbus HR0 命令映射、自动重试。

### 阶段 2：MES AUBO 业务接口

目标：WPF 只调用 MES 业务 API，不接触 AUBO 厂商报文。

建议接口：

```text
GET  /api/robot-arms/{id}/status
GET  /api/robot-arms/{id}/readiness
GET  /api/robot-arms/{id}/program
POST /api/robot-arms/{id}/program/load
POST /api/robot-arms/{id}/program/run
POST /api/robot-arms/{id}/program/stop
```

快速版本只支持一个现场确认工程 `测试`，请求体包含工程名和操作员名称；后续再
扩展工程白名单、操作审计和权限管理。

### 阶段 3：WPF 机械臂操作页

在现有 `AgvCommunicationView` 旁新增轻量机械臂面板：

- 连接状态、RobotMode、SafetyMode、OperationalMode、Runtime；
- 当前工程名；
- 工程名输入/下拉框，默认显示 `测试`；
- “加载工程”“启动工程”“停止工程”；
- 结果和超时状态；
- 仅显示当前现场单台机械臂 `ARM-01`。

第一版不做复杂工作流节点、不做视觉参数、不做夹爪参数编辑。

### 阶段 4：现场配置与 AGV 单段移动

目标：WPF 已有 AGV 页面真正指向现场控制器，而不是 Simulator。

配置要点：

- Profile 使用 `AGV-01 / W500-SZ / vendor-tcp / 192.168.1.2`；
- Adapter 使用标准模式和现场隔离数据库；
- AGV `AcquireControl=true`，Push 先关闭；
- 明确当前控制器地图、站点和有向边；
- 第一条路线只用现场确认的相邻站点，例如 `LM1 → LM2`；
- WPF 创建任务后人工点击一次“派发”，不批量、不自动重试；
- 轮询到站后再允许下一段。

快速版本可以不新增复杂审批页面，但必须保留：唯一任务 ID、单次派发、状态轮询、
超时转未知和现场急停路径。

### 阶段 5：AGV + AUBO 顺序联动

目标流程：

```text
WPF 选择起点/终点
  → MES 创建并派发 AGV 任务
  → AGV 到站
  → WPF/MES 显示到站
  → 调用 AUBO load("测试")（如工程未加载）
  → 调用 AUBO runProgram()
  → 轮询 AUBO Runtime
  → 记录完成/失败
```

第一版只做顺序执行，不做并行导航、视觉识别、夹爪控制和自动补偿。

### 阶段 6：再补安全和生产化防御

在最短链路跑通后补充：

- 工程白名单和版本哈希；
- 多操作员权限和双人确认；
- AGV 与 AUBO 资源互斥租约；
- 网络断线后的 unknown 对账；
- Watchdog、幂等键、禁止重复启动；
- WPF 审计日志和现场证据导出；
- Modbus Bridge 的 API 化或淘汰旧 HR0/HR1 方案。

## 4. 最短可交付顺序

按交付速度排序：

1. **先实现 WebSocket Adapter + MES AUBO 程序接口**；
2. **再加 WPF 机械臂三按钮和状态面板**；
3. **同时切换现场 AGV 标准配置，打通 WPF 单段移动**；
4. **最后把“AGV 到站后启动 AUBO”串成一个顺序动作**；
5. 再处理复杂门禁、Modbus 统一抽象和视觉/夹爪。

## 5. 阶段验收标准

### AUBO 单机

- WPF 能显示 `rob1`、RobotMode、SafetyMode、OperationalMode、Runtime；
- WPF 加载 `测试` 后 Runtime 保持 `Stopped`；
- WPF 点击启动只发送一次 `runProgram`，能显示 Running/Completed/Failed；
- WPF 点击停止能让 Runtime 回到 Stopped；
- 不需要 WPF 直接连接 AUBO，也不依赖示教器一直在线（需另做拔除验证）。

### AGV 单机

- WPF 能读到 AGV 在线、当前位置和控制权；
- 创建并派发一条 `LM1 → LM2` 单段任务；
- WPF 显示 accepted/moving/arrived/failed/unknown；
- 不产生未确认任务或残留控制权。

### 联动

- AGV 到站后只启动一次已确认 AUBO 工程；
- AGV 与 AUBO 的状态、任务 ID、工程名和结果均可回看；
- 任意一方通讯中断时不自动重复派发或重复启动。

## 6. 当前立即行动

下一步优先修改 Adapter：把 AUBO 真机传输从 TCP 行协议切换到现场已验证的
WebSocket `9012`，并先补一套单机 MES API。完成后再接 WPF 面板和现场 AGV 标准
配置。当前“测试”工程不再重复启动，Modbus Bridge 先只作为兼容验证工具运行。

## 2026-09-01 实施状态修订

本节覆盖上文仍以单一“测试”工程为前提的早期计划：当前实现已改为由控制器只读目录（预加载槽位）和显式允许列表提供程序名。流程模板只放置两个空的机械臂程序节点，操作员在“中控运营中心”刷新目录后分别选择实际名称；生产代码不包含固定测试程序名。现场最新只读预检仍是 Manual 且槽位 0 为空，因此顺序联动已在本地模拟器/loopback 验证，尚未获准对真实机械臂执行 load/run。
