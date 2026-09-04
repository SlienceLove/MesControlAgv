# AGV MES MVP Progress

Last updated: 2026-09-04

This is the concise active record. Detailed history remains in Git and the
linked acceptance documents.

## Current focus

### 2026-08-31 AUBO 首次现场网络/RPC 只读实测

- 交换网络已接通；`192.168.1.102:30004` TCP 可连接，但原始“单行 JSON + 换行”
  客户端无响应。现场控制器实际开放 WebSocket RPC `9012`，通过
  `ws://192.168.1.102:9012/` 成功完成 JSON-RPC 只读调用；HTTP RPC `8484` 未开放。
- 实时身份：`rob1`、`aubo_C5`、subtype `10`、控制箱 `cb_i`，控制软件版本码
  `31001`、接口版本码 `24000`。
- 实时状态：RobotMode=`Running`、SafetyMode=`Normal`、Runtime=`Stopped`、
  OperationalMode=`Manual`，预加载工程索引 0 为空。
- 候选 `mes_cmd/mes_seq/mes_ack/mes_result/mes_result_detail` 均不存在；现场实际注册
  Modbus 信号为 `总控输入`/`总控输出`，索引 `0/1`、类型 `3`、当前值均为 `0`。
- 本轮未写变量、未加载/启动工程、未运动。因处于 Manual 且无预加载工程，程序启动保持
  **NO-GO**；历史 `test260814/test260819` 工程包含立即执行的关节/直线运动及夹爪动作，
  不能猜测工程名直接运行。证据：`artifacts/aubo-field-readonly-20260831-103237.json`。
- 只读采集完成后有线网卡 Link 变为 `Disconnected`，`.11` 地址状态为 `Deprecated`，
  `192.168.1.102:9012` 随即不可达；程序加载/启动未执行，需先恢复并稳定交换机链路。
- 链路恢复后，现场确认工程名为 `测试.pro`；RPC 按官方规则使用无扩展名 `测试` 调用
  `RuntimeMachine.loadProgram`，返回 `0`，前后 Runtime 均为 `Stopped`，未启动、未运动。
  历史同名 Lua 含立即 `moveJoint`/`moveLine`、视觉 Socket 和 Modbus 初始化；当前仍为
  `Manual`，首次路径验证应由示教器监督，远程自动启动继续 NO-GO。证据：
  `artifacts/aubo-field-program-load-20260831-134401.json`。
- 在现场明确确认自动模式、路径已单步确认、空载、作业区无人和急停监护在位后，发送一次
  `RuntimeMachine.runProgram`，返回 `0`；未直接发送运动/夹爪/I/O 指令。随后只读状态为
  `Runtime=Running`、`Safety=Normal`、`Operational=Automatic`，计划上下文显示第 16 行
  弹窗消息 `trr`。不得重复启动；异常立即由现场按急停。证据：
  `artifacts/aubo-field-program-run-20260831-140544.json`。
- 通讯分层已确认：中控对 AUBO 的控制面为 WebSocket JSON-RPC `9012`；“测试”工程的
  数据面通过 AUBO Modbus TCP 主站访问 `192.168.1.11:502`，注册 `总控输入/总控输出`
  保持寄存器索引 `0/1`。现场读取值均为 `0`、原始错误码均为 `-1`，且总控电脑检查时
  `502` 无监听，因此 Modbus 数据面尚未闭环；不能把 RPC 启动成功误认为 Modbus 已连通。
- 已阅读 `res/modbus从站地址表_v1.15.xlsx` 与 AUBO ARCS Modbus 资料并新增
  `docs/AUBO-MODBUS-ROLE-AND-NEXT-STEP-PLAN-2026-08-31.md`：表中 300~555、399/400
  属于“AUBO 作为从站”方向；当前工程的 HR0/HR1 属于“总控作为从站”方向，不能混用。
  项目现有 `AuboModbusBridge` 只是独立最小 Server，下一步先无 `--allow-motion` 闭环
  只读 Modbus，再由厂家确认工程是否真正读取 HR0/HR1。
- 非直连部署决策：中控通过厂区控制交换机/无线 Bridge 以 IP 访问 AUBO `:9012` 和
  AGV 已批准端口，物理上不需要插在 AGV 上；JSON-RPC `9012`作为 AUBO 控制面，
  `.11:502` Modbus Server 仅为旧 Lua 工程兼容层，AUBO 从站表方案另行评估。
- 快速落地计划已写入 `docs/FAST-TRACK-AGV-AUBO-WPF-IMPLEMENTATION-PLAN-2026-08-31.md`：
  优先实现 WebSocket Adapter → MES 程序 API → WPF 机械臂三按钮/状态页 → AGV 单段
  移动 → AGV 到站后启动 AUBO；复杂审批、视觉/夹爪和 Modbus 统一抽象后置。
- 新会话交接说明已写入 `docs/FAST-TRACK-AGV-AUBO-WPF-HANDOFF-2026-08-31.md`，
  固定现场实测结果、协议决策、关键文件和阶段 1 的开发入口。

### 2026-08-31 快速落地软件阶段 1-5（本地/loopback）

- AUBO Adapter 新增现场已验证的 WebSocket JSON-RPC 客户端，默认端口为 `9012`；
  单请求/单响应、完整消息、响应 ID、RPC error、超时和断线重建均在 Adapter 内处理。
  可能已到达控制器的变更请求不自动重发；旧 `30004` 行协议类仅保留兼容测试，不再由
  默认 AUBO 模块注册。
- 新增独立的 AUBO 工程控制边界和 Adapter/MES 路由：工程查询、显式加载、启动、停止。
  工程名由请求传入并统一去掉 `.pro/.lua` 后缀；快速白名单默认只有 `测试`，运行中或
  未加载工程不会重复启动。历史变量握手继续单独隔离，默认不开启。
- WPF 新增 `AUBO 机械臂` 面板，通过 MES HTTP 显示 `rob1`、Robot/Safety/Operational/
  Runtime 和当前工程，并提供加载/启动/停止按钮；WPF 不直连 AUBO、不暴露寄存器。
- 新增现场标准配置模板 `src/MesControlAgv.Adapter/appsettings.FieldStandard.json`，
  固定单台 `AGV-01`、`W500-SZ`、单段 `LM1 → LM2`、`AcquireControl=true`、Push 关闭和
  `0.3 m/s` 上限；该文件不会被默认启动自动选用。
- 新增人工触发的 AGV 到站→AUBO 顺序协调服务/API，支持单次 sequence ID、到站相关性检查、
  必要时加载已批准工程、只启动一次并轮询 Runtime；异常进入人工处置状态，不自动重试。
  以上均只用 loopback/离线验证，尚未执行现场 AGV 移动或 AUBO 新一轮启动。

- 16:37 重新执行现场 WebSocket 只读预检（证据：
  `artifacts/aubo-field-readonly-20260831-163700.json`）：`rob1`/`aubo_C5` 可达，
  RobotMode=`Running`、SafetyMode=`Normal`、Runtime=`Stopped`，但
  OperationalMode=`Manual` 且索引 0 无预加载工程；因此仍为 **NO-GO**。本次未写变量、
  未加载/启动工程、未发送运动或 AGV 命令。

- 16:49 再次执行只读预检（证据：
  `artifacts/aubo-field-readonly-20260831-164900.json`）：笔记本当前有线接口
  `192.168.1.11` 可经 AGV 控制网访问 AUBO `:9012`；OperationalMode 已为
  `Automatic`，Runtime=`Stopped`，但索引 0 仍无预加载工程。仅执行机械臂程序不要求
  AGV 移动；切换到 WLAN 前必须确认透明 Bridge 或明确到 `192.168.1.0/24` 的路由，
  不得启用 Windows Bridge/ICS 或同时配置两条同网段路径。

- 16:55 将 AUBO `:9012` TCP 探测强制绑定到 WLAN `192.168.200.147`，连接超时，
  证明当前 Wi‑Fi 路径尚不能到达 `192.168.1.102`（证据：
  `artifacts/aubo-wlan-connectivity-20260831-165528.json`）。未进行 WebSocket 请求，
  未写入、未加载/启动工程、未发送 AGV 命令；有线控制网路径保持可用。

- 17:03 现场操作员拔掉有线网线后暂停联调；当前以太网为 `Disconnected`、WLAN 为
  `Up`。最后有线只读证据仍为 `artifacts/aubo-field-readonly-20260831-164900.json`
  （Automatic、Stopped、无预加载工程），未执行任何写入或运动。交接检查点记录在
  `artifacts/aubo-field-link-checkpoint-20260831-170356.json`。下次必须先恢复/授权链路，
  重新执行只读预检，再验证 WPF 刷新线程修复；不得直接加载或启动工程。

- 17:00 WPF 点击机械臂刷新时曾因后台线程直接触发 WPF `CanExecuteChanged` 而闪退；
  Windows `.NET Runtime` 事件已确认根因。`AuboArmControlViewModel` 已增加 Dispatcher
  回 UI 线程的通知/命令状态保护，并以仓库外临时输出完成编译和定向测试（2/2）；下次
  恢复链路后仍先只读验证实际刷新，不启用控制按钮。

### 2026-08-30 优先级切换：现场机械臂优先，离线阶段暂停

- 按最新指令暂停 O24 及后续离线快照恢复/归档开发；已完成的 O1-O23 代码、测试和文档保留，
  不执行自动清理或现场动作。
- 现场 P0 改为先联通机械臂：第一步只确认批准网络、JSON-RPC 端点、设备身份、运行/安全模式、
  Lua 工程和白名单变量的只读证据；不发送变量写入、控制权申请、程序启动或运动命令。
- 机械臂只读链路通过且四项现场变量契约（命令键、动作编号、`ack`/`result`、完成回复方式）
  明确后，才另行申请空载握手；离子色谱 D160+、SHA-18i/自动进样器现场验证列为 P1。
- 在现场控制电脑、批准的机械臂地址/端口和明确只读授权未确认前，不从当前开发环境发起设备连接。

### 2026-08-31 现场机械臂只读联调准备

- 新增 `docs/AUBO-FIELD-READONLY-RUNBOOK-2026-08-31.md`，固定交换机接入、
  `192.168.1.102:30004` JSON-RPC 只读检查、Adapter 隔离启动和停止步骤。
- 新增 `scripts/Invoke-AuboReadOnlyPreflight.ps1`：默认只读机器人/安全/运行/操作
  模式及预加载工程；变量读取必须由现场显式传入键名；输出 `writesAttempted=false`
  和 `canDetermineGo=false`，不自动判定 GO。
- `scripts/start-physical-acceptance-adapter.ps1` 增加显式
  `-EnableAuboReadOnly -AuboHost -AuboPort -AuboRobotName` 参数，控制写入保持关闭。
- `scripts/Invoke-AuboRpc.ps1` 增加只读 `readiness` 操作。交换机和批准的现场控制
  电脑尚未确认前，本开发环境不执行设备连接；当前仍为 **NO-GO**。

### 2026-08-31 AGV 内部交换机/网络桥接准备

- 新增 `docs/AGV-INTERNAL-SWITCH-BRIDGE-RUNBOOK-2026-08-31.md`，覆盖内部二层交换机、
  Wi‑Fi Bridge/NAT 判别、线缆记录、总控改址、连通性验证、回退和异常停机步骤。
- 现场照片初步显示黑色鳍片盒更像车载工控机/控制器，绿色 `16XT3/16XT4` 端子区更像
  AUBO 数字 I/O；目前尚未找到可确认的以太网交换机，不能据此接线。
- 新增照片中的中上部黑色带铭牌模块可能是 PLC/远程 I/O/通信模块，但未看到足够的
  RJ45/Link 证据；白色端子组件属于电气配线区域，均不能直接当作交换机。
- 当前仍未接入交换机；下一步由现场人员补拍 Ethernet/LAN/SW 设备和线缆去向，确认后
  再按手册完成网络跳线和只读验证，不启动机械臂、不移动 AGV、不触发 DI/DO。

### 2026-08-30 离线优化 O23：诊断快照人工清理确认

- 快照生命周期页新增双重确认：勾选确认框并输入完整短语后，才允许处理已扫描出的清理候选。
- 清理动作只将候选移动到源目录下的 `.offline-diagnostics-recycle/<时间>/`，并写入脱敏 `manifest.json`；
  不执行永久删除，源文件变化、路径穿越、非诊断文件名或过期候选都会被拒绝。
- 移动后自动重新扫描并写入有界脱敏审计；修改扫描目录会立即失效旧候选，避免把候选误用于另一目录。
- O23 定向回归 `9/9` 通过；完整解决方案回归 `880 passed / 5 skipped / 0 failed`，
  离线门禁与归档边界校验 `releaseEligible=true`。

