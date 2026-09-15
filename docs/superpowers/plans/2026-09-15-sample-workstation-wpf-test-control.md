# 开盖分液工作站 WPF 联调控制入口实施计划

日期：2026-09-15
设计依据：`docs/superpowers/specs/2026-09-15-sample-workstation-wpf-test-control-design.md`

## 实施原则

- 只在链接工作区 `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly` 修改。
- 主工作区 `D:\Project\Github\Mes` 的未提交改动不覆盖、不暂存、不合并。
- 先写失败测试，再写最小实现。
- 自动测试不访问 `192.168.200.157`，实现阶段不发送真机命令。
- 启动请求不重试；观察循环只调用只读快照。
- 不实现初始化、建任务、导入、暂停或停止。

## 基线

- WPF 工作站相关测试：6/6 通过。
- WPF 全量：423/424 通过；唯一现有失败为 `ShineLabHandoffRehearsalTests.Operator_csv_flows_through_parser_writer_and_handoff` 在链接工作区无法识别仓库根目录，与本功能无关。
- 先前真机联调遗留的本机 15041/15045 测试实例已关闭，以解除编译输出锁定；厂家 192.168.200.157:8082 服务未操作。

## 任务一：增加 WPF 到 MES 的单次启动契约

文件：

- `src/MesControlAgv.Wpf/Services/IMesClient.cs`
- `src/MesControlAgv.Wpf/Services/MesClient.cs`
- `tests/MesControlAgv.Wpf.Tests/MesClientHttpContractTests.cs`

步骤：

1. 在 HTTP 契约测试中增加启动任务用例，断言方法为 POST、路径中的设备 ID 和任务号已转义、总请求数为 1、响应反序列化为 `SampleWorkstationCommandResponse`。
2. 增加失败响应测试，断言 ProblemDetails 的 `detail` 能作为界面错误信息传出，不触发第二次请求。
3. 在 `IMesClient` 增加 `StartSampleWorkstationTestTaskAsync(deviceId, taskNo, cancellationToken)`，默认实现返回 `NotSupportedException`，保持现有测试替身兼容。
4. 在 `MesClient` 实现无请求体的单次 POST。成功时解析规范化响应；失败时读取 `detail`，读取不到则使用 HTTP 状态描述。
5. 运行新增 HTTP 契约测试。

## 任务二：增加确认、启动与只读终态观察

文件：

- 新增 `src/MesControlAgv.Wpf/Services/SampleWorkstationTestConfirmation.cs`
- `src/MesControlAgv.Wpf/ViewModels/ShineLabDeviceStatusViewModel.cs`
- `tests/MesControlAgv.Wpf.Tests/ShineLabDeviceStatusViewModelTests.cs`

步骤：

1. 新增小型确认接口及 MessageBox 实现，默认选择为“否”；测试使用记录型替身。
2. 为 ViewModel 增加：测试入口可见性、选中任务、忙碌状态、测试状态文本和启动命令。
3. 将观察间隔和最大轮询次数作为构造参数提供生产默认值（2 秒、300 次），测试传入零延迟和较小次数，避免真实等待。
4. 先增加测试：默认隐藏；显示后满足设备在线空闲和任务非 Running 才可启动；取消确认零请求；确认严格一次请求。
5. 增加测试：`Acknowledged=true` 只显示已接收；旧 Completed 在没有本轮运行证据时不能直接完成；观察到 Running 后，只有 Completed + Idle/0 + 结果码0 才完成。
6. 增加测试：明确失败、结果不明、观察超时和取消本地观察均不重新调用启动方法。
7. 实现最小状态观察循环；循环只复用 `GetSampleWorkstationSnapshotAsync`。
8. 选择切换、快照刷新和忙碌状态变化时更新命令可用性和相关绑定属性。

## 任务三：接入默认隐藏的页面控制区

文件：

- `src/MesControlAgv.Wpf/App.xaml.cs`
- `src/MesControlAgv.Wpf/ViewModels/MainViewModel.cs`
- `src/MesControlAgv.Wpf/Views/ShineLabDeviceStatusView.xaml`
- 视现有静态绑定测试结构修改 `tests/MesControlAgv.Wpf.Tests/ControlCenterBoundaryTests.cs`

步骤：

1. `App` 读取 `WPF_ENABLE_SAMPLE_WORKSTATION_TEST_CONTROL`，仅不区分大小写等于 `true` 时启用。
2. 通过 `MainViewModel` 将布尔值传给 `ShineLabDeviceStatusViewModel`；默认参数为 false，现有调用方不变。
3. 工作站任务 DataGrid 增加 `SelectedItem` 双向绑定。
4. 在任务区加入“联调测试”面板，绑定可见性、选中任务摘要、启动命令和状态文本。
5. 页面不增加初始化、导入、暂停或停止按钮。
6. 增加 XAML/边界测试，确认联调面板受配置属性控制，启动按钮绑定正确，禁止项不存在。

## 任务四：验证与交接

步骤：

1. 运行工作站相关 WPF 测试。
2. 运行 WPF 全量测试，确认除基线仓库根目录失败外没有新增失败。
3. 运行 `dotnet build MesControlAgv.sln --no-restore`；若遇到与本功能无关的基线问题，单独记录，不修改其他模块。
4. 检查 `git diff --check`，确认只修改计划内文件。
5. 更新 `docs/SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md`，记录测试入口开关、当前能力边界和后续正式工作流方向。
6. 提交 WPF 联调入口实现和测试，不包含主工作区或无关文档改动。
7. 真机 WPF 联调单独进行：仅在用户明确授权且现场材料就绪后启动一次任务；不在自动验证阶段执行。
