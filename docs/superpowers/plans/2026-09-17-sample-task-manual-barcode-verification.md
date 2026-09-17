# 样品任务表人工条码核对实施计划

日期：2026-09-17

设计依据：[`2026-09-17-sample-task-manual-barcode-verification-design.md`](../specs/2026-09-17-sample-task-manual-barcode-verification-design.md)

## 1. 实施目标

在现有实验任务和任务排程链路中增加最小的样品条码人工核对门禁：

```text
登记样品和条码
  -> 为实验任务准备样品编号/位置快照
  -> 自动校验
  -> 操作员在 WPF 页内目视核对
  -> 点击“核对完成”并保存版本/摘要/操作人/时间
  -> 任务数据变化则核对失效
  -> 只有当前版本已核对，工作站实验任务才允许运行准入
```

本计划不读取仪器 USB 扫码枪，不实现厂家任务表上传，不自动启动设备，也不扩大到耗材库存或完整 LIMS。

## 2. 全局约束

- 工作继续位于隔离工作区 `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly` 和分支 `feature/sample-workstation-http-readonly`。
- WPF 只访问 MES HTTP API，不直接访问 SQLite、Adapter 或厂家地址。
- 所有核对结论以 MES 持久化状态为准；WPF 的按钮状态只是提示，后端必须再次校验。
- 样品核对是页内明确动作，不显示二次确认弹窗。
- 核对只针对样品编号、条码、位置和顺序；仪器任务参数映射由未来厂家格式适配负责。
- 工作站已有任务启动继续保持单次发令、Running 证据、Unknown 停止后续流程和只读恢复语义。
- 自动化测试不得访问 `192.168.200.157`、启动现场服务或发送真实设备命令。
- 不修改默认现场控制开关，不影响当前运行的 MES、Adapter 和 WPF 进程。
- 厂家任务表导入只创建或准备任务，未来也不得隐式启动。

当前阶段的 `Verified` 只能证明“操作员核对了中控保存的样品快照”，不能证明同一份任务表已经送达仪器。厂家上传接口接入前，不得把该状态宣传为自动任务表追溯闭环；现场继续使用已有任务模式时，仍需明确这是人工核对与固定厂家任务的组合。

## Task 1：契约、实体和数据库升级

### 文件

- 新增：`src/MesControlAgv.Contracts/Experiments/ExperimentSampleVerificationContracts.cs`
- 新增：`src/MesControlAgv.Contracts/Experiments/ExperimentSampleVerificationCommandContracts.cs`
- 新增：`src/MesControlAgv.Mes/Entities/ExperimentSampleRecord.cs`
- 新增：`src/MesControlAgv.Mes/Entities/ExperimentSampleVerificationRecord.cs`
- 修改：`src/MesControlAgv.Mes/Data/MesDbContext.cs`
- 修改：`src/MesControlAgv.Mes/Program.cs`
- 新增或扩展测试：`tests/MesControlAgv.Mes.Tests/WorkflowRuntimeSchemaUpgradeTests.cs`

### 契约

增加以下稳定模型：

- `ExperimentSampleStatus`：首版只使用 `Active`、`Disabled`。
- `ExperimentSample`：样品记录 ID、业务样品编号、批次号、条码、显示名称、状态和时间字段。
- `ExperimentSampleVerificationStatus`：`Draft`、`ReadyForVerification`、`Verified`、`Invalidated`。
- `ExperimentSampleTaskRow`：稳定 RowId、SampleId、SampleBarcode、Position、DisplayName、Order。
- `ExperimentSampleVerification`：VerificationId、ExperimentJobId、Revision、Status、Rows、SnapshotHash、VerifiedBy/At/Note、InvalidatedAt/Reason、CreatedAt/UpdatedAt。
- 查询、登记样品、保存任务样品行和完成核对的请求契约；所有写请求包含 RequestId、Actor 和 Reason。
- 稳定问题码：样品不存在、条码重复、批次不匹配、位置重复、版本冲突、尚未核对、核对已失效。

条码规范化只执行 `Trim()`；首版保持大小写敏感，不猜测条码规则。

### 持久化

- `ExperimentSamples` 保存最小样品登记信息；业务样品编号和规范化条码分别建立唯一索引。
- `ExperimentSampleVerifications` 每个任务/Revision 保存一份不可变样品行 JSON 和摘要；`ExperimentJobId + Revision` 唯一。
- 核对、失效和并发拒绝继续写入现有 `ExperimentSchedulingAudits`，不新增第二套审计表。
- SQLite 启动升级只增表/索引，不重写已有实验任务。
- 历史任务没有核对记录时仍可查询；只有新的运行准入动作根据工作流类型执行门禁。

### 测试

- 新数据库创建表和唯一索引。
- 旧数据库升级后原有计划、任务、运行和审计仍可读。
- 重复业务样品编号和重复条码受唯一约束保护。
- 同一任务/Revision 不能出现两条核对快照。

### 提交

