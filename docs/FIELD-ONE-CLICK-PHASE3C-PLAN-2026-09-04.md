# 现场一键全流程：第三阶段 C 切片计划（2026-09-04）

第三阶段 A 已解决 AUBO 程序目录刷新过慢（短 TTL 缓存、并发合并、`fresh=true`
完整扫描）；第三阶段 B 已交付 UTF-8 HTTP、图文档导入和现场工具回放。本切片
集中处理第二阶段现场收尾清单中剩余的关联、监控和 WPF 交接问题。当前仍在离线
环境推进，不连接 AGV/AUBO，不申请现场授权，也不执行现场流程。

## 目标与顺序

1. 让每次 AUBO `load/run/stop` 都能携带并回显 workflow、node、device-operation、
   request 和 correlation 身份；worker 复用持久化的 device-operation ID，避免
   再产生无法对账的独立写入。
2. 在 MES 边界核对关联记录。完整关联不匹配时在 Adapter 前拒绝；没有关联的
   手工操作保留兼容路径，但必须产生 `AUBO_UNCORRELATED_WRITE` 明确告警和响应字段。
3. 为临时现场监控器提供正式只读脚本，统一展开 PowerShell 5.1 的真实数组及
   `value/items/data` 包装，并用离线 JSON 回放证明不泄漏包装、不重放设备写入。
4. 为 WPF 增加显式启动绑定（`--workflow-execution-id` / `--workflow-request-id`，
   或对应环境变量）。绑定失败不清空已有运行，也绝不猜测“最新运行”；成功时
   自动切到流程运行监控页。

## 安全边界

- 所有关联验证和监控读取都是只读/审计动作；脚本不包含 POST、PUT、DELETE，失败
  只记录并等待下一次只读轮询，不自动重试任何设备写入。
- AUBO 控制仍由启动配置 `ControlEnabled` 和现有安全状态门禁共同控制。未知、
  超时、断线或关联不明的写入继续进入人工核销路径。
- 现场恢复仍必须遵守“新鲜只读预检 → 新授权 → 只执行一次完整流程”；本切片不
  改变 PhysicalAcceptance 的单车、手工授权和自动派发关闭策略。

## 离线验收

- Contracts/Adapter/MES/WPF 定向构建 0 warning/0 error。
- AUBO driver、MES proxy、workflow worker 回归覆盖：关联字段回显、同一 durable
  operation ID、ID 不匹配拒绝、未关联告警。
- `scripts/Test-PhysicalWorkflowMonitor.ps1` 回放顶层数组、`value/items/data` 包装
  和 scalar，输出节点/操作/时间线数量正确，`wrappersLeaked=false`、
  `deviceWritesAttempted=false`、`automaticRetry=false`。
- 完整离线 Release 门禁通过后，生成新的独立部署目录/压缩包和 SHA-256 manifest；
  包内记录固定源码快照，避免与并行 WPF/UI 提交混用。

## 现场后续

本切片完成后只交付可审计部署包和现场执行清单。除非重新获得现场人员明确确认，
不连接设备、不申请新授权、不重复使用第二阶段的 execution/request/permit ID。

## 本轮执行结果

- Release 构建：0 warnings / 0 errors。
- 完整离线门禁：`artifacts/mes-offline-release-gate-20260904-phase3c-final6.json`，
  1004 passed / 5 allowed skipped / 0 failed，`releaseEligible=true`。
- 监控回放：`artifacts/phase3c-monitor-test-final3-20260904/`，数组包装已展开，
  `wrappersLeaked=false`、`deviceWritesAttempted=false`、`automaticRetry=false`。
- 独立部署目录：
  `bin/Verify/PhysicalOneClickPhase3C-Correlation-final-20260904-165050/`。
  压缩包校验值记录在同名 `.zip.sha256` sidecar 文件中。
- 本轮没有连接 AGV/AUBO，也没有申请或执行现场授权；下一次现场操作仍须重新
  只读预检、重新授权并只执行一次完整流程。
## WPF 外部运行交接示例

外部一次性客户端取得明确 execution ID 后，在启动 WPF 前设置
`$env:WPF_INITIAL_WORKFLOW_EXECUTION_ID = '<guid>'`，或传入
`--workflow-execution-id <guid>`；若只有 admission request ID，则使用
`WPF_INITIAL_WORKFLOW_REQUEST_ID` / `--workflow-request-id`。不要填写第二阶段旧
execution、request 或 permit ID。
