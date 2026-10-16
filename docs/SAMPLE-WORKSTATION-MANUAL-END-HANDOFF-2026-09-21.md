# 人工结束后同库新任务接续交接（2026-09-21）

## 当前结果

> 16:05后续：用户确认现场恢复后已启动新的任务WSC8024D60B67C926F8A，目前接口及中控Running，待现场确认实际分液；见[恢复后复测](diagnostics/2026-09-21-workstation-recovered-retest.md)。本页保留上一轮历史。

> 15:59更新：用户确认已在厂家操作窗口点击“终止”；读回Idle0/事件1后，已通过既有人工确认失败入口将本轮Run/Job收尾为Failed，资源释放成功。未重发任务。下文15:47的Unknown及占用是收尾前历史，不是当前状态。

代码a6af9dc已实现最小方案：Idle且新任务Waiting时允许历史人工结束事件1；单次启动观察继承此基线，看到0/3后永久清除，后续新停止/异常照常处理。不重跑旧任务、不自动重试、不修改30秒启动观察窗口。

本轮已从同一个验收数据库正式准备、导入、准入并启动新任务，15:36:45中控设备操作进入Running；厂家任务正在运行、设备1/忙、事件3。**接续启动通过；完整完成验收未通过：15:46:46达到原有600000ms完成观察上限后Run及设备操作转Unknown，15:47:27厂家仍正在运行/忙/事件3。** 未收到现场实际分液/弹窗反馈，不将接口运行态等同物理完成。

## 代码与离线验证

- 分支feature/workstation-ml-template-validation；目录D:/Project/Github/Mes-worktrees/sample-workstation-http-readonly。未推送、未合并，其他工作区不变。
- 唯一生产代码变更为WorkflowSampleWorkstationWorker；沿用现有WPF人工确认失败入口、资源释放及审计。
- MES相关74/74、MES全量611/611、WPF相关95/95通过；solution隔离Release构建0警告0错误。
- worker增加23项测试；同库集成测试覆盖旧任务人工结束→Unknown→ConfirmedFailed→释放lease→新任务历史1→Running3→Completed0。
- 独立审查7cf4593..a6af9dc无发现。
- 输出artifacts/workstation-manual-end-build及workstation-manual-end-release。

## 同库部署与旧任务保留

15:14按用户确认，将旧Run576b925f-ce7a-424d-bc37-a3e65420c76d通过既有unknown-resolution接口ConfirmedFailed收尾。旧Job a07415b7-bb8d-4ad4-9583-432f26add1c6为Failed，不是Completed；资源已释放，不改厂家侧旧任务“正在运行”的残留记录。

MES15053在确认无活动租约后由旧PID43604升级为PID31460，使用新Release输出，仍使用artifacts/workstation-barcode-field-20260921-1315/mes/mes.db。Adapter15052/PID31896未更换。PID仅为历史记录，后续操作前须重新核对命令行和端口。

15:34升级后health正常，旧Failed记录保留，工作站无lease且可用，厂家Idle0、事件1。不是通过新建空库绕过旧状态。

## 现场身份与证据

验证编号：FIELD-MANUAL-END-20260921-1530。
证据目录：artifacts/workstation-manual-end-field-20260921-1530/（ignored，本机保留，不提交数据库/原始日志）。

| 项目 | 值 |
| --- | --- |
| 厂家 | http://192.168.200.157:8082/Service/ |
| 设备 | SAMPLE-WORKSTATION-01 / CYC-001-1000 |
| 计划 | f68102db-8caf-4457-9cb9-46de113d8cd1 v1 |
| 工作流 | 8e400197-11ca-45c5-bf2f-5a71fcd368e1 v1 |
| 新任务 | WSCEAD74AFFB073CBF8B |
| Job | 81feca51-ce2c-4971-9b6d-1fa0a44fcab7 |
| Preparation | 15ed1d49-5457-4d68-bd18-69eb9a145990，Imported |
| Run | 9ed1dc65-1522-4520-8421-5c68efe3b9f6，Unknown |
| 设备操作 | 9cee6188-50f5-bde8-72a6-8eddcd5b8346，attempt1，Unknown |

SRC-260921-1530-01绑定CYC-001-1000/(1,1)→FYB-001八孔；SRC-260921-1530-02绑定CYC-002-1000/(1,1)→FYB-002八孔。来源为用户批准中控生成的测试ID，不是扫码所得。两组孔位顺序均为(1,1)..(1,4),(2,1)..(2,4)，默认枪头QT-001/(0,0)。

