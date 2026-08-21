# 实验流程 G3 验收记录

> 状态：G3-A 至 G3-D 已实现并通过自动化门禁，等待项目方总体验收；未进入 G4

日期：2026-08-21

## 1. 交付范围

### G3-A：目录和 schema 契约

- 增加不可变节点类型目录和设备能力目录，统一稳定 ID、schema 版本、配置/结果字段、端口、执行模式、安全分类、Profile 支持及启用策略。
- 首批节点包含 Start、End、AGV 到站、定时等待、人工确认、仪器读取和仪器稳定等待七类。
- `WorkflowGraphDocument` 继续使用 schema v2；已存在的 `NodeTypeId`、`SchemaVersion` 和 `Configuration` 承载类型化信息。
- 未知节点类型、未知字段和未来 schema 可在草稿中无损保存，但必须迁移后才能发布。
- D160 写能力仅作为禁用目录项可见，图 JSON 不能自行启用。

提交：`321517e feat(workflows): add G3 catalog contracts`

### G3-B：共享发布校验

- 保留 `workflow-contract-v1` 作为已发布定义的运行兼容校验，新增严格的 `workflow-publication-v2` 发布门禁。
- 发布校验覆盖节点/schema、必填字段和值域、Profile 站点、静态设备及能力策略、目录端口、边语义和基数、Start/End、可达性、默认/异常路径与环路。
- 校验问题可携带 `NodeId`、`EdgeId` 和 `ConfigurationKey`；可选异常路径缺失为警告，警告不阻止发布。
- MES 草稿预览、持久版本校验和发布使用同一规则源；发布会基于当前持久定义和 Profile 快照重新校验，不信任旧结果。
- 运行时在线状态、占用、控制器就绪和串口所有权没有被错误固化为发布条件。

提交：`eef0271 feat(workflows): enforce G3 publication validation`

### G3-C：schema 驱动属性面板

- WPF Inspector 根据共享 schema 投影文本、数值、布尔值和封闭选项控件，工具箱按目录创建七类节点。
- 站点、设备和能力选择来自 Profile/目录，不允许将未知 ID 当作普通自由文本提交。
- 能力、执行模式和安全元数据只读展示，不暴露协议、端点、寄存器或命令字段。
- 未知字段和未来 schema 继续无损保存，并明确显示为只读、需要迁移。
- 配置更新写回规范图快照，继续参与画布撤销/重做、保存和重启往返。

提交：`ded1752 feat(workflows): add G3 schema-driven inspector`

### G3-D：问题定位与总体验收

- 主窗口展示发布错误和警告，支持严重级别、定位类型过滤，并显示规则、字段、元数据和建议操作。
- 选择问题会同步选择并聚焦对应节点；边问题通过 `IWorkflowCanvasSurface.FocusEdge` 选择并聚焦对应连线。
- 本地和 MES 返回的校验结果使用同一可定位投影；发布按钮在存在错误时保持禁用。
- 增加类型化图 Draft -> Validate -> Publish -> version read 的 MES 往返测试，验证节点、边、配置、布局、视口和发布版本不丢失。
- 新增可选的 `WPF_WORKFLOW_STORE_PATH`，使进程级 UI 验收可使用临时流程文件，不再改写操作员默认本地存储。
- 问题面板没有替代既有 dry-run 和审计摘要；两者仍在原工作流界面可见。

G3-D 由本验收记录所在提交交付。

## 2. 自动化验证

最终 Release 门禁于 2026-08-21 执行：

| 检查 | 结果 |
| --- | --- |
| `dotnet build MesControlAgv.sln --configuration Release --nologo` | 0 警告，0 错误 |
| Workflow Contract | 54/54 通过 |
| Domain | 39/39 通过 |
| MES | 75/75 通过 |
| WPF | 203/203 通过 |
| Adapter | 176/176 通过 |
| Instrument Gateway | 50/50 通过 |
| Simulator | 5/5 通过 |
| E2E | 19 通过，5 个既有用例跳过，0 失败 |
| 全方案合计 | **621 通过，5 跳过，0 失败** |

其中 G3-D 聚焦验证覆盖：

- 校验元数据、节点/边/字段定位和严重级别/位置过滤。
- 选择问题后的节点选择、边选择及画布聚焦请求。
- 真实 WPF `DataGrid` 绑定和选择事件，而非仅测试 ViewModel 命令。
- 远程校验结果到可定位问题列表的投影。
- 显式临时 `WorkflowStore` 注入，防止测试依赖用户目录。
- MES 类型化图完整发布生命周期及持久化往返。

## 3. 已完成的隔离运行验证

使用 Release 输出、独立 Simulator/Adapter/MES 端口、临时 SQLite 和临时 WPF 流程文件完成真实界面冒烟。观察结果：