`feat: add experiment sample verification persistence`

## Task 2：样品登记、任务快照和核对状态机

### 文件

- 新增：`src/MesControlAgv.Mes/Services/ExperimentSampleVerificationService.cs`
- 新增：`src/MesControlAgv.Mes/Services/ExperimentSampleVerificationExceptions.cs`
- 新增：`src/MesControlAgv.Mes/Endpoints/ExperimentSampleVerificationEndpointRouteBuilderExtensions.cs`
- 修改：`src/MesControlAgv.Mes/Program.cs`
- 修改：`src/MesControlAgv.Application/ExperimentSchedulingApplicationBoundary.cs`，增加可被准入和未来上传复用的核对门禁接口
- 新增：`tests/MesControlAgv.Mes.Tests/ExperimentSampleVerificationApiTests.cs`

### API

首版提供：

- `GET /api/experiment-samples?batchId=...`
- `PUT /api/experiment-samples/{sampleId}`：登记或更新尚未进入运行快照的样品。
- `GET /api/experiment-jobs/{jobId}/sample-verifications/current`
- `PUT /api/experiment-jobs/{jobId}/sample-verifications/current`：保存当前任务样品行；内容改变时创建下一 Revision。
- `POST /api/experiment-jobs/{jobId}/sample-verifications/{revision}/verify`：页内“核对完成”。

不提供删除历史核对记录的接口。

### 状态机

1. 保存样品行时，服务端按 Row Order 生成确定性 JSON 和 SHA-256 摘要。
2. 服务端从 `ExperimentSamples` 解析每个 SampleId，并校验条码、批次、启用状态、位置唯一和条码唯一。
3. 校验通过进入 `ReadyForVerification`，失败保持 `Draft` 并返回逐行问题。
4. 核对请求必须携带当前 Revision 和 SnapshotHash；不一致返回版本冲突。
5. 核对成功保存 Actor、时间、备注并转为 `Verified`，请求 ID 幂等重放返回原结果。
6. 样品行内容改变时创建新 Revision，旧 Verified 记录转为 `Invalidated` 并保留原因。
7. 样品登记中的条码、批次或状态发生变化时，准入门禁重新比对当前样品记录；漂移时拒绝并使当前核对失效。
8. 任务进入 Admitted/Running/终态后，不允许修改其样品行或重新核对。

### 测试

- 登记、查询和重复请求幂等。
- 空字段、未知样品、跨批次、停用样品、重复条码和重复位置。
- 相同快照保存不增加 Revision；内容变化增加 Revision 并使旧核对失效。
- 核对保存操作人、时间、摘要；版本漂移时拒绝。
- 并发核对只有当前 Revision 成功。
- 样品登记漂移使核对门禁失败。
- 已准入任务拒绝修改。

### 提交

`feat: add manual sample verification workflow`

## Task 3：开盖分液运行准入门禁

### 文件

- 修改：`src/MesControlAgv.Mes/Services/ExperimentRuntimeAdmissionService.cs`
- 修改：`src/MesControlAgv.Mes/Services/ExperimentSchedulingExceptions.cs`
- 修改：`src/MesControlAgv.Mes/Services/ExperimentSchedulingPersistence.cs`（仅在需要共享映射/摘要时）
- 扩展：`tests/MesControlAgv.Mes.Tests/ExperimentRuntimeAdmissionApiTests.cs`
- 扩展：`tests/MesControlAgv.Mes.Tests/ExperimentSampleWorkstationBusinessChainTests.cs`

### 门禁规则

- 准入服务读取任务固定的工作流版本；当工作流包含 `sample-workstation.execute-existing-task` 节点时，要求当前样品核对为 `Verified`。
- 对多步骤任务，任一固定工作流版本包含该节点即要求核对；实体多步骤运行仍保持现有未支持边界。
- 后端在创建 WorkflowRun 和资源租约之前调用核对门禁，并重新比对 Revision、SnapshotHash 和当前样品登记。
- 缺失、Draft、ReadyForVerification、Invalidated、版本漂移或样品登记漂移均拒绝准入，不创建 WorkflowRun、租约或设备操作。
- 准入审计 DetailsJson 记录 VerificationId、Revision 和 SnapshotHash，形成运行与核对快照关联。
- 不影响不包含工作站节点的实验任务，也不回写历史已完成任务。
- 未来厂家任务上传服务必须复用相同的 `IExperimentSampleVerificationGate`；本 Task 不新增上传端点。
- 本阶段准入审计只关联中控核对快照，不生成或伪造厂家任务上传回执。

### 测试

- 工作站任务缺少核对时拒绝准入并返回稳定问题码。
- 待核对、失效和样品漂移均拒绝，设备操作数为 0。
- Verified 当前版本允许准入，审计包含核对 ID/版本/摘要。
- 非工作站任务沿用原准入行为。
- 现有跨边界工作站测试先登记样品、保存快照、完成核对，再验证单次启动和完整收口。

### 提交

`feat: gate workstation admission on sample verification`

