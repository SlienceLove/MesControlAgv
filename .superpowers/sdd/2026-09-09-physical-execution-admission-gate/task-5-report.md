# Task 5 离线验证与交接报告

日期：2026-09-09
验证基线：`e29289c890cb58de50b66682a7eebcc82111e3b0`
范围：仅内存 fake、回环测试服务器和本地 SQLite；未访问现场 IP/端口，未启动 MES、Adapter、WPF 或 PhysicalAcceptance 真实服务，未连接 AGV 命令端口，未执行 AUBO load/run/stop、Modbus 或 DI/DO 写入。

## 命令与实际结果

| 命令 | 实际输出/结果 |
| --- | --- |
| `dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore -m:1` | `测试总数: 224`，`通过数: 224`，退出码 `0`。覆盖 admission policy/boundary、workflow runtime/API/control、field-navigation acceptance/worker、Task API/service、AGV/AUBO direct endpoint、AUBO worker、PhysicalSafetyAction、schema/startup reconciliation。 |
| `dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-build --no-restore -m:1` | `测试总数: 411`，`通过数: 411`，退出码 `0`。首次带构建运行未完成：并发 `MesControlAgv.Wpf (PID 38312)` 锁定 `bin\\Debug\\net8.0-windows\\MesControlAgv.Domain.dll` 与 `MesControlAgv.Contracts.dll`，出现 MSB3026/MSB3027/MSB3021；按约束未停止该 Host。 |
| `dotnet test tests/MesControlAgv.WorkflowContract.Tests/MesControlAgv.WorkflowContract.Tests.csproj --no-restore -m:1` | `失败: 0，通过: 71，已跳过: 0，总计: 71`，退出码 `0`。 |
| `dotnet test tests/MesControlAgv.Simulator.Tests/MesControlAgv.Simulator.Tests.csproj --no-restore -m:1` | `失败: 0，通过: 5，已跳过: 0，总计: 5`，退出码 `0`。 |
| `dotnet test MesControlAgv.sln -m:1` | 各已运行项目合计 `690 passed / 5 skipped / 0 failed`。5 个 skip 均为既有 `CompoundTaskIntegrationTests` 的 `Requires full AGV simulator setup`；无新增 skip。命令退出码 `1`，原因是 WPF 构建阶段被 PID 38312 锁定上述 DLL，达到 10 次重试后报 MSB3027/MSB3021；WPF 测试未由该全量命令执行。 |
| `dotnet build MesControlAgv.sln -c Release --no-restore -m:1` | `已成功生成。0 个警告，0 个错误`，退出码 `0`。 |
| `git diff --check` | 退出码 `0`；仅 Git 对两个并发修改文件提示 LF→CRLF 预警，无 whitespace error。 |

## 静态检查

- `git diff 21648dd..HEAD` 与配置检索未发现本轮把 `PhysicalReadinessSupervisor.Enabled`、physical workers、automatic dispatch、field-navigation 或 WPF physical batch 默认改为 `true`；相关默认仍为关闭。没有改动 IP、SSID、Bridge、NAT、端口、地图事实或 ShineLab 写入链。
- MES AGV/AUBO 启动、继续和 direct-write HTTP/service 入口均先经过统一 physical admission；只读 health/status/map/catalog/readiness 路由保持独立，未被写门禁阻断。
- 写调用保持零自动重试；AGV 幂等只读恢复是单次重读，不适用于写请求。进程恢复路径只读并落 Unknown/人工核销，不调用 cancel/release/stop/dispatch/load/run；一次性安全收尾只在明确关联的终态路径执行，结果不确定即 Unknown。相同 safety action ID 的只读重放不访问 Adapter。
- 全部验证未启动真实服务、未访问设备；未生成或提交现场 evidence。

## 未决阻塞与交接

- 未决阻塞：WPF 并发本地 Host `PID 38312` 仍锁定 Debug 依赖 DLL，因此解决方案全量测试命令退出 1。未杀进程、未覆盖或删除锁定文件；可在该 Host 释放后重跑原命令确认全量 WPF 纳入。
- 该阻塞不影响已用现有程序集完成的 WPF `411/411` 定向测试，也不影响 Release 构建 `0/0`。
- 本轮仅离线加固；设备断电未访问。下次上电需新鲜只读预检、新 `SupervisorInstanceId`/设备 epoch 和新授权；本报告不宣称无线或现场全流程已由本轮执行。
