# 2026-09-16：WPF 开盖分液真机联调验收

## 结论

默认隐藏的 WPF 联调入口已经完成真实设备闭环验收：操作员在“中控运营中心”选择已有任务 `TEST-001`，经 WPF 二次确认后单次发送启动请求；厂家主程序完成整机初始化后，第三次人工确认的任务依次显示已接收、运行、完成，现场动作和厂家、Adapter、MES、WPF 四层状态一致。

本轮没有自动重发，也没有初始化、暂停或停止设备的中控命令。三次厂家 `StartExperiment` 请求与用户三次人工确认一一对应。

## 环境

- 隔离工作区：`D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`
- 分支：`feature/sample-workstation-http-readonly`
- WPF 实现提交：`ba077bf`
- 厂家程序：`DLHWorkstation_1625.exe`
- 厂家服务：`http://192.168.200.157:8082/Service/`
- 本机 Adapter：`127.0.0.1:15041`
- 本机 MES：`127.0.0.1:15045`
- WPF 开关：`WPF_ENABLE_SAMPLE_WORKSTATION_TEST_CONTROL=true`
- 测试任务：`TEST-001`，源 `CYC-001-1000`，目标 `FYB-001`，50 μl
- 本机运行日志：`C:\Users\33206\AppData\Local\Temp\mes-workstation-wpf-20260916-current\`

## 测试过程

### 启动前

08:33:11 厂家、Adapter、MES 三层一致：设备 Idle/0、结果码0、任务 Completed。WPF 显示已有任务列表，`TEST-001` 记录号为1，位于倒序任务表底部。

### 前两次人工确认：未进入运行

前两次均由用户在 WPF 选择 `TEST-001` 并在中控二次确认框点击“是”，每次只产生一个厂家 `StartExperiment?TaskNo=TEST-001` 请求。设备只出现初始化相关动作或提示，没有观察到 Running/1、结果码3或任务 Running；接口继续保留旧 Idle/0、结果码0、Completed。

WPF 没有将启动前残留的旧 Completed 误判为本轮完成，而是继续显示等待运行证据。用户截图中的厂家主程序红字“初始化成功！”是最后一条界面提示，不是设备仍处于初始化状态；它也不能证明夹持电机等全部模块已经完成初始化。

该现象再次确认厂家冷启动路径仍存在已知缺口：远程启动触发的自动初始化不足以保证 `Check_IsInit` 所要求的所有模块就绪。中控没有为此自动重发。

### 厂家主程序手动初始化后第三次人工确认

用户明确在厂家主程序点击初始化。08:48:49 再次核对为 Idle/0、结果码0、Completed，累计厂家启动请求基线为2。

- 第三次 WPF 二次确认后只新增一个启动请求。
- 08:53:50：厂家直读同时变为设备 Running/1、结果码3、任务“正在运行”。
- 08:53:50～08:56:17：状态持续稳定，没有假空闲、提前完成或掉线。
- 08:56:20：同时切换为设备 Idle/0、结果码0、任务“任务完成”。
- 08:56:41：Adapter 与 MES 延迟复核仍为 Idle/0、TaskCompleted/0、Completed。
- 用户确认 WPF 黄色联调区显示“任务完成”。

Adapter 日志中精确存在3条实际发送的厂家 `StartExperiment` 记录，对应三次人工确认；没有第四条请求或自动重试。

## 验收结果

已通过：

- 联调入口默认隐藏，显式开关后显示。
- 选择已有任务和 WPF 二次确认。
- 每次确认只发送一次，取消/刷新不启动设备。
- `Acknowledged` 不当作完成。
- 旧 Completed 不会误判为本轮完成。
- Running/1、结果码3和任务 Running 的运行态显示。
- Completed、Idle/0、结果码0三项一致后 WPF 显示完成。
- WPF 关闭本地观察不会发送停止命令。

仍需后续处理：

- 正式完整工作流节点与 MES worker 接入；正式流程不调用 WPF 联调按钮。
- 厂家冷启动自动初始化应覆盖完整初始化范围，至少核对夹持电机；在厂家修复前，现场启动任务前需要先在厂家主程序执行完整初始化。
- 手动停止仍未规划，本阶段不实现、不测试。
- 任务详情的 RequestTime、ProductionTime、CompletionTime 仍为空，优先级低。
- 任务表一键创建等待厂家样表和字段格式。