## Task 4：WPF 页内样品核对

### 文件

- 修改：`src/MesControlAgv.Wpf/Services/IMesClient.cs`
- 修改：`src/MesControlAgv.Wpf/Services/MesClient.cs`
- 新增：`src/MesControlAgv.Wpf/ViewModels/ExperimentSampleVerificationModels.cs`
- 修改：`src/MesControlAgv.Wpf/ViewModels/ExperimentSchedulingViewModel.cs`
- 修改：`src/MesControlAgv.Wpf/Experiments/ExperimentSchedulingView.xaml`
- 扩展：`tests/MesControlAgv.Wpf.Tests/MesClientExperimentSchedulingHttpContractTests.cs`
- 扩展：`tests/MesControlAgv.Wpf.Tests/ExperimentSchedulingViewModelTests.cs`
- 扩展：`tests/MesControlAgv.Wpf.Tests/ExperimentPlanningViewBindingTests.cs`

### 页面设计

在“任务排程”选中任务详情中增加页内“样品核对”区域：

- 展示批次号、当前 Revision、状态、摘要短值、最近核对人和时间。
- 显示样品编号、条码、位置、顺序和逐行校验结果。
- 提供最小样品登记/编辑输入和“保存样品行”。
- 提供“核对完成”按钮；执行时不弹二次确认框。
- 已核对版本以只读方式展示；编辑或重新保存不同内容后显示“核对已失效”。
- 不显示扫码输入框，不读取仪器 USB，不伪造实际扫码结果。
- `CanAdmit` 在工作站任务未 Verified 时为 false，并显示明确原因；后端门禁仍是最终裁决。

页面不新增一级导航，不新建工作站专用启动入口，继续复用现有任务排程、运行准入和运行监控。

### 测试

- HTTP 路由和请求体序列化正确。
- 选中任务时加载当前核对；切换任务不会显示上一任务数据。
- 自动校验问题按行显示。
- 未 Ready 时“核对完成”不可用；Ready 时可用且不调用确认弹窗。
- Verified 后准入可用；修改后立即显示 Invalidated 并禁用准入。
- 普通非工作站任务不受该 UI 门禁影响。
- XAML 绑定存在且默认不显示扫码控件。

### 提交

`feat: add sample verification to experiment scheduling`

## Task 5：集成验证、文档和现场边界

### 文件

- 更新：`docs/SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md`
- 更新：`docs/SAMPLE-WORKSTATION-FORMAL-WORKFLOW-HANDOFF-2026-09-16.md`
- 新增：`docs/diagnostics/2026-09-17-sample-verification-acceptance.md`

### 自动化验证

```powershell
dotnet test tests\MesControlAgv.WorkflowContract.Tests\MesControlAgv.WorkflowContract.Tests.csproj --no-restore
dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore
dotnet test tests\MesControlAgv.Wpf.Tests\MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerification|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
```

使用独立输出目录构建，避免覆盖运行中的现场文件：

```powershell
$buildRoot = Join-Path $env:TEMP "mes-sample-verification-build"
dotnet restore MesControlAgv.sln --artifacts-path "$buildRoot"
dotnet build MesControlAgv.sln --no-restore --artifacts-path "$buildRoot"
```

### 非真机验收

使用测试或新的隔离数据库完成：

1. 创建一个含工作站节点的实验任务。
2. 登记同批次的两条样品和条码。
3. 保存两个位置不同的任务样品行。
4. 验证未核对时运行准入被拒绝，且无 WorkflowRun/设备操作。
5. 在 WPF 页内点击“核对完成”，确认没有弹窗并保存操作人、时间和版本。
6. 修改其中一个位置，确认旧核对失效且运行准入再次被拒绝。
7. 重新核对后验证准入可以创建 WorkflowRun；使用测试替身或 Dry Run，不访问真实工作站。

本阶段不需要再次触发真实设备任务；正式工作站通信链路已经完成真机闭环。若后续厂家任务表上传接口交付，再单独设计上传与一次真机验收。

### 文档

- 记录手工核对状态机、API、WPF 操作和门禁问题码。
- 明确当前没有厂家任务表上传、USB 扫码输入或 last-scan 回传。
- 明确导入只准备任务，运行仍由独立“运行准入”动作触发。

### 提交

`docs: hand off manual sample verification`

## 3. 完成判定

- 中控可以登记样品编号、批次和条码，并为实验任务保存有序样品位快照。
- 操作员可在 WPF 页内无弹窗完成批次核对，记录操作人、时间、Revision 和摘要。
- 任一核对字段或样品登记发生变化，旧核对失效。
- 工作站任务未核对时，前后端均阻止运行准入且不产生设备操作。
- Verified 当前版本可通过现有正式准入链路。
- 非工作站任务和历史已完成任务不回归。
- 不访问仪器 USB，不实现厂家任务上传，不隐式启动设备。
- 定向测试、MES 全量测试和隔离构建通过，文档完成归档。
