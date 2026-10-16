# AI流程助手最小版本交接

本版在中控现有实验方案框架上增加自然语言规划入口：读取已发布流程模板，调用私有模型选择和排序，显示待补信息，用户复核后创建可编辑的实验方案草稿。不会发布方案、创建实验Job、排程、准入或调用设备。

## 分支和原成果

工作分支feature/lab-ai-assistant，独立目录D:/Project/Github/Mes/.worktrees/lab-ai-assistant。用户于2026-10-16批准接管旧AI工作区并继续最小场景。
2bc203f保存旧Excel/CSV报表助手实现；d40b98c将本地develop 95c6cf1同步到AI分支。合并保留检测标准、工作站批量导入与异常核对、紧凑界面等最新行为，不恢复已删除的独立仪器导入页面。
旧artifacts/lab-ai包及现场证据保留，未整体提交。未更改主目录、其他worktree、现场数据库或运行服务，未推送或合入develop/master。

## 操作方式

新版WPF运营总览增加“AI 流程助手”，原“实验报表助手”继续可用。

1. 输入目标，如“从待检区取料，送开盖分液，完成后送到检测位”。
2. 点击生成。目录里没有可用已发布模板时，明确提示先准备模板；不返回虚构流程。
3. 对话补充站点、批次或缺失要求后重新生成完整方案。最多保留8条历史用户描述，模型回答不被当成用户授权。
4. 查看顺序排列的模板卡片，展开核对节点、站点和固定参数；可编辑方案名称、批次及预计耗时。
5. 勾选已复核，点击“确认创建草稿”。客户端随后调用现有实验方案校验接口，但不自动发布。
6. 点击“在实验方案中打开”，进入现有编辑器继续调整顺序、物料和资源，再按既有发布/任务创建流程处理。

目录过长时可展开“限定本轮模板范围”，读取并勾选相关模板；不勾选默认提供全部模板。模型只允许引用本轮提供的真实模板。

## 当前能力边界

本版生成的是引用已发布模板的ExperimentPlan草稿，不是凭空编写新的设备节点图。新模板、模板内部节点编辑和参数声明仍由现有流程编辑器承担。
已支持用户编辑名称、批次、预计耗时以及通过补充描述重新组合模板。预计耗时用于排程估计，不修改设备超时。

模板里的站点、机械臂程序、任务号和分液量保持原值。若用户要求换站点或改50µL，而目录没有匹配模板，模型应提出待补信息，不能通过一个无效的Plan参数声称修改成功。结构检查能阻止虚构引用，但不能证明模型已经正确理解每个业务要求；首次模型验收需逐条人工核对缺项和交接顺序。
样品批次字段仅作为上下文，不自动登记实际样品、创建来源条码或绑定孔位；物料清单需在现有实验方案中补齐。不同模板间物料交接的完整性仍需人工复核，现有方案校验通过不等于现场执行已就绪。

## 接入结构

- Contracts/AiPlanningContracts：目录、对话、模型结构化方案及草稿请求。
- Application/AiPlanDraftBuilder：从真实模板引用构造ExperimentPlanDraft；生成稳定步骤ID，汇总站点/AGV/设备资源，保留版本。
- MES/AiPlanningService：只读读取最新已发布版本，用现有ValidateAsync重新校验；生成和保存前都核对目录。
- MES/OllamaPlanningModel：单并发、可取消的私有Ollama模型调用；无设备工具、Shell、SQL或云回退。
- WPF/AiPlanningViewModel与AiPlanningView：用户描述、补问、模板卡片、人工复核、创建草稿及跳转原编辑器。

接口：

| 接口 | 行为 |
| --- | --- |
| GET /api/ai-planning/catalog | 只读目录及配置/模板指纹 |
| POST /api/ai-planning/propose | 调用模型、结构检查，不写任务 |
| POST /api/ai-planning/drafts | 明确确认后通过原实验计划命令服务创建草稿 |

