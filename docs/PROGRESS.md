# AGV MES MVP 实施进度总结

最后更新：2026-08-03

## 已完成

- 技术基线：`.NET 8 + WPF`，旧技术方案已清理。

- 固定端口契约：已完成；Simulator `5183`、Adapter `5041`、MES `5045`。

- MES 任务状态、SQLite 持久化、审计事件和 WPF 中控流程已完成。

- Adapter 已覆盖固定操作 ID、失败重试、超时对账和同任务并发幂等。

- 固定路线：`SAMPLE_01 -> ST_PREP_01`。

## 自动化验证

```powershell
dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1
dotnet test MesControlAgv.sln --no-build -p:UseSharedCompilation=false -m:1
```

结果：构建 0 警告、0 错误；Domain 7、MES 8、Adapter 8、WPF 3、E2E 8、Simulator 1，共 35 项通过（包括十任务场景）。

Windows 应用控制策略可能阻止 `bin/Debug` 下未签名的服务 EXE。自动化测试加载 DLL，服务启动脚本使用系统签名的 `dotnet.exe` 直接加载 Web DLL。

## Live 验收

- 健康检查和 `SAMPLE_01 -> ST_PREP_01` 正常闭环：已通过。

- Simulator `fail` 后使用原操作 ID 重试：已通过。

- Simulator `timeout`：设备任务可查询，Adapter 能立即对账为 `moving`，MES 返回 `MovingToPickup`；只有无法确定设备状态时才进入 `Unknown`。

- MES 重启恢复：已使用 DLL 启动脚本完成干净进程级验收，任务恢复为 `MovingToPickup`，事件包含 `Timeout` 和 `ReconciledMoving`。

启动和停止：

```powershell
dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1
.\scripts\run-local.ps1
.\scripts\verify-local.ps1
.\scripts\stop-local.ps1
```

## 下一步

1. 完成最终整分支审查、提交文档和剩余源代码改动。

2. 确认厂商协议和控制权规则后，仅替换 Adapter 的真实设备客户端。

## 真实设备边界

对接真实 AGV 时，只替换 Adapter 的设备客户端实现。MES 生命周期、任务状态、审计事件、WPF 中控和 MES -> Adapter API 契约保持不变。
