# 开盖分液工作站正式工作流节点设计

日期：2026-09-16

## 目标

将已通过真机验收的“启动厂家已有任务并观察完成”能力接入正式工作流运行时，使开盖分液节点可以在持久化流程中单次发令、等待真实运行和完成，再推进后续节点。

厂家冷启动自动初始化尚未修复。本阶段使用现有人工确认节点作为明确的前置门禁，要求操作员先在厂家主程序完成整机初始化。任务创建、轨迹写入和任务表导入另行规划。

## 范围

本阶段包含：

- 正式节点 `sample-workstation.execute-existing-task`。
- 节点配置 `deviceId` 和 `taskNo`。
- 发布时校验直接前置节点为人工确认节点。
- MES worker 单次启动已有任务。
- 持久化启动确认、运行证据和完成结果。
- 只读状态观察、超时和 MES 重启恢复。
- WPF 流程编辑器、目录和运行监控中的节点显示。

本阶段不包含：

- 中控远程初始化。
- 厂家任务创建或轨迹写入。
- Excel/CSV 任务表导入。
- 暂停、停止或取消已执行的设备动作。
- 自动重试任何启动请求。
- 分液精度验收。

## 工作区与整合边界

实现位于链接工作区：

- `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`
- `feature/sample-workstation-http-readonly`

主工作区存在未提交的四阶段工作站 worker 草稿，会依次执行初始化、创建任务、添加轨迹和启动。三个前置写操作尚未完成真机验证，且任务表导入格式不在本阶段范围内，因此不整批复制该草稿。只按需复用现有工作流持久化、节点认领和设备操作记录模式。

## 节点和能力标识

- 节点类型：`sample-workstation.execute-existing-task`
- 设备族：`sample-workstation`
- 所需能力：`sample-workstation.start-existing-task`
- 执行模式：`DeviceCommand`
- 安全分类：`ControlledDeviceAction`

节点配置：

| 字段 | 必填 | 含义 |
| --- | --- | --- |
| `deviceId` | 是 | Profile 中启用且允许控制的开盖分液工作站 |
| `taskNo` | 是 | 厂家程序中已经存在的任务编号 |

节点输出：

| 字段 | 含义 |
| --- | --- |
| `deviceId` | 实际执行设备 |
| `taskNo` | 厂家任务编号 |
| `taskState` | 完成时厂家原始任务状态 |
| `runningObservedAtUtc` | 首次观察到本轮运行证据的时间 |
| `completedAtUtc` | 三项终态一致的时间 |

首版 `taskNo` 是节点中的显式配置值。未来任务导入完成后，可扩展为运行参数或上游节点输出，但不改变本节点的单次启动和观察语义。

## 流程结构与发布校验

推荐结构：

```text
开始
  → 人工确认：已在厂家主程序完成整机初始化
  → 开盖分液：执行已有任务
  → 后续设备节点
  → 结束
```

每个开盖分液执行节点必须有一个直接进入其成功控制入口的 `core.manual-confirmation` 前置节点。发布校验不依赖提示文字，而是检查节点类型和直接控制边；缺少此前置节点时阻止发布。

人工确认节点沿用现有工作流能力和运行记录。未确认、拒绝或取消时，工作站节点不会进入 Ready/执行路径，也不会调用厂家接口。

未来厂家修复并现场验证远程完整初始化后，再设计新的初始化节点或提升节点 schema 版本；本阶段不通过布尔开关绕过人工门禁。

## worker 数据流

1. worker 默认关闭；只有配置显式开启，且 Profile 中存在启用、允许控制、声明 `sample-workstation.start-existing-task` 的设备时运行。
2. worker 查询 Ready 的开盖分液节点。人工确认前置节点尚未成功时，该节点不会成为可执行节点。
3. 发令前只读获取设备状态、错误信息和指定任务状态。
4. 只有设备在线、Idle/0、错误码0、任务不是 Running，且所有读取成功时才认领节点；否则保持 Ready，不发命令。
5. 节点认领时创建并持久化稳定的设备操作 ID。
6. 通过 `ISampleWorkstationCommands.StartTaskAsync(deviceId, taskNo)` 发送一次启动请求。不得调用 `InitializeAsync`，也没有创建任务或轨迹接口依赖。
7. 明确收到 `Acknowledged=true` 后将设备操作记录为 Accepted，但不能完成节点。
8. worker 只读轮询设备状态、错误码和指定任务状态。首次观察到下列任一信号时，持久化 Running 证据和 `runningObservedAtUtc`：
   - 设备 Running/1；
   - 结果码3；
   - 指定任务 Running。
9. 已持久化 Running 证据后，只有指定任务 Completed、设备 Idle/0、结果码0三项同时成立，节点才成功并记录输出。
10. 节点成功后，现有工作流运行时按成功边推进后续节点。

## 并发和单次发令

- worker 使用现有节点原子认领机制，避免多个 worker 同时执行同一节点。
- 同一工作站同一时间只允许一个已认领或 Running 的工作站操作。
- 启动请求没有自动重试循环。
- 读取轮询可以在时限内重试，但不得调用任何写接口。
- 用户重新执行必须创建新的节点 attempt 和新的稳定操作 ID，不能复用旧发令结果。

