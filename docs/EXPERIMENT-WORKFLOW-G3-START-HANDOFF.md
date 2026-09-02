# 实验流程 G3 新会话启动摘要

> 状态：G2 总体验收通过；G3 已获准作为下一阶段，但尚未开始
> 日期：2026-08-20

## 1. 仓库基线

- 仓库：`D:\Project\Github\Mes`
- 分支：`docs/experiment-workflow-architecture-plan`
- 当前提交基线：`041ddae475b2e5adeb1e8e8bc3f3d230a9045787`
- 远端基线：开始 G2 验收修复前，HEAD 与 `origin/docs/experiment-workflow-architecture-plan` 一致。
- 新会话第一条命令必须是 `git status --short --branch`，并用 `git diff --cached --name-only` 确认暂存区为空。
- 不得执行 `reset`、`clean`、`stash`，不得覆盖或整理与 G3 无关的用户文件。

## 2. G2 最终结论

项目方已明确确认：**G2 总体验收通过**。

G2 已形成以下稳定边界：

1. `WorkflowGraphDocument` 是 WPF 编辑、本地存储和 MES Draft/Validate/Publish 之间的规范图文档。
2. `实验流程管理` 是唯一正式编辑入口；旧 `实验流程设计` 及独立 ViewModel/对话框已移除。
3. WPF 可观察集合仅是属性面板投影，不拥有持久化、MES 提交或画布历史。
4. 本地写出统一为 `mes.workflow.graph` schema v2；旧格式只允许显式兼容导入。
5. v1 和旧 WPF 无边线性流程可补顺序成功边；v2 断开草稿必须保持断开。
6. 画布连线可选择、删除和重建；节点/边编辑、属性投影、撤销/重做使用同一图文档。
7. 自动布局、适应画布、平移、缩放、流程切换和视口保持已通过人工验收；视口变化不占用撤销步骤。
8. 导入错误整批拒绝，不得部分修改编辑器；转换报告必须显示来源、schema、计数、迁移与问题。
9. 图文档不是执行引擎，不直接连接 AGV、机械臂、仪器或串口。

G2 最终自动门禁：

- Release 全方案构建：0 warning / 0 error
- 全量测试：578 passed / 5 existing E2E skipped / 0 failed
- Domain 37、MES 70、Adapter 176、WPF 193、E2E 19、Simulator 5、Workflow Contract 28、Instrument Gateway 50

## 3. G2 验收期间的未提交修复

以下变更属于 G2 验收修复和记录，目前未暂存、未提交：

- `src/MesControlAgv.Wpf/WorkflowCanvas/NodifyCanvasAdapter.xaml`
- `src/MesControlAgv.Wpf/ViewModels/WorkflowCanvasSpikeViewModel.cs`
- `tests/MesControlAgv.Wpf.Tests/WorkflowCanvasSpikeViewModelTests.cs`
- `docs/EXPERIMENT-WORKFLOW-G2-ACCEPTANCE.md`
- `docs/EXPERIMENT-WORKFLOW-IMPLEMENTATION-PLAN.md`
- `docs/EXPERIMENT-WORKFLOW-UI-DESIGN.md`
- `docs/PROGRESS.md`
- 本交接摘要

连线修复内容：自定义 Nodify 连线模板显式启用选择和命中高亮；连接完成事件按 Nodify 7.3 实际传入的 C# `ValueTuple` 解析。用户已验证选择、删除、重连、撤销、重做全部通过。

开始 G3 前应先决定是否将上述 G2 自有变更作为独立提交收口。不得把下节的用户文件带入该提交。

## 4. 必须保留的用户工作

下列既有内容不是 G2/G3 交付物，不得暂存、提交、删除、覆盖或格式化：

- 删除的 `artifacts/ion-chromatography/d160-capture-analysis-20260817-154030.md`
- 修改的 `docs/ION-CHROMATOGRAPHY-RDP-PACKET-CAPTURE-PLAN.md`
- `.claude/`、`.idea/`、`12131.txt`、`Input/`
- `artifacts/codex-task*.md`、`artifacts/raster-test/`
- `docs/DEVELOPMENT-ROADMAP.md`
- `docs/SYSTEM-ARCHITECTURE-ANALYSIS.md`
- `docs/agv-mes-architecture.png`
- `docs/agv-mes-task-flow.png`
- `scripts/generate_architecture_report.py`
- `scripts/render-architecture-diagrams.ps1`

## 5. G3 目标与范围

G3 目标是让流程节点从自由键值配置收敛为可发现、可版本化、可校验的类型化定义，并让发布门禁使用同一套目录和 schema。

既定交付范围：

1. 建立节点类型目录和设备能力目录，包含稳定 ID、schema 版本、配置/结果 schema、执行模式、安全分类、Profile 支持和启用状态。
2. 首批节点：Start、End、AGV 到站、定时等待、人工确认、仪器读取、仪器稳定等待。
3. WPF 右侧属性面板按 schema 提供站点、设备、能力、超时、重试和安全等级等类型化控件。
4. 发布校验覆盖节点 schema、必填配置、站点/Profile、设备/能力、端口基数、默认/异常路径和超时配置。
5. 每条校验问题携带可定位信息；WPF 点击问题后选择并聚焦对应节点，边问题应可定位到对应边。
6. D160 只暴露经过验证的只读能力；写能力必须显示为不可用且无法通过 JSON 或 UI 绕过发布门禁。

