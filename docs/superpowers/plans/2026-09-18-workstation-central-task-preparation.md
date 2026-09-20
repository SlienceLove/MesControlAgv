# 工作站任务准备实施计划

设计：docs/superpowers/specs/2026-09-18-workstation-central-task-preparation-design.md。
工作区固定：D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly；禁止修改主工作区或现场运行进程。
全局：真实设备零写入；导入不启动；单次发令不重试；无准备记录的既有任务路径兼容，有准备记录必须符合当前核对和导入门禁；WPF只访问MES；不修改发布的工作流定义。

## Task 1: Backend task preparation and frozen runtime binding

实现Contracts/Application任务准备契约、MES追加式准备持久化与增量建表、任务级GET current / POST prepare / POST import接口（复用requestId/actor/reason及核对gate）。
数据明确包含：job/preparation/device/vendor task、核对id/revision/hash、生成任务模板、两个大瓶与核对样品及来源module/x/y绑定、自身hash、状态/时间/错误。服务端从核对快照取条码，禁止客户端伪造；生成唯一厂家taskNo，不能覆盖模板原任务号。
Prepare无设备写；Import先验证current核对，再持久化Importing，再单次ImportTasksAsync回读确认+单次UpdateTaskBarcodesAsync；结果未知不能自动重试/跳过。持久化幂等与任务审计。变更核对后准备失效、运行/终态拒绝修改。一个任务只绑定一个工作站节点，验证device与固定工作流/排程设备一致。
Admission识别已准备任务且只能Imported当前快照通过；冻结准备ID/条码/任务号到运行及节点/操作输入，不改变发布定义。Worker对准备式运行使用两个冻结条码启动一次，保留旧taskNo-only路径及只读恢复。
后端测试覆盖真实临时DB生命周期、两个样品多个目标、零启动副作用、幂等、漂移、失败/Unknown、冻结参数正确、旧路径兼容和schema升级。
只改Contracts/Application/MES及MES/WorkflowContract测试；不要改WPF。提交后完整报告写 .superpowers/sdd/2026-09-18-workstation-central-task-preparation/task-1-report.md，包含对后续WPF的具体DTO/方法/路由说明。

## Task 2: WPF task-table preparation in scheduling

按Task1提交的契约扩展IMesClient/MesClient；任务排程页内提供指定任务模板读取、分液表展示/编辑、来源ID绑定、保存准备、显式导入及导入状态。大瓶→小瓶位置关系直接从模板提取，不重复录入位置；只需把中控来源ID关联到模板大瓶位置。不要新增顶层导航或专用启动。
已有样品登记和核对保持独立；空来源ID可通过现有登记流程生成唯一默认值，重复修改不得自动换ID。模板源位置和来源ID明确对应。
UI选中任务加载current preparation；有未保存编辑、核对漂移或非Imported时阻断新模板模式准入。两种模式明确，不让旧接口回退掩盖导入失败。保存/导入无隐式运行；当前已有运行准入确认照旧。
保持任务切换/operation ownership守卫；折叠和滚动布局确保时间线可见。补HTTP、VM、XAML定向测试。仅修改WPF相关文件与测试，提交报告到task-2-report.md。

## Task 3: Integrated regression and handoff

用测试替身验证完整两来源→多个目标→导入确认→写码→准入→单次带码启动→完成释放链，运行MES全量、Adapter工作站相关、WPF相关、WorkflowContract，隔离solution build。
更新集成说明和现场接续记录，记录能力边界/版本/问题码/测试数量，不冒充真机验收。不合并日常开发分支，等待真实验收通过后再按授权整合。
最后按requesting-code-review独立整体审查，修复实际缺陷，提交并交接。
