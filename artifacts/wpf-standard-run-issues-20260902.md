# WPF 料盘标准流程现场运行问题记录

日期：2026-09-02（Asia/Shanghai）

本次现场运行期间先记录、不修复，待完整流程结束后统一处理：

1. WPF“刷新机械臂程序目录”响应时间较长，操作员感知为卡顿；本次继续以最终返回结果为准，不重复点击、不自动重试。
2. 程序列表实际返回三项，但界面当前只显示两项，无法滚动查看，且列表控件位置偏下；本次先通过节点配置值/键盘选择完成参数核对，不在运行中调整布局。
3. 第 3 步 MES 发布校验出现 3 个错误：三个 AUBO 节点（取料盘、放料盘、回收料盘）均提示
   `Device 'ARM-01' does not allow workflow control`。根因是 MES PhysicalAcceptance 进程的
   `Profile:workflowDevices[ARM-01]:controlEnabled=false`，不是站点或程序名错误；本次运行后再优化
   配置提示与界面处理。

影响边界：问题属于 WPF 展示与交互，不改变已读取的现场目录、不改变模板节点顺序、不改变设备安全门禁。修复安排在本次完整标准流程结束并保留运行证据后。

本次运行的临时处理：仅在隔离 MES 进程环境变量中将 `ARM-01` 工作流控制能力设为 `true`，保持
`WorkflowFieldNavigationWorker=false`、`WorkflowAuboWorker=false`、自动派发关闭；不修改签入的
PhysicalAcceptance 源配置，不自动执行任何设备动作。

## 收尾状态（2026-09-02）

本次完整现场流程已完成并回到 LM1，详见：[material-wpf-20260902-final-run.md](physical-acceptance/material-wpf-20260902-final-run.md)。问题 1～4 已完成源码修复并通过 Release 构建与 Adapter/MES/WPF 测试；问题 5 保持只读策略，现场 30% 速度以 AuboStudio Automatic 运行屏幕为准。具体改动见：[wpf-standard-run-issues-20260902-addendum.md](wpf-standard-run-issues-20260902-addendum.md)。
