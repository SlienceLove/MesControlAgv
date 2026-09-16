# 新会话交接：AGV + AUBO 单次完整流程已通过

更新：2026-09-16。项目：`D:\Project\Github\Mes`，PowerShell / Windows。

## 最新结果（2026-09-16 10:55）

先读[完整自动流程通过记录](docs/physical-acceptance/2026-09-16-agv-aubo-full-flow-passed.md)。新 run `d5ebfe6d-1968-4323-8f0f-91c5c11ebcb4` 从实际 WPF 于 10:50:53 受理，10:54:54 自动 Completed：7/7 节点、7/7 设备操作成功，每节点 attempt=1，四段导航 arrived，三次回原点结束；末段控制权自动释放成功且审计只有一次。WPF 显示完成7、失败/取消/未知均0。

- 当前 AGV 在线、LM1、无任务、`controlOwner=none`；AUBO Automatic、Stopped、Safety Normal。MES 无活动/暂停/Unknown run，无活动/Unknown 导航许可。
- 历史旧返航 `ffa18c1a-09b1-481b-bcca-cfb48901f293` 已按用户批准的新分支双层核销，request `a8199417-086d-4826-bdb1-309d5f615a9b`；关联后续成功返航 `af1c9b93-1eac-4677-bef6-f7a92857647a`，原错误和审计保留。无需再次核销或重发。
- 控制权名称比较与历史核销均已部署；最终相关回归 Adapter141、MES183，共324通过，复核无阻塞项。
- 当前 Adapter PID35740 / 5141、MES PID6152 / 5145，均使用 `artifacts/historical-closure-build/bin/` 对应项目的 `release/`；WPF PID31920，仍用 `artifacts/homing-fix-build/`。下次重新核实 PID，不按旧 PID 操作。
- 原数据仍在 `artifacts/physical-acceptance/homing-20260911-151326/`；停写备份见本次证据目录 `historical-closure-original-db-backup/`。
- 本次证据目录 `artifacts/physical-acceptance/full-flow-reverification-20260916-0954/`，最终通过证据均以 `retest-` 开头，另有 WPF 完成截图和 SHA-256 清单。前一次人工核销 run 的记录另存，不能混淆。
- 通过范围是单次 AGV+AUBO 回原点联调；不代表实际取放料、开盖分液、离子色谱或连续批量验收。其他厂家模块仍按实际交付推进。

代码提交：用户已要求提交当前工作区代码，本次将项目代码、测试、脚本和相关文档（含此前样品及UI改动）纳入同一次提交，具体提交号见当前分支 Git 历史。数据库、日志、构建产物和原始现场证据留在本地；未推送远端。下一步推进厂家模块/最小样品结果关联。不要自动重新启动已经完成的流程；新增现场动作结合用户当次任务和设备现状执行。

以下是本日各阶段历史记录；“待核对”“仍被阻塞”等描述均已由上述最终结果更新。

## 本日修复前状态（历史记录）

先读[本次实际运行及收尾修复记录](docs/physical-acceptance/2026-09-16-agv-aubo-final-release-owner-fix.md)。用户已确认开机、允许验证、切换 AUBO 自动模式，并亲自在 WPF 点击执行；用户反馈中途没有其他问题。

- 本次 run `0627194a-5e3c-4f45-a14d-b09337c86184`：四段 AGV 均 arrived、三次 `回原点.pro` 成功。末段到达 LM1 后，控制权规范化名称比较错误导致自动释放被拒绝，曾进入 Unknown。
- 此名称比较已修复、110 项相关回归通过，MES 已部署到 `artifacts/final-owner-fix-build/bin/MesControlAgv.Mes/release/`，PID 41204 / 5145，继续使用原库。Adapter 41248 / 5141 未重启；WPF 31920 使用 `artifacts/homing-fix-build/`。PID 均需下次重新核实。
- 修复后补做释放，名称检查通过，但被 **9 月 11 日独立返航 `ffa18c1a-09b1-481b-bcca-cfb48901f293` 仍 Unknown** 拦住，未发送释放写入。其源站 LM7、目标 LM1；当前 AGV 在 LM1，现有要求“源站空闲+精确路段404”的核销条件不能直接满足。不能为完成核销而绕过检查或擅自移动设备。
- 本次 run 已按准确到站证据走 `ConfirmedArrivedAndCancel` 人工核销，request `a4c6cdc1-8b67-49d9-9ca2-fc2414b965b2`，最终 Cancelled；原释放失败审计保留。此前暂停 run `39fca078-edfc-4a0f-85c5-44348dcb69ab` 也已正式取消，不能再按下方历史记录当成当前 Paused。
- 最后 AGV 在线、LM1、空闲、控制权仍为 `adapter`，AUBO Stopped。未重跑新流程。动作完成及软件回归通过不等于自动完整收尾通过；尚未提交“现场验证通过版本”。
- 证据：`artifacts/physical-acceptance/full-flow-reverification-20260916-0954/`。MES 停写备份在该目录 `mes-before-fix-backup/`；Adapter 原库未替换。

