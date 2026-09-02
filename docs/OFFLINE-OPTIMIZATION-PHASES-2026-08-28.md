# 离线功能、界面与架构优化阶段计划

更新时间：2026-08-30

## 1. 范围与边界

本计划用于设备不在场时继续推进 MES。允许修改和验证：纯内存/文件协议、模拟器、
WPF、领域与应用服务、接口契约、配置校验、自动化测试和文档。

以下内容继续保留为现场任务，不用历史快照替代新鲜验收：

- 实体 AGV 控制权、移动、暂停、恢复、取消与 I/O；
- 机械臂、视觉的网络连通、坐标标定与动作闭环；
- SHA-18i 停止、清洗、托盘和缺瓶清除的动态字段确认；
- D160+ 物理激活、压力相关写入和真实进样；
- ShineLab 真实导入、启动与导出回执闭环。

## 2. 本轮已完成

### O1：离线可靠性

- 将 `StringToVisibilityConverter` 的不可实现反向转换改为 `Binding.DoNothing`，避免误设
  `TwoWay` 时 UI 线程收到未处理异常。
- 用 5 项实际转换器测试替换空占位测试。

验收：定向转换器测试通过；不依赖网络、串口或设备。

### O2：界面适配

- 主窗口最低尺寸调整为 `820x480`，导航栏由 226 缩至 184 逻辑像素，并收紧壳层边距。
- AGV 表格移除固定 1160 逻辑像素宽度及外层滚动容器，改用 DataGrid 自身滚动；
  右侧调度面板固定在页面可视区域。
- 页面操作区改为上下布局，调度详情在低高度窗口中使用独立纵向滚动。
- 新增紧凑窗口布局测试，验证调度面板边界和表格滚动策略。

验收：WPF 在 `820x480` 布局下可完成测量和呈现，右侧调度面板未越界。

### O3：视图解耦

- 新增独立 `AgvCommunicationView`，承载 AGV 状态表、空状态、调度面板和列布局恢复。
- `MainWindow` 只负责导航装配，不再直接持有 AGV 表格事件处理代码。

验收：主窗口导航/工作流绑定测试、实验页面装配测试和紧凑布局测试通过。

### O4：导入页面解耦

- 离子色谱样品任务导入提取为 `Views/ShineLabSequenceImportView`，文件选择事件只在该视图
  内处理，样品序列模型通过明确的嵌套绑定路径接入。
- 批量任务导入提取为 `Views/BatchTaskImportView`，保留原有解析、排序、提交、清空命令，
  并增加独立的批量表列布局恢复入口。
- 两个视图均有 WPF 装配测试，验证命令、数据源和持久化布局键没有因提取而断开。

验收：导入页绑定测试通过；不依赖网络、串口或现场控制电脑。

### O5-A：任务端点模块化

- 新增 `MesControlAgv.Mes/Endpoints/TaskEndpointRouteBuilderExtensions`，集中承载任务
  生命周期路由。
- `Program.cs` 通过单一 `MapMesTaskEndpoints()` 调用完成装配；路径、状态码和响应模型不变。

验收：任务 API 定向测试 `9/9` 通过，未改变现场设备入口。

### O5-B：工作流端点模块化

- 新增 `MesControlAgv.Mes/Endpoints/WorkflowEndpointRouteBuilderExtensions`，承载工作流
  定义、版本、发布、执行、运行查询、外部信号、人工确认和运行控制路由。
- 工作流控制与交互的异常映射、幂等返回码和 202/200 语义保持原样；启动文件只负责调用
  `MapMesWorkflowEndpoints()`。

验收：工作流 API 定向测试 `34/34` 通过，未连接任何设备。

### O5-C：设备网关端点模块化

- 新增 `MesControlAgv.Mes/Endpoints/DeviceGatewayEndpointRouteBuilderExtensions`，集中承载
  仪器只读状态、样品工作站、AGV 状态/命令/I/O、机械臂状态/准备度/握手路由。
