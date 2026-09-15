# 开盖分液工作站交接：新会话从这里开始

归档日期：2026-09-14，北京时间。目标：在已打通的真实通讯链路上继续接入中控，完成剩余工作；保持最小实现，不做无关重构，不整理或合并其他分支。

## 先看结论

厂端“单次远程发令→无现场点击直接执行→任务 Running→Completed”已验证成功一次（16:44～16:47）。不必重新排查端口、旧 EXE 空入口或重新搭建协议客户端。

2026-09-15 16:32 使用厂家 `DLHWorkstation_1625.exe` 再次完整验证正常路径：设备 Idle→Running→Idle、任务 Completed→Running→Completed、结果码 0→3→0，现场顶部运行提示与接口同步并在完成后清除。正常运行的设备总状态问题已通过；暂停、停止、故障及冷启动初始化仍未验收。

2026-09-15 已在隔离分支完成 WPF 默认隐藏的联调控制入口：选择已有任务、中控二次确认、单次启动、只读观察 Running→Completed。代码尚未合入主工作区，也尚未通过 WPF 对真机发送启动请求。

剩余重点：现场执行一次 WPF 联调入口验收，然后转入正式完整工作流节点与 MES worker 接入；后续依据厂家样表接入任务导入。批量导入和分液精度尚未验收。手动停止功能目前不纳入中控接入范围，只保留为后续规划项；本阶段不实现、不测试，也不要求厂家为此新增远程停止接口。

归档后没有继续发送设备命令；本机临时测试服务已关闭。新会话先读文件和检查工作区，不自动启动真实设备任务。

## 工作区与提交

| 用途 | 路径 / 分支 | 归档时状态 |
| --- | --- | --- |
| 开盖分液隔离工作区，继续从这里工作 | `D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly` / `feature/sample-workstation-http-readonly` | 代码基线 `29343b8c1bec11b727dc4a6d2ec142aee5ab7580`；其后的归档提交仅含文档和证据 |
| 用户的中控主工作区 | `D:\Project\Github\Mes` / `feature/wpf-ui-layout-optimization` | HEAD `0e785b4bb48165702d35c8992cea3353628d21c0`；大量其他未提交工作，不能覆盖、重置或整体搬入隔离分支 |

本次在主工作区仅新增 `docs/SAMPLE-WORKSTATION-START-HERE.md` 入口指针，不修改主工作区代码、不提交该工作区的其他内容。归档提交号可在隔离分支执行 `git log -3 --oneline` 获取。

已有关键提交：`31417f2` 最小控制、`be7511a` 启动超时兼容、`d5f4e89` 启动失败识别、`29343b8` 中控接入准备优化。

## 已验证事实与当前约定

- 服务：`http://192.168.200.157:8082/Service/`。之前的 `192.168.1.112` 已不是本轮目标。
- MES/Adapter 设备 ID：`SAMPLE-WORKSTATION-01`；测试任务：`TEST-001`。
- 16:30 人工点“是”后真实运行，任务 Running→Completed、返回码 3→0 已验证。
- 16:44:16 单次远程启动，2.058 秒收到确认；16:44:31 任务 Running，用户明确“没有任何点击，就开始执行了”；16:47:15 厂家直读完成，16:47:35 MES/Adapter 同样完成。
- 2026-09-15 16:32:04 单次启动 `TEST-001`，16:32:17 起稳定为设备 Running/1、任务 Running、结果码 3；16:34:46 同时回到设备 Idle/0、任务 Completed、结果码 0。用户确认现场任务完成且顶部红色运行提示消失。
- 最后已知任务为 Completed。这是历史结果，不是下次新发令已完成的证明；任务可复用，必须观察本次变化，不能只看旧 Completed。
- 最新分工：中控二次确认在发送请求之前；确认后发一次，厂家直接执行；取消/关闭确认框则不发请求。不是两次发送启动。
- 发令前取消不需要厂家取消接口。发令后的停止是另一种设备操作，本次未实现、未授权扩大测试。
- 不代点厂家弹窗；不要再沿用旧方案去要求厂家提供人工“否”的取消回传。

## 剩余工作：按这个顺序推进

