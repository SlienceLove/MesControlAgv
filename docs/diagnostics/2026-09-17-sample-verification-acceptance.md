# 2026-09-17 样品任务人工核对离线验收

## 结论

样品登记、任务快照、WPF 页内核对和运行准入门禁已用测试替身及全新的测试数据库验收。本次完全离线：没有访问 `192.168.200.157`，没有启动现场服务、发送真实工作站命令、关闭现场进程或修改现场配置/开关。

`Verified` 只表示操作员已对中控保存的样品编号、条码、位置和顺序快照完成核对；它不是厂家任务表上传成功、到达仪器或设备已运行的证明。

## 非真机业务链证据

`ExperimentSampleVerificationApiTests`、`ExperimentSampleVerificationContractTests` 和 WPF 定向测试使用测试替身及测试工厂的新数据库，覆盖：

1. 创建带 `sample-workstation.execute-existing-task` 节点的实验任务。
2. 登记同一批次的两个样品和条码，并保存两个位置不同、有稳定顺序的任务样品行。
3. 未核对时，前端预检和后端准入均以 `EXP-SAMPLE-VERIFICATION-REQUIRED` 拒绝；拒绝不创建 `WorkflowRun`、运行租约或设备操作。
4. WPF 页内“核对完成”无弹窗，保存操作员、时间、`Revision` 与 `SnapshotHash`，且不访问仪器。
5. 修改任一位置（以及业务样品编号、条码或顺序漂移）使旧核对失效；准入再次以 `EXP-SAMPLE-VERIFICATION-INVALIDATED` 被拒绝。
6. 重新保存快照并核对后，独立准入动作可以创建 `WorkflowRun`；其后的工作站调度仅在测试替身/Dry Run 语义下验证，从不访问真实工作站。

核对、保存行和任何未来导入只能准备任务，不能隐式创建 `WorkflowRun` 或发送仪器命令。运行中取消仅阻止后续流程；本阶段不实现或发送远程停止命令。既有单次发令、Running 证据、Unknown 停止后续流程和只读恢复语义不变。

## 当前边界

- 没有厂家任务表上传、USB 扫码输入、last-scan 回传或远程停止。
- 未来导入只能创建/准备任务；运行仍由独立准入动作触发。

## 自动化与隔离构建

在 `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly` 执行：

```powershell
dotnet test tests\MesControlAgv.WorkflowContract.Tests\MesControlAgv.WorkflowContract.Tests.csproj --no-restore
# 通过 74，失败 0，跳过 0

dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore
# 通过 337，失败 0，跳过 0

dotnet test tests\MesControlAgv.Wpf.Tests\MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerification|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
# 通过 30，失败 0，跳过 0

$buildRoot = Join-Path $env:TEMP "mes-sample-verification-build"
dotnet restore MesControlAgv.sln --artifacts-path "$buildRoot"
dotnet build MesControlAgv.sln --no-restore --artifacts-path "$buildRoot"
# 成功，0 警告，0 错误
```

隔离输出路径：`C:\Users\33206\AppData\Local\Temp\mes-sample-verification-build`。构建输出没有覆盖运行中的现场文件。定向 WPF 过滤器排除了已知无关的 `ShineLabHandoffRehearsalTests` 链接 worktree 根目录识别失败；本次指定命令未触发该既有问题。
