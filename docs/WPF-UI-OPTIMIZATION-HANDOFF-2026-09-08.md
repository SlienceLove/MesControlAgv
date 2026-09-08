# WPF 界面优化交接（2026-09-08）

本文是下一会话继续优化 WPF 展示时的入口文档。当前目标是继续改善信息密度、响应式布局和运行监控可读性；暂不合入主分支。

## 1. 当前基线

- 分支：feature/wpf-ui-layout-optimization
- 当前提交：2b8ad59 fix(wpf): render first tab on startup
- 远端：origin/feature/wpf-ui-layout-optimization 已同步
- 主分支：未合入
- 最近关键提交：
  - 2b8ad59：修复 WPF 首次打开白屏
  - b6a9640：排程时间轴增加计划/实际/当前三层视觉和当前块证据突出
  - 3cf66a3：安全关联复合子流程证据
  - ba3e290：运行监控增加复合运行准备入口
  - 465763b：运行监控按任务展示复合步骤状态
  - d0a0746：持久化复合运行准备快照

工作区目前有大量现场验证、物理设备就绪监督和文档改动，另有部分未跟踪证据文件。这些不是本次 UI 修复的一部分，不能使用整体 git add .、reset --hard 或清理命令处理。

## 2. 用户目标与当前范围

用户希望 WPF 在有限屏幕上能完整查看和操作：

1. 一个节目/方案包含多个流程时，能按用户可理解的任务和步骤监控，而不是要求输入内部 ID。
2. 实验方案可以自由编排多个已经验证的固定流程模板。
3. 任务排程能看到各时间段、各设备当前执行内容和状态。
4. 流程编辑器、运行监控和排程页面支持更大的编辑/查看空间，节点多时可全屏或聚焦查看。
5. 首次打开页面即显示默认内容，不需要先点击左侧导航。

本分支只做展示层、交互层和已有业务数据的投影，不改变设备写入安全边界、MES 状态机或现场协议。

## 3. 已完成的界面和业务投影

### 3.1 主窗口与响应式布局

- 主窗口由长 Tab 标题栏改为四组可折叠左侧导航：运营总览、任务与设备、实验工作流、系统与诊断。
- MainTabs 保留，既有自动化 ID 和测试访问方式未删除；TabControl 只承载当前内容，导航与内容区解耦。
- 任务监控、AGV、设备状态、样品导入、批量导入、实验方案、任务排程、流程管理、流程运行监控、地图和启动诊断等页面已完成首轮窄屏适配。
- 常见的“列表 + 详情/操作区”在窄窗口自动改为上下布局，减少固定右栏把内容推出视口的问题。
- 标题栏和操作栏支持换行；长错误、审计、路径和验证信息放入可滚动详情区。
- 数据表格保留核心列，次要/诊断字段移至详情区；已有 DataGridLayoutPersistence 用于列布局持久化。
- 流程编辑器和运行监控保留画布/属性检查器结构，并提供聚焦或全屏窗口入口。

主要入口：

- src/MesControlAgv.Wpf/MainWindow.xaml
- src/MesControlAgv.Wpf/Views/TaskMonitorView.xaml
- src/MesControlAgv.Wpf/Views/AgvCommunicationView.xaml
- src/MesControlAgv.Wpf/Experiments/ExperimentPlanManagementView.xaml
- src/MesControlAgv.Wpf/Experiments/ExperimentSchedulingView.xaml
- src/MesControlAgv.Wpf/Views/WorkflowManagementView.xaml
- src/MesControlAgv.Wpf/WorkflowCanvas/WorkflowRunMonitorView.xaml

### 3.2 流程模板、实验方案和排程

- 已验证/已发布的固定流程模板可筛选；模板能力、资源要求、预计时长和最近验证信息会在方案页展示。
- 方案支持多个 WorkflowSteps，每一步固定保存 StepId、顺序、流程版本、业务名称、预计时长和参数覆盖。
- 旧方案仍兼容单流程字段；目录中已不存在的旧版本会保留引用并明确标记不可用。
- 排程页展示方案步骤摘要、计划时间段、资源占用和冲突信息；时间轴已支持计划层、实际设备活动层和当前层的区分。
- 当前活动块可突出显示，并能定位到任务、方案步骤和最近审计证据。

### 3.3 复合运行与流程监控

- 已有 ExperimentRun / ExperimentStepRun 外层运行快照和步骤状态机。
- 复合运行准备只创建外层快照，不提前创建子流程或写设备。
- 模拟器子流程按顺序协调；Completed 才推进下一步，Failed、Cancelled、Unknown 分别收口，Unknown 默认 fail-closed 并需要人工核验。
- 子流程活动通过 WorkflowStepId 关联到方案步骤和设备活动时间轴。
- WPF 运行监控优先按任务/方案选择复合运行；内部 execution/request ID 仅保留在高级折叠入口或外部启动绑定中，普通用户不需要输入 ID。

