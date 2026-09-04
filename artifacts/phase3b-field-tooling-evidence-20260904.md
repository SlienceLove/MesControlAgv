# 第三阶段 B：现场工具可靠性离线证据（2026-09-04）

## 范围

本轮只验证现场准备工具，不启动 MES/Adapter、不连接 AGV/AUBO、不申请授权、不发送设备写入。

## 回放

输入使用第二阶段保存的 `wpf-workflows.json`（包含中文料盘标准流程）。执行：

```text
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Test-FieldWorkflowTooling.ps1
```

结果：

- 9 个节点、8 条边；节点/边 ID 均唯一。
- `取料盘`、`放料盘`、`回收料盘` 中文内容保持。
- 输出为 UTF-8 无 BOM；没有 `/Date(...)/` 包装。
- `publicationAttempted=false`、`deviceWritesAttempted=false`、`automaticRetry=false`。
- 最终回放目录：`artifacts/phase3b-tooling-test-final3-20260904-141500/`。

## 工具门禁

- `Invoke-MesJsonUtf8.ps1` 使用 .NET `HttpClient`、`ByteArrayContent` 和显式 UTF-8 字节；一次请求失败即停止，不调用 `Invoke-WebRequest -Body` 或 `Invoke-RestMethod`。
- `Import-WorkflowGraph.ps1` 默认只导出定义；`-Publish` 是显式开关，草稿、校验、发布各保存独立响应，不自动重试。
- 静态门禁：`mes-offline-release-gate-20260904-phase3b-static-final3.json` 通过。
- 完整门禁：[mes-offline-release-gate-20260904-phase3b-final2.json](mes-offline-release-gate-20260904-phase3b-final2.json)：996 项测试，991 通过、5 个登记跳过、0 失败。

## 交付包

`bin/Verify/PhysicalOneClickPhase3B-Tooling-final4-20260904-142500.zip`

SHA-256：`622451FA6118D059667449187F29CEC339BDFD3A7A9AEF7CC2F1204C8F3697F6`。