- 保留原有 Adapter 异常到 HTTP 状态码映射、工作站查询默认参数、AGV 命令回写任务状态和
  机械臂握手操作 ID 规则；没有在迁移过程中发起设备调用。

验收：设备网关定向测试 `19/19` 通过，未打开现场端口。

### O6：离线状态体验统一

- 新增 `OfflineDataStateViewModel`，提供统一的状态枚举、中文状态文案、忙碌/错误/过期/可重试
  派生属性和状态迁移方法。
- 主监控、AGV、CIC-D160+、就绪地图、批量导入和 ShineLab 导入均接入该状态；刷新失败、空数据、
  旧数据过期和用户取消分别展示，不再只依赖一段自由文本。
- `StatusBrushConverter` 增加对“刷新中、暂无数据、数据过期、已更新”等状态的统一颜色映射。
- 新增状态机单元测试，并在现有主监控/仪器/就绪测试中校验状态结果。

验收：WPF 全量回归 `278/278` 通过；无网络、串口或现场设备调用。

### O7：配置与启动诊断

- 新增 `StartupConfigurationInspector`，在连接或启动服务前检查运行模式、URL、本地服务托管、
  Adapter JSON、驱动类型、Adapter 运行模式和可选功能路径。
- 语法/模式错误阻止启动；缺失的可选能力和无法离线确认的 physical 配置以警告展示。
- 新增“启动诊断”页面，展示检查结果、当前值、诊断代码和说明；真实写入状态单独显示，
  不把“可能开放”误写成现场授权。
- 默认端口合同测试已改为验证新的单一配置来源，避免在 `App.xaml.cs` 复制常量。

验收：启动诊断定向测试 `12/12`、WPF 全量 `286/286`、全解决方案
`836 passed / 5 skipped / 0 failed`；未执行网络或设备探测。

### O8：离线审计与脱敏导出

- 新增有界 `OfflineDiagnosticAuditTrail`，记录离线数据状态转换和失败后重试动作；状态事件携带
  面向用户的文案与仅供诊断的原始错误，审计入口统一负责脱敏。
- `DiagnosticRedactor` 在数据进入审计集合前移除 URL、IP、主机、COM、路径和常见凭据；
  `OfflineDiagnosticExporter` 在导出时再次脱敏并使用无 BOM 原子 JSON 写入。
- 启动诊断页增加离线审计列表、清空和“导出脱敏诊断”功能；导出状态只显示文件名，不回显完整路径。

验收：O8 定向测试 `13/13`、WPF 全量 `290/290`、全解决方案
`840 passed / 5 skipped / 0 failed`；未连接现场系统。

### O9：运营页面继续拆分

- `TaskMonitorView` 承载任务创建、列表、详情、操作区和模拟器开发面板，列布局恢复不再由主窗口处理。
- `KpiDashboardView` 独立承载状态卡、环图、趋势图、样品、耗材和仪器状态。
- `MapDashboardView` 独立管理地图导入、缩放、拖拽、站点选择、动画生命周期和 PNG 导出；
  选项卡显示时自动适配路线区域，卸载时停止动画并解除 ViewModel 订阅。
- Mock 机械臂和视觉支持 `PickFailurePercent`、`RecognitionFailurePercent` 与 `RandomSeed`，
  成功路径测试不再依赖随机结果。

验收：运营页面绑定与地图定向测试通过；WPF 全量 `291/291`；全解决方案
`841 passed / 5 skipped / 0 failed`。

### O10：设备模块启动诊断扩展

- Adapter 配置检查新增 AUBO 机械臂和样品工作站模块规则，覆盖启用/控制开关、运行模式冲突和
  必填端点字段。
- 视觉诊断明确区分“Mock 驱动存在”和“Adapter 模块已注册”；当前结果固定为未注册警告，
  直到真实视觉模块进入组合根和配置架构。
