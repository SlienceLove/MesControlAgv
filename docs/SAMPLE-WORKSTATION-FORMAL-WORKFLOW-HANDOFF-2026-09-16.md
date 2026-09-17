# 开盖分液正式工作流交接（2026-09-16）

## 当前状态

- 隔离工作区：`D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`
- 分支：`feature/sample-workstation-http-readonly`
- 设计提交：`f615e2a`
- 计划提交：`ec9327a`
- 实现提交：`9d5f10f`
- 单次发令与恢复审查修复：`9c2a4f6`
- 设备大小写排他与新鲜持有者保护：`bda8f79`
- 正式流程交接：`62029a4`
- 隐藏无关 Move 验收面板：`b136427`
- WPF 人工确认设计：`63ac494`
- WPF 人工确认实现：`c4cae1c`
- 人工确认目标与状态复核加固：`e19eb26`
- 人工门禁改为可选设计：`a79d847`
- 取消工作站强制人工前置：`c47dd5e`
- 保留旧校验码兼容符号：`ac28f55`
- 主工作区 `D:\Project\Github\Mes` 未被覆盖、重置或合并。

默认隐藏的 WPF 联调入口、正式工作流节点以及 WPF 人工确认入口均已通过真机闭环。正式运行已观察到人工确认、单次启动、Running 和 Completed 全链路证据。

## 已实现能力

- 节点：`sample-workstation.execute-existing-task`
- 能力：`sample-workstation.start-existing-task`
- 设备族：`sample-workstation`
- 配置：`deviceId + taskNo`
- 可选前置节点：`core.manual-confirmation`（不再强制）

发布校验不再强制人工确认节点直接进入工作站节点；流程设计者仍可按需保留人工确认。工作站节点只启动厂家已有任务，不调用远程初始化、任务创建、轨迹写入、暂停或停止。厂家新版已验证会在冷态首次任务中自动初始化并继续实验。

MES worker 默认关闭。启用后先只读检查设备 Idle/0、错误码0、指定任务非 Running，再认领节点并发送一次启动请求。收到厂家确认后，必须先观察本轮 Running，再以任务 Completed、设备 Idle/0、错误码0三项一致完成节点。

启动请求不自动重试。旧 Completed 不会完成本轮节点；不确定结果进入 Unknown 并停止后续流程。MES 重启时，仅持久化为 Running 的操作允许只读恢复；只有 Prepared/StartPending/Accepted 时不重发启动，新鲜记录先等待当前发令租约，启动观察窗口过期后转 Unknown。

## 启用条件

Adapter：

```json
{
  "Devices": {
    "SampleWorkstation": {
      "DeviceId": "SAMPLE-WORKSTATION-01",
      "EquipmentNo": "CYC-001-1000",
      "BaseUrl": "http://192.168.200.157:8082/Service/",
      "Enabled": true,
      "ControlEnabled": true
    }
  }
}
```

MES Profile：

```json
{
  "deviceId": "SAMPLE-WORKSTATION-01",
  "deviceFamily": "sample-workstation",
  "capabilityIds": ["sample-workstation.start-existing-task"],
  "enabled": true,
  "controlEnabled": true
}
```

MES worker：

```json
{
  "WorkflowSampleWorkstationWorker": {
    "Enabled": true,
    "PollIntervalMs": 1000,
    "ReadinessRetryIntervalMs": 2000,
    "StartObservationTimeoutMs": 30000,
    "CompletionTimeoutMs": 600000
  }
}
```

未准备真机验收时保持 `Enabled=false`。

## 已完成的正式节点真机验收

- 流程：`开盖分液正式验收-20260916`
- Workflow ID：`5d409b9b-45fc-46e9-bd4f-46868ddefbcf`
- 版本：`1`
- 结构：开始 → 人工确认 → 执行已有任务 `TEST-001` → 结束

首次正式成功执行：

- Execution ID：`0913d6ed-d70f-4e0c-97f1-1c39bedd91c2`
- 结果：Completed
- 设备操作：单次启动，Running 后 Completed

