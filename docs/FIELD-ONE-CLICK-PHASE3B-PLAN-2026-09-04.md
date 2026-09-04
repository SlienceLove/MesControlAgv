# 现场一键全流程：第三阶段 B 切片计划（2026-09-04）

第三阶段 A 已完成 AUBO 目录缓存、`fresh=true` 强制新鲜扫描和独立 Release 包。本切片只处理第二阶段现场清单中的“准备工具可靠性”，不连接 AGV/AUBO、不申请授权，也不执行任何设备写入。

## 目标

1. 为 Windows PowerShell 5.1 提供统一的 UTF-8 JSON HTTP 入口，消除默认编码、`/Date(...)/` 日期和多值参数转发差异。
2. 将批准的 `mes.workflow.graph` 转换为 MES `WorkflowDefinition` 的过程做成可审计命令；默认只生成文件，只有显式 `-Publish` 才调用草稿/校验/发布接口。
3. 所有 HTTP 请求只发送一次；失败、超时或未知结果不自动重发。

## 交付范围

- `scripts/Invoke-MesJsonUtf8.ps1`：使用 .NET `HttpClient`、显式 UTF-8 字节和 ISO-8601 检查；拒绝 `.NET /Date(...)/` 日期包装。
- `scripts/Import-WorkflowGraph.ps1`：UTF-8 图文档读取、九节点料盘模板校验、ID 重映射、定义导出和可选发布；输出文件拒绝覆盖。
- `scripts/Test-FieldWorkflowTooling.ps1`：离线回放测试，验证中文节点、节点/边数量、唯一 ID、无设备写入和脚本单次请求契约。

## 安全边界

- 默认不发布、不启动服务、不连接设备；`-Publish` 仅访问 MES 工作流草稿/校验/发布接口，不调用 AGV/AUBO 写接口。
- 不猜测工作流、程序或设备地址；输入模板和 `MesBaseUrl` 必须由操作员显式提供。
- 任何 HTTP 失败都保留错误并停止，不自动重试；后续现场流程仍须“新鲜只读预检 → 新授权 → 一次完整流程”。

## 验收标准

- Windows PowerShell 5.1 下离线回放能生成 UTF-8（无 BOM）定义，中文保持原字节，9 个节点/8 条边和模板顺序正确。
- 发布模式的每个 HTTP 阶段都有独立响应文件，且工具源码没有 `Invoke-WebRequest -Body`、隐式重试或 PowerShell 日期对象序列化。
- 定向测试、完整 Release 构建和离线门禁通过；新工具不改变现有 AUBO/AGV 控制门禁。

## 本轮执行结果

- `scripts/Test-FieldWorkflowTooling.ps1` 已在 Windows PowerShell 5.1 下完成离线回放：中文保持、9/8 数量和 ID 唯一性通过，`publicationAttempted=false`、`deviceWritesAttempted=false`。
- 回放证据目录为 `artifacts/phase3b-tooling-test-final3-20260904-141500/`；默认导入产物目录为 `artifacts/phase3b-workflow-import-20260904-141000/`。
- `scripts/verify-offline-release.ps1 -SkipTests` 已增加并通过 UTF‑8 工具、导入器和回放脚本静态门禁；完整门禁也已在稳定快照通过，报告为 `artifacts/mes-offline-release-gate-20260904-phase3b-final2.json`（996 项测试，991 通过、5 个登记跳过、0 失败）。
- 新独立交付包为 `bin/Verify/PhysicalOneClickPhase3B-Tooling-final4-20260904-142500.zip`，SHA-256 为 `622451FA6118D059667449187F29CEC339BDFD3A7A9AEF7CC2F1204C8F3697F6`；包内 `Tools/` 携带三个脚本，manifest 指向本计划。
- 详细证据见 `artifacts/phase3b-field-tooling-evidence-20260904.md`。

该包固定在门禁通过时的源码快照 `34b1e65`。之后并行 WPF/UI 任务已产生新的提交（当前工作树仍在推进）；这些改动未纳入本包。现场部署前必须待并行任务结束后重新构建、测试并生成新的 manifest，不得把不同快照混用。