### 2026-08-30 离线优化 O22：诊断快照生命周期

- 新增 `OfflineDiagnosticSnapshotLifecycle`，只扫描指定目录下的
  `mes-offline-diagnostics*.json`，区分当前格式、需升级、不支持和无法读取文件。
- 提供保留天数、最少保留最新文件数、最大文件数、总容量和扫描上限策略，展示总大小与清理候选；
  最近快照受保护，非法/不可读文件不会进入自动清理候选。
- 启动诊断新增“快照生命周期”页签和扫描入口；扫描结果只写入内存审计，默认不创建目录、不删除、
  不上传、不覆盖快照。
- O22 定向回归 `24/24` 通过；完整解决方案回归 `877 passed / 5 skipped / 0 failed`，
  离线门禁与归档边界校验 `releaseEligible=true`。

### 2026-08-30 离线优化 O21：诊断审阅筛选与留痕

- 配置差异页增加按状态（全部/仅变化/新增/移除/未变化）和代码/名称搜索，筛选结果只读，
  不修改导入快照或当前诊断。
- 增加“记录本次审阅”入口，可填写本地审阅人简称和备注；记录进入有界脱敏审计，包含基线时间、
  筛选条件和可见/变化数量，不自动写盘、不调用网络，也不改变现场结论。
- 无基线时审阅按钮保持禁用；审阅输入长度受限并在入审计前脱敏，避免把路径、端点或凭据带入留痕。
- O21 定向回归 `30/30` 通过；完整解决方案回归 `868 passed / 5 skipped / 0 failed`，
  离线门禁与归档边界校验 `releaseEligible=true`。

### 2026-08-30 离线优化 O20：配置差异审阅

- 启动诊断导入基线后，`OfflineDiagnosticDiffBuilder` 按稳定代码比较当前与基线的离线值、状态，
  并把 `FIELD_` 前缀的现场只读预检输入一并纳入；比较前后均执行脱敏，避免差异成为地址/路径侧信道。
- 诊断页新增“配置差异审阅”标签，展示新增、移除、变化和未变化项；未读取基线时保持空状态，
  不会误把当前报告当作历史证据。
- 带基线再次导出时增加 `configurationDiff`（`mes.offline-diagnostic-diff/1.0`）区块，支持回读；
  差异报告与导出 DTO 的 `CanDetermineGo` 均固定为 `false`。
- O20 定向回归 `27/27` 通过；完整解决方案回归 `865 passed / 5 skipped / 0 failed`，
  离线门禁与归档边界校验 `releaseEligible=true`。

### 2026-08-30 离线优化 O19：CI 脱敏归档

- 新增 `.github/workflows/offline-release-gate.yml`，在 Windows runner 上调用现有离线门禁，
  再执行归档边界校验；流程不启动本地服务以外的现场端口，也不读取设备。
- CI 只上传系统临时目录中的单一 `mes-offline-release-gate.json`；`assert-offline-release-report.ps1`
  校验 schema、仓库身份脱敏、路径/凭据/原始日志字段和 `fieldPreflight.canDetermineGo=false`。
- 门禁摘要记录允许的 5 个既有“需要完整模拟器”的 E2E 跳过项，并将新增的非预期跳过标为失败，
  防止 CI 静默扩大跳过范围；测试输出中的路径和端点先脱敏后才进入摘要。
- 本地完整门禁与归档校验通过：`858 passed / 5 skipped / 0 failed`、`unexpectedSkipped=0`，
  22 项静态检查全部通过，`releaseEligible=true`；当前工作区未配置远端 CI，首次推送后由仓库
  runner 执行实际 Artifact 归档。

### 2026-08-30 离线优化 O18：现场只读预检输入对齐

- 新增 `FieldPreflightChecklistBuilder`，把启动诊断中的运行模式、Adapter
  `read-only-preflight`、AGV `AcquireControl`/`EnablePush`/`MinimumConfidence`
  映射为现场只读预检输入项，并明确区分“离线已知 / 需现场确认 / 离线阻断 / 不适用”。
- 现场必须重新取得控制器身份、控制权持有者、当前站点/空闲状态、地图身份、定位、
  安全门禁、授权和单段路线等新鲜证据；历史快照或离线数值不会被当作现场通过证明。
- 启动诊断页面新增“现场只读预检输入”标签页；导出 JSON 增加
  `fieldPreflight`（`mes.field-preflight/1.0`）区块，保留机器可读代码、状态和脱敏值。
- `CanDetermineGo` 固定为 `false`，发布门禁增加字段 schema 和“不自动判定 GO”的静态检查；
  物理交接记录继续维持 **NO-GO** 边界。
- O18 定向回归 `20/20` 通过；完整解决方案回归 `858 passed / 5 skipped / 0 failed`，
  离线发布门禁 `releaseEligible=true`。

### 2026-08-30 离线优化 O17：自动化发布摘要

- `verify-offline-release.ps1` 现在使用临时 TRX 目录聚合各测试项目的计数，生成单一脱敏 JSON 摘要，
  包含 revision、工作区状态、诊断 schema、fixture 名称/数量、门禁检查和
  `tests.passed/failed/skipped/total`。
- 摘要新增 `gatePassed` 与 `releaseEligible`：使用 `-SkipTests` 时静态检查可以通过但不可标记为发布资格；
  默认模式要求全量测试执行且无失败，当前 5 个带明确原因的既有 E2E 跳过项列入允许清单。
- 默认门禁实际执行结果：`passed=854`、`failed=0`、`skipped=5`、`total=859`，
  `releaseEligible=true`；TRX 和原始测试输出均不落入仓库。

### 2026-08-30 离线优化 O16：视觉与仪器脱敏样本扩展

- 新增视觉候选只读配置和真实仪器只读配置 fixture；视觉样本明确表达“配置存在但 Adapter 未注册”，
  不会因配置样本出现而开放动作。
- `StartupConfigurationInspector` 增加视觉配置存在/注册状态区分；仪器样本继续验证启用参数和
  “HTTP 与驱动只读”的写入边界。
- 回放测试扩展到 7 个用例，发布门禁敏感信息扫描已覆盖新增 fixture；样本只含脱敏或 loopback 数据。
- O16 定向回归 `7/7`，WPF 全量回归 `304/304`，全解决方案回归
  **854 passed / 5 skipped / 0 failed**。

### 2026-08-30 离线优化 O15：发布前质量门禁

- 新增 `scripts/verify-offline-release.ps1`：默认用 `dotnet test MesControlAgv.sln -m:1` 做单进程
  全量回归，同时校验诊断导出/规则 schema、5 个启动配置 fixture 的脱敏、现场交接和验收记录中的
  NO-GO 边界。
- 脚本不启动 Simulator/Adapter/MES，不访问网络，不打开串口；默认报告写入系统临时目录，仓库路径
  在报告中固定为 `[REDACTED]`。`-SkipTests` 仅用于快速静态检查，发布门禁默认不跳过测试。
- O15 门禁实际执行通过；全解决方案结果保持 **852 passed / 5 skipped / 0 failed**。

### 2026-08-30 离线优化 O14：配置规则样本回放

- 新增 `tests/MesControlAgv.Wpf.Tests/fixtures/startup-diagnostics/` 样本集，覆盖 simulator、
  physical + read-only-preflight、physical + standard 冲突、仪器有效只读和仪器参数错误五种基线。
- 新增 `StartupConfigurationReplayTests`，逐例保护启动结论、AGV 驱动、Adapter 运行模式、真实写入
  边界和诊断代码；同一输入重复执行的结果投影必须完全一致。
- 样本回放只读取仓库 fixture，不读取环境变量、不连接网络、不打开 COM；更新规则时先运行回放，再改现场配置。
- O14 定向回归 `5/5`，WPF 全量回归 `302/302`，全解决方案回归
  **852 passed / 5 skipped / 0 failed**。

### 2026-08-30 离线优化 O13：视图生命周期契约

- 新增 `ViewLifecycleContractTests`，验证 `MapDashboardView` 卸载后停止动画并解除地图订阅，
  `WorkflowManagementView` 卸载后解除编辑器订阅。
- 对任务、KPI、地图、流程、两个导入页和启动诊断页重复创建进行资源字典 smoke test，确保不会
  累积主题字典或重复加载资源。
- O13 定向生命周期测试 `2/2`，WPF 全量回归 `297/297`，全解决方案
  **847 passed / 5 skipped / 0 failed**。

### 2026-08-30 离线优化 O12：诊断规则与导出版本化

- 新增版本常量：导出格式 `mes.offline-diagnostics/1.0`、启动诊断规则
  `mes.startup-diagnostics/1.0`；每份新导出同时写入 `schemaVersion` 和 `diagnosticRuleVersion`。
- “启动与离线诊断”页增加“读取诊断”：当前版本可直接读取；缺少版本号的旧文件兼容读取并提示
  重新导出升级；未知主版本安全拒绝，不影响当前审计。
- 导出/读取使用显式 DTO，保留未知字段兼容空间；读取和再次导出继续执行脱敏，未引入现场网络或串口探测。
- O12 定向测试 `6/6`，WPF 全量回归 `295/295`，全解决方案回归
  **845 passed / 5 skipped / 0 failed**。

### 2026-08-30 离线优化 O11：流程编辑器组合视图解耦

- 实验流程管理页已提取为 `Views/WorkflowManagementView`，承载流程预设、工具箱、Nodify 画布、
  属性检查器、验证列表、转换报告和兼容导入。
- 流程画布 Attach/Detach、验证问题定位、拖拽新增节点、快捷键和画布适配行为已迁移到独立视图
  生命周期；`MainWindow` 不再持有 `_workflowEditor` 或流程控件字段。
- `WorkflowMainWindowBindingTests` 改为通过视图边界检查画布和验证表，保持现有发布/校验契约。
- WPF 全量回归保持 `294/294`，全解决方案回归 **844 passed / 5 skipped / 0 failed**。

### 2026-08-30 离线优化 O10：设备模块启动诊断扩展

- `StartupConfigurationInspector` 已扩展检查 Adapter 中的 AUBO 机械臂和样品工作站模块：
  Enabled/ControlEnabled 冲突、read-only-preflight 写入冲突、机械臂 Host、工作站 EquipmentNo
  和 BaseUrl 均在连接前静态验证。
- 当前 Adapter 未注册视觉模块，启动诊断明确显示“视觉模块：未注册”；进程内 `MockVisionDriver`
  不再可能被误读为现场视觉已经接入。
- 新增 `WPF_INSTRUMENT_GATEWAY_CONFIG_PATH`，可纯本地解析 CIC-D160+ 的启用状态、仪器 ID、
  COM、波特率、数据位、超时和从站地址；网关真实写入入口始终显示
  “禁用（HTTP 与驱动只读）”。
- 补充配置冲突、视觉缺口、仪器禁用安全基线和启用参数错误测试；检查过程不执行网络或串口探测。
- O10 定向测试 `15/15`，WPF 全量 `294/294`，全解决方案
  **844 passed / 5 skipped / 0 failed**。

### 2026-08-29 离线优化 O9：运营页面解耦

- 任务监控、KPI 看板和只读地图分别提取为 `TaskMonitorView`、`KpiDashboardView` 和
  `MapDashboardView`；任务列布局恢复、地图导入/缩放/拖拽/动画/PNG 导出随页面迁移。
- `MainWindow.xaml` 从约 1280 行降至约 700 行，`MainWindow.xaml.cs` 降至约 240 行；
  主窗口继续保留导航与流程画布装配，不再持有任务表和地图控件字段。
- 新增运营页面装配测试，校验任务表布局键、KPI 图表数据源和地图控件 DataContext；地图专项
  布局、动画、导出测试保持通过。
- 全量回归发现成功路径 E2E 依赖 Mock 驱动随机失败率；Mock 机械臂/视觉新增可配置失败率和随机种子，
  成功与并发合同测试显式使用 0% 失败率，消除随机抖动，默认开发行为仍保持 5%/10%。
- WPF 全量回归 `291/291`；全解决方案回归 **841 passed / 5 skipped / 0 failed**。

### 2026-08-29 离线优化 O8：审计与脱敏诊断导出

- 新增容量上限为 500 条的内存离线审计，自动记录主监控、仪器、就绪地图和导入页的状态转换；
  从失败/过期/取消进入刷新时单独标记为 `retry`。
