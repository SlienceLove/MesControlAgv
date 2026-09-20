# 开盖分液工作站正式工作流实施计划

日期：2026-09-16
设计依据：`docs/superpowers/specs/2026-09-16-sample-workstation-formal-workflow-design.md`

## 实施原则

- 只在链接工作区 `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly` 修改。
- 主工作区现有改动不覆盖、不重置、不整批合并。
- 只启动厂家已有任务，不调用初始化、任务创建或轨迹写入。
- 每个节点 attempt 最多发送一次启动请求；结果不明确时进入 Unknown。
- 自动测试使用协议替身，不连接 `192.168.200.157`，不触发真机动作。

## 任务一：增加正式节点、能力与发布门禁

文件：

- `src/MesControlAgv.Contracts/Workflows/WorkflowCatalogContracts.cs`
- `src/MesControlAgv.Contracts/Workflows/WorkflowGraphContracts.cs`
- `src/MesControlAgv.Domain/Workflows/BuiltInWorkflowCatalog.cs`
- `src/MesControlAgv.Domain/Workflows/WorkflowPublicationRules.cs`
- `tests/MesControlAgv.WorkflowContract.Tests/WorkflowCatalogTests.cs`
- `tests/MesControlAgv.WorkflowContract.Tests/WorkflowPublicationValidatorTests.cs`

步骤：

1. 声明 `sample-workstation.execute-existing-task`、`sample-workstation.start-existing-task`、设备族和 `taskNo` 配置键。
2. 注册仅含 `deviceId + taskNo` 的节点和能力 schema。
3. 增加发布规则：工作站节点必须由 `core.manual-confirmation` 的成功控制边直接进入。
4. 测试目录元数据、Profile 能力校验、缺少或错误前置节点时发布失败。

## 任务二：扩展持久化认领与恢复查询

文件：

- `src/MesControlAgv.Application/WorkflowApplicationBoundary.cs`
- `src/MesControlAgv.Mes/Services/WorkflowNodeExecutionOrchestration.cs`
- `src/MesControlAgv.Mes/Services/WorkflowRuntimeRecordPersistence.cs`

步骤：

1. 增加工作站 Ready/Running 节点查询。
2. 让工作站节点参与现有原子认领并创建稳定设备操作 ID。
3. 在请求摘要中持久化 `deviceId` 和 `taskNo`。
4. 将 Unknown 原因、厂家任务号和只读结果摘要完整传入现有完成持久化链路。

## 任务三：实现单次启动 worker

文件：

- 新增 `src/MesControlAgv.Mes/Services/WorkflowSampleWorkstationWorker.cs`
- `src/MesControlAgv.Mes/Program.cs`
- `src/MesControlAgv.Mes/appsettings.json`
- `src/MesControlAgv.Mes/appsettings.PhysicalAcceptance.json`
- 新增 `tests/MesControlAgv.Mes.Tests/WorkflowSampleWorkstationWorkerTests.cs`

步骤：

1. Ready 前只读检查设备 Idle/0、错误码0和指定任务非 Running。
2. 认领后只调用一次 `StartTaskAsync(deviceId, taskNo)`，明确确认后持久化 Accepted。
3. 只读观察设备、错误码和指定任务；观察到本轮 Running 后持久化 Running。
4. 仅在 Completed + Idle/0 + 错误码0同时成立时完成节点并推进流程。
5. 旧 Completed、启动结果不明确、观察超时、异常终态和取消按设计进入 Unknown，绝不自动重发。
6. MES 重启时仅恢复已持久化 Running 的只读观察；仅 Accepted 或身份不完整直接 Unknown。
7. worker 默认关闭，Profile 必须显式启用控制并声明能力。

## 任务四：接入 WPF 编辑器和通用监控

文件：

- `src/MesControlAgv.Wpf/ViewModels/WorkflowEditorViewModel.cs`
- `src/MesControlAgv.Wpf/Workflows/WorkflowInspectorModels.cs`
- `tests/MesControlAgv.Wpf.Tests/WorkflowInspectorTests.cs`

步骤：

1. 节点目录增加“开盖分液：执行已有任务”。
2. 编辑器使用通用 schema 控件提供工作站选择和任务编号输入。
3. 允许在默认未启用物理控制时进行设计，但发布仍由 MES Profile 严格校验。
4. 沿用现有运行监控的输入/输出摘要展示 `taskNo`、运行观察时间和完成时间。
5. 保留并继续默认隐藏现有联调入口。

## 任务五：验证和提交

1. 先运行新增工作流契约、MES worker 和 WPF inspector 测试。
2. 运行相关项目全量测试与解决方案构建。
3. 执行 `git diff --check`，核对没有初始化、建任务、轨迹或自动重试代码。
4. 更新工作站中控集成文档和交接记录。
5. 请求代码审查并修正发现的问题后提交实现。