- 新增 `WPF_INSTRUMENT_GATEWAY_CONFIG_PATH`，静态校验 CIC-D160+ 串口参数；即使网关启用，
  诊断仍显示 HTTP 与注入传输均只读，不产生写入授权。

验收：O10 定向测试 `15/15`、WPF 全量 `294/294`、全解决方案
`844 passed / 5 skipped / 0 failed`；未探测网络、串口或设备。

### O11：流程编辑器组合视图解耦

- 新增 `Views/WorkflowManagementView`，承载流程预设、工具箱、Nodify 画布、属性检查器、验证
  列表、转换报告和兼容导入。
- 视图自身管理 `WorkflowEditorViewModel` 和画布的 Attach/Detach，主窗口仅装配导航；拖拽、快捷键、
  验证问题定位和画布适配行为保持不变。
- 更新 WPF 流程绑定测试，验证提取后的画布和验证表仍能联动。

验收：流程/WPF 相关回归通过；WPF 全量 `294/294`；全解决方案
`844 passed / 5 skipped / 0 failed`；无网络、串口或现场设备调用。

### O12：诊断规则与导出版本化

- 导出 JSON 增加 `schemaVersion=mes.offline-diagnostics/1.0` 和
  `diagnosticRuleVersion=mes.startup-diagnostics/1.0`。
- 增加显式导出 DTO 和兼容读取：当前版本直接接受；缺少版本号的旧格式作为可升级文件接受并提示；
  不支持的主版本拒绝读取。
- 诊断页增加“读取诊断”，读取结果和升级提示只落在本地状态，不覆盖当前审计记录；重新导出会生成
  当前版本且再次脱敏。

验收：O12 定向测试 `6/6`、WPF 全量 `295/295`、全解决方案
`845 passed / 5 skipped / 0 failed`；无网络、串口或现场设备调用。

### O13：视图生命周期契约

- 新增 `ViewLifecycleContractTests`，验证地图动画计时器和流程编辑器订阅在 UserControl 卸载时
  被释放。
- 对运营/诊断 UserControl 重复创建进行资源字典 smoke test，限制每个视图只加载一次本地主题资源，
  其它视图资源保持独立。

验收：O13 生命周期测试 `2/2`、WPF 全量 `297/297`、全解决方案
`847 passed / 5 skipped / 0 failed`；未连接现场系统。

### O14：配置规则样本回放

- 新增 `tests/MesControlAgv.Wpf.Tests/fixtures/startup-diagnostics/`，提供五种不依赖环境的
  Adapter/仪器配置样本。
- `StartupConfigurationReplayTests` 逐例校验启动结论、驱动/运行模式、写入边界和错误代码，
  并要求同一输入重复回放结果稳定。

验收：O14 回放测试 `5/5`、WPF 全量 `302/302`、全解决方案
`852 passed / 5 skipped / 0 failed`；fixture 只读本地文件，不连接网络、串口或现场系统。

### O15：发布前离线质量门禁

- 新增 `scripts/verify-offline-release.ps1`，一次性校验诊断 schema、启动 fixture 脱敏、现场 NO-GO
  文档和全解决方案单进程测试。
- 默认测试命令不启动任何本地服务；脚本只读取文件并调用 `dotnet test`，报告默认写入系统临时目录，
  仓库字段固定脱敏。
- `-SkipTests` 仅适合快速静态检查；发布前必须使用默认模式完成全量测试。

验收：O15 默认门禁实际通过；全解决方案 `852 passed / 5 skipped / 0 failed`。

### O16：视觉与仪器脱敏样本扩展

- 新增视觉候选只读配置和物理仪器只读配置 fixture，均不包含真实地址、凭据或现场身份信息。
- 启动检查器区分视觉“配置存在”与“Adapter 已注册”；当前仍报告未注册警告。
- 配置回放和发布门禁敏感信息扫描覆盖新增样本，仪器样本继续保护只读写入边界。

