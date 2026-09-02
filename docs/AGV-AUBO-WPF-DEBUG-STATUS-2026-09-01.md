# AGV + AUBO + 中控运营中心调试状态（2026-09-01）

## 当前结论

- 本地 `FieldSimulation` 已可直接启动，不再与旧 `Development` 站点数组合并。
- 地图站点为 `LM1、LM2、LM4、LM5、LM6、LM7`；LM1 显示“充电原点”，LM7 显示“站点1”，其余显示 LMn。
- 流程编辑器提供 LM1→LM4 到站模板。两个机械臂节点默认不写入程序名；刷新只读目录后，程序名称字段变为下拉选择。
- 生产代码不包含固定的“测试1/测试2”程序名。程序名来自控制器预加载槽位和显式允许列表。
- 现场 AUBO 最新只读结果（`artifacts/aubo-field-readonly-20260901-140905.json`）为 `Running / Normal / Automatic / Stopped`；Dashboard Server 读到当前加载工程 `测试2`，而 0–99 预加载槽位仍为空。Adapter/MES 目录接口已正确返回 `availablePrograms=[测试2]`，证据见 `artifacts/aubo-field-program-catalog-20260901-141200.json`。

## 本地调试启动

先构建：

```powershell
dotnet build MesControlAgv.sln --no-restore
```

可以分别以 `FieldSimulation` 环境启动 Simulator、Adapter、MES（使用独立 SQLite 路径）；WPF 在本地 loopback 下默认也会自动托管这三个 FieldSimulation 服务。若服务已由外部启动，将 `WPF_MANAGE_LOCAL_SERVICES=false`：

```text
WPF_RUNTIME_MODE=simulator
WPF_MANAGE_LOCAL_SERVICES=false
MES_BASE_URL=http://localhost:5045/
ADAPTER_BASE_URL=http://localhost:5041/
SIMULATOR_BASE_URL=http://localhost:5183/
MAP_SMAP_PATH=<RoboshopPro>\maps\guangzhou606.smap
```

需要保留旧兼容配置时，可显式设置 `WPF_LOCAL_SERVICE_ENVIRONMENT=Development`。

窗口标题应为“中控运营中心”。流程页点击“LM1/LM4 AUBO模板”，在机械臂节点中选择程序；未刷新目录时不要填写猜测名称。

## 现场切换顺序

1. 重新接好网线，确认 `以太网` 为 Up，并确认 `192.168.1.102:9012` 可达。
2. 只读预检（默认最多两次、间隔 30 秒）：

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-AuboWsReadOnlyPreflightWithBackoff.ps1 `
     -ControllerHost 192.168.1.102 -MaxAttempts 2 -RetryDelaySeconds 30
   ```

3. 只有预检确认 Automatic、Safety Normal、Runtime 稳定并得到现场授权后，才在中控页面点击“刷新列表”；该动作只读预加载槽位/允许列表。
4. 如需写入预加载槽位，必须单独运行 `Invoke-AuboWsPreloadProgram.ps1` 的双重授权参数；脚本只发一次写请求，超时结果按 Unknown 人工对账，绝不自动重试。
5. 配置实际程序允许列表和 `ControlEnabled=true` 后，再发布流程并进行单步空载测试。

429/5xx 重试只适用于只读 GET；任何 load/run/stop/preload 或 AGV 控制请求都不自动重发。

## 离线 AUBO 模拟器（2026-09-01）

FieldSimulation 现在可显式使用 `Devices:AuboArm:Driver=simulator`：Adapter 通过进程内
loopback 控制器提供状态、程序目录和 `load/run/stop`，不会连接 `192.168.1.102` 或打开
任何 AUBO 端口。模拟配置允许 `测试1/测试2`，运行约 1 秒后自动回到 `Stopped`，用于
演练 MES robot node 的终态观测。生产/physical 配置仍使用 WebSocket 驱动，现场恢复前
必须重新完成只读预检和授权。

## 物理工作流关联（离线实现，未现场启用）

工作流运行监控现可为一个 `Ready Move` 节点创建并授权现场导航验收单，MES 将验收单与
workflow run/node/device operation 持久化关联。默认关闭的
`WorkflowFieldNavigationWorker` 仅消费人员已授权的有效许可；到站证据写回同一 run 后才推进
到 AUBO 节点。WPF 不直接派发 AGV，Unknown 不自动重试。该能力尚未经过实体现场验收，
FieldSimulation 与 PhysicalAcceptance 配置中的 worker 均保持关闭。
## 2026-09-01 标准流程现场完成记录

完整证据：`artifacts/physical-acceptance/standard-flow-evidence-20260901.md`。

已完成 LM1→LM4→LM1 串行流程：LM4 运行 `测试2.pro`，LM1 加载并运行 `测试1.pro`；两次均在超过 60 秒后由现场明确停止并确认 `Stopped`。两段验收单均由 MES 对账为 `arrived`，最终 AGV 位于 LM1、无活动任务、控制权为 `none`；AUBO 目录显示 `测试1|测试2`。

断网前应停止本次物理 MES/Adapter 进程并保留上述运行目录、数据库和日志。离线优化阶段不得复用物理验收数据库，也不得在无网状态下调用任何设备写接口。
