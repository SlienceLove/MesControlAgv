# 现场标准模板一键执行设计与离线实现（2026-09-02）

## 目标

现场恢复后，操作员在 WPF 选择已发布的“料盘标准流程（LM1→LM7→LM2→LM7→LM1）”，
填写一次安全监护人、许可前缀和有效分钟，确认一次即可提交整条流程。后续 Move 与
AUBO 节点由 MES worker 按模板顺序推进，不再要求操作员逐段点击“创建并授权”。

## 已实现的离线能力

- `WorkflowExecutionRequest` 增加可选的 `PhysicalAuthorization`，保存 AGV、操作者、
  安全监护人、许可前缀和过期时间。
- WPF 新增“ 一键现场执行 ”命令，仅对现场显式打开的标准料盘模板、已发布版本可用；
  默认运行模式和默认启动配置均不可用。
- 每次确认只提交一个批量授权；WPF 会显示安全确认对话框并生成唯一 request/correlation ID。
- MES 物理 Move worker 增加可选的 `AutoAuthorizeFromRunRequest`：节点 Ready 时读取
  当前 AGV 快照，确认在线、无活动任务、站点有效且 AGV ID匹配，再为该节点生成唯一许可
  并写入现有验收单审计；随后仍沿用原有物理预检和派发边界。
- 许可 ID 为 `前缀-运行ID-节点ID-a尝试次数`，不会跨节点复用；过期、身份不匹配、状态
  不确定或读取异常均保持节点待人工处理，不自动重试。
- AUBO worker 仍只在 `WorkflowAuboWorker.Enabled=true` 且 Profile 明确允许 ARM-01
  控制时执行；Unknown 不自动重发。

## 现场启用前的独立门禁

以下配置必须由现场完成新鲜 AGV/AUBO 只读预检、确认作业区和急停监护后，作为一次新的
维护变更显式打开；当前签入配置仍全部关闭：

1. WPF 进程：`WPF_RUNTIME_MODE=physical`、`WPF_ENABLE_PHYSICAL_BATCH=true`，并填写
   `WORKFLOW_SAFETY_OBSERVER`、`WORKFLOW_PERMIT_PREFIX`、`WORKFLOW_PERMIT_MINUTES`。
2. MES PhysicalAcceptance：`Profile.features.enableFieldNavigationAcceptance=true`、
   `WorkflowFieldNavigationWorker.Enabled=true`、`AutoAuthorizeFromRunRequest=true`、
   `WorkflowAuboWorker.Enabled=true`。
3. Adapter PhysicalAcceptance：按现场批准的标准运行配置打开对应的现场派发能力；不得
   使用 read-only-preflight 配置冒充批量执行配置。
4. 运行前仍需确认 AGV 位于 `LM1`、定位置信度不低于 `0.90`、无阻挡/急停/故障，AUBO
   为 `Automatic / Normal`，三个实际 `.pro` 名称与发布版本一致。

这些门禁未同时满足时，“一键现场执行”按钮不会出现或请求会被 MES 拒绝；不能通过
WPF 参数绕过 PhysicalAcceptance 或 Adapter 预检。

## 验证

- WPF 端新增请求测试确认：标准已发布模板可以提交一次物理批量授权，默认模式不暴露命令；
  MES worker 测试确认会基于当前 AGV 快照为 Ready Move 创建唯一许可。
- Release 隔离构建和完整解决方案回归通过：937 项通过、5 项既有 E2E 跳过、0 失败；现场服务未因本次改动重启。
- 当前现场 AGV/AUBO 状态保持收尾状态，自动重定位接口仍未实现。