下一步：先核对历史独立返航的实际证据及允许的审计处置方式，再清理控制权并重新验证自动收尾；不要重放该旧返航，也不要恢复已核销的 run。

### 历史返航只读调查补充

已找到同日另发的新返航 `af1c9b93-1eac-4677-bef6-f7a92857647a`，MES/Adapter 均持久化 arrived，路径同为 LM7→LM6→LM1。2026-09-16 02:24:43 UTC 只读1110确认旧 `ffa18c1a...` 两个精确路段均404，无过滤列表仅有已完成任务；当前位置LM1。原源站核销入口不适用，尚未调用核销或再次释放。

已整理[关联后续成功返航的历史核销设计](docs/superpowers/specs/2026-09-16-historical-return-closure-design.md)，待用户确认这个新增分支后实施。只读证据已存入本次现场目录的 `historical-*.json`；正式动作前需重新取得新鲜证据。

## 2026-09-16 规划记录（以下状态已被上述执行结果更新）

用户要求继续下一阶段规划，并明确：“现在确认路由不会断联了，可以执行流程了。”真实流程验证已获当次用户同意；下文 2026-09-14 的“留待后续”描述属于历史归档。不重复询问网络是否稳定或是否允许执行；实际启动前仍核对当前设备状态、尚未明确的现场监护信息，若需独立返航则按实际起点与 LM1 目标另行确认。

已整理[单次完整流程重新验证计划](docs/superpowers/plans/2026-09-16-agv-aubo-full-flow-reverification-plan.md)。2026-09-16 本机只读核对：5141/5145 仍监听；旧 run `39fca078-edfc-4a0f-85c5-44348dcb69ab` 仍 Paused，第二段机械臂待执行；未发现 WPF 进程。本轮只更新规划，没有处置旧 run、启动新流程或发送设备动作，尚无完整现场验收结果。下一步沿用已批准的最小联调策略，按新计划核对、关闭旧 run 并从 WPF 发起新的七动作流程。

以下为 2026-09-14 归档记录；其中网络诊断证据、原数据保留和禁止重放不确定命令的约束继续适用。

## 先读这里

用户最新决定：**结束网络监听，归档本次证据；真实设备全流程留待后续重新验证。**
当前不要自动恢复旧流程、派发返航或运行机械臂。此文件是交接记录，不是下一次动作授权。

首次接续先读本文件和 [本次诊断归档](docs/physical-acceptance/2026-09-14-amr-watchdog-diagnosis-archive.md)，检查本机服务/原数据库/暂停状态；现场动作等用户当次确认后再做。
不要从头重复本次 35 分钟监听，不要把既有正常等待重新改成 Unknown，也不要重新叠加已移除的运行期重复检查。

## 用户目标与优先级

- P0：四类设备 AGV、AUBO、开盖分液、离子色谱可在 WPF 流程管理中添加、按固定流程真实运行和监控，并回传所需结果/报表。
- 当前验收目标是单次真实执行跑通，不要求连续批量、复杂异常恢复或无人值守。UI 优化靠后。
- 当前现场可继续验证的是 AGV + AUBO。开盖分液与离子色谱的可用厂家模块/接口仍受阻；厂家未交付时继续其他工作，不阻塞任务流，也不能标成已真实接通。
- 样品管理走最小记录/追踪方案，支持扫码和按模板填写 Excel 后人工导入；优先级不高于真实控制。本文件不宣称样品设备联动已验收。
- 机械臂三段均用 `回原点.pro`，用户已经补了结束节点。操作者、监护人均为 `admin`，但历史许可不能直接当新会话授权。

