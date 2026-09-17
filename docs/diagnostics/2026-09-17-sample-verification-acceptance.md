# 2026-09-17 样品任务人工核对离线验收

## 结论

样品登记、任务快照、WPF 页内核对和运行准入门禁已用测试替身及全新的测试数据库验收。本次完全离线：没有访问 `192.168.200.157`，没有启动现场服务、发送真实工作站命令、关闭现场进程或修改现场配置/开关。

`Verified` 只表示操作员已对中控保存的样品编号、条码、位置和顺序快照完成核对；它不是厂家任务表上传成功、到达仪器或设备已运行的证明。

## 非真机业务链证据

`ExperimentSampleWorkstationBusinessChainTests.Unverified_drifted_and_reverified_sample_snapshot_gates_workstation_admission_without_gateway_start` 是一条单测试方法、单个新 SQLite 测试数据库的连续恢复链证据。它使用 `RecordingWorkstation` 替身，不配置现场 gateway，也不调度/启动设备。七步映射如下：

1. 测试发布含 `sample-workstation.execute-existing-task` 节点的工作流，创建计划、任务和工作站排程。
2. 测试登记同一 `TEST-001` 批次的两个不同业务样品编号/条码：`S-CHAIN-01`/`BC-CHAIN-01` 与 `S-CHAIN-02`/`BC-CHAIN-02`。
3. 测试保存两条有序任务行：位置 `A01`/`B01`，顺序 `1`/`2`。
4. 首次未核对准入以 `EXP-SAMPLE-VERIFICATION-REQUIRED` 拒绝；测试断言没有 `WorkflowRun`、租约、`WorkflowDeviceOperation` 或 recording gateway start。
5. 测试完成当前快照核对，并断言 `sample-operator`、非空时间、相同 revision 与 hash。WPF 无弹窗命令行为由 `ExperimentSchedulingViewModelTests.Sample_verification_loads_per_selected_job_gates_workstation_admission_and_never_confirms_twice` 单独覆盖。
6. 测试将第二行位置改为 `C01`，断言旧 revision 为 `Invalidated`、当前新 revision 为未核对的 `ReadyForVerification`；因此第二次准入以当前 revision 未核对的 `EXP-SAMPLE-VERIFICATION-REQUIRED` 拒绝，且仍无运行副作用或 gateway start。
7. 测试重新核对当前 revision，再显式调用独立 runtime admission；断言它创建 `WorkflowRun` 和工作站租约，并在 admission audit 中关联正确 verification id/revision/hash。测试不 dispatch worker，因此 `WorkflowDeviceOperation` 与 gateway start 均为零。

核对、保存行和任何未来导入只能准备任务，不能隐式创建 `WorkflowRun` 或发送仪器命令。运行中取消仅阻止后续流程；本阶段不实现或发送远程停止命令。既有单次发令、Running 证据、Unknown 停止后续流程和只读恢复语义不变。

## 当前边界

- 没有厂家任务表上传、USB 扫码输入、last-scan 回传或远程停止。
- 未来导入只能创建/准备任务；运行仍由独立准入动作触发。

## 自动化与隔离构建

在 `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly` 执行：

```powershell
dotnet test tests\MesControlAgv.WorkflowContract.Tests\MesControlAgv.WorkflowContract.Tests.csproj --no-restore
# 通过 74，失败 0，跳过 0

dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleWorkstationBusinessChainTests.Unverified_drifted_and_reverified_sample_snapshot_gates_workstation_admission_without_gateway_start"
# 通过 1，失败 0，跳过 0

dotnet test tests\MesControlAgv.Mes.Tests\MesControlAgv.Mes.Tests.csproj --no-restore
# 通过 338，失败 0，跳过 0

dotnet test tests\MesControlAgv.Wpf.Tests\MesControlAgv.Wpf.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleVerification|FullyQualifiedName~ExperimentSchedulingViewModelTests|FullyQualifiedName~MesClientExperimentSchedulingHttpContractTests|FullyQualifiedName~ExperimentPlanningViewBindingTests"
# 通过 30，失败 0，跳过 0

$buildRoot = Join-Path $env:TEMP "mes-sample-verification-build"
dotnet restore MesControlAgv.sln --artifacts-path "$buildRoot"
dotnet build MesControlAgv.sln --no-restore --artifacts-path "$buildRoot"
# 成功，0 警告，0 错误
```

隔离输出路径：`C:\Users\33206\AppData\Local\Temp\mes-sample-verification-build`。构建输出没有覆盖运行中的现场文件。定向 WPF 过滤器排除了已知无关的 `ShineLabHandoffRehearsalTests` 链接 worktree 根目录识别失败；本次指定命令未触发该既有问题。