1. **异常终态与后续停止规划**：1625 版正常运行期间 `GetInstrumentStatus` 已实测返回 `1/Running`，结束回到 `0/Idle`。但静态代码中结果 1、2、-1～-4 没有像成功分支一样调用 `Update_TaskData()`，任务可能继续停留 Running；暂停 2、故障 3、初始化 4 也未主动测试。手动停止功能尚未规划，本阶段不实现远程停止、不安排真机停止测试，只记录此风险供后续决策；当前也不在中控伪造终态。
2. **中控最小确认/取消接入**：对齐现有页面/工作流入口，发令前显示确认；取消请求数为 0，确认请求数为 1；显示“已发出/运行/完成”的实际区别。先用模拟响应做离线验证，不添加复杂状态机、审计框架或自动重试。
3. **当前厂家程序**：已取得并实测 `res/DLHWorkstation_1625.exe`，1496064 字节，SHA-256 `B475E0CEFF4F3EA19BDC3F1F715AD71A2928423EE5AB54257F249DA068B37116`。不要修改厂家二进制；后续若再次更新，必须记录新文件名和哈希。
4. **任务详情时间字段**：RequestTime、ProductionTime、CompletionTime 仍为 null，优先级低于总状态回写。旧 WCF 详情实现没有填这些字段，不能将 null 单独当作没运行。
5. **任务表一键创建**：接口已预留，厂家样表/格式未确定。拿到格式再实现解析和上传，不猜 Excel 列、不提前添加占位导入接口行为。

## WPF 联调入口实现状态

实现提交：`ba077bf feat(wpf): add hidden workstation test control`。设计与计划提交分别为 `1826103`、`efff7c8`。

- 默认不显示。启动 WPF 前设置 `WPF_ENABLE_SAMPLE_WORKSTATION_TEST_CONTROL=true` 才显示“联调测试”区域。
- 入口位于“仪器状态 → 开盖分液”，从现有任务表选择任务后启动。
- 取消确认不发请求；确认后只 POST 一次，不重试。
- `Acknowledged=true` 只显示设备已接收；先观察本轮 Running 证据，再以任务 Completed、设备 Idle/0、结果码0三项一致判定完成。
- 每 2 秒只读刷新，设置严格 10 分钟观察时限；超时、关闭页面或退出只停止本地观察，不停止设备。
- 部分读取失败时保留上次数据显示，但禁用启动按钮，必须成功刷新后才允许再次启动。
- 不包含初始化、建任务、导入、暂停或停止；后续正式工作流不得调用 WPF 测试按钮。

验证：工作站相关 WPF 测试 25/25 通过；排除一个既有且无关的链接工作区根目录用例后，WPF 441/441 通过；解决方案构建 0 警告、0 错误。两轮代码复审已完成，最终无 Critical/Important/Minor 问题。

真机 WPF 验收尚未执行。此前用于命令行真机联调的本机 15041/15045 测试实例已关闭，厂家 192.168.200.157:8082 服务未被关闭。下一轮需重新启动隔离工作区的 MES/Adapter/WPF，确认现场材料和厂家程序就绪后，只在用户明确确认时从 WPF 启动一次任务。

分支清理合并留到用户另行安排。中控接入时按成员整合，不能整文件覆盖主工作区。

## 代码入口与冲突提醒

调用链：WPF/工作流 → MES → Adapter SampleWorkstation 模块 → 厂家 HTTP。

- Application：`ISampleWorkstationReader`、`ISampleWorkstationCommands`、`ISampleWorkstationCapabilityReader`；`ISampleWorkstationTaskImporter` 仅契约。
- MES：`AddSampleWorkstationGateway(configuration)` 注册，`MapSampleWorkstationEndpoints()` 映射。现有 `MapMesDeviceGatewayEndpoints()` 已包含工作站路由，不能重复映射。
- Adapter：`SampleWorkstationAdapterModule`、`SampleWorkstationDriver`、`VendorSampleWorkstationHttpClient`。
- Contracts：`SampleWorkstationContracts.cs`；`Acknowledged` 是命令确认，不是任务完成；结构化错误保留 `errorCode/outcomeUnknown/vendorCode/vendorData`。
- 主工作区另有较完整且签名不同的 `ISampleWorkstationController`、`SampleWorkstationControlledDriver`、`WorkflowSampleWorkstationWorker` 等未提交工作。最小端口命名为 `ISampleWorkstationCommands` 是为避免重名，并非已解决全部合并冲突。
- 能力接口 `Source=AdapterConfiguration` 只是配置能力，不是在线/可执行证明；`TaskImportSupported=false`，没有导入 HTTP 路由。