## 本次网络结论

高置信度主因指向 AP Ping 看门狗：目标设为 AGV `192.168.1.2`，周期/启动延迟各 300 秒，最大丢包 3；与原约 956 秒的掉线周期高度吻合。
用户已关闭看门狗。16:21:27–16:49:01 实际观察约 27 分 34 秒，0 断线、0 重连；主动 Ping 656/656、0 丢包；停止持续 Ping 后约 13 分 47 秒仍无断线。
用户提前结束，**不是完成 35 分钟测试，也不是完整真实流程验收通过**。

AP 自身为什么在更早 AGV 开机时仍探测失败，以及固件内部触发细节未独立证明；不要再次无证据归因于 MES、RoboShop、USB 省电或网卡崩溃。
保持看门狗关闭，不重启 AP、不改其他网络设置。现有后台 ETW/持续 Ping/本次被动监听已结束，不需要继续清理或停其他用户程序。

| 网络项 | 本次已核对值（下次作为参考，非实时状态） |
| --- | --- |
| AMR AP | TP-LINK TL-XAP1506GC-PoE/DC 易展版 V2.0；管理 192.168.1.254 |
| BSSID | 9C:47:82:D5:06:3F |
| 本机控制网 | WLAN 3 / COMFAST WiFi6 USB；192.168.1.11；192.168.1.0/24 经接口 6 直连，无默认网关 |
| 办公网 | 主 WLAN / MediaTek / SHINE，与 AMR 分开 |
| AGV-01 | 192.168.1.2 |
| ARM-01 / AUBO | 192.168.1.102 |

用户约 15:09–15:10 给 AGV 断电充电，外接 AP 未断电。下次必须重新确认设备是否上电及当前位置；不要假定仍在 LM2/LM7。

## 当前流程与历史核销

- workflow：`465daad1-a127-4bb2-876d-c973415a1dc3` v1。
- 当前暂停 run：`39fca078-edfc-4a0f-85c5-44348dcb69ab`。
- 15:02:28 从实际 WPF 受理；15:03:09 LM7 到站；15:03:19 第一段回原点完成；15:04:11 LM2 到站。7 节点仅完成 3 节点，第二段机械臂未下发。
- 15:07:07 为排查网络暂停后续节点；最终确认仍 `runtimeStatus=4 (Paused)`。暂停 request：`32ce1d5d-e40f-4c21-96da-cc218eae0ceb`。未发送物理停止/取消。
- 旧 run `84a52310-c2a7-4f4f-859a-01f25474f336` 已核销，不重复核销。
- 旧独立返航 `88a254fb-df7a-495d-877f-d10bb28be76b` 于 15:00:37 已 `manually_closed`；新返航 `990cb01a-d121-45c9-916d-86bdd247d06b` 随后一次派发并到 LM1。这些不是待重发命令。
- 下次计划是重新验证完整单次流程；先核对当前暂停 run 与设备状态，再按已有审计流程处置旧 run。不得清库、手改 DB/epoch、复用旧命令 ID 或重放不确定命令。

## 已有实现与验证，不要重做

- 最小联调等待：保留启动检查；运行观察不重复下载地图、不反复做整套 Ready/授权；按本任务回执等待与完成，短暂读取失败重试读取。
- 保留急停、保护停、任务错配和不确定命令禁止重发。
- 新命令发送前刷新 AGV command channel；不重放任何结果不明的 mutation。
- 独立 Unknown 人工核销已实现 Adapter/MES 双层持久化审计，要求全部精确路段 1110 查询返回 404 并核实源站空闲；opaque active task ID 漏检已修复。
- 2026-09-14 部署前相关测试：Adapter 198/198、MES 156/156，共 354；Release Adapter/MES 0 警告、0 错误。只是当时的相关测试记录，不是本次重新运行的全仓测试。
- 参考：[命令恢复计划及执行记录](docs/superpowers/plans/2026-09-14-agv-command-reconnect-plan.md)、[等待简化设计](docs/superpowers/specs/2026-09-14-commissioning-wait-simplification-design.md)。

## 本机运行入口与数据保留

下表 PID 仅为 2026-09-14 部署/归档参考，下次先核实监听端口、进程身份与实际路径，禁止按旧 PID 杀进程。