目录指纹包含当前节点目录版本、Profile及模板定义。生成期间或保存前发现变化，提示重新生成。撤回/归档模板不能用旧方案继续保存。
保存使用现有幂等请求ID，网络结果不明时保留原请求，人工再次保存核对同一请求；不自动重试。用户原描述保存在方案说明中，审计原因保留目录版本。
临时对话和未保存草稿在客户端内存中，重启不恢复；已保存方案由现有MES持久化。

## 模型与有线网络部署

AI与中控可以分机部署，中控通过有线局域网调用，不需要把模型安装在中控电脑。AI离线不影响既有设备控制、任务调度和统计报表。
之前i5/8GB配置指独立Windows试用主机，尚未确认是否就是离子色谱控制电脑。若共用仪器电脑，先检查空闲内存、CPU占用和同时运行的影响，再决定是否适合长期部署；本轮未安装或启动真实模型。

新规划模块使用配置LabPlanning:OllamaBaseUrl和LabPlanning:Model；默认http://127.0.0.1:11434/及qwen3:1.7b。旧LabReports配置互不改变。
延续既有私有部署约束：模型地址必须回环，远端模型通过中控侧经过认证的本地隧道/代理接入。不自动开放端口或更改防火墙。
规划调用与旧短摘要不同：8192上下文、最多2048输出token、单并发、4线程、120秒超时、结束后卸载。输入按UTF-8字节保守限制5500，超限提示缩小范围，不静默丢弃用户要求或模板。真实8GB主机内存和中文规划效果未验收，不能沿用旧摘要容量估计。

## 验证

本轮使用假模型HTTP和临时测试数据库验证链路，没有下载模型、联网查询资料或调用真实设备。
MES新增11个测试用例覆盖完整草稿链、只读生成、模板范围、虚构模板、重复保存、目录变化、撤回模板、缺少信息、模型格式、截断、取消和并发；WPF新增6项覆盖人工复核、补充需求失效、耗时格式、目录变化、保存重试和历史消息。
最终Release验证：MES810/810、WPF778/778（排除上述2项旧演练）、Reporting31/31、WorkflowContract89/89，合计1708项通过；解决方案Release构建0警告0错误。新增11项后端及6项WPF用例均包含于上述数量，不重复计数。最终离线界面冒烟通过，1000/1200宽截图及草稿保存检查保留在planner-smoke-20261016-final。

可重复验证：

    dotnet build MesControlAgv.sln -c Release --nologo -v minimal
    dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj -c Release --no-build --no-restore
    dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName!~ShineLabHandoffRehearsalTests
    dotnet test tests/MesControlAgv.Reporting.Tests/MesControlAgv.Reporting.Tests.csproj -c Release --no-build --no-restore
    dotnet test tests/MesControlAgv.WorkflowContract.Tests/MesControlAgv.WorkflowContract.Tests.csproj -c Release --no-build --no-restore

WPF排除原有2项ShineLabHandoffRehearsalTests：它们按.git目录定位，会在linked worktree越界写主目录；没有删除或放宽断言。
隔离artifacts构建配合SkipLocalServiceCopy时，现有Launcher打包检查会报告缺少服务runtime；本轮未修改打包行为，改用本独立worktree正常Release构建验证。

离线界面工具scripts/lab-ai/PlannerSmoke只创建独立窗口和假MES HTTP，不调用App.OnStartup、主程序轮询或模型。截图位于artifacts/lab-ai/planner-smoke-20261016及后续final目录；页面为合成示例，不是现场流程。

## 下一步

先准备少量可解释的真实已发布模板，例如取料转运、工作站处理、取回送检，并补清模板描述与动作边界。确认模型主机和接入方式后，用10至20条真实用户表达验证：同义站点、缺少位置、多候选、取放动作、批次修改、单位和模板不支持的参数。
中文语义质量合格后再扩展受控参数映射和资料检索。PDF/Word解析、SOP引用、自由节点生成、自动任务发布和设备执行均未纳入本次交付。