验收：O16 回放测试 `7/7`、WPF 全量 `304/304`、全解决方案
`854 passed / 5 skipped / 0 failed`。

### O17：自动化发布摘要

- `verify-offline-release.ps1` 使用临时结果目录聚合所有 TRX 测试文件，避免只读取最后一个项目的计数。
- 摘要包含 revision、工作区状态、诊断 schema、fixture 名称/数量、各门禁项和通过/失败/跳过/总计数字。
- `gatePassed` 表示静态门禁是否通过；`releaseEligible` 要求默认模式执行全量测试且无失败，
  并限制跳过项只能来自当前明确登记的五个完整模拟器 E2E 用例。
- 临时 TRX 与原始测试输出在脚本结束后删除，默认只保留脱敏 JSON 摘要。

验收：默认门禁实际生成 `passed=854`、`failed=0`、`skipped=5`、`total=859`，
`releaseEligible=true`。

### O18：现场只读预检输入对齐（本阶段完成）

- 新增 `FieldPreflightChecklistBuilder`，从同一份 `StartupConfigurationReport` 生成现场交接需要
  重新采集的输入项。静态配置只用于标出已知值或离线阻断，不会替代控制器实时证据。
- `AcquireControl`、`EnablePush` 在只读预检中明确要求关闭；即使配置文件标为 `standard`，
  显式开启也会在清单中标记为“离线阻断”。`MinimumConfidence` 只作为离线参考，仍需现场复核。
- 启动诊断页面新增“现场只读预检输入”页签，展示输入代码、离线值、状态和现场要求；
  导出/回读契约增加 `fieldPreflight` 与 `mes.field-preflight/1.0`，所有值和说明继续脱敏。
- 清单的 `CanDetermineGo` 永远为 `false`，并在离线发布门禁中加入 schema 与禁止自动 GO 的静态检查。
  现场交接记录仍必须由授权人员填写，任何离线报告都不能单独改变 **NO-GO** 结论。
- O18 验收：只读、standard 冲突和 simulator 三类 fixture 回放；导出/回读及 WPF 绑定定向测试
  `20/20` 通过；全解决方案 `858 passed / 5 skipped / 0 failed`，离线发布门禁
  `releaseEligible=true`。

### O19：CI 脱敏归档（本阶段完成）

- 新增 `.github/workflows/offline-release-gate.yml`，在 Windows runner 上执行
  `verify-offline-release.ps1` 和归档边界校验，不启动现场服务或设备连接。
- 新增 `assert-offline-release-report.ps1`，只接受当前门禁 schema、成功且可发布的摘要，
  拒绝路径、凭据、原始日志字段和任何 `fieldPreflight.canDetermineGo=true` 的内容。
- 工作流的唯一制品是系统临时目录中的脱敏 JSON；不上传 TRX、测试原始输出或工作区日志。
  门禁还登记允许的五个既有 E2E 跳过项，新增非预期跳过会使发布资格失效。
- 本地完整门禁与归档校验已通过：`858 passed / 5 skipped / 0 failed`、`unexpectedSkipped=0`、
  `releaseEligible=true`；远端 runner 尚未配置/触发，首次推送后需核对 Artifact 内容仍为单一 JSON。

### O20：配置差异审阅（本阶段完成）

- `OfflineDiagnosticDiffBuilder` 按稳定代码比较当前启动报告和导入的离线基线，覆盖启动检查与
  `FIELD_` 现场只读预检输入；值、状态和名称在比较前统一脱敏。
- 启动诊断新增“配置差异审阅”页签，展示新增/移除/变化/未变化及基线值与当前值；没有基线时
  保持明确的空状态，不从当前快照推断历史结论。
