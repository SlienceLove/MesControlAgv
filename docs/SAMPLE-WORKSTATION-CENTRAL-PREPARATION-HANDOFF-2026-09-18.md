# 开盖分液工作站中控任务准备交接（2026-09-18）

## 当前可接续状态

- 隔离工作区：`D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`
- 隔离分支：`feature/sample-workstation-http-readonly`
- 最新自动化复审源代码头：`34bb395`（2026-09-20 人工排程布局收尾，独立审查无遗留问题）
- `3c6302b` 补齐 WPF 选择切换同步失效和右侧详情滚动布局；这是自动化测试/编译证据，不是真机或现场验收结论。
- 日常集成目标分支：`feature/wpf-ui-layout-optimization`，不是 `master`；现场验收前不要合并。该日常分支曾见 `201dbb4` 及无关用户改动，真正集成时必须重新检查，不能覆盖。
- 自动化准备链已通过；证据见 [2026-09-18 中控准备链验证](diagnostics/2026-09-18-workstation-central-preparation-validation.md)。本阶段没有真实设备写入或现场进程操作。

当前功能将模板位置和已核对样品显式绑定，生成唯一厂家任务号并保存不可变准备快照。保存准备只读模板且不写设备；显式导入依次执行一次任务导入/精确回读和一次双条码更新，不启动；只有另一次运行准入才能创建运行并由 worker 进行一次带码启动。

## WPF 操作步骤

1. 在“任务排程”选择目标 Job。它应包含唯一固定的 `sample-workstation.execute-existing-task` 节点、唯一匹配设备的 `Scheduled` 排程，并已有当前 `Verified` 样品快照；填写操作者和操作原因。
2. 展开“工作站任务准备”，点“新建模板准备”。WPF 只读获取节点配置的来源任务模板，并列出每条来源、目标模块/X/Y 和体积。
3. 为 1、2 号条码槽分别选择已核对样品；把模板中每个不同的来源模块/X/Y 显式关联到一个槽位。不要按行序或坐标猜测 1/2 号槽，也不要重复录入来源/目标位置。
4. 如有需要，仅编辑目标模块、目标 X/Y 和体积。表格的“来源ID / 条码”应随绑定显示；每个不同模板来源必须且只能绑定一次。
5. 点“保存准备”。成功后状态为 `Prepared / rev N`，服务器生成新的 `VendorTaskNo`；此步没有上传、写码或启动。
6. 复核生成任务、来源→目标行和两个条码后点“显式导入”。成功必须精确回读全部转移行并确认两个条码写入，状态才变成 `Imported`；此步仍不启动。
7. 保持当前核对 revision/hash、准备记录和本地表格无漂移，再执行独立“运行准入”。worker 使用冻结的生成任务号和两个条码只启动一次；现场必须观察 Running 后再观察 Completed/Idle 和租约释放。

单来源模板仍要求两个真实条码槽各选择已核对样品，但只给实际模板来源绑定一个槽。另一个槽显示“Unused barcode slot — not dispensed material”，返回 `IsUsedByTemplate=false`，不得出现在已分液来源列表中。

若已存在准备记录，它就是该 Job 的权威路径，不能退回 legacy taskNo 流程。`Importing` 或 `Unknown`、核对漂移、未保存编辑、加载失败都必须阻止准入；不要自动重试导入。

## HTTP 契约

| 方法 | 路径 | 成功结果与边界 |
| --- | --- | --- |
| `GET` | `/api/workstations/{deviceId}/tasks/{taskNo}/template` | 只读返回文件、SHA256、解析模板和观察时间。 |
| `GET` | `/api/experiment-jobs/{jobId}/workstation-preparations/current` | `200 ExperimentWorkstationPreparation`；没有记录时 `404`。 |
| `POST` | `/api/experiment-jobs/{jobId}/workstation-preparations/prepare` | 请求含元数据、设备、来源任务、核对 revision/hash、可选编辑行及两个瓶位绑定；返回 `201` 和 current Location。服务器拥有生成任务号。 |
| `POST` | `/api/experiment-jobs/{jobId}/workstation-preparations/{preparationId}/import` | 请求只含 `RequestId/Actor/Reason`；返回 `200` 的 `Imported`、`Unknown` 或幂等重放结果。导入不启动。 |
| `POST` | `/api/experiment-jobs/{jobId}/admit` | 独立运行准入；只有当前 `Imported`、核对和运行绑定均未漂移时可接受。 |

`Transfers=null` 表示克隆已捕获模板；提供时必须是非空编辑列表。`BottleBindings` 固定包含 1、2 两槽，每个生成转移使用的不同 `SourceModule/SourceX/SourceY` 必须精确绑定一次。返回 Payload 同时保留来源模板/文件/hash、生成模板/文件/hash、瓶位绑定和有序转移；每条转移包含瓶号、核对行、样品 ID、业务 ID 和条码。

状态：`Prepared → Importing → Imported`；不确定/部分写入转为 `Unknown`。`Revision` 是每 Job 单调递增的当前记录顺序。相同写请求 ID 和相同负载可幂等重放；同一 ID 换动作或负载是冲突。