WPF 人工确认真机闭环：

- Execution ID：`ab5ad733-ad1a-4db3-ac4a-696bc141b143`
- 人工节点执行 ID：`96b784ea-9f2c-4c40-a071-75e246cdc28c`
- 工作站节点执行 ID：`379f5c2c-8d1b-4162-9854-1c741ff369a1`
- 设备操作 ID：`07851dd7-ff46-71ef-9969-4c14f5b81428`
- WPF 操作者：`admin`
- 2026-09-16 14:57:40（本地时间）完成 WPF 人工确认。
- 14:57:41 worker 认领工作站节点，只创建 1 条设备操作。
- 14:57:45 观察到 Running；设备 `Running/1`、任务“正在运行”、厂家状态码 `3 / ExperimentStarted`。
- 15:00:13 观察到任务 Completed、设备 `Idle/0`、厂家状态码 `0 / TaskCompleted`。
- 15:00:14 WPF/MES 工作流为 Completed；人工节点和工作站节点均为 Succeeded。

第一次厂家新版验证中，现场未观察到明确初始化动作，因此没有据此下结论。随后重启整机并确认厂家主程序未初始化，完成了独立冷态验证。

厂家服务更新后曾短暂返回 503；约一分钟后 `192.168.200.157:8082` 恢复监听并返回正常状态。流程在服务恢复且设备 Idle 后才人工放行。

## 已完成的冷态自动初始化验证

- Execution ID：`2614a47e-baaa-471c-9b63-580c43f0540a`
- Device Operation ID：`883498e1-1a65-313b-7e50-0e868cec78b2`
- 现场条件：整机重启，厂家程序已监听，未手动初始化。
- 人工确认备注：`冷态未初始化，验证厂家新版是否自动初始化并启动`
- 中控启动请求：1 次。
- 15:25:12 厂家主程序现场截图显示“正在进行设备初始化”。
- 随后厂家程序自动继续实验；接口进入设备 `Running/1`、任务“正在运行”、状态码 `3 / ExperimentStarted`。
- 15:27:42 设备回到 `Idle/0`、任务 Completed、错误码0；15:27:43 工作流 Completed。

结论：厂家新版已验证支持“冷态首次任务自动初始化 → 自动继续实验”。中控不增加远程初始化调用，开盖分液流程不再强制人工初始化确认。

## 已发布无人工门禁版本

- Workflow ID：`5d409b9b-45fc-46e9-bd4f-46868ddefbcf`
- Version：`2`
- 名称：`开盖分液正式流程-20260916`
- 结构：开始 → 开盖分液执行 `TEST-001` → 结束
- 状态：已校验、已发布
- 人工确认：不包含；v1 保持不变
- 发布警告：工作站 failure/timeout 未接线，仍为提示性警告；运行失败或 Unknown 会停止后续流程。

v2 发布后先执行 Dry Run `4227d813-051c-4ee1-bdc9-e8b364549b68`，设备操作数为0，WPF 正确显示3个节点且无人工确认操作条。

2026-09-17 完成 v2 真机闭环：

- Execution ID：`9dda26e7-7f5c-4199-a27a-b1341bf9d5de`
- Request ID：`fb25eb4a-fae7-4bc6-bb15-a99561e91dff`
- Node Execution ID：`92f0e7c1-902d-46a7-b3e0-1d56050f314b`
- Device Operation ID：`d206797b-e64b-4086-dbc5-c9c7c2aba5ed`
- 08:20:25 接受 v2 执行；没有人工节点等待。
- 08:20:26 worker 创建唯一设备操作并开始单次启动。
- 08:20:50 观察到 Running：设备 Running/1、任务 Running、状态码3。
- 08:23:18 观察到设备 Idle/0、任务 Completed、错误码0。
- 08:23:19 工作流终态 Completed；节点和设备操作均 Succeeded。
- 全程设备操作数为1，没有重复启动。