## 6. 明确不属于 G3

- 不实现工作流执行引擎、节点 worker、设备操作记录或恢复编排；这些属于 G4。
- 不新增 WPF 到设备、串口、Adapter 或 Instrument Gateway 的直连路径。
- 不发送 AGV、机械臂或仪器实体命令。
- 不开放 D160 写入、泵控制、温度/流量设置或分析启动能力。
- 不把寄存器地址、原始报文、厂商命令或自由脚本放入流程节点。
- 不在 G3 中顺带实现排程、资源租约、并行、循环或子流程。

## 7. 建议的 G3 子门禁

### G3-A：目录和 schema 契约

- 先定义不可变的节点类型、字段 schema、能力描述和目录查询接口。
- 保持 `WorkflowGraphDocument` schema v2 可兼容；优先复用节点已有的 `NodeTypeId`、`SchemaVersion` 和 `Configuration`，不要无必要提升文档 schema。
- 定义首批七类节点的字段、端口和迁移默认值。
- 明确未知类型/未来 schema 的编辑、保存和发布行为。

验收：目录合同测试通过，旧 v2 文档无损加载，尚未接 UI 或设备路径。

### G3-B：共享发布校验

- 在 Domain 扩展共享校验，MES Draft/Validate/Publish 与 WPF 预览必须使用同一规则源。
- 将现有 `WorkflowValidator` 的线性合同校验与图级 schema/端口/边校验明确分层。
- 复用 Profile 站点数据，但区分“发布时静态可用性”和“运行时现场准入”。
- 校验问题至少支持 `NodeId`；如需边定位，应以兼容方式扩展定位字段。

验收：无效站点、未知设备、禁用能力、缺少超时/默认路径/必填字段均阻止发布；警告不被误当作错误。

### G3-C：schema 驱动属性面板

- 用稳定的字段 ViewModel 投影 schema，按字段类型使用下拉框、数值输入、复选框等控件。
- 站点、设备和能力必须来自目录，不允许把不存在的 ID 当作普通自由文本提交。
- 保留未知字段以保证兼容往返，但明确标记只读/需迁移，不能静默丢弃。
- 属性提交继续进入规范图文档和现有撤销/重做历史。

验收：七类节点可配置，节点切换不残留字段，撤销/重做和保存重启无损，布局和连线无回归。

### G3-D：问题定位与总体验收

- 在编辑器中展示发布错误/警告列表，点击问题选择并聚焦节点或边。
- 完成 MES Draft/Validate/Publish/version 往返和隔离 Simulator UI 冒烟。
- 形成 `EXPERIMENT-WORKFLOW-G3-ACCEPTANCE.md`，在用户验收前不进入 G4。

验收：第一批实验步骤可在不暴露协议细节的情况下完成配置；禁用能力无法发布；无实体设备活动。

## 8. 首轮代码阅读入口

- 图契约：`src/MesControlAgv.Contracts/Workflows/WorkflowGraphContracts.cs`
- MES 生命周期合同：`src/MesControlAgv.Contracts/Workflows/WorkflowContracts.cs`
- 图到现有合同适配：`src/MesControlAgv.Domain/Workflows/WorkflowGraphContractAdapter.cs`
- 当前共享校验：`src/MesControlAgv.Domain/Workflows/WorkflowValidator.cs`
- 当前 Profile 运行准入：`src/MesControlAgv.Mes/Services/ActiveProfileWorkflowAdmissionPolicy.cs`
- WPF 规范文档所有者：`src/MesControlAgv.Wpf/ViewModels/WorkflowEditorViewModel.cs`
- WPF 属性投影：`src/MesControlAgv.Wpf/Workflows/WorkflowModels.cs`
- 主属性面板：`src/MesControlAgv.Wpf/MainWindow.xaml`
- 图契约测试：`tests/MesControlAgv.WorkflowContract.Tests/WorkflowGraphEditorTests.cs`
- MES 生命周期测试：`tests/MesControlAgv.Mes.Tests/WorkflowApiTests.cs`
- WPF 编辑器测试：`tests/MesControlAgv.Wpf.Tests/WorkflowEditorTests.cs`

## 9. 新会话启动指令

可将以下内容作为新会话首条消息：

```text
仓库 D:\Project\Github\Mes，分支 docs/experiment-workflow-architecture-plan。
G2 总体验收已通过，现开始 G3。先完整阅读
docs/EXPERIMENT-WORKFLOW-G3-START-HANDOFF.md、
docs/EXPERIMENT-WORKFLOW-IMPLEMENTATION-PLAN.md 的 G3 章节、
docs/EXPERIMENT-WORKFLOW-ARCHITECTURE.md 和
docs/EXPERIMENT-WORKFLOW-UI-DESIGN.md 的相关章节。

第一步运行 git status --short --branch 和 git diff --cached --name-only，确认暂存区为空并保护交接文档列出的用户文件。不得 reset、clean、stash 或覆盖用户内容。

先盘点现有图契约、WorkflowValidator、Profile 准入、WPF 属性投影与 MES 发布路径，提出 G3-A 目录/schema 契约的具体设计和兼容策略。确认设计后实施 G3-A 并完成相应自动化验证。不要提前实现 G3-B/C/D，不要进入 G4，不要新增任何实体设备控制、串口或 D160 写入路径。
```