## 问题码与处理

准备 API 的业务冲突返回 HTTP 409 `{ detail, code }`：

| code | 含义/操作 |
| --- | --- |
| `EXP-WORKSTATION-PREPARATION-VERSION-CONFLICT` | 当前记录、请求 ID、Job 状态或 revision 已变化；刷新后重新核对，不复用陈旧动作。 |
| `EXP-WORKSTATION-PREPARATION-INVALID-BINDING` | 模板来源、瓶位、样品或编辑行绑定不完整/冲突；纠正显式绑定。 |
| `EXP-WORKSTATION-PREPARATION-NOT-IMPORTED` | 当前准备尚未成功导入；禁止准入。 |
| `EXP-WORKSTATION-PREPARATION-IMPORT-UNKNOWN` | 导入或条码写入结果不确定；停止，不自动重试，现场只读对账。 |
| `EXP-WORKSTATION-PREPARATION-DEVICE-BUSY` | 同一 Job 或设备存在 `Importing/Unknown` 或写入冲突；先人工解决不确定状态。 |
| `EXP-WORKSTATION-PREPARATION-RUNTIME-BINDING-INVALID` | 工作流、固定设备、排程、核对或冻结运行绑定漂移；禁止写入/准入。 |

样品快照仍可能返回 `EXP-SAMPLE-VERIFICATION-REQUIRED`、`EXP-SAMPLE-VERIFICATION-INVALIDATED` 及版本冲突。底层工作站错误继续使用 `workstation_*` ProblemDetails 和 `outcomeUnknown`；HTTP/厂家 Code 200 本身不是导入、写码或执行成功证明。

## 真实能力边界

- 来源→目标关系是已生成任务的计划溯源。当前没有逐孔传感或厂家逐行结果，任务 `Completed` 不能解释为每一目标均被单独感知成功。
- `IsUsedByTemplate=false` 的槽只有条码槽身份，没有对应转移，不能声称其样品已分液。
- `Importing`（进程在写入中断）和 `Unknown`（可能部分完成）都故意 fail closed；没有自动重试、自动删除重建、手工 SQL 重置或 reconciliation 写入口。
- 本阶段没有真实上传、条码写入、启动、进程重启或已发布定义修改；自动化使用临时 SQLite 与替身工作站。
- USB 扫码、last-scan 回传、远程停止及厂家逐孔 outcome 不在本阶段能力内。

## 下一会话现场验收清单

### 最终复审与 2026-09-20 收尾

整体复审的任务切换隔离、两个展开面板重叠问题均已修复并复审通过；无剩余 Critical/Important。最后扩大 WPF 相关回归为 99/99（覆盖排程、任务准备、布局和全部 MesClient 相关测试）。

上轮保留的人工放置区行高 Minor 已于 2026-09-20 修复：恢复 `Auto, Auto, *, Auto`，让资源列表在剩余空间内滚动、排程按钮保持自然高度。新增单资源/32 个资源的实际布局测量测试，确认滚动有效、排程与撤排按钮完整可见、时间线仍可见；WPF 相关回归更新为 101/101，隔离 Release 构建 0 警告、0 错误。详见 [布局收尾验证](diagnostics/2026-09-20-workstation-manual-placement-layout.md)。本轮仍未进行真机验收或合并日常分支。

1. 确认最新厂家服务版本、可用设备、代表性模板和实际装载样品；记录版本、时间与操作者。
2. 在排程页登记/确认稳定来源 ID，保存并核对当前样品快照；进入“工作站任务准备”，将模板实际来源显式绑定到 1/2 号条码槽，确认来源→多个目标行，无重复位置录入。
3. 保存准备，记录 Preparation ID/revision、唯一生成任务号、核对 revision/hash、source/generated file hash；确认设备没有动作。
4. 经单独授权点一次“显式导入”。必须逐行匹配厂家回读的全部转移，并取得两个条码确认；仍应没有动作。任何部分、超时或不确定结果立即停止验收，不重试、不替换任务、不改 SQL。
5. 另行授权运行准入；观察恰好一次带码启动、Running、Completed/Idle 和租约释放。对比任务号、双条码、run ID、preparation/payload hash 与持久化证据。
6. 重新打开 Job 读取溯源；确认未使用槽不列为已分液来源，并能按 Job/Preparation/Run 区分被覆盖或复用的位置。记录厂家是否提供逐孔 outcome；在确认前保持“无逐孔证据”的结论。
7. 现场证据完整通过后，才评估合入 `feature/wpf-ui-layout-optimization`；先重新检查其当前头和用户改动，按授权整合，不能整批覆盖。

任何一项不确定都应保持现场未验收状态。旧的已有任务真机证据继续见 [2026-09-16/17 正式流程交接](SAMPLE-WORKSTATION-FORMAL-WORKFLOW-HANDOFF-2026-09-16.md)，不能替代本次生成任务、真实导入回读和带码运行验收。