- 诊断详情进入内存前即经过 `DiagnosticRedactor`：HTTP(S) URL、IPv4、host/address/endpoint、
  COM 口、Windows/UNC 路径以及 password/token/secret/authorization/API key 均替换为占位符。
- “启动诊断”页扩展为“启动配置 / 离线审计”双页，可清空审计或导出脱敏 JSON；导出采用 UTF-8
  无 BOM 和临时文件原子替换，内容包含配置摘要与状态审计，但不包含真实敏感地址或凭据。
- 新增审计容量、进入内存前脱敏、状态失败/重试、原子导出和 UI 绑定测试。
- WPF 全量回归 `290/290`，全解决方案回归 **840 passed / 5 skipped / 0 failed**。

### 2026-08-29 离线优化 O7：配置与启动诊断

- WPF 在启动任何本地服务或创建 HTTP Client 前，先由 `StartupConfigurationInspector` 纯本地解析
  运行模式、服务 URL、本地服务托管策略、Adapter 配置和可选功能路径。
- 无效运行模式、URL、服务托管开关、Adapter JSON 和 physical+simulator 驱动冲突会阻止启动；
  缺少 Adapter/SMAP/流程存储/ShineLab 路径只产生明确警告，不伪装成已就绪。
- 新增“启动诊断”页面，持续展示 WPF 模式、AGV 驱动、Adapter 运行模式、真实写入入口状态及
  每项检查的代码和说明。`read-only-preflight` 与 simulator 均明确显示真实写入禁用；
  physical+standard 只显示“可能开放（仍需现场授权）”。
- 默认端口契约已迁移到启动检查器并由 E2E 合同测试保护；检查过程不连接 MES、Adapter、设备或端口。
- WPF 全量回归 `286/286`，全解决方案回归 **836 passed / 5 skipped / 0 failed**。

### 2026-08-28 离线优化第一批完成（O1-O3）

- 本批次只修改离线可靠性、WPF 布局与视图结构；未连接现场 COM、AGV、机械臂、视觉或
  ShineLab，也未发送任何设备命令。
- O1 可靠性：`StringToVisibilityConverter` 的反向误绑定不再抛出
  `NotImplementedException`，空值、空白值、正常文本及反向绑定均有回归测试。
- O2 界面：主窗口最低尺寸由 `1180x720` 调整为 `820x480`，侧栏和内容边距收紧；
  AGV 表格取消固定 `1160` 宽度，改为表格内部横向滚动，保证紧凑窗口下右侧调度面板
  仍处于可视区；调度面板在低高度窗口中可独立纵向滚动。新增 `820x480` WPF 布局回归测试，
  覆盖 1366x768、150% DPI 的典型场景。
- O3 架构：AGV 通讯与调度页及其列布局恢复行为已从巨型 `MainWindow` 提取到独立
  `Views/AgvCommunicationView`，后续可独立迭代和测试。
- O4 架构/界面：离子色谱样品任务导入和批量任务导入已分别提取为
  `Views/ShineLabSequenceImportView` 与 `Views/BatchTaskImportView`；文件选择、列布局恢复和
  页面命令绑定均由对应视图承载，`MainWindow` 只负责导航装配。
- O5-A 架构：任务生命周期端点已提取到
  `MesControlAgv.Mes/Endpoints/TaskEndpointRouteBuilderExtensions`；`Program.cs` 只保留
  `app.MapMesTaskEndpoints()` 装配，创建、派发、到站、确认、重试、取消、恢复、列表和详情
  的 HTTP 契约保持不变。
- O5-B 架构：工作流定义、版本、发布、执行、运行查询、交互和运行控制端点已提取到
  `MesControlAgv.Mes/Endpoints/WorkflowEndpointRouteBuilderExtensions`；工作流异常到状态码
  的映射保持不变，`Program.cs` 只保留 `app.MapMesWorkflowEndpoints()` 装配。
- O5-C 架构：仪器只读状态、样品工作站、AGV 状态/命令/I/O、机械臂状态/准备度/握手端点已
  提取到 `MesControlAgv.Mes/Endpoints/DeviceGatewayEndpointRouteBuilderExtensions`；
  Adapter 异常映射与现场写入接口边界保持不变，`Program.cs` 只保留设备网关装配。
- O6 离线体验：新增 `OfflineDataStateViewModel`，统一表示未加载、刷新中、已更新、暂无数据、
  数据过期、刷新失败和已取消；已接入主监控、AGV、仪器、就绪地图和两个导入页，并统一状态颜色。
  刷新失败时明确提示“可点击刷新重试”，空数据与旧数据过期不再混用。
- 截至 O6 的 WPF 全量回归为 `278/278` 通过；任务 API 定向回归为 `9/9` 通过；
  阶段范围和后续离线队列见
  `docs/OFFLINE-OPTIMIZATION-PHASES-2026-08-28.md`。

### 2026-08-28 厂商 18i 双通道协议表已取得并完成交叉核对

- 新增原始资料 `res/18i双通道自动进样器通讯协议.xlsx`，SHA-256 为
  `674FDAFD7FC45925D44FD7CE48F0415CA1F6AB28E0570B95293F5F0DEEAC073C`；工作表覆盖
  普通运行、调试校准、位置微调和用户程序四条路径。
- 普通运行表与 2026-08-27 COM3/USBPcap 证据逐字节一致：状态读取为
  `0x076C/6 + 0x0772/2`，方法块为 `0x0640/27 + 0x065B/3`，
  `0x0709=1` 为自动进样触发。上一轮 27-word 方法帧的进样模式、洗针、样品位、
  体积和托盘规格均已按协议解码。
- 纠正旧假设：`0x044C/11` 属于“用户程序”工作表的命令完成状态，不是普通运行状态。
  `0x0708` 在厂商运行表中命名为初始化，而静态 `CmdStop` 指向同一地址；因此 ShineLab
  “终止”是否复用初始化/复位仍需现场动态确认，不能把它当作已证实的硬件急停。
- `Sha18iProtocolCodec` 已更新为正确的普通运行状态、阀状态、设备身份和方法块地址，
  增加离线 0x06/0x10 帧构造及响应校验；不含串口写入路径。专项测试 `32/32` 通过，
  InstrumentGateway 全量测试 `80/80` 通过。
- 完整交叉核对见 `artifacts/ion-chromatography/SHA18I-PROTOCOL-CROSSCHECK-20260828.md`。
- 当前现场路线从“探索性猜协议”切换为“协议表驱动的离线解析 + 最小受控现场验证”；
  在确认设备身份/固件、串口参数及停止/清洗互锁前，不向真实 SHA-18i 重放写帧，
  不把写入路由接入 MES。

### 2026-08-28 本地协议回归推进

- 基于 2026-08-27 控制电脑抓包结果，新增 `Sha18iProtocolCodec`：固化
  自动识别 `0x04@0x06A4/1`、普通运行状态 `0x04@0x076C/6`、阀状态
  `0x04@0x0772/2`；`0x044C/11` 已单独标记为用户程序状态块。
- 新增离线证据解析：可校验 SHA-18i 的 `0x10@0x0640/27` 方法块及其标准回显，
  但不发送、不重放，也不为 payload 字段赋予未经确认的业务含义。
- InstrumentGateway 回归由 50 增至 80 项；Adapter 213 项、MES 124 项、
  E2E 24 项（19 通过、5 跳过）均通过。测试未打开现场 COM，也未发送设备帧。
- 单节点全解方案回归为 **794 passed / 5 skipped / 0 failed**；并行执行曾因环境
  内存不足（MSBuild OOM）中断，随后以 `-m:1` 完整通过。
- 现场下一步仍是控制电脑上的双串口盘点和被动补抓（停止、清洗、托盘、缺瓶清除）；
  在动态字段与安全条件确认前，不注册 SHA-18i 写入路由，不开放 D160+ 物理激活。

### 2026-08-28 校准工具资料纳入分析（现场动作暂缓）

- 控制电脑共享目录新增完整 `SHA-17i18i校准软件20230708`，含
  `ShDeviceDebugTool.exe`、`ShAutoSampler12/17.exe`、协议 DLL 和历史日志；
  已复制到本地隔离目录 `artifacts/ion-chromatography/sha18i-calibration-20230708/`，
  未启动任何 exe/DLL。
- `ShDeviceDebugTool.exe` 的导入表直接包含 `ShAS11D` 的到样品瓶、到洗针口、
  到废液口、针上下、托盘、阀和连接接口；对话框资源还包含“注射泵[AS18]”和
  低层校准控件。12 组串口日志共有 911 条写帧、882 条非空读帧，写帧 CRC 全部通过。
- 2026-08-13 日志已把“版本、原点、洗针口/废液口、样品位、针上下、轴移动”与
  原始 TX/RX 对应起来，并出现 `01 06 07 08 00 01 C8 BC` 的原样回显；但校准包
  与当前 ShineLab 同名 DLL 哈希不同，`0x0708` 在校准代码中同时被 `Init_New` 使用，
  不能直接命名为停止或直接重放。
- 验证路线调整为：先做版本/固件兼容性盘点 → 获授权后用校准工具逐项验证低层动作 →
  再用当前 ShineLab/AutoSampler 验证完整方法进样。旧 `RUN-2`/`RUN-3` 在兼容性确认前
  暂停；新的现场手册为 `FIELD-RUNBOOK-SHA18I-CALIBRATION-V4-20260828.txt`。
- 进一步比对发现，当前 ShineLab 自带 `ShDevice.dll` 也导出 `ShAS11D` 的
  `MoveToSamplerNo`、`MoveToWash`、`MoveToWaste`、`MoveToZero`、`Init_New`、
  `Wash_New`、`Inject_New` 和泵操作接口；因此优先使用现场 ShineLab 目录中与 DLL
  配套的高层 ShineLab/AutoSampler 路径做动态取证。现场目录中的
  `ShDeviceDebugTool.exe` 目前已证实与 `ShUI.dll`/`ShTool.dll` 不匹配，不能启动；
  共享目录的 2023 校准包仍不与现场文件混装。
- 现场第 2 步初次报错的根因是把 `<D:\Program Files (x86)\ShineLab>` 占位符带尖括号
  原样输入，导致路径未解析；使用真实路径后 `Get-ChildItem -File` 和 `Get-FileHash`
  均正常。V4 手册仍保留不依赖动态参数的兼容写法，并已重新同步到共享目录。
- 现场随后完成了配套文件盘点：`ShDeviceDebugTool.exe` 120,320 字节、
  `ShDevice.dll` 157,696 字节；两者 SHA-256 分别为
  `EE9A31B057C7095D2E32F61B150C5C5F8704E0137A04132B3368DADB2E91EF90` 和
  `134EAF7537E84EC1E913B79C1650D957F3C4B7864EE5F91132DCF41C9FA7944B`，
  与仓库 `res/ShineLab` 完全一致。但现场启动该调试工具时缺少
  `?SetLimit@CEditNumber@@QAEXMM@Z` 入口；进一步静态比对确认当前 `ShUI.dll` 和
  `ShTool.dll` 也不是该 EXE 的 ABI 配套。第 3 步因此暂停，不能替换单个 DLL；
  若无法取得厂家完整匹配工具包，则不再依赖该调试工具，改走当前 ShineLab
  的被动抓包和静态调用链路线。
- 当前目录时间戳也显示混合构建：调试工具/`ShDevice.dll` 为 2022-02，
  `ShUI.dll`/`ShTool.dll` 为 2026 年文件。完整依赖证据见
  `SHA18I-DEBUGTOOL-DEPENDENCY-MISMATCH-20260828.md`。
- 在拿到厂家匹配调试工具前，低层校准分支关闭；可在授权的空载测试条件下，
  保持当前 ShineLab 运行并使用 USBPcap 被动抓取高层方法/进样/停止/自动洗针流程，
  不让第二个程序占用 COM。
- 共享目录的最新 `dual-serial-inventory.json` 仍显示 `ShineControl-Normal.exe`
  处于运行状态；在其正常退出并确认 COM 释放前，不能启动调试工具，避免两个程序抢占同一串口。
- 依赖入口错误的完整静态证据见
  `artifacts/ion-chromatography/SHA18I-DEBUGTOOL-DEPENDENCY-MISMATCH-20260828.md`；
  2023 校准包内部依赖可解析，但不能与当前 ShineLab 文件混装。
- 已生成并同步 `NEXT-SELF-VALIDATION-20260828.txt`：先做 D160+ COM4 只读，
  再走当前 ShineLab 的被动抓包分支；低层校准包保持隔离和暂停。
