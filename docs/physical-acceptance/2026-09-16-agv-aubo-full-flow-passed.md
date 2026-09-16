# 2026-09-16 AGV + AUBO 单次完整流程通过

## 结果

实际 WPF 发起的新流程于北京时间 **10:50:53–10:54:54** 自动完成，约 4 分 1 秒。

- **7/7 节点、7/7 设备操作成功**，每节点 attempt=1。
- 四段 AGV 导航均有匹配的 arrived 回执，三次 AUBO `回原点.pro` 均完成。
- 最后一段到达 LM1 后，自动释放控制权成功；流程终态 **Completed**。
- WPF 显示“已完成”“完成7、失败0、取消0、未知0”和“AGV 控制权已释放”。
- 最终回读：AGV 在线、LM1、无当前任务、`controlOwner=none`；AUBO Automatic、Stopped、Safety Normal。
- MES 只读核对：活动/暂停/Unknown 流程 0，活动/Unknown 导航许可 0。

本结论限定于 AGV + AUBO 的单次回原点联调流程，不代表实际取放料、开盖分液或离子色谱已验收，也不代表连续批量运行已验证。

## 运行标识及节点结果

- workflow：`465daad1-a127-4bb2-876d-c973415a1dc3` v1。
- RunId：`d5ebfe6d-1968-4323-8f0f-91c5c11ebcb4`。
- requestId：`45fb8106-4910-46a4-871b-5eaa26ddf9a9`。
- correlationId：`wpf-physical-batch-0c7736a07eac417fbebb5e23c18294b4`。
- 从实际 WPF 的“一键现场执行”及确认窗口提交一次；未用直接 execute API 代替 WPF。

| 动作 | 节点完成时间（北京时间） |
| --- | --- |
| LM1 → LM7 | 10:51:34 |
| 第一次回原点 | 10:51:43 |
| LM7 → LM6 → LM2 | 10:52:34 |
| 第二次回原点 | 10:52:44 |
| LM2 → LM1 → LM7 | 10:53:58 |
| 第三次回原点 | 10:54:08 |
| LM7 → LM6 → LM1，自动释放控制权 | 10:54:54 |

该 run 只有一条最终释放安全动作：`d69aa6f9-143b-459e-a34b-e7207054d9e0`，请求 `27d9858a-95b2-f25a-9332-a21b30a18093`，状态 Succeeded。`WorkflowFinalMoveRelease` 审计同样为 Succeeded，`terminalReason=workflow_final_move_completed`。

## 此前两个阻塞的处置

1. 控制权名称比较已修复：快照中的 `adapter` 是规范化归属，不能与设备昵称 `MesControlAgv.Adapter` 直接比较。先前 run `0627194a-5e3c-4f45-a14d-b09337c86184` 的动作完成、人工核销与失败审计保留，详见[前段运行与修复记录](2026-09-16-agv-aubo-final-release-owner-fix.md)。本次通过使用新的 RunId。
2. 9 月 11 日独立旧返航 `ffa18c1a-09b1-481b-bcca-cfb48901f293` 已按用户批准的[历史核销方案](../superpowers/specs/2026-09-16-historical-return-closure-design.md)在两层持久化为 `manually_closed`。

历史核销关联同日后续成功返航 `af1c9b93-1eac-4677-bef6-f7a92857647a`，核对同车、时间、地图及完整路径，并实时取得两个精确路段404、目标LM1空闲和地图身份匹配的证据。

- 核销 request：`a8199417-086d-4826-bdb1-309d5f615a9b`；北京时间 10:48:09 完成。
- 原 `dispatch_not_confirmed_by_1110` 错误、路线和历史审计全部保留。核销未发送导航、取消或机械臂命令。
- 随后的独立收尾释放 request：`e96bc8ec-b0a5-4ebd-af8d-1b470eab49c5`，安全动作 `fc30aca3-b822-4dcc-a21a-d5de36065380`，10:48:38 成功，回读 `none`。
- 以上人工核销/独立释放与 10:50 开始的新完整自动流程分开记录。

## 实现、测试与复核

新增明确选择的“关联后续成功返航”分支；原源站分支保留。MES 校验后续已消费许可的独立到站记录；Adapter 独立校验自己的到站记录、全部精确路段、实时空闲/位置与当前地图。两个分支均不允许不明的实时任务状态作为空闲证据。

新增可选字段在为空时不参与 JSON 序列化，保持旧请求审计兼容；重复请求返回已持久化结果，不重放设备动作。地图完整目录的原观测时间保留，另记本次地图身份检查时间。

独立代码复核发现的当前任务状态不严谨、旧结果读取顺序、操作者长度不一致均已修复并补测试；复核后无阻塞项。复核与反例记录见证据目录 `historical-closure-review.md`。

最终 Release 相关回归：**Adapter 141 + MES 183 = 324 项通过，0 失败、0 跳过**；构建无警告/错误。包含旧源站规则、新目的站分支、错误关联/地图/活动任务拒绝、请求兼容、重启/丢回执/并发、命令不重放和流程自动收尾。本轮没有重跑全仓测试。

## 当前部署与数据

| 组件 | 当前 PID / 地址 | 构建入口 |
| --- | --- | --- |
| Adapter | 35740 / 5141 | `artifacts/historical-closure-build/bin/MesControlAgv.Adapter/release/` |
| MES | 6152 / 5145 | `artifacts/historical-closure-build/bin/MesControlAgv.Mes/release/` |
| WPF | 31920 | `artifacts/homing-fix-build/bin/MesControlAgv.Wpf/release/` |

PID 仅为本次观测。继续使用 `artifacts/physical-acceptance/homing-20260911-151326/` 原数据库和流程文件。部署前停止两名数据库写入者，保存各数据库与当时存在的 WAL/SHM；没有清库、手改 DB/epoch 或复用旧运动命令 ID。

完整停写备份：本次证据目录 `historical-closure-original-db-backup/`。部署参数、二进制哈希与源码哈希分别保存在 `historical-*-environment.json`、`historical-binaries.json`、`historical-source-manifest.json`。

## 证据与交接

证据目录：`artifacts/physical-acceptance/full-flow-reverification-20260916-0954/`。

- `retest-verification.json`：对终态、七节点/七操作、四到站、一次自动释放及零活动记录的核验结果。
- `retest-final-{run,nodes,device-operations,field-navigation-acceptances,timeline}.json`：最终 MES 投影。
- `retest-final-agv.json`、`retest-final-aubo.json`：设备最终状态。
- `wpf-retest-completed.png`：实际 WPF 完成画面。
- `historical-close-request.json`、`historical-close-result.json`、`historical-adapter-durable-closure.json`、`historical-acceptance-closed.json`：旧返航核销及双层审计。
- `tests/historical-adapter-final.trx`、`tests/historical-mes-final.trx`：最终相关回归。
- `acceptance-evidence-manifest.json`：选定静态证据 SHA-256，不包含持续变化的日志和数据库。

现场通过条件已满足。用户随后要求提交当前工作区代码；项目代码、测试、脚本和相关文档（含此前样品模块与UI改动）纳入提交，运行数据库、日志、构建产物和原始现场证据留在本地。此提交不扩大上述现场验收范围。后续设备接入仍按厂家模块实际交付推进。
