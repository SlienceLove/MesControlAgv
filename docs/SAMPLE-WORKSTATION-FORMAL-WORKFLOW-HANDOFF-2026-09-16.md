# 开盖分液正式工作流交接（2026-09-16）

## 当前状态

- 隔离工作区：`D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`
- 分支：`feature/sample-workstation-http-readonly`
- 设计提交：`f615e2a`
- 计划提交：`ec9327a`
- 实现提交：`9d5f10f`
- 单次发令与恢复审查修复：`9c2a4f6`
- 主工作区 `D:\Project\Github\Mes` 未被覆盖、重置或合并。

默认隐藏的 WPF 联调入口已经通过真机验收；正式工作流节点已经完成代码和离线测试，但尚未执行正式节点的真机验收。

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

## 下一次正式节点真机验收

1. 启动厂家服务、厂家主程序、Adapter、MES 和 WPF。
2. 在厂家主程序手动完成整机初始化，确认界面初始化成功。
3. 在 WPF 创建最小流程：开始 → 人工确认 → 开盖分液执行已有任务 → 结束。
4. 工作站节点选择 `SAMPLE-WORKSTATION-01`，任务号使用现场确认存在的 `TEST-001`。
5. 为人工确认节点补齐 timeout/cancelled 到结束节点的路径，发布并启动流程。
6. 在 WPF 完成人工确认后，核对厂家启动接口只新增一次。
7. 观察节点 Ready → Running → Succeeded；设备 Idle/0 → Running/1 → Idle/0；任务 Completed → Running → Completed；错误/结果码 0 → 3 → 0。
8. 确认结束节点只在工作站节点成功后推进。

本轮只做一次正常正式流程验收，不制造断线、异常、停止或重复启动，也不测试任务创建和导入。

## 验证结果

- `MesControlAgv.WorkflowContract.Tests`：73/73 通过。
- `MesControlAgv.Mes.Tests`：320/320 通过。
- 工作站相关 `MesControlAgv.Adapter.Tests`：38/38 通过。
- `MesControlAgv.Wpf.Tests`：442/443；唯一失败为既有 `ShineLabHandoffRehearsalTests` 在链接工作区无法识别仓库根目录，与本功能无关。
- `dotnet build MesControlAgv.sln --no-restore`：成功，0 警告、0 错误。
- 自动测试未启动本地服务，未访问 `192.168.200.157`，未发送真机命令。

## 后续边界

- 厂家冷启动完整初始化修复后，再单独规划远程初始化节点；当前人工确认门禁不能绕过。
- 任务表导入和动态创建任务后续单独扩展，不加入当前节点。
- 手动停止继续暂缓；流程取消只阻止后续步骤，不向已执行设备动作发送停止命令。
- 与主工作区另一条四阶段工作站实现整合时，只保留本节点的“已有任务、单次启动、Running 证据、只读恢复”语义，不整批带入未真机验证的写操作。