## 错误和状态语义

发令前：

- 设备离线、忙碌、初始化中、暂停、读取失败或错误码非0：节点保持 Ready，不认领、不发令。
- `deviceId`、`taskNo` 或 Profile 能力缺失：发布阶段阻止；若旧数据绕过发布校验，运行时 Failed，且不发令。

发令时：

- 厂家明确拒绝且确认没有执行：节点 Failed。
- 超时、断线、空响应或任何不能证明未执行的结果：节点 Unknown。
- `Acknowledged=false`：节点 Failed；不重发。
- 流程取消发生在调用启动接口之后：节点 Unknown，不发送停止命令。

发令后：

- 启动确认后在启动观察窗口内始终没有 Running 证据：节点 Unknown。旧 Completed 不能当作本轮完成。
- 已观察 Running 后，读取暂时失败：仅重试读取；超过完成时限转 Unknown。
- 任务、设备和错误码终态不一致：继续读取至完成时限；超时转 Unknown。
- 厂家任务报告未知状态：节点 Unknown。
- 手动停止或异常终态尚无完整厂家任务状态语义，本阶段统一停止后续流程并进入 Unknown，不猜测成功。

## MES 重启恢复

恢复只读取状态，绝不重放启动：

- 设备操作记录已经持久化 Running 证据：可继续读取指定任务；三项终态一致时完成，否则保持观察或在恢复时限后 Unknown。
- 设备操作记录只有 Accepted，没有 Running 证据：直接转 Unknown，需要人工核对，因为复用任务的旧 Completed 无法证明本轮完成。
- 缺少操作 ID、设备 ID 或任务号：转 Unknown。
- 已 Failed、Unknown 或完成的节点不重新派发。

## WPF 行为

- 流程目录显示“开盖分液：执行已有任务”。
- 编辑器提供工作站设备选择和任务编号输入。
- 发布校验明确提示缺少人工初始化确认节点、设备能力或任务编号。
- 运行监控沿用现有节点状态：Ready、Running、Succeeded、Failed、Unknown。
- 运行监控展示任务编号、首次运行时间和完成时间。
- 默认隐藏的联调按钮继续保留，但正式工作流不会调用或模拟点击该按钮。

## 配置

MES 配置新增默认关闭的 worker 段：

```json
{
  "WorkflowSampleWorkstationWorker": {
    "Enabled": false,
    "PollIntervalMs": 1000,
    "ReadinessRetryIntervalMs": 2000,
    "StartObservationTimeoutMs": 30000,
    "CompletionTimeoutMs": 600000
  }
}
```

Profile 设备示例：

```json
{
  "deviceId": "SAMPLE-WORKSTATION-01",
  "deviceFamily": "sample-workstation",
  "capabilityIds": ["sample-workstation.start-existing-task"],
  "enabled": true,
  "controlEnabled": true
}
```

Adapter 的 `Devices:SampleWorkstation:Enabled` 和 `ControlEnabled` 仍分别控制读取和写入。WPF 联调入口开关不影响正式 worker。

## 自动测试

自动测试全部使用内存数据库、假时钟和协议替身，不访问真机，覆盖：

- 节点目录、schema、设备族和能力声明。
- WPF 节点配置与显示。
- 缺少直接人工确认前置节点时发布失败。
- 人工确认未完成时启动请求为0。
- 发令前非 Idle、错误码非0或读取失败时启动请求为0。
- 正常执行只发送一次启动请求。
- 断言 Initialize 调用次数为0，且不存在任务创建、轨迹写入调用。
- 旧 Completed 在没有 Running 证据时不能完成。
- Running→Completed 三项终态一致后成功并推进后续节点。
- 启动明确拒绝、结果不明、完成超时和读取故障。
- MES 重启时 Running 证据恢复成功，以及仅 Accepted 时转 Unknown。
- worker 默认关闭和 Profile 能力门禁。

## 真机验收

1. 在厂家主程序执行完整初始化。
2. 使用已有 `TEST-001` 创建最小正式流程：开始→人工确认→工作站→结束。
3. 在 WPF 完成人工初始化确认。
4. 验证厂家 `StartExperiment` 仅新增一次。
5. 观察节点 Ready→Running→Succeeded，设备 Idle→Running→Idle，任务 Completed→Running→Completed，结果码0→3→0。
6. 验证工作流只有在工作站节点成功后进入结束节点。

不在同一真机验收中制造停止、异常或断线，也不测试任务创建和导入。

## 成功标准

- 未确认初始化时不发令。
- 工作站节点每次 attempt 最多发送一次启动请求。
- 旧 Completed 不会使流程提前成功。
- 真实 Running 和三项完成终态被持久化，后续节点只在成功后执行。
- 不确定结果统一停止后续流程并进入 Unknown。
- MES 重启不重放启动命令。
- 主工作区未提交改动不被覆盖。