结论：无人工门禁的 v2 已通过真实设备闭环，可以作为后续完整实验流程中的开盖分液节点使用。

## 验证结果

- `MesControlAgv.WorkflowContract.Tests`：73/73 通过。
- `MesControlAgv.Mes.Tests`：320/320 通过。
- 工作站相关 `MesControlAgv.Adapter.Tests`：38/38 通过。
- 正式工作站节点初始验证时 `MesControlAgv.Wpf.Tests`：442/443。
- 增加人工确认 UI 及复审修复后 `MesControlAgv.Wpf.Tests`：449/450；唯一失败仍为既有 `ShineLabHandoffRehearsalTests` 在链接工作区无法识别仓库根目录，与本功能无关。
- 人工确认 HTTP、ViewModel 和 XAML 定向测试：11/11 通过。
- 人工门禁改为可选后 WorkflowContract：73/73；MES：320/320。
- 新版 MES 隔离构建：0 警告、0 错误。
- 正式节点实现阶段 `dotnet build MesControlAgv.sln --no-restore`：成功，0 警告、0 错误；人工确认 UI 使用隔离输出目录完成编译，避免覆盖正在运行的 MES/Adapter 文件。
- 自动测试未启动本地服务，未访问 `192.168.200.157`，未发送真机命令。

## 2026-09-17 单步骤正式业务链归档

本节归档一次由 WPF **Run admission** 发起的真实单步骤正式业务链；它不是
预演，也不是自动化测试发令。计划 `c0841566-6040-40b1-9409-200a4bf36f15`
v`1`、任务 `3bfa15d6-77d4-4de9-9b5f-05db3c621b4e` 和排程
`404aef92-bad4-4c3a-a164-e9bc17ee0606` 均完成。WPF 准入请求为
`0a1ca92c-7a19-46fd-b501-600fccccdab7`，产生 WorkflowRun
`eef3ad56-dead-4a35-a8e9-662d38c4e780`、节点
`98f78aa5-edce-4992-8f4a-88c5be8472d7` 和设备操作
`202170a0-eb2a-94f9-d646-c2f6f5ede152`。

- 单次 `TEST-001` 启动已由持久化操作（attempt `1`）和 MES 出站日志共同证实；没有第二次启动、重试、初始化、取消或停止调用。
- 本轮观察到 `Running`：`2026-09-17T02:13:24Z` 时 Job、Schedule、Workflow、节点和设备操作均为 Running，活动租约存在，设备为 `Running/1`、任务为 Running、厂家码为 `3 / ExperimentStarted`。
- `2026-09-17T02:14:18.307331Z`，Job 和 Schedule 均为 Completed；Workflow 为 Completed，节点和设备操作均为 Succeeded，唯一排程活动为 Succeeded。完成审计记录 `releasedLeaseCount=1`，最终 `activeLeases=[]`。
- 新增跨边界自动化用例提交 `055a966`，随后以提交 `951ebbb` 隔离其宿主；二者均为测试改动，**本次验收未发现且未实施产品代码修复**。定向用例 `1/1`、MES 全量 `321/321`；回归 `475/475`；隔离构建 20 个项目、0 警告、0 错误。

完整的可复核 ID、时间线、原始 GET/日志证据及限制见
[2026-09-17 工作站业务链验证](diagnostics/2026-09-17-workstation-business-chain-validation.md)。

## 后续边界

- 厂家冷态自动初始化已验证；v2 已移除开盖分液专用人工门禁，WPF 通用人工确认能力仍保留。
- 任务表导入和动态创建任务后续单独扩展，不加入当前节点。
- 手动停止继续暂缓；流程取消只阻止后续步骤，不向已执行设备动作发送停止命令。
- 与主工作区另一条四阶段工作站实现整合时，只保留本节点的“已有任务、单次启动、Running 证据、只读恢复”语义，不整批带入未真机验证的写操作。
- 尚未实现多步骤实验实体运行、厂家任务表导入、动态任务创建或远程停止；它们不属于本次单步骤验收，也不得由导入/准备动作隐式触发。