- 分支 A 已由现场完成：D160+ `COM4/115200 8N1` 连续 3 轮、4 组查询共
  `12/12` 成功，所有功能码均为 `0x04`，CRC/长度/从站校验通过且响应逐轮一致；
  设备标识仍为 `YA7261078`。证据 JSON 为
  `d160-protocol-read-validation-20260828.json`（SHA-256
  `B2F883A4C2C14637FCF890FFC776274CECACE55BB3556CF363E9287F55C11F44`）。
- 当前下一步为分支 B：启动当前 ShineLab 后仅用 USBPcap 被动捕获 SHA-18i
  的方法/单次测试进样及已有界面的停止或自动洗针；调试工具入口错误已绕开，
  不发送任何手工写帧。
- 分支 B 已完成一轮抓包（运行 1、暂停 2、恢复 1、终止 1），但现场窗口是
  `ShineDataAcquisition - D160+-test111`。PCAP 中 COM3/SHA-18i 仅有 `0x04`
  状态轮询，没有任何进样/停止/清洗写帧；D160+ COM4 出现 10 个 `0x06` 写帧。
  最后一条 D160 过程读回仍为压力 raw `9`、泵状态 raw `1`，下一轮前必须先由
  现场确认 D160+ 已安全停机。详见 `SHA18I-CAPTURE-ANALYSIS-20260828-132249.md`。
- 下一轮不得继续在 D160+-test111 上重复生命周期按钮；已生成
  `SHA18I-ACTION-CAPTURE-V2-20260828.txt`，要求先确认 D160 安全状态，
  再选择明确启用 SHA-18iA/AS18 的方法，并按 S0–S4 分场次单动作抓包。
- 已分析共享目录的 `ShineLab标准版中文说明书V3.0.pdf`（62 页）。手册明确：
  SHA-18A 通道在“仪器→仪器管理→配置”中加入并选 COM；自动进样器参数在
  “仪器→色谱方法管理”的方法选项卡/“进样器信号”中设置；样品行的“进样盘编号”
  和“进样体积[μL]”位于“分析控制→样品编辑”，通常需水平滚动查看。现场截图已
  证实 SHA-18iA 已勾选且 COM3 正确；下一步应只读查看方法和样品表，不再在
  D160+-test111 上重复运行控制按钮。详见 `SHINELAB-MANUAL-ANALYSIS-20260828.md`。

### P1 - 离子色谱 D160+ + SHA-18i 现场通讯验证（2026-08-27 起，机械臂只读联通后）

当前任务已从机械臂切换到两台串口仪器。笔记本网线已直接接入连接 D160+ 与
SHA-18i 的控制电脑；控制电脑地址由旧 `192.168.250.2` 改为当前
`192.168.1.108`，本机最后观测为 `192.168.1.106/24`。已确认 `ping .108`
成功、TCP 445 可达；旧共享 `\\192.168.250.2\\MES-RPA-Inbox` 已失效，
新共享 `\\192.168.1.108\\MES-RPA-Inbox` 尚未确认。由于远程 WinRM/CIM
未配置凭据，ShineLab 监听端口必须在控制电脑本机检查。

当前执行顺序：

1. 在控制电脑管理员 PowerShell 中按 PID 查询 ShineLab 进程的
   `LISTENING` 端口；5556/5557/5558 等内部 ZMQ 端口不能直接当作下游表协议。
2. 若 ShineLab 未被证实为 TCP Server，停止继续猜测下游协议，转入中控双串口
   代理路线；ShineLab 仅保留查图谱和辅助复核用途。
3. D160+ 先按既有 COM4/115200 8N1/Modbus 从站 1 的只读脚本复核；SHA-18i
   必须在控制电脑上做被动串口/USBPcap 抓包，先确认 COM 和动态报文，再开放进样/启动。

详细交接见 [`ION-CHROMATOGRAPHY-FIELD-HANDOFF-2026-08-27.md`](ION-CHROMATOGRAPHY-FIELD-HANDOFF-2026-08-27.md)。

### P0 现场准备：机械臂变量握手 + AGV 到站取放整套流程（2026-08-27，当前优先联通）

目标：中控派发 AGV 导航到站点 → 确认到站后中控写入机械臂控制器内部变量 →
机械臂驻留 Lua 脚本按固定 `if` 语句块执行抓取 → 完成后回复中控。

已完成（离线验证，未接触硬件）：

- 通讯方案定案并有厂家文档依据：AUBO/ARCS `RegisterControl` **命名变量** + JSON-RPC。
  四种绑定（C++/Python/Lua/JSON-RPC）共享同一变量存储，故中控写入的键即 Lua 读取的键。
  已排除两条错误通道：编号输入寄存器（地址空间 0:47，`[0:23]` 保留给 FieldBus/PLC、
  `[24:47]` 保留给 RTDE 外部客户端）与 Modbus 接口（该接口使机械臂成为**主站**，
  不能作为我们的从站）。`Int16Register` 属 Modbus-Slave 空间，仅作备选。
- 只读适配层已接入运行时（四个 GET：`status`/`readiness`/`handshake`/`variables/{key}`）。
  厂家枚举归一化为契约值（`RobotModeType 8 → Running`、`SafetyModeType 1 → Normal`、
  `OperationalModeType 1 → Automatic`）；未文档化整数一律落 `Unknown` 并保留原始值，
  不猜测、不跨类型强转。
- 就绪判定 fail-closed：离线、非 Running、非 Normal、Manual/示教模式、解释器状态异常、
  加载的 Lua 工程名不符，任一条都给出可读阻塞原因。命名变量白名单强制生效，
  五个握手键始终在内，白名单外的键直接拒绝，避免用状态接口扫控制器变量存储。
- 握手状态由 `seq`/`ack`/`result` 三元组**无状态**推导（`Idle → Dispatched → Running →
  Completed/Failed`），不查历史，故 Adapter 重启后结论一致。
- 写入路径已实现并保持显式配置：走独立的 `IAuboArmControlledRpcTransport`，只读接口仍
  不表达写操作；受控握手路由仅在 `ControlEnabled=true` 且标准模式下注册。派发次序为
  先清 `result`、再写 `seq`、最后写 `cmd`（用写入日志断言该次序，防止 Lua 读到上一轮结果码），
  并随派发下发 `setWatchDog` 失效保护。
- 环回控制器兼作 AUBO 控制器与驻留 Lua 工程的替身，`RunLuaHandshakeCycle` 复现
  「取命令 → 回显序列号 → `if` 块执行完毕再发布结果码」，整条链在无硬件条件下可回归。
- `ControlEnabled` 默认关闭；标准模式可显式启用，`read-only-preflight` 仍在中间件层
  拒绝所有 POST（`Program.cs:27-43`）。当前只进入机械臂只读联通阶段，现场写入和动作仍未启用。

阻塞项 —— 以下四项属现场约定，**不可推测**，Lua `if` 块编号定义权在项目方，
猜错会让机械臂走错分支：

1. 命令变量键名（代码默认 `mes_cmd`）
2. 整数 → 动作映射表（抓取、放料、复位等各对应哪个编号）
3. `ack`/`result` 变量键名及结果码含义
4. 完成回复采用中控轮询，还是 Lua 用 `Socket` API 主动推送

现场报告中的 `config/aubo_control.conf` 已确认当前 JSON-RPC 端口为 `30004`；
是否需要额外登录/鉴权以及外部连接的完整握手仍需在机械臂链路上做一次只读实测，
因此主机地址仍通过部署配置注入。

待实现：`RobotArmStepExecutor` 填入 `AgvMoveStepExecutor` 旁预留的位置，以 AGV 状态码
`4 = arrived` 为触发条件；工作流节点类型（现有 `WorkflowGraphNodeTypeIds` 无设备调用类型，
`"robot.execute-program"` 仅为 spike 期硬编码字符串）；以及 AGV 派发链路（WPF → MES API →
Application → Adapter → `TcpAgvClient`）的完整梳理。

#### 2026-08-27 - AGV I/O 直连链路已核实并接入驱动

现场 RoboshopPro 当前连接的 AGV 控制器为 `192.168.1.2`（W500-SZ，
`v3.4.8.0011`），而不是早期架构示意中的 `.100`。厂商《机器人 API 2023》
第 55、353 页和控制器本地 `Config.ini` 共同确认：API `1013` 在 `19204`
读取 DI/DO，API `6001` 在 `19210` 以 `{"id":n,"status":bool}` 设置单个 DO，
响应为 `16001`。

用户现场已取得 AGV 控制权，DO6 高/低切换时听到继电器动作；这证明 AGV 控制器
输出链路成立。`TcpAgvClient` 现已提供 `GetIoAsync`/`SetDoAsync`，Adapter 暴露
`GET /agv/io` 与 `POST /agvs/{agvId}/io/do/{id}`，并附带
`scripts/Invoke-AgvIoApi.ps1` 现场直发工具。下一步只需在机械臂 DI 页面同步观察
哪个输入变化，确定端子/有效电平后即可把“AGV 到站 → 写 DO → 机械臂 Lua 按 DI 执行”
接入任务流程；不需要中控运行 Modbus 服务或新增 PLC。详见
[`AGV-IO-DIRECT-CONTROL.md`](AGV-IO-DIRECT-CONTROL.md)。

报告中的 `default_0.ins` 已补充确认机械臂端 `DI17=pin15`，以及 `DO02/DO03`
被命名为“小车DI7/小车DI8”；这只说明端子命名，不能替代 DO6→DI 的现场脉冲
映射。当前标准输入 `action=0`，所以 Lua 触发程序仍需在确认点位后配置。

当前笔记本网线接的是 AGV 外部口而非 AUBO `enp1s0`，因此只能访问
`192.168.1.2`，不能访问机械臂 `192.168.1.102:30004`。示教器的 TP 会话
可能影响控制权/运行模式，但不会制造 ping 不通；报告日志已看到 TP 与其他
RPC 用户请求并存。要让中控启动/写入 AUBO，需临时直连 AUBO、增加第二块网卡，
或将两台设备接入同一交换机；若不建立该 TCP 路径，只能采用 AUBO 端常驻 Lua
轮询 DI 的实体 I/O 方案。

已新增 `scripts/aubo/MesDiTrigger.lua`（常驻 DI 上升沿监听模板）和
`scripts/aubo/MesDiStartProgramSetup.lua`（把指定 DI 配为 `StartProgram(3)`
的一次性设置脚本），操作说明见 [`AUBO-DI-TRIGGER-SETUP.md`](AUBO-DI-TRIGGER-SETUP.md)。
官方 API 已确认 `getStandardDigitalInput(index)` 与 `StartProgram=3`，因此
第二种方案不需要中控连接 AUBO TCP；AUBO 端预先加载/运行工程后，中控只写 AGV DO。

厂家已提供 `AUBO-CB-AGV-V2` 控制柜手册；已据此确认实体施工应由厂家完成，
并新增 [`VENDOR-IOMAPPING-WORKORDER.md`](VENDOR-IOMAPPING-WORKORDER.md)，明确
AGV 底盘 DO6 → AUBO-CB `16XT7` DI、共地/电平匹配、AUBO `16XT8` 回执 DO 等
待确认项。中控不需要交换机，也不应自行拆柜接线。

当前路线调整：暂不实施实体 I/O，优先恢复网络拓扑。机械臂网口目前占用视觉
Wi-Fi 模块唯一 LAN，AGV 外部/上装口只可达 `192.168.1.2`。已新增
[`NETWORK-SWITCH-RECONNECTION-PLAN.md`](NETWORK-SWITCH-RECONNECTION-PLAN.md)，
要求用同一二层交换网络同时接入 AGV `192.168.1.2`、AUBO `192.168.1.102`、
视觉和中控；网络通过后再做 AUBO `30004` 直连写入/启动验证。

### P1 - 离子色谱 ShineLab CSV 导入 RPA POC（2026-08-24，暂停）

- 已在真实 ShineLab 界面确认分析控制的样品任务表存在“导出CSV”和“从CSV导入”。
- 已用 `ExportData.csv` 验证导入会追加 7 条任务，不会覆盖原任务；重复导入存在重复任务风险。
- RPA 阶段一只负责导入任务，不自动点击“运行”，也不改变既有 D160+ 串口只读安全边界。
- 现场联调脚本更新到 `2026-08-24.13`。已确认 ShineLab 的 `highestAvailable` 权限会拦截非管理员 PowerShell 的模拟输入；改用管理员 PowerShell 后，RPA 成功触发菜单、选择“从CSV导入”、提交文件，操作人员确认空序列页面显示 7 条任务。POC 人工验收通过；自动行数/字段校验和批次幂等保护仍待完成。
- 下一阶段先做只读“导出当前序列并比对”，再增加 `BatchId + CSV SHA256 + 目标序列` 幂等回执和 MES 文件交接；现有成功批次禁止重跑。
- 已完成（2026-08-25）：MES 生成的 CSV 经真机导入验证被 ShineLab 接受。禁止重跑的批次现为
  `batch-20260824-001`、`batch-20260825-onsite-03`（后者已真实追加 3 条任务）。
