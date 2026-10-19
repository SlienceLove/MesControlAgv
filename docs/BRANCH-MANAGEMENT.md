# 分支管理与统一基线

## 项目强制规则

本文件是项目会话的必读规则，由根目录 [AGENTS.md](../AGENTS.md) 引用。每次新会话开始必须完整阅读，检查当前分支、工作区和 worktree 归属后再开发；不能用上一会话记忆代替。

多窗口并行必须同时满足“独立分支 + 独立 worktree 目录”，并由指定集成会话串行汇总。纯 UI 任务使用 UI 分支，业务功能使用从最新 `develop` 建立的任务分支；涉及前后端联动的同一任务在同一任务分支完成。合入顺序为 **任务/UI 分支 → develop 验证 → master**。

暂停、历史和归档分支不再继续开发。旧会话或旧分支缺少规则时，应先读取当前 `develop` 中的规则文件，再迁移任务，不能直接覆盖原工作目录。规则是开发约束，不自动授权推送或设备操作。

## 当前职责

| 分支 | 用途 | 合入方向 |
| --- | --- | --- |
| master | 已通过本轮离线回归的统一基线；现场部署仍需现场验收 | 接收 develop |
| develop | 唯一集成入口 | 功能、UI 分支先汇入这里 |
| feature/wpf-ui-layout-optimization | 后续界面优化 | develop |
| test/offline-closure-audit-20261008 | 从 develop `95c6cf1` 建立；样品/任务/流程离线回归，修复离子色谱超过500条历史后最新任务遗漏；目录 `.worktrees/offline-closure-audit` | 五个测试项目2238项通过，解决方案构建0警告/错误；待集成，未部署；开盖设备已修复，待现场验收 |
| feature/shinelab-autocurve-validation-20261016 | 从 develop `95c6cf1` 建立；新版 Config.standard 保存/预览、独立 AutoCurve 接口及应答审计；目录 `.worktrees/shinelab-autocurve-validation` | MES812/WPF770通过，构建0警告/错误；新能力默认关闭，未集成/部署，厂家实际行为待验证 |
| feature/sample-detection-standards | 从 develop `11f4fad` 建立的独立功能分支；物料管理中的自定义检测标准、样品人工判定、单条录入与批量方法绑定；工作目录 `.worktrees/sample-detection-standards` | 已快进合入本地 develop `09d6e84`；MES 796 项、WPF 770 项及解决方案构建通过，未部署或推送 |
| fix/workstation-start-barcode-validation | 从develop 7ccff42建立；95d0f27补齐启动前双瓶条码校验及WPF/MES/Adapter联动，独立目录.worktrees/workstation-start-barcode-validation | 已合入develop d1f3c74；完整2072项及集成MES26/WPF72项通过；未部署或启动设备 |
| feature/workstation-import-reconciliation | 从develop 2d73132建立；6248e3f新增独立人工结案审计、只读核对与WPF异常批次入口，原Unknown日志不改 | 已合入develop 5aa3cad；集成回归MES批次51项、WPF核对11项通过；现场结案须显式确认 |
| fix/workstation-text-task-id | 从develop 9210fe8建立；ead3010新增WS文本编号与新导入长纯数字拦截，保留历史回读/重放和Unknown阻断 | 已合入develop f0034ca；不自动部署或解除现场异常批次 |
| feature/digital-twin-single-agv | 用户于2026-09-24授权接管并补提交模型清理成果；8d6c54f删除冗余静态机器人/盒体，配套指纹与测试一并提交 | 已合入develop 9da0526；原证据与规划草稿保留，后续任务从最新develop开始 |
| fix/workstation-empty-task-page | 从 develop e547c94 建立；仅修复任务列表末页201“无数据”的兼容，独立目录 .worktrees/workstation-empty-page；0e70445 已集成 | develop efea896，网关现场切换另行确认 |
| feature/device-current-task-display | 从 develop 4855e14 建立；两设备当前任务显示与 MES 只读设备状态时间字段，c4a6098 已集成 | develop dca6a2c 已通过 WPF 730 / MES 731，后续现场部署另行确认 |
| fix/workstation-current-task-identity | 从 develop f08dd2d 建立；d84ff15 移除开盖分液从历史 Running 行推断当前任务身份，保留运行态待确认提示 | 已快进合入 develop d84ff15；WPF 762 项与解决方案构建通过，未部署或操作设备 |
| feature/workstation-ml-template-validation | 开盖分液 mL、条码 POST、手动结束后续跑；9b639cb 已于 2026-09-24 合入本地 develop | 后续任务以更新后的 develop 为起点 |
| feature/workstation-template-batch | 单任务模板与批量导入；3d80c37 已于 2026-09-24 合入本地 develop | 本轮单位集成修复在 develop，原工作区未被修改 |
| feature/shinelab-dcseq-protocol | 离子色谱通讯、样品关联、托盘与操作员页面；43fb941 已于 2026-09-24 合入本地 develop | 后续任务以更新后的 develop 为起点 |
| feature/user-access-management | 本地登录、三角色权限及隔离客户演示；独立 worktree user-access-management | develop |
| feature/sample-workstation-http-readonly | 已暂停，最终归档源 4fbddc4 | 已集成，后续任务重新从统一基线开始 |
| feature/material-management | 已集成的物料功能历史 | 保留追溯，不继续分叉 |
| feature/physical-admission-v2 | 已集成的准入与物料历史 74cfb88 | 保留追溯，不继续分叉 |
| feature/three-device-sequence | 已提交的数字孪生源 8ef073e 已集成 | 未提交的三设备执行修改仍是待评估草稿 |
| archive/* | 本地整理前的提交点和源码快照 | 不作为发布版本 |

已有旧地图、流程、架构及临时 UI 分支暂不删除，避免丢失工作目录或其他会话的上下文。新工作只以统一基线为起点，不在旧分支上继续累计。

2026-09-20 本会话将干净的独立目录 `D:/Project/Github/Mes-worktrees/sample-workstation-http-readonly` 切换到新任务分支 `feature/workstation-ml-template-validation`，起点为develop `ad2d958`。前处理预开发成果仍完整保留在原分支 `feature/sample-workstation-http-readonly` 的 `a469425`；原分支上关于前处理定向复用的记录随该分支保留，本任务不合并前处理代码。其他会话分支、目录和现场服务不变。

## 已纳入本轮基线

- 开盖分液准备、核验、启动，以及最终的批量体积编辑和用量摘要。
- 历史工作站模板执行兼容路径（独立 legacy 接口和配置）。
- 已验证的数字孪生、只读姿态与全屏功能。
- 物料管理、耗材库存、仓位、扫码追溯、排程预占与现场准入。
- 物料管理成为与运营总览并列的一级导航模块。

物料模块包含“物料与库存”和“样品流转”：前者管理新物料台账中的样品、耗材、批次和库位，后者保留历史样品流转记录。历史样品业务编号与新台账 GUID 没有被强制合并或覆盖；排程兼容两种来源，有库存条码时必须执行库存预占。台账样品准入必须仍由当前任务预占且批次匹配。

## 验证与边界

本轮离线回归：Adapter 451、MES 582、WPF 623、WorkflowContract 74，合计 1,730 项通过。包括新物料台账样品独立准入、历史样品绑定及独立物料导航检查。未运行现场设备动作。

本轮没有合入三设备工作目录中未提交的执行草稿；之前的 archive/three-device-wip-20260920 快照保留其早期状态，原工作目录仍保留后续修改。原 UI 目录的旧数字孪生草稿在同步前保存在定名 stash 中，不覆盖已集成的数字孪生源。

## 2026-09-24 本地集成检查点

用户授权本会话接管 `.worktrees/branch-integration`，按 UI `c377c8a` → 模板/批量 `3d80c37` → 现场修复 `9b639cb` → 离子色谱 `43fb941` 串行合入 develop，合并点 `9e6c5d1`。恢复引用为 `archive/develop-before-ui-workstation-shinelab-20260924`（`ad2d958`）。设备页冲突保留两种工作站模板入口和离子色谱记录列表，不显示无法从 AQ 查询的勾选状态。

集成补齐批量导入厂家文件单位的获取、换算、传递、回读核对及持久化；缺少单位的旧 Adapter 不执行批量导入。详见[本轮集成交接](INTEGRATION-UI-WORKSTATION-SHINELAB-2026-09-24.md)。

本轮仅本地 develop 集成验证；master 仍为 `ad2d958`，未推送、未部署、未替换现场服务或操作设备，也未同步/修改其他会话的源分支工作目录。此前 1,730 项统计属于旧基线，不代表本轮验证。

## 2026-09-24 UI 接管与紧凑设备页

上述集成完成后，用户另行明确授权本会话接管主目录 `D:/Project/Github/Mes` 的 UI 分支。已从 `c377c8a` 快进同步已验证 develop `6aac6d4`，原未跟踪现场资料保留。本轮仅在 UI 分支修改开盖分液设备页布局与展示属性，验证后串行合入 develop；其他功能工作区仍不修改。实现范围及 701 项 WPF 回归记录见[紧凑设备页交接](WORKSTATION-COMPACT-UI-2026-09-24.md)。未自动部署、推送或推进 master。

## 后续工作规则

2026-09-24 数字孪生补集成：用户确认此前模型清理未提交，授权本会话接管 `.worktrees/digital-twin-single-agv`。按限定文件提交8d6c54f并合入develop 9da0526，未合入three-device-sequence的未提交执行草稿。合入后Node24、WPF733项通过（排除会越界写历史产物的2项旧RPA演练），原生WPF/WebView离线位姿冒烟通过。发布新WPF候选包，尚未切换现场窗口/网关、未推送或推进master，详见[本轮模型集成记录](DIGITAL-TWIN-MODEL-INTEGRATION-2026-09-24.md)。

2026-09-24 UI 后续：用户确认删除独立“仪器任务导入”页面，离子色谱导入改为设备状态页按钮弹窗。实施在已授权 UI 主目录，保留开盖分液模板/批量入口；WPF735项通过。详见[导入弹窗交接](ION-IMPORT-DIALOG-2026-09-24.md)。本轮不改 MES、设备协议或运行服务，验证后串行合入 develop。

2026-09-24 用户确认两个设备页增加紧凑当前任务栏。因发现心跳时间与设备状态更新时间不同，本会话在已授权主目录从相同基线建立 `feature/device-current-task-display`，保留本轮未提交改动；UI 分支仍保留原基线。仅扩展只读响应时间字段，不修改设备协议/控制。详见[当前任务展示说明](DEVICE-CURRENT-TASK-UI-2026-09-24.md)。

1. 先确认 develop 与工作分支起点，并检查工作目录是否有未提交文件。
2. UI 优化在 feature/wpf-ui-layout-optimization；功能变更使用从 develop 新建的明确任务分支。
3. 每次合入 develop 同时提交必要测试和说明，禁止按整边覆盖解决业务冲突。
4. master 只从验证后的 develop 正常合并，禁止强推；UI 完成一轮集成后再同步基线。
5. 活跃工作目录不要由其他会话同时合并或切换分支；未提交的现场证据、数据库和生成文件不整体加入 Git。
