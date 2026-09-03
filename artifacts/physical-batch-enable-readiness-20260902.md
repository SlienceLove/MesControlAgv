# 现场批量执行启用前复核（2026-09-02）

目标：现场恢复后在 WPF 只填写安全监护人、许可前缀和有效期，点击一次即可按料盘标准模板完成全流程。

## 最新只读复核

- MES：`/health = ok`。
- AGV-01：在线，当前位置 `LM1`，无活动任务，控制权 `none`。
- AGV 安全读数：`relocationStatus=1`、`localizationConfidence=0.9525`、无急停、无阻挡、无故障，地图 `guangzhou606 / 1.0.6` 指纹与现场配置一致。
- AUBO ARM-01 / `rob1`：在线，`Running / Normal / Automatic / Stopped`。
- AUBO 程序目录请求：本次客户端等待超时；未执行任何写入。需使用更新后的只读扫描超时配置重新刷新，并确认 `取料盘.pro`、`放料盘.pro`、`回收料盘.pro`。
- 当前安全门禁：`adapter_does_not_hold_control`、`automatic_dispatch_disabled`。这两个阻断符合收尾后的安全模式，不能视为批量执行已就绪。

## 尚未启用

- 未设置 `WPF_ENABLE_PHYSICAL_BATCH=true`。
- `WorkflowFieldNavigationWorker`、`AutoAuthorizeFromRunRequest`、`WorkflowAuboWorker` 和现场自动派发均未打开。
- 未申请 AGV 控制权，未创建新许可，未执行工作流，未加载/启动 AUBO。

## 启用条件

完成程序目录复核、现场作业区和急停监护确认后，作为独立维护变更同时打开 WPF 批量入口、MES 两个 worker 和现场导航功能；再由操作员在 WPF 填写三项参数并确认一次。任何 Unknown、阻挡、超时或状态不明均保持人工处置，不自动重试。