- 实现入口：[ShineLab CSV 导入 RPA POC](ION-CHROMATOGRAPHY-RPA-POC.md)。

#### 2026-08-25 - 中控导入接口打通（离线验证）

中控 WPF「导入CSV」到 ShineLab 的整条链路已实现并通过离线验证：

- 中控新增独立的「离子色谱任务导入」页签，不挤占原有仪器状态栅格。操作人员选择
  `.csv`/`.xlsx` 样品任务文件后，界面显示解析结果与问题清单；只有零问题且至少一条
  任务时才允许下发批次。
- MES 侧 `ShineLabBatchHandoff` 生成规范 CSV（13 列、表头行尾多一个逗号、UTF-8 无 BOM、
  CRLF、序号从 1 连续编号），先写 `.tmp` 再原子改名，随后写出 `.manifest.ready`，
  并按 `BatchId + CSV SHA256 + 目标序列` 轮询控制电脑回执。
- 控制电脑侧 `scripts/Start-ShineLabBatchAgent.ps1`（当时为 `batch-agent-2026-08-25.1`）串行消费
  交接目录，校验清单与 CSV 哈希/行数/表头，命中账本则跳过重复导入，再调用已冻结的
  `Invoke-ShineLabCsvImport.ps1`（`2026-08-24.13`，未修改）。
- 安全边界不变且已由测试固定：代理不含任何点击“运行”的路径，回执 `runTriggered` 恒为
  `false`；清单出现 `allowRun=true` 直接判 `Failed`；`allowAppend` 未确认时 MES 侧即拒绝，
  不让批次流到现场；导出比对为 `Match` 才判 `Verified`，无法判断一律 `Unknown` 且不自动重试。
#### 2026-08-25 - 首次真机导入成功（batch-20260825-onsite-03）

- 真机导入已跑通。MES 下发 `batch-20260825-onsite-03`（3 条样品任务，CSV SHA256
  `F651E8EA…0822B`，413 字节无 BOM），控制电脑管理员 PowerShell 单次执行代理并带
  `-ExecuteImport`，ShineLab 空测试序列新增 3 条 水样-101/102/103。
  **至此 ShineLab 接受 MES 生成的 CSV（13 列表头、行尾逗号、UTF-8 无 BOM）已被现场证实。**
- 前两个批次 `onsite-01`/`onsite-02` 被演练命令占掉账本后作废：代理在发出导入前先占
  账本（导入不可回滚、崩溃不许重放），因此不带 `-ExecuteImport` 的跑法同样消耗批次号。
  现场文档已整节删除演练步骤，只保留唯一一条实跑命令。
- 本次暴露并修复一个代理包装层缺陷（见下）：导入本身成功，但回执被判成 `Unknown`，
  只读导出比对整段未执行。

#### 2026-08-25 - 修复代理读取 `$LASTEXITCODE` 崩溃（`batch-agent-2026-08-25.2`）

- 现象：真机导入实际成功（耗时 69 秒，任务已写入 ShineLab），但回执 `status=Unknown`，
  且 `evidence` 目录下没有该批次的 `import.log`。
- 根因：代理开头 `Set-StrictMode -Version Latest`；冻结内核 `2026-08-24.13` 只在**校验模式**
  走 `exit 0`，`-ExecuteImport` 路径跑到文件末尾自然结束且不调用外部 exe，故全新会话中
  `$LASTEXITCODE` 从未被赋值，严格模式下直读即抛「未设置该变量」。写 `import.log` 的语句
  排在其后，于是日志也没落盘。异常被 catch 判为 `Unknown`（设计如此，导入可能已部分生效，
  不敢判 `Failed`），导出比对因此整段跳过。
- 两处修复：写 `import.log` 提到读退出码**之前**（导入不可回滚，任何异常都不能让现场丢掉
  唯一的动作证据）；新增 `Get-LastExitCode`，用 `Get-Variable -ErrorAction SilentlyContinue`
  读取，未赋值按 0 处理。已在全新严格模式会话复现验证：未赋值时直读抛异常而 helper 返回 0，
  外部进程真返回 3 时 helper 仍如实返回 3，不吞失败信号。
- 该缺陷只在实跑时触发：演练走 `exit 0`，反而把 `$LASTEXITCODE` 设上了，把问题完美掩盖。

- 仍未完成：只读导出比对在真机上从未成功执行过，因此 `Verified` 判定链路尚未端到端验证；
  「导出CSV」保存对话框的标题与编码行为仍待现场确认。导出为只读操作，不会追加任务，
  可在同一序列上单独补跑。
- 样品等级在 ShineLab 界面未显示：MES 侧已排除。下发的 CSV 与本地留档逐字节相同
  （同哈希），第 5 列即 `样品等级`、第 3 行值为 `"07"`（带引号保留前导零文本语义）。
  操作人员反馈手工导入同样不显示等级，故现有证据无法区分「ShineLab 未存该字段」、
  「存了但不在当前视图列」与「前导零被吞」三种情况；需靠 ShineLab 自身导出的 CSV 判定。

ShineLab 工作已于 2026-08-26 暂停（见下方「未完成」条目）。恢复顺序：核对健康文件中
`shineLabProcess`/`shineLabWindowTitle` 的检测条件与实际运行的 ShineLab 是否一致 → 推送修复后
的代理并确认计划任务进入 `Running` → 确认 `Register-ShineLabAgentTask.ps1` 带 UTF-8 BOM →
补跑只读导出比对。batch-11 的列保真度是继续与否的判据。

#### 2026-08-27 - P1 离线确定性收口（未操作真实 ShineLab）

- 复核 `batch-20260826-onsite-07` 证据：只读导出已在真机成功生成 10 行 CSV；旧代理用
  `Full` 把 7 行历史数据与本批次 3 行错位比较，正式账本因此为 `Failed`。事后
  `AppendTail` 人工复核能命中末 3 行，但它无法证明历史前缀未变化，也会把 5 个
  `样品等级/清除校正` 空值继承当作相等，不能作为生产 `Verified` 依据。
- 代理更新为 `batch-agent-2026-08-27.4`：MES 解析器、CSV writer 和控制电脑代理均要求
  `样品等级/处理方法/清除校正` 显式填写，在任何 ShineLab UI 操作前拒绝可能继承上一行的
  空值。旧证据需要查看继承时只能显式启用 forensic 口径，生产默认严格。
- 新增 `AppendDelta` 生产门禁：实际导入前先只读导出基线；基线成功前不占幂等账本、
  不提交导入。导入后必须同时满足“历史前缀逐字段不变、总行数恰好增加 N、末 N 行与
  MES CSV 一致”，才允许 `Verified`。只看末尾 N 行的 `AppendTail` 不再用于代理判定。
- 新增两种非消耗单次检查：`-ValidateOnly -RunOnce` 只校验清单/CSV；
  `-PreflightOnly -RunOnce` 在现场只打开并观察右键菜单后关闭。两者都不写幂等账本/回执，
  不移动 `.manifest.ready`，解决旧演练模式消耗批次号的问题。
- 离线门禁通过：PowerShell 比对与代理非消耗校验测试全部通过；该阶段全方案构建 0 警告/0 错误，
  .NET **772 passed / 5 existing E2E skipped / 0 failed**。随后新增 AGV I/O 和 AUBO 路由后，
  最新 Release 全套为 **789 passed / 5 skipped / 0 failed**。尚未把 `.4` 代理部署到控制电脑，
  尚未运行 `-PreflightOnly`，也尚未用新批次取得 `Verified`；这些均需要实际机器操作。

### P2 - 实验流程

Active branch: `docs/experiment-workflow-architecture-plan`

- Experiment workflow G2, G3, and G4 have passed overall acceptance.
- G5-A contracts, additive SQLite storage, and read-only scheduling projections
  have passed project acceptance.
- G5-B manual planning, G5-C runtime admission, and G5-D independent WPF
  planning/scheduling pages have passed project acceptance. G5 overall
  acceptance passed on 2026-08-24; the project authorized entry into G6.
- G6-A versioned advanced-flow contracts and deterministic static publication
  gates have passed project acceptance. G6-B durable conditions, external
  signals, and manual confirmations are implemented; all automated gates pass.
- Experiment workflow work is paused at the G6-B project-acceptance gate while
  the P0 robot-arm handshake work is active. G6-C has not started.

The AGV MVP remains in frozen maintenance mode. Production, unattended,
automatic/batch dispatch, and Push are **NO-GO**. G6-B changes MES workflow state
only: it does not send pause/cancel commands to devices, retry Unknown operations,
add serial access, expose protocol/register fields, or enable any CIC-D160+ write
path.

## Latest verification

Re-verified on 2026-08-27 after the AUBO robot-arm layers and the offline
ShineLab deterministic-import hardening:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **779 passed / 5 existing E2E skipped / 0 failed**, of which
  **29** are AuboArm tests (18 read-only surface plus 11 controlled-write and
  quarantine assertions).
- Current breakdown: Domain 39, Workflow Contract 70, MES 122, WPF 264,
  Adapter 210, Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- The two new .NET tests reject ambiguous ShineLab inherited fields at parse/write
  time. Separate PowerShell suites cover deterministic append deltas and
  non-consuming agent validation; both pass offline.
- Quarantine confirmed by search: `AuboArmControlledHandshakeSession` is
  referenced only by its own tests — no DI registration and no HTTP route.
- Nothing was sent to the arm or to the AGV at `192.168.200.151` during this
  work.

The 2026-08-25 baseline, retained for comparison:

- Full solution build: **0 warnings / 0 errors**.
- Full test suite: **738 passed / 5 existing E2E skipped / 0 failed**.
- Breakdown: Domain 39, Workflow Contract 70, MES 120, WPF 259, Adapter 176,
  Instrument Gateway 50, Simulator 5, and E2E 19 passed plus 5 skipped.
- The WPF count rose from 226 to 259: 33 new tests covering the sequence parser,
  the canonical CSV writer, the import view model, and the batch handoff
  (including replay/idempotency, tampered-hash rejection, and `allowRun`
  rejection).

On-site update (2026-08-25, later the same day): the first real ShineLab import
succeeded on `batch-20260825-onsite-03` — 3 tasks appended, confirmed manually.
The agent's receipt still read `Unknown` because of a `$LASTEXITCODE` strict-mode
defect in the agent wrapper, now fixed in `batch-agent-2026-08-25.2`. The
read-only export comparison has not yet completed on the real machine, so the
`Verified` path remains unverified end to end. Note the agent is a PowerShell
script outside the .NET solution, so the build/test counts above are unaffected
by that fix.

The earlier G6-B Release gate on 2026-08-24 recorded 705 passed / 5 skipped.
- G6-B focused coverage: **22/22 passed**. It covers typed decisions, durable
  interaction replay, timeout, pause/cancel, Unknown continuation, restart
  recovery, HTTP mappings, additive schema upgrade, and absence of advanced-node
  device operations.

## Recent changes

### 2026-08-27 - ShineLab deterministic append hardening (offline)

- Replaced production `AppendTail` verification with a before/after `AppendDelta`
  gate that preserves the historical prefix and requires exactly N appended rows.
- Made the three observed inheritable fields explicit and fail-closed before UI
  automation. Added non-consuming `ValidateOnly` and `PreflightOnly` modes.
- No ShineLab process, control computer, serial port, instrument, robot arm, or AGV
  was accessed while implementing and verifying this change.

### 2026-08-27 - Priority adjustment

- 机械臂变量握手与「AGV 到站 → 取放」整套流程设为 **P0 最高优先级**。
- 离子色谱 ShineLab 导入降为 P1 并暂停；实验流程降为 P2，G6-B 项目验收继续推迟，
  G6-C 在 G6-B 明确验收前保持关闭。

### 2026-08-27 - AUBO 机械臂只读层与隔离写入层

- 新增 `Modules/AuboArm/`：选项绑定（fail-closed）、JSON-RPC 传输（读写分离接口）、
  只读驱动、适配模块、环回控制器（兼 Lua 工程替身）、隔离的受控握手会话。
- 模块经 `AdapterCompositionRoot.CreateDefaultModuleCatalog()` 接入运行时，
  仅注册四个 GET；派发 POST 以 `403 OperationDisabled` 显式拒绝，而非留成裸 404，
  使操作人员得到有据可查的原因。