详细文件清单、配置和兼容事项见 [中控接入说明](SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md)。

## 协议和测试数据

中控/Adapter 对外启动是 POST `/api/workstations/{deviceId}/tasks/{taskNo}/start`，内部才转换为厂家 GET `StartExperiment?TaskNo=...`。厂家 GET `Init` 和 `StartExperiment` 都会控制设备，不可当只读探测调用。

`GetErrorInformation` 返回 Data=3 表示实验已启动，Data=0 表示任务完成；这与设备总状态中的 3=故障不是同一张码表。非 200 业务码、Code=200 但“启动失败”、超时/断线都不自动重发。

`TEST-001` 的已核对配置：源 `CYC-001-1000`、X/Y=1/1；枪头位置 `QT-001`、X/Y=1/1；目标 `FYB-001`、X/Y=1/1；`TargetData` 中 TransferVolume=50 μl。重复启动会再做一次分液，需现场重新确认材料条件。

历史测试配置的 `EquipmentNo=TEST-001` 是占位值，不是已确认的真实设备编号；旧 WCF 状态读取使用全局参数，实际忽略该值。接入正式配置前向厂家确认设备编号。

允许在用户任务范围内使用的只读核对示例：

```powershell
curl.exe --noproxy '*' --connect-timeout 3 --max-time 6 --silent --show-error 'http://192.168.200.157:8082/Service/GetTaskState?TaskNo=TEST-001'
```

若 IP 能 ping 通但 8082 拒绝连接，需现场确认 WCF 服务已监听；仅打开厂家主程序不代表服务已启动。不要扫描其他设备或反复发送启动试探。

## 本机实例与验证基线

本次专用 MES `127.0.0.1:15045`、Adapter `127.0.0.1:15041` 已于 16:52:46 关闭。原 PID 为 31624、36076，仅作历史记录，不要按旧 PID 操作。现场厂家服务没有被关闭。

若后续需重建本机测试实例：沿隔离分支启动，使用独立临时数据库；AGV 用 simulator，AUBO 关闭，物理工作流自动执行关闭。工作站读写配置见接入说明；默认控制关闭，真机发令前需本次现场明确授权。不要复用主工作区生产数据库或直接启动一键物理验收配置。

本轮代码基线的已完成验证（归档阶段未改代码，没有重复跑全套）：

- Adapter 全量 270/270 通过，其中工作站 38 项。
- MES 工作站 14/14 通过，工作站专项合计 52 项。
- 全解决方案构建成功，0 警告、0 错误。
- MES 全量 288 通过、16 失败，为原有工作流 `UnknownReason` 问题；不要把它误算为工作站新回归，也不要未经安排修其他模块。

复现离线检查：

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --filter FullyQualifiedName~SampleWorkstation
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter FullyQualifiedName~SampleWorkstation
dotnet build MesControlAgv.sln --no-restore
```

## 证据阅读顺序

1. 本交接文件。
2. [中控接入说明](SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md)：当前架构和确认/取消约定。
3. [最新版联调记录](diagnostics/2026-09-14-workstation-latest-confirmation.md)：完整时间线，后面的 16:44 结论优先于前面的旧版本缺陷。
4. [归档目录与校验值](archives/sample-workstation/2026-09-14/README.md)：原始请求日志、13 份工具快照，不再依赖临时目录。
5. 仅在定位历史问题时再读 Beta/new/最早远程入口诊断。不要重复推断已修复的空方法或缺少 Add 问题。

反编译工具曾位于 `C:/Users/33206/AppData/Local/Temp/mes-workstation-ilspy-20260914/ilspycmd.exe`，临时工具未随本次归档打包，使用前检查是否仍存在。