## 4. 最近白屏修复

现象：首次进入 WPF 时内容区白屏，点击左侧导航后才显示。

原因：左侧首个 RadioButton IsChecked="True" 只是初始视觉状态，不会触发 Click；自定义 ContentOnlyTabControlStyle 依赖 SelectedContent，启动阶段没有显式建立首个 Tab 的内容选择关系。

修复内容：

- MainWindow.xaml 的 MainTabs 明确设置 SelectedIndex="0"。
- MainWindow.xaml.cs 在构造阶段和 Loaded 阶段调用 EnsureInitialTabSelection()，并同步导航 RadioButton 状态。
- MainWindowNavigationTests 新增首屏回归测试，验证 SelectedItem、SelectedContent、可见性和实际尺寸。

对应提交：2b8ad59。

## 5. 当前验证结果

本次修复后已执行：

~~~powershell
dotnet build src/MesControlAgv.Wpf/MesControlAgv.Wpf.csproj --no-restore -p:SkipLocalServiceBuild=true -p:SkipLocalServiceCopy=true -p:BuildProjectReferences=false

dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore -p:SkipLocalServiceBuild=true -p:SkipLocalServiceCopy=true -p:BuildProjectReferences=false
~~~

结果：WPF 本体 0 警告/0 错误；WPF 测试 404 passed / 0 failed。

普通 WPF 构建会触发项目自定义的 BuildLocalServices。本次工作区曾遇到旧 Simulator 进程锁定 DLL，以及其他未提交现场改动造成的 TemporaryObstacle 编译错误；下一会话优先使用上面的 scoped 命令，不要为了测试去清理或回滚现场文件。

## 6. 下一阶段建议（按优先级）

### P0：先做可见效果验收

1. 启动当前分支的 WPF，确认首次打开直接显示“任务监控”，不再白屏。
2. 在 820x480、1024x640、1440x900 三档窗口检查任务监控、方案、排程、流程编辑器和运行监控。
3. 记录截图或人工检查结果，重点看：标题/操作栏是否挤压、详情是否可滚动、DataGrid 是否出现无意义横向滚动。

### P1：统一页面视觉细节

- 抽取统一的页面标题栏、状态徽标、连接来源徽标、空状态、加载中和错误详情样式，避免每个 XAML 重复定义。
- 将错误、路径、审计和连接来源等长文本统一为“摘要 + 可展开详情”。
- 统一状态表达为颜色 + 文本 + 图标/徽标，Unknown 要单独高亮且明确不可自动重试。
- 检查页面内嵌套 ScrollViewer，尽量保证每个页面只有一个主滚动方向，表格自身负责行滚动。

### P2：继续改善排程和复合监控

- 验收复合方案的每个步骤、每台设备是否都能同时看到计划、实际、当前状态和异常摘要。
- 为时间轴增加图例、缩放/时间范围控制和“只看当前任务/只看异常/只看设备”筛选。
- 对没有实际证据的计划块明确显示“未开始/未采集”，不能用计划数据冒充实际执行。
- 继续保留“按任务/方案选择”的用户入口，避免把内部运行 ID 暴露为必填字段。

### P3：导航和视图生命周期

- 将 MainWindow.xaml 中硬编码的导航映射逐步收敛为模块描述元数据和 ViewFactory；先做只读抽象，不要一次性重写所有 ViewModel。
- 对低频页面考虑首次访问延迟创建和暂停刷新，减少启动绑定树和无关轮询。
- 统一页面 DataContext 约定，逐步减少依赖 x:Name 跨视图访问。

## 7. 下一会话推荐起手式

~~~powershell
git switch feature/wpf-ui-layout-optimization
git pull --ff-only
Get-Content docs/WPF-UI-OPTIMIZATION-HANDOFF-2026-09-08.md
git log -5 --oneline
git status --short
~~~

然后只选择一个页面做一个可回滚切片，建议优先从“统一页面标题/状态徽标”开始。每个切片都应：

1. 先确认没有覆盖工作区已有现场改动；
2. 修改对应 XAML/测试；
3. 用 scoped WPF build/test 验证；
4. 只暂存本次文件或 hunk；
5. 独立提交并推送 feature/wpf-ui-layout-optimization；
6. 等用户确认效果后再考虑合入主分支。

## 8. 不可越过的边界

- 不要合并主分支，不要重写历史。
- 不要把现场 physical 默认模式改成 simulator，也不要开启物理批量写入。
- 不要在 UI 优化中绕过 Adapter/MES 的授权、就绪检查、Unknown fail-closed 或人工核验流程。
- 不要删除或整体恢复当前工作区的现场证据、AUBO/AGV 脚本、数据库和日志文件。
- 任何设备写入、AUBO load/run/stop、AGV 派发/移动或仪器写入都不属于本阶段任务。

