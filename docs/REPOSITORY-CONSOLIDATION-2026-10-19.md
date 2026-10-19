# 仓库清理与分支整合

日期：2026-10-19。用户指定本会话接管本次串行集成。整理工作在独立目录 `.worktrees/repository-consolidation-20261019`、分支 `maintenance/repository-consolidation-20261019` 完成；原开发工作区及现场服务不变。

## 基线与范围

整理前本地 develop 和 UI 均为 `95c6cf1`，master 为 `ad2d958`。读取远端成功：origin/develop 和 origin/master 仍为 `ad2d958`，origin/UI 为 `c377c8a`。本地 develop 比远端多 62 个提交。

本轮只整合已提交、具备离线验证交接的成果，不整体提交其他会话未完成的代码，不重置或重放现场实验。原始点位、Lua/PRO、res、数据库、现场日志及 stash 均保留。

## 整合清单

| 来源 | 源提交 | 整合内容 |
| --- | --- | --- |
| test/offline-closure-audit-20261008 | e693333 | 离子色谱超过 500 条历史任务时的最新任务排序修复 |
| feature/shinelab-autocurve-validation-20261016 | a17d8e1 | standard 和 AutoCurve 协议、持久化及应答记录；新能力默认关闭 |
| feature/user-access-management | 66d386f | 本地登录、固定三角色、账号管理、会话失效及业务身份保护；Access 仍需显式启用 |
| feature/lab-ai-assistant | 32ffd69 | 本地 AI 流程草稿和报告；不自动发布、启动设备或加载现场模型 |

采用三方合并，不以旧任务分支的整树覆盖新版 develop。权限分支虽然从较早基线分出，但只合入共同祖先之后的有效改动；原有物料、设备页、导航和数字孪生行为保留。

主窗口冲突处理：设备页只隐藏原来的连接/AGV 状态徽标，登录账号、用户管理、改密和退出入口保持可见；启动页跳转后继续执行角色权限校验。新增 Viewer/Admin 两项设备页会话控件回归。

AI 与权限模块首次组合时，新 AI/报表 POST 接口按服务端既有规则属于管理员操作。界面同步限制对应两页，避免操作员看到可操作入口却得到 403；未扩大服务端授权。Access 未启用的旧现场实例行为不变。

## 保留但不合入的分支

| 分支 | 原因及下一步 |
| --- | --- |
| feature/visitor-full-demo-20261012 | 已提交 HEAD `9b3cd14` 缺少 MES 模板路由和 worker 注册；现场目录另有 54 项已跟踪未提交修改。本次不把旧提交当作最新可运行版本，后续由现场会话按实际差异提交并验证 |
| feature/visitor-demo-catalog-20261012 和 feature/visitor-demo-devices-20261012 | 与 full-demo 是重复功能的独立提交链，不再叠加合入；目录和提交保留，作为来源参考 |
| feature/vision-docking-calibration-20261016 | 本会话的视觉纯计算和偏移适配仍未提交、尚未完成现场接入；原目录保留 |
| test/aubo-workstation-tray-actions | 用户明确指定为机械臂测试分支，不合主线；现场点位与程序保留 |
| feature/three-device-sequence | 已提交数字孪生部分原已集成；20 项已跟踪未提交修改不纳入本轮 |
| feature/sample-workstation-http-readonly | 旧前处理预开发仍按既有规则暂停，不因已提交就自动启用或合入 |
| worktree-agv-mes-mvp-wpf 和 archive 下的 WIP | 旧架构及历史未验收快照保留，不作为新基线 |

## 本地冗余清理与恢复

移出仓库而未永久删除：

| 原路径（仓库根目录下） | 字节数 |
| --- | ---: |
| .tmp_modeldll_inspect | 137001787 |
| _tmp_downstream | 247570 |
| _tmp_downstream.zip | 25449 |

合计 137274806 字节，约 131 MiB。恢复目录为 `D:/Project/Github/Mes-repository-archive/20261019/temporary-files`，三个项目均保留原名；恢复时先确认原位置没有新文件，逐项移回，不能覆盖新成果。移出只减少仓库内杂项，不等于释放了整块磁盘空间。