- 安全边界由测试固定：只读驱动不实现写接口；`ControlEnabled=true` 在配置校验层直接抛错；
  受控写入会话无 DI、无路由。

### 2026-08-24 - Priority adjustment

- Set ion-chromatography ShineLab CSV task import to P0, including RPA simulation,
  duplicate-import protection, and post-import verification. Automatic “Run” stays closed.
- Moved experiment workflow to P1 and deferred G6-B project acceptance. G6-C
  remains closed until G6-B is explicitly accepted after the P0 work.

### 2026-08-25 - Central-control ShineLab import interface

- Added a dedicated central-control import page, the canonical CSV writer, and the
  file-based MES→control-PC batch handoff with SHA-256 batch identity, atomic
  write, and receipt polling.
- Added the control-PC batch agent that serially consumes the handoff directory and
  drives the frozen `2026-08-24.13` import script. The agent has no “Run” path.
- The interface was initially verified offline at 738 passed / 5 skipped and
  build 0 warnings. A later supervised import succeeded on
  `batch-20260825-onsite-03`; deterministic `Verified` remains the open on-site gate.

### 2026-08-24 - G6-B durable interaction runtime

- Enabled server-side typed conditions, durable early external signals, and
  audited manual outcomes with explicit success/timeout/cancelled paths.
- Added additive interaction storage, restart-safe matching, serialized controls,
  output evidence retention, and isolated background recovery. Parallel, subflow,
  compensation, automatic scheduling, device control, serial, and D160 writes stay closed.
- Implementation commit: `e749489`; project acceptance is pending.

## Historical trace

| Date | Retained trace |
| --- | --- |
| 2026-08-24 | G6-A advanced-flow contracts: graph schema v3 with v2 still publishable, typed condition/signal/parallel/subflow/compensation contracts, deterministic publication rules, all advanced nodes runtime-disabled. Implementation `3f184d7`; accepted 2026-08-24. See [G6 acceptance](EXPERIMENT-WORKFLOW-G6-ACCEPTANCE.md). |
| 2026-08-24 | G5-D planning and scheduling UI: separate plan/scheduling pages, manual job creation, schedule/unschedule/cancel and explicit admission. No scheduler optimization, device command, serial, or D160 write. Implementation `27d63c8`, header alignment `858d3bd`. See [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md). |
| 2026-08-22 to 2026-08-24 | G5-C transactional runtime admission, active lease mutex and restart reconciliation passed; implementation `75655cd`. See [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md). |
| 2026-08-21 | G5-B manual planning, scheduling, idempotent commands and additive upgrade passed; implementation `2e5ef8f`. See [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md). |
| 2026-08-21 | G4-D audited pause/resume, cancellation and Unknown resolution passed; implementation `379fd59`. See [G4 acceptance](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md). |
| 2026-08-21 | G5-A planning foundation passed: immutable plan/workflow references, additive storage, and read-only projections. Implementation `b690289`; see [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md). |
| 2026-08-21 | G4 overall acceptance: durable runtime evidence, Simulator-only node execution, monitoring, and audited controls. See [G4 acceptance](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md). |
| 2026-08-21 | G3 overall acceptance: typed catalog, strict publication gate, schema-driven inspector, and issue navigation. See [G3 acceptance](EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md). |
| 2026-08-20 | G2 overall acceptance: one canonical v2 graph editor, Nodify canvas, compatibility importer, and lossless MES lifecycle. See [G2 acceptance](EXPERIMENT-WORKFLOW-G2-ACCEPTANCE.md). |
| 2026-08-19 | G1 canvas evaluation and graph convergence baseline. See [G1 acceptance](EXPERIMENT-WORKFLOW-G1-ACCEPTANCE.md). |
| 2026-08-17 to 2026-08-18 | Durable recovery/audit, Simulator-only Wait/Move, read-only Instrument Gateway, and D160 protocol evidence. |
| 2026-08-04 to 2026-08-13 | MES/Adapter/Simulator MVP, WPF operations/map work, physical AGV supervised acceptance, idempotent transport, and read-only preflight. Production remained NO-GO. |

## Retained safety baselines

- CIC-D160+ production integration remains read-only and allowlisted. Pump,
  temperature, flow, method, injection, and analysis controls remain disabled.
- Every physical AGV connection or movement requires fresh authorization and a
  separate read-only preflight. Production and unattended operation remain
  **NO-GO**.
- Robot arm and vision scaffolding has no G5 workflow execution authorization.
- AUBO arm integration is read-only. Named-variable writes, `setWatchDog`, and
  command dispatch stay quarantined (no DI, no route) until the four variable
  contract items are confirmed on site, the read-only verification passes against
  the real controller, and 空载 authorization is granted. 原始坐标 stay inside the
  teach pendant and the Lua project; MES/WPF never sees them.

## Next gate

1. **P0 — 先联通机械臂（只读）**：在批准的控制电脑上确认网络链路、JSON-RPC 端点/鉴权、设备身份、
   运行与安全模式、Lua 工程和白名单变量；只读 `readiness` 不通过或证据缺失时停止，不发送变量写入、
   控制权申请、程序启动或运动命令。四项变量契约（命令键、整数→动作映射、`ack`/`result`、完成回复方式）
   必须由现场/厂家确认，不能从代码或历史抓包推断。
2. **P0-后续 — 空载握手与 AGV 到站取放**：仅在机械臂只读证据通过、四项契约明确并取得独立空载授权后，
   才能评估一次受控握手；AGV 与机械臂动作授权相互独立，任何异常进入人工处置，不自动重试。
3. **P1 — requires actual ShineLab control-computer operation.** Deploy
   `batch-agent-2026-08-27.4`, prepare a brand-new small batch whose
   `样品等级/处理方法/清除校正` are all explicit, then in administrator PowerShell run
   `-RunOnce -ValidateOnly` followed by `-RunOnce -PreflightOnly`. Both checks must
   leave the ready manifest and ledger untouched. After reviewing their evidence,
   run that new batch once with `-RunOnce -ExecuteImport`; the agent must capture a
   before-import snapshot and return `Verified`, `runTriggered=false`,
   `baselineUnchanged=true`, and `appendedRowCount=expectedRows`. Do not rerun any
   previous onsite batch. The RPA must not click “Run”.
4. After the active P0/P1 gates, resume project acceptance for G6-B using the
   reproducible service/API steps in the G6 acceptance record. Until explicit
   confirmation, do not begin G6-C parallel, subflow, or compensation runtime.
5. Keep automatic scheduling, physical device commands, serial control,
   protocol/register fields, and D160 writes closed unless separately authorized
   by a later device-specific safety gate.

## Planning and evidence

- [G6 acceptance](EXPERIMENT-WORKFLOW-G6-ACCEPTANCE.md)
- [G5 acceptance](EXPERIMENT-WORKFLOW-G5-ACCEPTANCE.md)
- [G4 acceptance](EXPERIMENT-WORKFLOW-G4-ACCEPTANCE.md)
- [G3 acceptance](EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md)
- [Experiment workflow architecture](EXPERIMENT-WORKFLOW-ARCHITECTURE.md)
- [Experiment workflow UI design](EXPERIMENT-WORKFLOW-UI-DESIGN.md)
- [Experiment workflow implementation plan](EXPERIMENT-WORKFLOW-IMPLEMENTATION-PLAN.md)
- [Physical acceptance index](physical-acceptance/README.md)
- [Ion chromatography task-import RPA POC](ION-CHROMATOGRAPHY-RPA-POC.md)
- [Ion chromatography RPA handoff](ION-CHROMATOGRAPHY-RPA-HANDOFF-2026-08-24.md)
- [Ion chromatography RPA import evidence](../artifacts/ion-chromatography/shinelab-rpa-import-evidence-20260824.md)
- [Ion chromatography protocol verification](ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md)

## 2026-09-01 AGV/AUBO/WPF 调试里程碑

- `中控运营中心` 已完成 LM 地图配置：LM1=充电原点、LM7=站点1，其余按 LMn；起点站/终点站读取 MES 目录。
- AUBO 到站流程节点、只读程序目录（预加载槽位/允许列表）和流程编辑器下拉选择已实现；生产代码不写死任何“测试1/测试2”程序名。
- 本地 FieldSimulation 与真实链路边界已验证：解决方案构建通过，899 个测试通过、5 个旧 E2E 场景跳过；现场 AUBO 仍因 Manual/无预加载槽位保持 NO-GO。
- 13:35 现场网线已恢复并完成新只读预检：AUBO 为 Automatic/Normal/Stopped，但 0–99 预加载槽位为空；真实单步测试等待现场提供并批准实际程序名。
- 14:12 已接入 Dashboard Server 只读 `get loaded program`：现场返回当前加载工程 `测试2`；Adapter/MES 目录接口可显示该名称，但第二个程序名和控制授权仍待现场确认。

## Maintenance rule

Keep only the current state and the latest three to five milestones detailed.
Reduce older work to one trace row and link to its acceptance record, evidence,
and commits instead of appending session transcripts.

## 2026-09-01 离线 AUBO 模拟流程闭环（本阶段）

- 在 Adapter AUBO 模块增加显式 `Devices:AuboArm:Driver=simulator` 分支，复用受控
  loopback 协议实现只在进程内运行的状态、程序目录、load/run/stop；该分支不创建
  WebSocket/Dashboard 连接，生产默认仍为已确认的 WebSocket 驱动。
- FieldSimulation 配置启用本地 AUBO 模拟器（`ARM-01`、`测试1/测试2` 允许列表），
  启用 `WorkflowAuboWorker` 和机器人 Profile 控制能力；模拟运行约 1 秒自动回到
  `Stopped`，可完整演练 robot node 的 load → run → terminal observation。
- 启动诊断识别 simulator 驱动为“本地模拟控制”，不要求 Host，也不把本地控制误报为
  现场写入授权；非法驱动仍 fail-closed。
- 定向测试新增 Adapter 组合根和启动诊断覆盖；本次全解决方案回归：915 通过、5 跳过、
  0 失败（跳过项仍为登记的完整模拟器 E2E）。

边界：该闭环只证明本地模拟流程和持久化工作流 worker 的软件行为，不能替代现场
AUBO/AGV 只读预检、授权或真实程序节点验收；联网恢复仍须先执行新的只读预检，现场
配置不得切换为 simulator。

## 2026-09-01 物理 Move 验收与 AUBO 工作流关联（离线实现）

- 导航验收单现可绑定 workflow run/node/device operation；MES 对 run、Ready Move、目标站和
  唯一性执行 fail-closed 校验，并兼容升级旧 SQLite 表。
- 新增默认关闭的物理 Move worker：只消费已由人员创建并授权的有效验收单，不自行创建许可，
  不在 Simulator Profile 运行，Unknown 不自动重试。
- 已关联 workflow 的验收单禁止从通用 dispatch API 绕过节点认领；worker 必须提交匹配的
  node/device-operation 关联，未关联验收单的既有现场流程保持兼容。
- WPF 运行监控增加现场许可字段和“创建并授权”入口；该入口不直接派发 AGV。Move 到达证据
  写入同一 workflow node 后才推进到 AUBO 节点。
- 离线持久化集成测试覆盖 Move → arrived → AUBO → workflow completed；解决方案构建
  0 警告/0 错误，全量测试 915 通过、5 跳过、0 失败。

现场仍未验收该自动链路；两个 worker 和物理 feature 均保持关闭。恢复现场时必须先取得新的
AGV/AUBO 只读预检和明确授权，再决定是否逐项开启。

## 2026-09-01 O24 诊断快照恢复预览（离线完成）

- 回收目录支持只读扫描：校验 quarantine schema、manifest 字段、路径安全、源文件存在性和
  文件大小；不一致批次不会提供恢复候选。
- 恢复需要单独勾选和完整确认短语；恢复时再次校验 manifest/文件，禁止覆盖已有目标，完成后
  更新 manifest 并重新扫描。无后台恢复、永久删除、上传或自动创建目录。
- 启动诊断新增“回收目录恢复”页签；恢复结果和失败原因只进入脱敏内存审计。
- O24 全量回归：构建 0 警告/0 错误，全解决方案 920 通过、5 跳过、0 失败。

O24 仍是离线软件能力；任何现场快照都必须人工复核，不能替代联网后的新鲜设备只读预检。

## 2026-09-02 本地模拟流程显式执行（现场暂缓）

- 流程管理新增“执行模拟”入口，仅在 WPF `simulator` 模式且工作流版本已发布时可用；请求
  使用 `DryRun=false` 进入隔离 Simulator/Adapter/MES worker，便于验证完整节点顺序。