每孔50µL，Excel填写0.05mL，总800µL。冻结XLSX SHA256：51976C217BFAB41D533899066FD127D6A8C340076A80B151D2A51030A392B292。

15:35准备/导入成功，自动POST+URL参数同步双来源条码；15:36下载模板与HTTP轨迹逐字段、顺序、体积比对全部一致（16孔）。启动前设备Idle0、新任务Waiting、历史事件1。15:36:40正式admit返回202，随后单次Start；15:36:56及15:37:32中控Running，厂家正在运行/设备忙/事件3。

准备、导入、回读、比对、准入与轮询证据分别为central-preparation.json、import-result.json、import-readback.json、readback-comparison.json、admit-result.json、poll-*.json；冻结文件在frozen-import/。人工收尾证据在artifacts/workstation-manual-close-20260921/。

## 后续边界

本轮10分钟内未出现Completed+Idle0+事件0一致终态；未临时延长超时或重新启动服务。错误为：`The workstation task did not reach a consistent Completed + Idle/0 + code 0 terminal state before timeout.`

15:47只读核对：Run=Unknown、节点=Unknown、设备操作=Unknown，工作站hasActiveLease=true/isAvailable=false，未释放可能仍运行的设备。Job接口仍返回status5/Running、lastError=null；这是本轮观察到的汇总状态差异，后续WPF/业务展示应复核，不能只看Job断言流程健康。此轮不扩大范围修改其映射。

厂家详情RequestTime/ProductionTime/CompletionTime均null，不能用这些字段证明执行时间。尚无法区分实际16孔运行超过10分钟、设备等待人工交互、或厂家完成状态未更新；等待现场反馈，不能直接认定厂家故障或放大中控超时。

仅继续只读核对，不补发Start/Init，不发送停止，不将旧人工收尾授权套用到这次Unknown。若实际仍运行则等待实际终态，再决定如何按既有人工对账入口收尾；若实际停下则结合日志/界面确认原因。中控Unknown未触发物理停止。

outbound-counts.json及Adapter日志证明本轮Start1、条码更新1；本轮导入1，未重跑旧任务。final-readback.json包含Run、Job、节点、时间线、设备操作、资源、厂家状态和详情。下一步首先询问现场是否实际分液/已经停止/弹窗，不重复新建任务。

独立小瓶条码模型、10mL上限校验、准备阶段Unknown归档入口、WPF现场显示不在本次实现范围。现有溯源是来源条码→目标位置关系，不声称已有每只小瓶独立条码闭环。

## 15:55截图与15:59人工终止收尾

用户离开现场，无法确认实际分液过程；后提供截图，任务号WSCEAD74AFFB073CBF8B及两条SRC-260921-1530来源ID正确显示。截图为流程1“移液”、动作1“单泵取针”的操作提示，包含“动作完成”文字以及终止/重试/忽略/初始化重试实验按钮；不是条码确认窗口，不能仅凭截图认定具体机械故障或整轮完成。

15:56只读返回仍为任务正在运行、设备忙1、事件3，说明当时接口未区分截图中的人工处置窗口；未知窗口出现的准确时间，不能断言整个10分钟一直卡在该处。

用户明确回复“已经点了终止了”。15:58:19核对设备已Idle0、事件1，厂家任务仍残留正在运行，中控Run此前仍Unknown。
15:59:17使用现有POST /api/workflow-runs/9ed1dc65-1522-4520-8421-5c68efe3b9f6/unknown-resolution，以local-operator执行ConfirmedFailed，requestId=93479f78-e30f-4090-aac7-ebd9e6f36bb5，保留超时及人工处置原因，不直接修改数据库、不发设备命令。
15:59:34读回Run=Failed/isTerminal=true、Job=Failed，工作站hasActiveLease=false/isAvailable=true。没有将中断任务判成功，也未启动新任务。

证据为本轮目录popup-readback.json及manual-stop-preflight/request/result/readback.json。人工收尾与占用释放已再次实测通过；完整分液Completed闭环仍未通过。
待厂家确认取针处置窗口原因及现场恢复就绪后，再按用户新一轮测试要求创建新任务，不重放本任务、不点击忽略绕过。应向厂家核实两点：此操作提示为何出现，以及待人工处理时如何通过接口区分正常运行（当前仍返回忙1/事件3）；人工结束后任务状态也未同步更新。
