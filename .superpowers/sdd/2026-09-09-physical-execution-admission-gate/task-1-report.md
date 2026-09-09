# Task 1 报告：统一准入策略与 MES 启动组合

## 状态

DONE

## 改动文件

- `src/MesControlAgv.Contracts/Devices/PhysicalReadinessContracts.cs`
  - 新增 `PhysicalReadinessReasonCodes.EpochAuthorizationRequired`，精确值为
    `physical_epoch_authorization_required`。
- `src/MesControlAgv.Mes/Services/PhysicalExecutionAdmissionPolicy.cs`
  - 新增 `PhysicalExecutionAdmissionPolicy` 和固定异常
    `PhysicalExecutionAdmissionException`。
- `src/MesControlAgv.Mes/Program.cs`
  - 将启动时 `physicalReadinessOptions.Enabled` 纳入
    `WorkflowPhysicalBatchAdmissionGate`。
  - 监督器配置缺失时只拒绝物理批量 Execute，原因准确说明“物理就绪监督器”，不阻断 MES 启动。
  - 注册准入策略，不启动 PhysicalAcceptance、不访问现场设备。
- `tests/MesControlAgv.Mes.Tests/PhysicalExecutionAdmissionPolicyTests.cs`
  - 覆盖 Simulator 旁路、监督器关闭优先级、监督器开启但 direct write 未绑定 epoch，以及异常稳定字段。

## 设计说明

策略位于 MES Services 范围，只依赖 `ProfileConfiguration`、`IPhysicalReadinessState` 和结构化 logger，
不持有、不调用 Adapter。Simulator profile 两个检查均直接旁路，保持原行为。

Physical 模式下，`RequireSupervisedExecution(operation)` 在监督器关闭时抛出既有
`physical_readiness_supervisor_disabled`。`RejectUnboundPhysicalWrite(operation)` 使用相同优先级；
监督器关闭时仍先返回该既有原因，监督器开启但 direct write 没有当前监督执行授权时返回
`physical_epoch_authorization_required`。固定异常暴露稳定 `Code` 和不含凭证/授权全文的安全 `Detail`。

未接入 Task 2 业务入口。启动组合仅增加监督器开关条件，缺项不会导致 MES 启动失败。

## 测试命令与完整结果摘要

1. `dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore --filter FullyQualifiedName~PhysicalExecutionAdmissionPolicyTests -m:1`
   - 通过 4，失败 0，跳过 0，总计 4。
2. `dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore --filter 'FullyQualifiedName~PhysicalReadinessWorkflowAdmissionTests|FullyQualifiedName~WorkflowRuntimePersistenceTests|FullyQualifiedName~PhysicalExecutionAdmissionPolicyTests' -m:1`
   - 通过 19，失败 0，跳过 0，总计 19。
3. `dotnet build src\MesControlAgv.Mes\MesControlAgv.Mes.csproj -c Release --no-restore -m:1`
   - 成功，0 个警告，0 个错误。
4. `git diff --check`
   - 通过，无 whitespace 错误。

以上验证均为本地离线构建/测试；未启动 PhysicalAcceptance，未访问现场 IP 或端口。

## 提交哈希

`1d0c1e3a9d8b4730fabf263161f7e757be9e705d`（实现提交；报告提交后以最终提交哈希更新）

## 自审问题

- 策略没有 Adapter、网络客户端或设备协议引用；通过构造依赖和源码边界检查确认。
- Simulator 旁路发生在 readiness 检查之前；通过单元测试确认监督器关闭不会改变 Simulator 行为。
- direct physical write 的原因优先级为 supervisor disabled 优先于 epoch authorization required；通过单元测试确认。
- 启动 gate 的监督器缺项只改变 gate 快照，不抛出启动异常；通过源码检查和 Release 构建确认。
- 本 Task 未实现 Task 2 的业务入口接线，符合范围边界。
- 初始实现提交不包含任何用户 artifacts、未跟踪文件或删除项；报告仅是本任务要求的新增文档。
