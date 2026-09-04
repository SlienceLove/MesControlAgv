# AUBO 程序目录刷新优化离线验证（2026-09-04）

## 结论

普通目录刷新现在使用 Adapter 进程内 30 秒短 TTL 缓存；同一 ARM-01 的并发请求合并为一次扫描。缓存命中会返回 `isCached=true`，并保留原始 `observedAtUtc` 与 `cacheExpiresAtUtc`。`load`、`run`、`stop` 进入可能写入边界时立即失效缓存。

现场只读预检和验收脚本使用 `GET /api/robot-arms/ARM-01/programs?fresh=true`，该请求绕过缓存并继续完整扫描 0..99 槽位。扫描的 100 ms 节流和 15 秒完整预算未缩短；因此新鲜证据的等待时间仍可能约 11 秒，这是有意保留的安全门禁。

## 自动化证据

- `AuboArmProgramCatalogCacheTests`：TTL 命中/过期、并发合并、fresh 绕过、失效和不完整结果不缓存，3 项通过。
- `AuboArmProgramDriverTests.Successful_load_invalidates_the_catalog_cache`：写操作后重新扫描，1 项通过。
- `AdapterCompositionRootTests.Aubo_catalog_cache_is_shared_across_http_scopes`：不同 HTTP scope 共享缓存，1 项通过。
- `AdapterAuboArmClientTests.Program_catalog_can_request_a_fresh_scan_explicitly`、`AuboArmProgramApiTests`、`MesClientHttpContractTests`：fresh 查询参数逐层转发，均通过。
- 当前快照最终离线门禁：[mes-offline-release-gate-20260904-phase3-cache-final.json](mes-offline-release-gate-20260904-phase3-cache-final.json)，992 项测试，987 通过、5 个登记跳过、0 失败。
- 当前快照部署包：`bin/Verify/PhysicalOneClickPhase3-cache-final-20260904-121500.zip`，SHA-256 `7D7E5E2D805121AA4552C55CDA9D3FD5BCCC16312D1E47FFFB748F78B3E9E08A`。

## 现场约束

本记录仅来自离线模拟器和契约测试；本轮未连接 AUBO/AGV，也未改变现场运行证据。下一次现场使用前仍须执行新鲜只读预检并由现场人员确认授权。
