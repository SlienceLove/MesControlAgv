# 人工排程区布局收尾（2026-09-20）

起点：`3614049`，隔离分支 `feature/sample-workstation-http-readonly`。用户确认继续处理上轮遗留布局项。

## 改动和边界

仅将 `ExperimentSchedulingView.xaml` 的人工放置网格恢复为 `Auto, Auto, *, Auto`：资源列表使用剩余高度滚动，排程/撤排按钮按内容高度显示。没有改动工作站通讯、任务准备、来源绑定、准入或 worker；没有访问真机、重启现场服务、修改主工作区或合并日常分支。

新增 `Manual_placement_bounds_resource_scrolling_and_keeps_actions_visible`，分别以 1 个和 32 个资源测量实际 WPF 界面，验证列表高度受限、多资源可滚动至底部、按钮完整处于人工放置区域内且不与列表重叠、时间线仍可见。保留已有双展开面板与时间轴全屏测试。

## 验证

基线已有布局用例 2/2 通过。先只增加新用例：两例均失败于 `Expected: Star / Actual: Auto`，确认能捕获旧布局。恢复行高后全部布局用例 4/4 通过。

```powershell
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --nologo -v minimal --filter FullyQualifiedName~ExperimentPlanningViewBindingTests -p:BuildLocalServices=false -p:SkipLocalServiceCopy=true
# 4 passed / 0 failed

dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-build --no-restore --nologo -v minimal --filter "FullyQualifiedName~ExperimentScheduling|FullyQualifiedName~ExperimentWorkstationPreparation|FullyQualifiedName~ExperimentPlanningViewBinding|FullyQualifiedName~MesClient"
# 101 passed / 0 failed

dotnet build MesControlAgv.sln -c Release --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-workstation-layout-20260920-build -p:SkipLocalServiceCopy=true --nologo -v minimal
# succeeded / 0 warnings / 0 errors
```

后端未变更，本轮未重跑 MES/Adapter/WorkflowContract 全量；其既有证据仍见 9 月 18 日记录。本轮验证不等于新版厂家服务的真实导入/写码/执行验收。

下一步按[交接清单](../SAMPLE-WORKSTATION-CENTRAL-PREPARATION-HANDOFF-2026-09-18.md)完成现场验证。通过后按既有条件授权合入 `feature/wpf-ui-layout-optimization`，整合前重新核对目标分支和用户改动。