- 带基线导出增加 `configurationDiff`（`mes.offline-diagnostic-diff/1.0`）并支持回读；差异对象
  固定 `canDetermineGo=false`，不改变现场 **NO-GO** 边界。
- O20 定向测试 `27/27` 通过；完整解决方案 `865 passed / 5 skipped / 0 failed`，离线门禁与归档
  边界校验 `releaseEligible=true`。

### O21：诊断审阅筛选与留痕（本阶段完成）

- 配置差异页增加状态筛选与代码/名称搜索；结果只读，不会改写任一快照。
- “记录本次审阅”只向内存中的有界脱敏审计追加一条记录，包含审阅人简称、基线时间、筛选条件和
  可见/变化数量；无基线时按钮禁用，输入长度受限且不自动写盘。
- 导出时审阅记录随 `audit` 一并脱敏，仍不产生现场 GO、设备调用或网络访问。
- O21 定向测试 `30/30` 通过；完整解决方案 `868 passed / 5 skipped / 0 failed`，离线门禁与归档
  边界校验 `releaseEligible=true`。

### O22：诊断快照生命周期（本阶段完成）

- `OfflineDiagnosticSnapshotLifecycle` 只读取指定目录下的
  `mes-offline-diagnostics*.json`，区分当前格式、需升级、不支持和无法读取文件。
- 保留策略包含保留天数、至少保留最新文件数、最大文件数、总容量和扫描上限；最近快照受保护，
  非法/不可读文件不会自动列为清理候选。
- 启动诊断新增“快照生命周期”页签和扫描按钮，显示文件格式、大小、最后写入时间和清理候选理由。
  扫描只更新内存和脱敏审计，绝不自动创建目录、删除、上传或覆盖文件。
- O22 定向测试 `24/24` 通过；完整解决方案 `877 passed / 5 skipped / 0 failed`，离线门禁与归档
  边界校验 `releaseEligible=true`。

### O23：诊断快照人工清理确认（本阶段完成）

- 快照页增加勾选确认和完整确认短语；只有已扫描、未变化且明确标记为候选的诊断文件才可处理。
- 处理方式为移动到 `.offline-diagnostics-recycle/<时间>/` 可恢复回收目录，并写入脱敏 `manifest.json`；
  永久删除、后台清理和上传均未实现。
- 路径穿越、非诊断文件名、源文件变化和错误确认均 fail-closed；处理后自动重新扫描并记录脱敏审计。
- O23 定向测试 `9/9` 通过；完整解决方案 `880 passed / 5 skipped / 0 failed`，离线门禁与归档边界
  校验 `releaseEligible=true`。

## 3. 后续离线队列（历史记录；O24 已在 2026-09-01 完成）

按最新优先级，O24 及后续离线快照工作暂时暂停；现场机械臂只读联通和受控调试排在前面。
恢复离线工作时，每批只选择一个可独立验收的切片：

1. **O24（本阶段完成）— 诊断快照恢复/归档审阅**：增加回收目录恢复预览和 manifest 一致性检查，
   继续要求显式确认并禁止后台恢复或上传。恢复只移动 manifest 一致、源文件未变化且目标
   不存在的文件，并更新回收 manifest；恢复后自动重新扫描。

O24 验收：回收目录扫描、manifest 篡改/文件变化/目标冲突拒绝、错误确认拒绝、恢复后重扫和
WPF 绑定定向测试通过；全解决方案 `920 passed / 5 skipped / 0 failed`。现场机械臂只读联通
和受控调试仍需在联网恢复后另行执行。

## 4. 每阶段统一完成标准

- 不打开现场端口、不发送设备帧；
- 先有可复现基线，再做最小范围修改；
- 新行为有定向测试，随后执行相关项目全量测试；
- 全解决方案使用 `dotnet test MesControlAgv.sln -m:1 --nologo` 验证，避免并行构建 OOM；
- 更新 `docs/PROGRESS.md`，明确“已离线完成”和“仍需现场确认”的边界。
