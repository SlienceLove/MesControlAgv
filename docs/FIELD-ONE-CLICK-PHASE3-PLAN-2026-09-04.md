# 现场一键全流程：第三阶段计划（2026-09-04）

第二阶段已在新授权下完成一次九节点现场流程并取得完整证据。本阶段只在离线环境推进产品修复和可重复验收；重新连接设备前仍须执行新的只读预检、取得新的授权，并且只执行一次完整流程。

## 阶段目标

1. 集中处理第二阶段现场清单中的工具、编码、监控和运行关联问题。
2. 先解决 AUBO 程序目录刷新等待过长的问题，同时保留现场安全门禁：完整新鲜扫描不能被缓存或截断所替代。
3. 输出可审计的测试结果、Release 构建和独立部署包，供下一次现场验证。

## A：本次先执行的低风险修复

- AUBO 目录普通读取增加可配置短 TTL（默认 30 秒）缓存；同一设备的并发读取合并为一次扫描。
- `load`、`run`、`stop` 进入可能写入边界时立即失效目录缓存；不完整、超时或离线目录不进入成功缓存。
- 目录接口增加 `fresh=true` 查询参数。现场只读预检和验收脚本必须使用该参数，继续扫描配置的全部槽位并记录 `ObservedAtUtc`。
- 返回 `IsCached` 和 `CacheExpiresAtUtc`，WPF 显示“缓存/新鲜读取”及原始观测时间，避免把旧目录当作新鲜现场证据。

## B：后续离线任务（按风险排序）

1. 提供统一 UTF-8 .NET HTTP/PowerShell 包装器：ISO-8601 日期、UTF-8 字节请求、稳定的多值参数转发；失败请求不自动重发。
2. 将图文档到 `WorkflowDefinition` 的转换和发布做成正式、可审计的 UTF-8 工具，并加入九节点模板校验。
3. 修正现场监控脚本的数组/标量处理，加入真实 JSON 回放测试。
4. 为 WPF 增加显式 execution ID 初始绑定和回归测试，避免外部一次性提交的运行不显示在界面中；不猜测“最新运行”。
5. 为 AUBO `load/run/stop` 增加 workflow/run/device-operation correlation；未关联写入至少产生明确告警，再评估标准会话是否拒绝。

## AUBO 刷新验收标准

- 首次普通读取仍返回完整目录；重复读取在 TTL 内不重新扫描，并明确 `IsCached=true`。
- 并发普通/新鲜请求只触发一次底层扫描；`fresh=true` 绕过已有缓存。
- 任一成功写操作后下一次普通读取重新扫描；旧扫描的迟到结果不能重新填充缓存。
- 超时、槽位读取错误或离线响应不会被缓存。
- 完整扫描门禁仍为 `ProgramCatalogMaxSlots` 个槽位、现有节流和 `ProgramCatalogScanTimeoutMs`；不通过减少槽位或自动重试来换取表面速度。

## 交付门禁

- Adapter、MES、WPF 定向测试通过，且新增缓存/`fresh` 契约测试通过。
- 完整 Release 构建 0 警告、0 错误；离线门禁无非预期失败或跳过。
- 新部署目录和压缩包使用独立路径，不覆盖第二阶段现场证据或部署包。
- 现场恢复前重新阅读本计划和最新进度，执行“新鲜只读预检 → 新授权 → 一次完整流程”。

本轮交付已满足上述 A 阶段门禁：当前工作树快照的最终报告为
`artifacts/mes-offline-release-gate-20260904-phase3-cache-final.json`，部署包为
`bin/Verify/PhysicalOneClickPhase3-cache-final-20260904-121500.zip`。为避开本机运行中的本地服务锁定 Debug 二进制，门禁脚本新增了可选的
`-Configuration Release -NoBuild -NoRestore -DisableBuildServers` 参数；默认调用方式不变。

第三阶段 C 的关联、监控和 WPF 交接计划见
[FIELD-ONE-CLICK-PHASE3C-PLAN-2026-09-04.md](FIELD-ONE-CLICK-PHASE3C-PLAN-2026-09-04.md)。完成后
必须重新生成独立 Release 包，不与 A/B 包混用源码快照。
