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
- 主工作区 `D:\Project\Github\Mes` 未被覆盖、重置或合并。

默认隐藏的 WPF 联调入口、正式工作流节点以及 WPF 人工确认入口均已通过真机闭环。正式运行已观察到人工确认、单次启动、Running 和 Completed 全链路证据。

## 已实现能力

- 节点：`sample-workstation.execute-existing-task`
- 能力：`sample-workstation.start-existing-task`
- 设备族：`sample-workstation`
- 配置：`deviceId + taskNo`
- 直接前置节点：`core.manual-confirmation`

发布校验要求人工确认节点的 `success` 控制边直接进入工作站节点。工作站节点只启动厂家已有任务，不调用远程初始化、任务创建、轨迹写入、暂停或停止。

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

本轮厂家新版首次收到任务后直接进入实验，现场未观察到明确的完整初始化动作。但设备/控制器没有确认断电或清除初始化状态，因此不能据此判定厂家新版是否支持“未初始化时自动完整初始化并启动”。

厂家服务更新后曾短暂返回 503；约一分钟后 `192.168.200.157:8082` 恢复监听并返回正常状态。流程在服务恢复且设备 Idle 后才人工放行。

## 下一次冷态初始化验证

1. 由现场确认设备进入明确的未初始化状态；仅重启 WPF、中控或厂家界面不一定清除控制器初始化状态。
2. 不在厂家主程序点击初始化。
3. 先只读核对厂家服务在线、设备状态和 `TEST-001` 存在。
4. 新建唯一流程执行，在 WPF 人工确认栏记录“冷态自动初始化验证”。
5. 只发送一次确认并继续，现场记录是否先执行完整初始化，再进入实验动作。
6. 核对设备操作仍只有 1 条，并保存厂家日志中初始化与实验启动的先后时间。

在完成这项冷态验证前，正式流程仍保留“厂家主程序已完成整机初始化”的人工确认门禁。

## 验证结果

- `MesControlAgv.WorkflowContract.Tests`：73/73 通过。
- `MesControlAgv.Mes.Tests`：320/320 通过。
- 工作站相关 `MesControlAgv.Adapter.Tests`：38/38 通过。
- 正式工作站节点初始验证时 `MesControlAgv.Wpf.Tests`：442/443。
- 增加人工确认 UI 及复审修复后 `MesControlAgv.Wpf.Tests`：449/450；唯一失败仍为既有 `ShineLabHandoffRehearsalTests` 在链接工作区无法识别仓库根目录，与本功能无关。
- 人工确认 HTTP、ViewModel 和 XAML 定向测试：11/11 通过。
- 正式节点实现阶段 `dotnet build MesControlAgv.sln --no-restore`：成功，0 警告、0 错误；人工确认 UI 使用隔离输出目录完成编译，避免覆盖正在运行的 MES/Adapter 文件。
- 自动测试未启动本地服务，未访问 `192.168.200.157`，未发送真机命令。

## 后续边界

- 厂家冷态自动初始化行为尚未证实；完成明确未初始化状态下的单次验证前，当前人工确认门禁不能移除。
- 任务表导入和动态创建任务后续单独扩展，不加入当前节点。
- 手动停止继续暂缓；流程取消只阻止后续步骤，不向已执行设备动作发送停止命令。
- 与主工作区另一条四阶段工作站实现整合时，只保留本节点的“已有任务、单次启动、Running 证据、只读恢复”语义，不整批带入未真机验证的写操作。