已清理三条目标目录不存在的 worktree 登记：`Mes-baseline-9bb2d69-bf161822`、`Mes-physical-build-20260812`、`Mes-physical-build-20260812-r2`。没有删除实际工作目录；其 HEAD 分别由 `archive/20261019/stale-worktree-baseline`、`stale-worktree-physical-build`、`stale-worktree-physical-build-r2` 标签保留。

已删除以下 9 个本地旧分支名：

- codex-ui-only-push
- feat/experiment-flow-nodify
- feat/ion-chromatography-direct-control-validation
- feat/robot-arm-vision-integration
- feat/wpf-map-export
- feat/wpf-map-observability
- feature/3d-simulation-dashboard
- feature/device-current-task-display
- feature/material-management

删除前均确认提交完整包含于 develop，且没有 worktree 占用；每个分支的原 HEAD 已保存为 `archive/20261019/<原分支名>` 标签。可用 `git branch <原分支名> <对应标签>` 恢复。远端分支没有删除。

`docs/experiment-workflow-architecture-plan` 虽已包含于本地 develop，但其远端跟踪分支未包含最后一个本地提交。Git 普通删除拒绝后未强删，改名为 `archive/experiment-workflow-architecture-20261019`，原提交和跟踪关系保留。

本轮原始分支、worktree 状态和 stash 清单在仓库外 `D:/Project/Github/Mes-repository-archive/20261019/before.json`。主分支、开发分支和 UI 原基线另有 `archive/20261019/*-before-consolidation` 标签；清单不是未提交文件的备份，未提交成果仍保留在各原工作区。

## 验证与发布状态

整合代码的解决方案构建通过，0 警告、0 错误。最终串行回归如下，不重复计入复跑次数：

| 项目 | 通过 | 失败 | 跳过 |
| --- | ---: | ---: | ---: |
| MES | 855 | 0 | 0 |
| WPF | 792 | 0 | 0 |
| Adapter | 533 | 0 | 0 |
| WorkflowContract | 89 | 0 | 0 |
| Domain | 48 | 0 | 0 |
| Reporting | 31 | 0 | 0 |
| InstrumentGateway | 83 | 0 | 0 |
| Simulator | 5 | 0 | 0 |
| E2E | 22 | 0 | 5 |
| 合计 | 2458 | 0 | 5 |

5 项跳过来自既有 `CompoundTaskIntegrationTests` 的完整 AGV 模拟环境要求，本轮未增加跳过。TRX 在整理目录 `artifacts/repository-consolidation-20261019`；WPF/E2E 以 `*.final.trx` 为最终结果，首次运行记录仍保留。

首次端到端回归暴露两处陈旧的地图只读请求顺序断言。基线已有的 `TcpAgvClient` 先查询 1300/1302 验证地图身份，再读 1301 站点目录；旧用例仍预期 1300/1301/1302。该逻辑来自既有 `201dbb4`，本次未改 Adapter/现场预检脚本。现仅同步两个用例的精确顺序，保留请求数量、禁止写入、禁止获取控制、进程清理等全部断言，没有改成忽略顺序或放宽设备准入。

本轮不重启现场 MES/Adapter/WPF，不发送设备动作，不恢复等待厂家修复的开盖实验。AutoCurve 厂家行为、实际模型效果和视觉补偿现场验证仍按各自任务推进。

本地整合检查点使用 `integration/repository-consolidation-20261019` 标签定位，按整理分支 → develop → master 的顺序快进，并同步 UI 到同一检查点。只推进 Git 基线，不将构建产物覆盖到现场服务。GitHub 推送须另行明确确认；没有强推或删除远端分支。

## 后续分支规则

develop 继续是唯一集成入口；master 只接受验证后的 develop；UI 在验证后同步同一基线。已合入的旧任务分支保留来源，不再继续分叉新功能。

新功能从最新 develop 建立明确任务分支和独立 worktree；已有活跃任务先由所属会话整理自己的未提交改动，再处理基线同步。禁止对有未提交现场成果的工作区批量清理、切分支或强制同步。
