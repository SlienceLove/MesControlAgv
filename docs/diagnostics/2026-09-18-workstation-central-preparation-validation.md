# 中控工作站任务准备验证（2026-09-18）

## 结论

在隔离分支 `feature/sample-workstation-http-readonly` 的已复审实现 `4845a0e` 上，中控任务准备链已完成自动化回归。Task 3 又将既有业务链用例补强为一个连续断言：两个已核对来源绑定到模板的两个来源位置，生成三条目标转移行，经真实 ASP.NET HTTP 路由保存准备、显式导入及精确回读、双条码确认、独立运行准入后，只进行一次带码启动；随后必须观察旧 Completed/Idle、Running、最终 Completed/Idle，并将 Workflow、节点、设备操作、Job、Schedule 置为终态且释放工作站租约。

该用例使用临时 SQLite 和内存 `RecordingWorkstation`，验证业务边界和持久化状态，但没有连接真实 Adapter、厂家服务、IP 或设备。测试补强没有暴露产品缺陷，生产代码未修改。

## 连续业务链证据

用例：

`ExperimentSampleWorkstationBusinessChainTests.Prepared_task_import_is_idempotent_and_closes_with_frozen_task_and_barcodes_once`

明确断言：

- 两个不同模板来源 `SOURCE-A/1/1`、`SOURCE-B/2/1` 分别绑定两个已核对样品，三条转移的来源样品序列为 `sample1, sample1, sample2`，即两个来源对应多个目标。
- `POST .../prepare` 生成服务器拥有的新任务号；只读模板调用一次，准备阶段的导入、写码和启动调用均为零。
- `POST .../import` 导入一次且替身按生成 XLSX 解析回读同一任务；条码更新一次；导入阶段启动调用仍为零。相同 request ID 重放不产生第二次写入。
- 独立准入把 Preparation ID、生成任务号和两个条码冻结到运行节点；worker 只调用一次带码启动，不调用旧的 taskNo-only 启动。
- 生成任务号的观察序列严格为 `Idle/Completed → Running/Running → Idle/Completed`，旧 Completed 不会提前完成本轮。
- 最终 Workflow=`Completed`、工作站节点=`Succeeded`、唯一设备操作=`Succeeded`、Job/Schedule=`Completed`，租约=`Released` 且 `ActiveResourceKey=null`。

这里的“来源→目标”是生成任务中的逻辑/计划溯源，不是设备逐孔执行成功传感。模板未使用的条码槽位必须保持 `IsUsedByTemplate=false`，不能列作已分液来源。

## Task 3 实际执行

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --nologo -v minimal --filter "FullyQualifiedName~Prepared_task_import_is_idempotent_and_closes_with_frozen_task_and_barcodes_once"
# 1 passed / 0 failed / 0 skipped

dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --nologo -v minimal
# 371 passed / 0 failed / 0 skipped
```

## 复用的最终回归证据

产品实现未因 Task 3 改动。以下结果由控制器在同一产品代码的最终复审头 `4845a0e` 执行，Task 3 未重复相同套件：

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --nologo -v minimal
# 327 passed

dotnet test tests/MesControlAgv.WorkflowContract.Tests/MesControlAgv.WorkflowContract.Tests.csproj --no-restore --nologo -v minimal
# 74 passed

dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-build --no-restore --nologo -v minimal --filter "FullyQualifiedName~ExperimentScheduling|FullyQualifiedName~ExperimentWorkstationPreparation|FullyQualifiedName~ExperimentPlanningViewBinding|FullyQualifiedName~MesClient" -p:BuildLocalServices=false -p:SkipLocalServiceCopy=true
# 92 passed

dotnet build MesControlAgv.sln -c Release --no-restore --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-sample-field-20260918-build -p:SkipLocalServiceCopy=true --nologo -v minimal
# 0 warnings / 0 errors
```

## 尚未由本记录证明

- 未向最新 V1.02 厂家服务上传生成 XLSX、回读真实任务、写入真实条码或启动真实设备。
- 未证明厂家逐孔/逐目标执行结果；当前协议只支撑任务级 Running/Completed 观察。
- 未修改或重启现场进程，未修改已发布工作流定义。
- 未合并到日常开发分支 `feature/wpf-ui-layout-optimization`；合并必须等现场验收通过并另行授权。

现场步骤与停止条件见 [中控任务准备最新交接](../SAMPLE-WORKSTATION-CENTRAL-PREPARATION-HANDOFF-2026-09-18.md)。