- 界面显示 21 条问题，其中 17 条错误、4 条警告。
- 校验器为 `workflow-publication-v2`，目录版本为 `1.0`，Profile 为 `MES-AGV / 1.0`。
- 无效流程的发布按钮禁用；选择节点问题后画布导航生效，边选择/聚焦由真实 WPF 绑定测试覆盖。
- 隔离 MES 中的类型化图以版本 1 发布成功，发布前有效且仅有 2 条非阻断警告。
- 节点/边 ID、配置、布局、视口和 `PublishedVersion` 在版本读取后保持一致。
- 冒烟没有调用 dry-run、执行或设备端点，没有发送实体设备命令。

该次 MES 验证使用的临时工作流 ID 为 `eb64e962-0fb2-4adc-aec9-4f517465f1c1`；它只存在于隔离 SQLite 中，不是共享环境数据。

## 4. 推荐人工验收

建议使用 Simulator Profile 和隔离存储，约需 10 至 15 分钟。示例启动方式如下；如端口被占用，可整体更换为其他未占用的本机端口：

```powershell
$runId = 'g3-acceptance-' + (Get-Date -Format 'yyyyMMddHHmmss')
$runRoot = Join-Path ([IO.Path]::GetTempPath()) "MesControlAgv-$runId"
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

.\scripts\run-local.ps1 `
  -Configuration Release `
  -RunId $runId `
  -SimulatorUrl http://localhost:5683 `
  -AdapterUrl http://localhost:5641 `
  -MesUrl http://localhost:5645 `
  -MesDatabasePath (Join-Path $runRoot 'mes.db') `
  -AdapterDatabasePath (Join-Path $runRoot 'adapter.db') `
  -RequireIsolatedStores

$env:MES_BASE_URL = 'http://localhost:5645/'
$env:WPF_WORKFLOW_STORE_PATH = Join-Path $runRoot 'workflows.json'
& .\src\MesControlAgv.Wpf\bin\Release\net8.0-windows\MesControlAgv.Wpf.exe
```

验收操作和预期现象：

1. 打开“实验流程管理”，确认工具箱包含七类节点；逐类选择后，右侧只显示其 schema 字段和只读能力/安全元数据，不显示寄存器、串口或原始命令。
2. 在 AGV 节点选择站点，在仪器节点选择设备/能力；确认输入来自下拉选项，无法自由填写未知 ID。
3. 制造必填字段缺失、无效站点或缺少默认路径并执行校验；确认错误进入问题列表且发布按钮禁用。
4. 切换错误/警告及节点/边/字段过滤；点击节点问题和边问题，确认画布分别定位到对应对象。
5. 对属性修改执行撤销/重做，切换节点后再切回；确认字段不串位，未知兼容字段不丢失。
6. 修复阻断问题后保存草稿、校验并发布，再读取版本；确认图、属性、布局、连线和视口保持一致。
7. 关闭 WPF 后运行 `.\scripts\stop-local.ps1 -RunId $runId`，确认本次启动的隔离服务退出；再运行 `Remove-Item Env:MES_BASE_URL,Env:WPF_WORKFLOW_STORE_PATH` 清理当前 PowerShell 会话。

项目方需要确认：**通过 / 需修改**。确认通过前只修复 G3 问题，不开始 G4。

## 5. 兼容性、风险与回退

- 图文档仍为 schema v2，旧 v2 草稿可继续无损加载；未知或未来 schema 只禁止发布，不会在保存时静默删除。
- `workflow-contract-v1` 仍服务于既有发布版运行兼容，严格门禁单独标识为 `workflow-publication-v2`。
- 首次 UI 冒烟在增加隔离变量前调用了既有 `SaveDraft`，使 `%LOCALAPPDATA%\MesControlAgv\workflows.json` 的哈希从 `B2EAE34B...` 变为 `665217B5838F209EBAA84362316DDBC86655AD29C34780EFAB2F637E24381108`。原字节没有备份，因此没有猜测性回退；后续冒烟使用 `WPF_WORKFLOW_STORE_PATH`，并确认该用户文件哈希保持此值不再变化。
- 回退应在干净分支上按相反顺序使用 `git revert` 回退 G3 提交；不得使用 `reset`、`clean` 或覆盖本工作树中的用户文件。
- G3 没有新增 worker、节点执行记录、设备操作记录、恢复编排、排程或资源租约；这些仍属于后续阶段。

## 6. 安全边界

- 未新增 WPF/MES 到 Adapter、Instrument Gateway、串口或实体设备的直连控制路径。
- 未发送 AGV、机械臂或仪器实体命令。
- D160 仅保留已验证只读能力；泵、温度、流量、方法、进样、分析启停及任何写入仍不可发布。
- G3 总体验收通过只允许讨论 G4，不构成任何实体设备活动授权。