- `physical` 模式和默认编辑器不暴露该命令；执行结果仍需在流程运行监控中观察，不把模拟结果
  当作现场设备完成。
- 定向测试覆盖 simulator 开关、发布版本门禁、请求载荷和主窗口绑定；现场设备保持充电，
  未启动 PhysicalAcceptance 或调用设备接口。
- 全量回归：922 通过、5 跳过、0 失败（WPF 355、MES 138、Adapter 221）。

## 2026-09-02 会话交接快照（现场暂缓）

- 当前现场安全状态保持不变：AGV 最终位于 LM1、无活动任务、控制权为 `none`；AUBO
  当前加载“测试1”、运行态 `Stopped`；物理 MES/Adapter 与临时 Simulator/WPF 均已停止。
- 本轮未恢复有线网络，未启动 PhysicalAcceptance，未访问或写入 AGV/AUBO；保护端口
  `5141/5145/5041/5045/5183` 均无监听。
- 已完成 WPF 流程编辑器“执行模拟”入口：只在 `simulator` 模式且工作流已发布时启用，
  以 `DryRun=false` 进入隔离本地执行链；默认/physical 模式保持禁用，并保留运行快照与审计回读。
- 已补充 simulator 门禁、请求载荷、操作员/correlation、审计回读和主窗口绑定测试；全量
  回归为构建 0 警告/0 错误，922 通过、5 跳过、0 失败。
- 详细证据与下一会话入口见 `artifacts/aubo-agv-wpf-handoff-20260902-next.md` 和
  `artifacts/offline-workflow-simulator-execution-20260902.md`。

下一会话默认继续离线工作：先验证本地 Simulator/WPF 实际点击执行链，再优化流程运行监控、
节点状态刷新和失败恢复提示；除非重新完成现场只读预检并取得明确授权，否则不得恢复设备写操作。

## 2026-09-02 流程运行监控优化（离线完成）

- 运行监控页新增可见生命周期内的只读自动刷新：页签卸载/窗口关闭会取消轮询，运行进入终态后
  自动停止；不调用 AGV/AUBO 写接口，也不自动重发请求。
- 新增节点处理进度、当前节点、失败证据汇总和取消后的安全语义提示；设备操作表补充结果/错误
  合并显示，避免失败只留在详情字段中。
- 主窗口释放时显式释放运行监控 ViewModel；绑定测试覆盖进度条、自动刷新开关、失败/取消面板。
- simulator 执行被 MES 受理后，主窗口自动把 `ExecutionId` 投影到运行监控；试运行和 physical
  模式不会触发该联动。
- 离线证据见 `artifacts/offline-workflow-monitor-20260902.md`；WPF 全量回归 **358 通过、0 失败**，
  解决方案回归 **936 通过、5 跳过、0 失败**，WPF 项目构建 0 警告/0 错误。

现场设备继续保持充电，未恢复网络、未启动 PhysicalAcceptance；恢复现场时仍须先取得新的
AGV/AUBO 只读预检和明确授权。

## 2026-09-02 隔离本地流程运行链回归

- 使用新增 `scripts/verify-offline-workflow-simulator-run.ps1` 在 loopback 临时端口和临时
  SQLite 数据库中实际执行一次 `DryRun=false` 的已发布 Move 工作流，观察到
  `Prepared → Running → Completed`，并读取到节点/设备操作成功终态。
- 相同 `RequestId` 再次提交返回同一运行 ID 且 `replayIsIdempotent=true`；运行快照、节点、设备
  操作和时间线均来自隔离 MES 数据库。
- 最近一次证据 run 为 `ab1802b7-e0de-468e-953c-778b6bb6a4ec`，包含 5 类时间线事件；机器可读
  结果保存于 `artifacts/offline-workflow-simulator-run-20260902.json`。
- 为使本地 Move worker 能推进，本次脚本进程级覆盖 `enableAutomaticDispatch=true`；签入的
  FieldSimulation 配置和现场配置均未改变，默认仍为 fail-closed。
- 证据见 `artifacts/offline-workflow-simulator-run-20260902.md` 与同名 JSON；同时确认签入的
  FieldSimulation 默认关闭自动派发时 worker 会 fail-closed 返回 409，只有隔离脚本进程级
  override 才允许模拟 Move。回归结束后
  `6241/6245/6283` 均无监听，未启动 PhysicalAcceptance、未访问 AGV/AUBO。
- WPF 全量测试 **358 通过、0 失败**；解决方案全量测试 **936 通过、5 跳过、0 失败**。

## 2026-09-02 WPF 在线状态来源明确化

- AGV 与 AUBO 不再单独显示模糊的“在线”：新增“本地模拟器在线 / 物理设备在线 /
  物理设备未验证 / 来源未验证”投影。
- AGV 标题徽标、车队连接列、调度面板、汇总状态、MES 连接状态，以及 AUBO 标题徽标和连接
  卡片均显示同一运行来源。
- simulator 页面明确标注“非现场设备”，说明 localhost 在线不代表实体设备可达；physical
  页面继续提示必须以新鲜现场只读预检和授权为准。
- 证据见 `artifacts/runtime-connection-source-labels-20260902.md`；WPF 全量测试
  **360 通过、0 失败**，解决方案全量测试 **938 通过、5 跳过、0 失败**，构建 0 警告、0 错误。

本改动不改变自动派发、控制权、AUBO 控制或 PhysicalAcceptance 门禁。

## 2026-09-02 料盘四段标准流程模板

- WPF 流程管理新增“料盘四段标准模板”，顺序固定为：原点前置条件 → 站点1 执行
  `取料盘.pro` → 站点2 执行 `放料盘.pro` → 返回站点1 执行 `回收料盘.pro` → 返回原点。
- 模板保留显式 AGV Move 与 AUBO Robot Program 节点，程序名预填为现场提供的三个 `.pro`
  名称；运行前仍须以现场只读目录和允许列表为准。
- 现场已确认模板站点映射为原点 `LM1`、站点1 `LM7`、站点2 `LM2`，三个 `.pro` 程序名与模板
  预填一致；创建消息仍会显示实际 ID。
- 本次仅完成模板和离线回归，未启动物理 worker、未发送 AGV/AUBO 写命令；现场网络恢复后仍须
  重新只读预检、逐段授权并由操作员启动实际运行。

证据：[料盘四段标准流程模板](../artifacts/standard-material-handling-workflow-template-20260902.md)。

## 2026-09-02 现场恢复后只读预检

- 网线恢复后，AGV `192.168.1.2:19204/19206`、AUBO `192.168.1.102:9012/29999` TCP 均可达。
- 同步 Release 现场配置后，AGV 新鲜只读预检确认 `AGV-01 @ LM1`、无活动任务、控制权 `none`；该次
  预检门槛为 `0.95`，定位置信度 `0.7541`，因此 `dispatchPermitted=false`。随后按现场确认将
  PhysicalAcceptance/FieldStandard/MES 配置门槛调整为 `0.90`；`0.7541` 仍未达标。
- AUBO 新鲜只读预检确认 `Running / Normal / Automatic / Stopped`，当前 Dashboard 加载工程为
  `回收料盘`、预加载槽位为空，与流程首个 `取料盘.pro` 节点不一致。
- 只读会话已停止；未申请控制权，未发送 AGV 派发、AUBO 加载/启动或运动命令。现场仍保持 NO-GO，
  详见 [现场运行前检查单](../artifacts/field-standard-workflow-runbook-20260902.md)。
- 按现场确认将软件门槛同步为 `0.90` 并完成 Release 构建；新配置只读复核的实际定位置信度为
  `0.8957`，仍略低于门槛，`dispatchPermitted=false`，需现场继续调整后再复检。
- 门槛调整记录见 [现场 AGV 定位置信度门槛调整记录](../artifacts/field-confidence-threshold-change-20260902.md)；
  本次未打开任何设备控制门禁。
- 现场决定暂不实现低置信度自动重定位；继续采用低于 `0.90` 时只读预检阻断并人工处理，详见
  [AGV 自动重定位延期记录](../artifacts/agv-auto-relocalization-deferred-20260902.md)。
- 标准流程启动前最新只读复核显示 AGV `LM1 / confidence=0.9625 / reloc_status=1` 已通过定位门槛；
  AUBO 控制器当前加载 `回收料盘`、预加载为空。现场已确认三个程序名，标准运行配置已登记三项
  允许列表，但控制器实际加载结果仍需在受控运行时验证，现场继续 NO-GO。
  综合证据：[AGV/AUBO 只读复核](../artifacts/physical-acceptance/agv-aubo-readonly-preflight-20260902-standard-flow.json)。
- 允许列表登记后的新鲜复核返回三项合并可用程序，AGV 置信度 `0.9675`；AUBO 当前加载仍为
  `回收料盘`，这是自动模式下的当前工程显示，不代表首节点不能请求 `取料盘`。实际 load/run 仍以
  控制器回执为准，见 [目录确认复核](../artifacts/physical-acceptance/agv-aubo-readonly-preflight-20260902-catalog-confirmed.json)。
- 启动前最新只读复核仍为 `AGV @ LM1 / confidence=0.9675`，三项 AUBO 允许列表均可见；标准写入会话
  尚未启动，等待本次运行的操作员、安全监护人和唯一许可编号。

## 2026-09-02 现场标准运行收尾与下一阶段

- 已完成一次完整的已发布料盘标准工作流运行：`9f0a99c8-7e53-4285-91b5-3abf29cf82b5`，
  `Completed`，7/7 节点完成；AGV 回到 `LM1`，控制权释放，机械臂停止且无故障。
- 现场运行收尾证据：[material-wpf-20260902-final-run.md](../artifacts/physical-acceptance/material-wpf-20260902-final-run.md)。
- 已修复目录扫描超时/进行中提示、程序列表滚动布局、运行画布最小高度与首次适配、
  PhysicalAcceptance `admin` 权限和 ARM-01 工作流控制门禁。Adapter/MES/WPF 相关测试均通过。
- WPF 已关闭；现场 MES/Adapter 保持安全空闲，不自动启动新流程。
- 关闭 WPF 后完成隔离 Simulator 下一阶段回归：`Prepared→Running→Completed`，同 RequestId
  幂等回放成功（最新 run `ad941a2f-1662-4d1e-82b8-370e229dd273`）。证据：[offline-workflow-simulator-run-20260902-next.md](../artifacts/offline-workflow-simulator-run-20260902-next.md)。
- 后续继续开发仍使用独立 Simulator 数据/端口；恢复现场操作前必须重新只读预检并取得新的授权。

## 2026-09-02 现场标准模板一键执行（离线能力）

- 为满足现场不逐段点击的目标，WPF 新增受显式配置保护的“一键现场执行”命令，仅针对已发布
  料盘标准模板；一次提交操作者、监护人、许可前缀和有效期。
- `WorkflowExecutionRequest` 增加 `PhysicalAuthorization`；MES Move worker 可选地在 Ready
  节点读取当前 AGV 快照后自动生成唯一节点验收许可，再继续原有物理预检和派发流程。
- 默认配置仍关闭批量 worker、自动许可和自动派发；没有新鲜只读预检与独立授权时按钮不出现
  或请求被拒绝。Unknown、阻挡、过期和连接异常不自动重试。
- 设计及现场启用门禁见 [一键执行设计](../artifacts/physical-batch-workflow-one-click-20260902.md)。
- 完整解决方案 Release 回归：937 通过、5 个既有 E2E 场景跳过、0 失败；现场服务未重启。

## 2026-09-02 运行监控全屏查看

- 监控页新增“全屏查看”按钮，打开独立最大化窗口复用同一只读运行数据，完整显示流程图、当前节点、
  节点执行、设备操作和时间线；关闭后返回原页面，镜像窗口不改变自动刷新或设备状态。
- 新版 WPF 已同步到 Release 启动目录并以 physical 只读模式启动，未启用批量物理执行。
- 全屏按钮增加单实例保护：打开后立即禁用，只有独立窗口 `Closed` 后恢复；重复点击只激活已有窗口。

## 2026-09-03 断网期间离线优化与自动重定位评估

- 已记录 Roboshop“自动重定位”按钮对应接口的待确认事项；当前仅确认 `1021` 为只读定位状态查询，未确认重定位写命令，网络恢复前不向 AGV 发送任何相关请求。详见 [自动重定位接口评估](../artifacts/agv-auto-relocalization-assessment-20260903.md)。
- 流程管理顶部命令栏改为自适应换行，窗口较窄时不再裁剪“发布、试运行、执行模拟”等末尾按钮。
- AUBO 工作流服务重启恢复时，若控制器已停止但无法证明本次程序完成，改记为 `Unknown` 并要求人工核销，避免把中断或手动停止误判为成功。
- 物理 Move 预检增加“无阻断原因但明确未通过”保护，防止异常驱动以空原因绕过安全门禁。
- MES/WPF/Adapter 继续保持离线开发和物理写入关闭；网络恢复后先重新执行只读预检，再确认是否启用现场 worker。

