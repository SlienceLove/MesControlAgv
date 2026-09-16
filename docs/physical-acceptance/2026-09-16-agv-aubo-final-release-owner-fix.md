# 2026-09-16 AGV + AUBO 实际运行与末段释放修复

后续更新：历史返航已双层核销，新的 WPF run 已于 10:54:54 完整自动通过，详见[最终通过记录](2026-09-16-agv-aubo-full-flow-passed.md)。下文保留前一次运行的原始结果与当时阻塞，不代表当前状态。

## 结论

用户从实际 WPF 确认执行，四段 AGV 导航均到站，三次 AUBO `回原点.pro` 均结束。用户确认运行中途没有其他问题。

最终 LM1 已到站，但自动释放控制权因软件比较了不同含义的名称而被拒绝，流程进入 Unknown。该比较已修复并部署到 MES，相关回归 110/110 通过。

修复后通过现有 MES 接口补做释放，名称检查已通过，随后被 **2026-09-11 遗留的独立 Unknown 返航** 拦住；释放命令未下发。该历史任务仍需另行核对。本次流程通过已有“确认到站并结束”入口人工核销，最终为 Cancelled，不能写成自动完整验收通过。

## 本次运行

- workflow：`465daad1-a127-4bb2-876d-c973415a1dc3` v1。
- RunId：`0627194a-5e3c-4f45-a14d-b09337c86184`。
- requestId：`23954a71-78c4-4d15-a2b9-837da1dbec17`。
- 关联：`wpf-physical-batch-e270ba78872846c68e9d9c0c3922eda0`。
- 操作者/监护字段：`admin`。用户已确认开机，并在回读 Manual 后切换 AUBO 为 Automatic；实际回读程序 Stopped、安全 Normal。
- WPF PID 31920；使用 `artifacts/homing-fix-build/bin/MesControlAgv.Wpf/release/` 和原 `wpf-workflows.json`。
- 10:02:39（北京时间）用户点击 WPF 确认后受理。助手的前次自动确认在前台检查处终止，未发送鼠标点击；`execution-confirmation-click.marker` 仅记录该次准备尝试，不能当成派发证据。

| 动作 | 结果时间（北京时间） | 证据 |
| --- | --- | --- |
| LM1 → LM7 | 10:03:23 节点完成 | 导航 arrived |
| 第一段回原点 | 10:03:33 | 机械臂节点 Succeeded |
| LM7 → LM2 | 10:04:23 节点完成 | 导航 arrived |
| 第二段回原点 | 10:04:32 | 机械臂节点 Succeeded |
| LM2 → LM1 → LM7 | 10:05:45 节点完成 | 导航 arrived |
| 第三段回原点 | 10:05:54 | 机械臂节点 Succeeded |
| LM7 → LM6 → LM1 | 10:06:32 导航 arrived | 最终许可 `ddd485ca-b1fa-4898-a432-ba021fdd1a56` |

七节点均为 attempt 1。最后节点于 10:06:41 因释放检查拒绝进入 Unknown；后来按已持久化到站证据人工核销，不混淆原始自动结果和人工结果。

旧暂停 run `39fca078-edfc-4a0f-85c5-44348dcb69ab` 已于启动前通过正式取消入口结束，request `3df4b479-4aea-4048-97d3-af89ca36b7c6`，保留三节点完成记录。

## 根因与最小修复

`TcpAgvClient.QueryControlAsync` 在控制器昵称与本客户端昵称一致时，把快照归属规范化为 `adapter`。现场配置的控制器昵称为 `MesControlAgv.Adapter`。

`PhysicalSafetyActionService.CheckAgvPreflightAsync` 原来将规范化快照与昵称直接比较，导致自己的控制权被误判为不匹配。现在只认可快照中的 `adapter` 角色，与现有设备就绪检查一致；配置昵称保留用于诊断。其他归属、未持有控制、活动任务、历史 Unknown、epoch 检查和单次持久化释放逻辑均保留。

改动范围：

- `src/MesControlAgv.Mes/Services/PhysicalSafetyActionService.cs`：修正归属比较及诊断说明。
- `tests/MesControlAgv.Mes.Tests/PhysicalSafetyActionServiceTests.cs`：现场昵称配置下的规范化归属、其他归属拒绝、持久化结果及重复请求不重复释放。
- `tests/MesControlAgv.Mes.Tests/WorkflowFieldNavigationWorkerTests.cs`：加入现场昵称配置，覆盖最终到站后释放一次、流程 Completed 和审计成功。

修复前新增 7 个参数用例中 3 个失败、4 个通过，复现误拒绝自己的归属和误接受未经规范化昵称；修复后最终相关回归 **110 通过、0 失败、0 跳过**。包含安全动作、导航 worker、到站顺序、联调等待、流程控制及离线回原点循环。Release 构建无警告/错误；本轮没有运行全仓测试。

## 部署与实际收尾结果

- 只重启 MES，保留 Adapter/WPF。停止 MES 写入后，将原 `mes.db` 与当时存在的 WAL/SHM 保存到证据目录 `mes-before-fix-backup/`。
- MES 现为 PID **41204**，5145，入口 `artifacts/final-owner-fix-build/bin/MesControlAgv.Mes/release/MesControlAgv.Mes.dll`；仍使用原数据库。PID 为本次记录，下次先核实进程身份。
- Adapter 仍为 PID 41248 / 5141，入口为 `artifacts/command-recovery-build/`。MES 启动时保持 Unknown，不重放历史命令。
- 原自动释放安全动作 `2776888c-c433-416a-8ef1-ef63902a6522` 为 Rejected，在物理写入前拒绝。
- 修复后单次释放请求 `1dc29455-a20c-458d-9091-bb3a0a9d4585`，安全动作 `f01bffd2-243d-421c-9eeb-7a206831a261`，仍为 Rejected，原因变为 `AGV has an active field-navigation acceptance.`，同样未发送释放写入。
- 阻塞来源：独立返航 `ffa18c1a-09b1-481b-bcca-cfb48901f293`，2026-09-11 创建，LM7 → LM6 → LM1，`dispatch_not_confirmed_by_1110`，仍 Unknown。它不是本次新任务，也不是交接中已核销的另两条返航。
- 现有独立核销条件要求源站 LM7 空闲及全部精确路段 1110 返回 404；本次 AGV 在 LM1，不能直接套用该入口。没有绕过检查、手改数据库或为凑核销条件移动 AGV。
- 本次运行核销 request `a4c6cdc1-8b67-49d9-9ca2-fc2414b965b2`：按最终节点的准确到站证据走 `ConfirmedArrivedAndCancel`。终态 Cancelled，保留原始释放失败审计；此操作不派发导航、机械臂或控制权释放。

最后状态：AGV 在线、LM1、无当前任务、控制权仍为 `adapter`；AUBO 程序 Stopped。后续先处理历史独立返航记录，再验证完整自动收尾。未提交或宣称“现场验证通过版本”。

## 证据

目录：`artifacts/physical-acceptance/full-flow-reverification-20260916-0954/`。

关键文件：`session.json`、`workflow-version.json`、运行快照、`blocked-*.json`、`final-*.json`、`release-after-fix-journal.json`、`other-active-acceptance.json`、`arrival-resolution-*.json`、WPF 截图、`verified-binaries.json`、`tests/owner-normalization-red.trx`、`tests/owner-normalization-final.trx`。

网络稳定依据用户本次确认；本轮未重做长时间监听。三次回原点不代表实际取放料、开盖分液或离子色谱验收。