| 组件 | 地址 / 历史 PID | 入口 |
| --- | --- | --- |
| Adapter | http://127.0.0.1:5141 / 41248 | `artifacts/command-recovery-build/bin/MesControlAgv.Adapter/release/MesControlAgv.Adapter.dll` |
| MES | http://127.0.0.1:5145 / 47356 | `artifacts/command-recovery-build/bin/MesControlAgv.Mes/release/MesControlAgv.Mes.dll` |
| WPF | 40532（沿用已有进程） | 先检查现有窗口与实际可执行路径，不假定用了本次 Adapter/MES 构建目录 |

原数据目录：`artifacts/physical-acceptance/homing-20260911-151326/`。
保留 `adapter.db`、`mes.db`、各自 WAL/SHM 以及 `wpf-workflows.json`；不得用空数据库替代以绕过未完成流程。
不要在仍有写入者时只拷主 db 文件充当一致性备份；既有停写备份见 `artifacts/physical-acceptance/command-recovery-20260914-1500/original-db-backup/`。

部署参考：`artifacts/physical-acceptance/command-recovery-20260914-1500/deploy.ps1`。
**不要直接重跑该脚本或同目录 `return-once.ps1`**：它们带旧 PID、旧 run、站点前置条件和现场控制动作，不是通用启动入口。
若服务需要重启，先检查当前运行状态，按当次情况整理已有启动参数，不自动派发。
当前部署并非纯只读：流程 AGV/AUBO worker 已启用，`AutoAuthorizeFromRunRequest=true`；普通 `enableAutomaticDispatch=false` 不等于流程 worker 被禁用。归档状态依赖旧 run 保持 Paused，不要创建/恢复 run 来“试接口”。

## 下一会话按这个顺序接续

1. 只读检查 `git status`、当前进程/5141/5145、原数据文件与上述暂停 run 的 MES 投影；服务未启动则先报告状态，不直接执行旧部署脚本。必要时先读 `GET http://127.0.0.1:5145/api/workflow-runs/39fca078-edfc-4a0f-85c5-44348dcb69ab`，此查询本身不派发。
2. 向用户确认 AGV/AUBO 已上电、现场有人监护，且准备进行本次重新验证。AGV 尚在充电或厂家模块未交付时，不催动作、不恢复监听，可继续用户指定的离线工作。
3. 获得当次现场确认后，进行一次启动前只读核对：AMR/AP 连通、看门狗仍关闭、AGV 位置/空闲与控制归属、AUBO 自动模式/程序停止/回原点程序已含结束节点。沿用已批准的最小联调策略，不恢复运行期重复地图/Ready/授权检查。
4. 依据真实状态和已有恢复入口处置旧暂停 run，重新从 WPF 发起一次有新 RunId/回执关联的流程；如需先返航，向用户说明实际起点与目标后另行确认，不复用旧返航命令。
5. 验证 7 节点的到站、机械臂执行/结束、状态回传与结果记录；AGV 到站后应推进机械臂，正常等待/暂时读失败不能直接变成 Unknown。仅命令本身结果不明时走既有人工处置，不重发。
6. 只有本次全流程真实成功且无未处理 bug，才按用户之前要求评估“最新现场验证版本”提交。提交前核对测试和 diff，保留用户无关改动；网络诊断通过不能替代此条件。

## 仓库与证据入口

- 分支（归档时）：`feature/wpf-ui-layout-optimization`；HEAD `0e785b4`（`docs: record Rike Config timeout and pause`）。分支名称不代表当前优先做 UI。
- 工作区已有大量未提交修改；本次恢复修复尚未作为现场验证版本提交。不得重置或批量提交整个工作区。
- [网络诊断正式归档](docs/physical-acceptance/2026-09-14-amr-watchdog-diagnosis-archive.md)。
- [原始证据说明](artifacts/physical-acceptance/network-investigation-20260914-1507/README.md)、[最终结果](artifacts/physical-acceptance/network-investigation-20260914-1507/watchdog-off-final.json)、[SHA-256 清单](artifacts/physical-acceptance/network-investigation-20260914-1507/evidence-manifest.json)。
- 三张关键截图已原样复制到该证据目录的 `screenshots/`，不再依赖微信缓存；没有归档密码或带密码的标签照片。
- 本次只完成诊断/证据/交接归档，没有进行后续真实流程、重新构建或发布验证版本。