## 2026-09-03 料盘标准流程一键执行韧性加固

- MES 增加物理批次启动门槛、标准模板精确校验、同 AGV 单活动批次约束和授权幂等校验；未启用现场导航、自动许可或 AUBO worker 时立即拒绝。现场验收流程继续保持通用自动派发关闭，避免绕过验收单。
- AGV 对下发前瞬态条件做限时只读复核，兼容临时 `paused`/状态读取失败且不重复派发；最终返回 LM1 后在流程完成前单次释放控制权。
- AUBO 增加启动前就绪稳定等待、启动后只读状态恢复和安全终态稳定判定；写入结果不明继续进入 `Unknown`，不重发 load/run。
- WPF 增加 AGV/AUBO 当前节点告警、一次性弹窗、许可过期提示和窄屏命令区换行；一键入口只提交本次授权，不负责打开服务写权限。
- 回归：WPF 364、MES 147、Adapter 221、工作流契约 71，均为 0 失败；整个解决方案 946 通过、5 个既有 E2E 跳过、0 失败。随后新增 Adapter 3066/1110 超时结构化 Unknown 修复回归为 222 通过。隔离部署见 `bin/Verify/PhysicalOneClickResilienceDeploy/`，详细记录见 [离线加固记录](../artifacts/physical-one-click-resilience-20260903.md)。

## 2026-09-03 断网/回执异常边界修复

- Adapter 增加统一 transport 异常边界：AGV 状态、预检和派发遇到超时、控制器错误、连接断开时分别返回可识别的 504/502/503，不再把设备不可达冒成 HTTP 500。
- 3066 导航请求进入写入边界后，若 1110 只读回执仍不可确认，持久化 `Unknown` 并禁止自动重派；MES/WPF 仅显示等待或人工核销提示。
- WPF 对预检超时与 Adapter/AGV 不可达显示独立网络告警，保持当前节点暂停并等待只读恢复。
- Adapter 222、MES 147 回归通过；最新部署见 `bin/Verify/PhysicalOneClickResilienceDeployFinal/`。当前现场因以太网链路断开，未启动新 MES、未创建新流程。

## 2026-09-04 现场一键全流程第一阶段收尾

- 现场运行 `a6a6e311-616a-466e-9dbf-bb208d057973` 完成 `LM1 -> LM7` 和 `取料盘`，在第二段导航中按操作员要求取消；最终为 `Cancelled`，未执行后续节点。
- 修复一键物理批次中途取消后的控制权收口：仅对带运行级授权的批次单次尝试释放，回执不明仍保持取消终态且禁止重试。
- 修复控制器离线时控制权释放返回通用 500：所有权读取失败不会发送 4006，并按 502/503/504 暴露真实边界。
- 修复 AUBO 100 槽位目录扫描必然在 5 秒超时的问题：现场预算调整为 15 秒、WPF 请求预算 30 秒，并增加槽位节流预算校验。
- Release 构建 0 警告/0 错误；离线门禁 963 通过、5 个已登记 E2E 跳过、0 失败。阶段一收尾见 [收尾记录](FIELD-ONE-CLICK-PHASE1-CLOSEOUT-2026-09-04.md)，新会话从 [第二阶段交接](FIELD-ONE-CLICK-PHASE2-HANDOFF-2026-09-04.md) 开始。
- 当前控制器和设备已断电、网线已拔，WPF/MES/Adapter 均停止；下一次不得复用旧运行、验收单或许可，必须重新只读预检和授权。

## 2026-09-04 现场一键全流程第二阶段完成

- 已在新授权下完成一次完整料盘标准流程：Workflow execution
  `937e061e-82c8-434e-ba38-8b9ac320a0c1`，Request
  `b572ebf6-a38e-4140-aad3-d1d24a71b529`，Workflow
  `65d9bd5e-73e8-49ae-9510-8541842637fb` / v1，最终 `Completed`。
- 操作员/安全监护人均为 `admin`，许可前缀为 `一键启动流程-0904`，有效期 60 分钟；现场操作员确认本次运行正常、无中途错误。
- 新鲜只读预检确认 AGV 在线、`LM1`、无活动任务、owner=`none`、定位置信度 `0.9376`、重定位 `1`、无急停/阻挡/故障；AUBO 为 `Running / Normal / Automatic / Stopped`，目录扫描完整并确认三项目标程序名。
- 运行记录包含 7 个可执行节点（4 个 Move + 3 个 AUBO Program），全部 `Succeeded` 且 attempt=1；Start/End 由工作流边界状态表示，语义节点共 9/9。4/4 AGV 验收单 `arrived`，3/3 AUBO 程序均有 load/run/Stopped 结果。
- 收尾状态：AGV 回到 `LM1`、无活动任务、控制权 `none`；AUBO Runtime=`Stopped`。最终 Move 前仅一次释放控制权，Adapter 返回 HTTP 200；WPF、MES、Adapter 已关闭，现场端口无监听。
- 完整机器记录见 [第二阶段现场证据](../artifacts/physical-acceptance/phase2-std-20260904-093754/phase2-field-acceptance-evidence-20260904.md)、[后台验收 JSON](../artifacts/physical-acceptance/phase2-std-20260904-093754/phase2-field-acceptance-summary.json) 和 [证据压缩包](../artifacts/physical-acceptance/phase2-field-acceptance-evidence-20260904-093754.zip)。
- 本次运行使用的隔离 Release 包快照为 `ab34c7f`；WPF 未自动绑定由外部一次性客户端提交的运行 ID。PowerShell 编码/参数、临时监控器和一条未关联 AUBO load 请求已集中列入 [流程后修复清单](../artifacts/physical-acceptance/phase2-std-20260904-093754/phase2-followup-fixes.md)，不得把这些问题静默当作已修复。

## 2026-09-04 第三阶段启动：AUBO 目录刷新优化

- 已建立 [第三阶段计划](FIELD-ONE-CLICK-PHASE3-PLAN-2026-09-04.md)。本阶段继续保持“新鲜只读预检 → 新授权 → 一次完整流程”的现场门禁，当前未连接或操作现场设备。
- AUBO 普通程序目录读取增加可配置短 TTL 缓存（默认 30 秒），同一设备的并发读取合并为一次底层扫描；完整、在线结果才可缓存，超时/部分/离线结果不缓存。
- `load`、`run`、`stop` 进入可能写入边界时立即失效缓存，并以 generation 防止旧扫描迟到后重新填充缓存。
- Adapter/MES/WPF 目录接口支持 `?fresh=true`；现场只读预检必须使用该参数，继续执行 0..99 完整扫描（100 ms 节流、15 秒预算不变）。返回 `IsCached`、`ObservedAtUtc` 和 `CacheExpiresAtUtc`，WPF 明确显示缓存或新鲜观测。
- 详细离线验证记录见 [AUBO 目录缓存验证](../artifacts/aubo-program-catalog-cache-verification-20260904.md)。
- 新增缓存、并发、fresh、失效和 HTTP 转发回归；完整 Release 构建 0 警告/0 错误。当前工作树快照的离线门禁报告 [mes-offline-release-gate-20260904-phase3-cache-final.json](../artifacts/mes-offline-release-gate-20260904-phase3-cache-final.json)：992 项测试，987 通过、5 个登记跳过、0 失败，`releaseEligible=true`。
- 新独立部署包：[PhysicalOneClickPhase3-cache-final-20260904-121500](../bin/Verify/PhysicalOneClickPhase3-cache-final-20260904-121500/)；压缩包：[PhysicalOneClickPhase3-cache-final-20260904-121500.zip](../bin/Verify/PhysicalOneClickPhase3-cache-final-20260904-121500.zip)。包内 manifest 记录源码 revision、门禁哈希、缓存参数和核心文件 SHA-256；压缩包 SHA-256 为 `7D7E5E2D805121AA4552C55CDA9D3FD5BCCC16312D1E47FFFB748F78B3E9E08A`。
- 第二阶段清单中的 UTF-8 工具、正式工作流导入、监控回放、WPF execution ID 绑定和 AUBO 未关联写入审计仍列为后续离线任务，尚未宣称完成。

## 2026-09-04 第三阶段 B 切片：现场工具可靠性

- 新增 `scripts/Invoke-MesJsonUtf8.ps1`：Windows PowerShell 5.1 使用 .NET `HttpClient`、显式 UTF-8 字节和一次性请求；检测并拒绝 `/Date(...)/` 日期格式，不自动重试。
- 新增 `scripts/Import-WorkflowGraph.ps1`：读取 UTF-8 `mes.workflow.graph`，严格校验料盘标准九节点/八边结构，重映射工作流/节点/边 ID，生成 MES `WorkflowDefinition`；默认只导出，只有显式 `-Publish` 才调用草稿、校验和发布接口。
- 新增 `scripts/Test-FieldWorkflowTooling.ps1`，使用第二阶段保存的中文图文档离线回放通过：9 个节点、8 条边、ID 唯一、中文保留，未发布且未产生设备写入。最终证据目录为 `artifacts/phase3b-tooling-test-final3-20260904-141500/`。
- UTF-8 工具、导入器和回放脚本静态门禁通过；完整离线门禁报告 [mes-offline-release-gate-20260904-phase3b-final2.json](../artifacts/mes-offline-release-gate-20260904-phase3b-final2.json)：996 项测试，991 通过、5 个登记跳过、0 失败，`releaseEligible=true`。
- 新独立部署包：[PhysicalOneClickPhase3B-Tooling-final4-20260904-142500](../bin/Verify/PhysicalOneClickPhase3B-Tooling-final4-20260904-142500/)；压缩包：[PhysicalOneClickPhase3B-Tooling-final4-20260904-142500.zip](../bin/Verify/PhysicalOneClickPhase3B-Tooling-final4-20260904-142500.zip)，SHA-256 为 `622451FA6118D059667449187F29CEC339BDFD3A7A9AEF7CC2F1204C8F3697F6`。包内 `Tools/` 包含三个脚本，manifest 指向本切片计划。
- 该工具包固定在门禁快照 `34b1e65`；之后并行 WPF/UI 任务提交的 `296beb5` 等改动未纳入，现场部署前需等并行任务结束后重新打包。
- 详细证据见 [第三阶段 B 工具证据](../artifacts/phase3b-field-tooling-evidence-20260904.md)。
- 本切片仍遵守“新鲜只读预检 → 新授权 → 一次完整流程”；本轮未连接现场设备。WPF execution ID 绑定、AUBO 写入 correlation 和监控数组回放仍待后续切片。
## 2026-09-04 Phase 3C correlation, monitor and WPF handoff

- Added durable AUBO workflow correlation fields for `load/run/stop`; the AUBO
  worker now reuses the claimed `WorkflowDeviceOperation.OperationId` and emits
  correlation evidence instead of generating an unrelated mutation id.
- MES rejects mismatched workflow/node/device-operation identities before the
  Adapter boundary and marks explicitly unassociated manual writes with
  `AUBO_UNCORRELATED_WRITE` warnings. Existing control-disabled/read-only gates
  remain unchanged.
- Added `scripts/Monitor-PhysicalWorkflow.ps1` with PowerShell 5.1 collection
  normalization and `scripts/Test-PhysicalWorkflowMonitor.ps1` offline replay;
  replay evidence records `wrappersLeaked=false`, `deviceWritesAttempted=false`,
  and `automaticRetry=false`.
- WPF accepts only explicit startup `--workflow-execution-id` or
  `--workflow-request-id` (also `WPF_INITIAL_WORKFLOW_*` environment variables),
  preserves an existing run when the supplied identity is unknown, and opens the
  monitor tab only after a successful bind. No “latest run” lookup is used.
- Offline monitor replay and targeted Adapter/MES/WPF tests pass. The complete
  Release gate is `artifacts/mes-offline-release-gate-20260904-phase3c-final6.json`
  (1004 passed / 5 allowed skipped / 0 failed, `releaseEligible=true`). The
  independent deployment package is
  `bin/Verify/PhysicalOneClickPhase3C-Correlation-final-20260904-165050.zip`
  with its SHA-256 recorded in the adjacent `.zip.sha256` sidecar file.
  No field device was connected or authorized in this slice.
