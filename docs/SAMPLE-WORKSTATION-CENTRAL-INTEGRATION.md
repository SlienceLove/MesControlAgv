# 开盖分液工作站：中控接入说明

新会话先读 [2026-09-14 交接文件](SAMPLE-WORKSTATION-HANDOFF-2026-09-14.md)。本轮原始证据见 [归档目录](archives/sample-workstation/2026-09-14/README.md)。

2026-09-15 最新进度：16:32 使用 `DLHWorkstation_1625.exe` 再次完整验证正常路径，设备 Idle→Running→Idle、任务 Completed→Running→Completed、返回码 0→3→0，现场顶部运行提示与接口同步并在完成后清除。隔离分支已增加 WPF 默认隐藏的联调测试入口；正式运行仍由后续完整工作流调度。冷启动初始化、异常终态和任务空时间字段仍需跟踪。详见 [0915 诊断记录](diagnostics/2026-09-15-workstation-init-only.md) 与 [WPF 联调入口设计](superpowers/specs/2026-09-15-sample-workstation-wpf-test-control-design.md)。下文 09-14 内容保留为历史基线，以本段最新进展为准。

日期：2026-09-14。范围：最小通讯模块的接入准备，不包含本次主工作区合并、工作流自动执行或真实设备启动。

## 当前结论

现场 HTTP 读取和命令往返已经打通。2026-09-14 下午的 `DLHWorkstation_Beta.exe` 已修复此前发现的空执行入口、任务未加入列表及定时器恢复问题；经远程任务打开、人工确认和录码、当前页面手动启动后，用户已确认真实设备正在执行。此前问题保留在 [历史诊断](diagnostics/2026-09-14-workstation-remote-start.md)。

16:30 的人工确认运行，以及 16:44 的远程直接运行，均已观察到任务 Running→Completed、厂家返回码 3→0。16:44 轮次用户明确确认“没有任何点击就开始执行”，16:47 厂家直读和 MES/Adapter 均回传完成，本轮仅发送一次请求，厂端不再二次确认的行为已验证。运行中设备总状态曾错误返回 Idle/0；该问题已在 09-15 的 13:24 和 16:32 两轮正常任务中验证修复。任务时间字段仍为 null，批量任务导入尚未接入。

最新约定：由中控在发请求前二次确认。确认后发送一次启动请求，厂家直接执行；用户取消则中控不发送请求。不再要求厂家为中控的发令前取消新增取消回传。手动停止功能尚未规划，本阶段不实现、不测试，也不要求厂家新增停止接口。

## WPF 默认隐藏的联调测试入口

现有“仪器状态 → 开盖分液”页面保留只读状态和任务列表，并增加默认隐藏的联调区。只有启动 WPF 进程前设置以下环境变量，控制区才显示：

```powershell
$env:WPF_ENABLE_SAMPLE_WORKSTATION_TEST_CONTROL='true'
```

控制区只允许选择厂家已经存在的任务，经 WPF 二次确认后启动一次。取消确认不发送请求；确认后不自动重发。界面把 `Acknowledged=true` 显示为“设备已接收”，随后每 2 秒读取状态，必须先看到本轮 Running 证据，再以任务 Completed、设备 Idle/0、结果码0三项一致判定完成。观察最长 10 分钟，超时或关闭界面只停止本地观察，不停止设备。

该入口不提供初始化、建任务、轨迹、导入、暂停或停止。它只用于当前 WPF→MES→Adapter→真机链路测试，后续完整实验通过正式工作流节点和 MES worker 执行；正式工作流不调用 WPF 测试按钮。

## 模块边界

```text
中控 WPF / 工作流
    → MES：工作站路由 / 读取、命令、能力三个应用端口
    → Adapter：SampleWorkstation 模块 / 厂家协议转换
    → 厂家 WCF：192.168.200.157:8082/Service/
```

- WPF 只访问 MES，不保存厂家 URL，不解析厂家 `Code/Data`。
- MES 的 `SampleWorkstationAdapterClient` 只访问 Adapter；厂家协议保留在 Adapter 中。
- `ISampleWorkstationReader`：状态、错误、任务和协议只读查询。
- `ISampleWorkstationCommands`：初始化、启动已有任务；不负责任务调度。
- `ISampleWorkstationCapabilityReader`：查询当前配置允许的能力。
- `ISampleWorkstationTaskImporter`：未来任务表导入端口，目前没有实现、服务注册或 HTTP 导入路由。

本模块没有增加数据库表、后台轮询器、自动初始化或自动启动逻辑。工作流的等待/完成判定由现有中控负责。

## 已约定的确认与取消边界

1. 用户在中控发起任务运行，中控先展示二次确认，此时不向设备发送启动请求。
2. 用户确认后，沿现有 MES → Adapter → 厂家 HTTP 链路发送一次请求；厂家不再弹二次确认，直接执行。
3. 用户取消或关闭确认框，在中控结束该次尚未发出的操作，启动请求数为 0，不调用厂家取消接口。
4. 请求发送后的超时、关闭等待窗口或取消本地等待，不等于设备已停止；运行中停止不属于上述发令前取消，本次不扩展这项能力。

接入验收关注两条：取消不发令；确认只发令一次且可观察 Running→Completed。中控不模拟点击厂家弹窗，不把“二次确认”实现为两次启动请求。此处是接入约定，不代表本次已经修改中控界面。

## 接入入口与配置

MES 注册和路由入口（命名空间分别为 `MesControlAgv.Mes.Services`、`MesControlAgv.Mes.Endpoints`）：

```csharp
builder.Services.AddSampleWorkstationGateway(builder.Configuration);
// builder.Build() 后：
app.MapSampleWorkstationEndpoints();
```

本分支 `Program.cs` 已完成服务注册，`MapMesDeviceGatewayEndpoints()` 已调用工作站路由入口；保留原总入口时不要再重复映射工作站路由。

MES 配置片段：

```json
{
  "SampleWorkstationGateway": {
    "AdapterBaseUrl": "http://localhost:5041/",
    "TimeoutSeconds": 70
  }
}
```

不配置专属地址时复用现有 `Adapter:BaseUrl`，再缺省则使用 `http://localhost:5041/`。若 MES 与 Adapter 不在同一台电脑，应填写 Adapter 的实际地址。MES 超时应大于 Adapter 的请求超时。

Adapter 配置片段（需合入现有 `Devices`，不要替换其他设备配置）：

```json
{
  "Devices": {
    "SampleWorkstation": {
      "DeviceId": "SAMPLE-WORKSTATION-01",
      "EquipmentNo": "",
      "BaseUrl": "http://192.168.200.157:8082/Service/",
      "Enabled": false,
      "ControlEnabled": false,
      "RequestTimeoutMs": 20000,
      "MaximumPageSize": 100
    }
  }
}
```

填入厂家真实设备编号后设置 `Enabled=true` 即可只读联通；明确需要现场控制时再打开 `ControlEnabled`。此前联调用过的 `TEST-001` 是任务号/占位值，不能据此认定它就是厂家设备编号。两个开关默认关闭；直接调用驱动也检查开关。

厂家启动握手约 16 秒，Adapter 默认请求超时为 20 秒（可设 100～60000 ms）；MES 默认 70 秒。模块不配置命令自动重试，也不自动跟随 HTTP 重定向。

## 对外接口

MES 与 Adapter 路径相同，前缀为 `/api/workstations/{deviceId}`：

| 方法 | 相对路径 | 用途 |
| --- | --- | --- |
| GET | `/capabilities` | 配置能力查询，不请求厂家 |
| GET | `/status`、`/errors` | 设备状态、错误和厂家原始错误内容 |
| GET | `/tasks` | 按状态、日期和分页查询任务 |
| GET | `/tasks/{taskNo}`、`/tasks/{taskNo}/state` | 已有任务详情、执行状态 |
| GET | `/protocol/{operation}` | 厂家扩展只读信息 |
| POST | `/initialize` | 初始化；内部转换为厂家 GET `Init` |
| POST | `/tasks/{taskNo}/start` | 启动已有任务；内部转换为 GET `StartExperiment?TaskNo=...` |

任务列表查询支持 `state=Waiting/Running/Completed`、`startDate/endDate=yyyy-MM-dd`、`startNo`（从 1 开始）和 `recordNum`（默认 50）。协议分页沿用这些日期和分页参数；详情操作用 `key`。

`capabilities.source` 固定为 `AdapterConfiguration`，`commands` 由配置开关决定，`protocolReads` 只列扩展协议读取项，不包含上表单独列出的基础状态/任务查询。它不是在线探测，更不能证明厂家已修复远程启动。`taskImportSupported` 当前固定为 `false`。

当前 DLL 缺少的七项扩展读取默认不发送，返回 501：`WorkflowList`、`WorkflowDetails`、`WorkflowTemplate`、`MaterialTypeList`、`PlatformLayoutList`、`PlatformLayoutDetails`、`PlatformLayoutTemplate`。其他十项仅表示可尝试调用，不代表全部已通过现场验收（例如物料参数详情仍有已知厂家实现问题）。

厂家升级后，可在 `Devices:SampleWorkstation:UnsupportedProtocolOperations` 配置完整的不支持项数组；显式数组替换代码默认列表，`[]` 清除代码默认列表。只在确认厂家支持后更改。多份配置文件的数组仍遵循 .NET 按索引合并规则，不要用上层空数组清除下层配置文件已定义的元素；应修改定义该数组的源配置。

## 命令结果如何交给中控

只有已知确认文本才返回命令成功响应：初始化为“正在进行初始化”或“初始化成功”，任务启动为“启动成功”。响应保留原有字段，增加 `acknowledged=true` 和启动任务的 `taskNo`。

`acknowledged` 仅表示厂家确认接收/启动，不代表设备动作或任务完成。中控应继续通过任务状态读取观察 `Running`、`Completed`，不能将 `Code=200`、设备 `Idle` 或错误码 `0` 单独当作本次任务完成。

失败使用 ProblemDetails，MES 保留 Adapter 的 HTTP 状态及扩展字段：

```json
{
  "status": 502,
  "detail": "Sample workstation did not confirm task 'TEST-001' start: 启动失败",
  "errorCode": "workstation_command_unconfirmed",
  "outcomeUnknown": true,
  "vendorCode": 200,
  "vendorData": "启动失败"
}
```

基本分类：参数错误 400、控制关闭 403、设备/任务不存在 404、配置不支持 501、厂家业务错误/异常响应 502、不可用 503、超时 504。厂家 HTTP 404 也映射为 502 的 `workstation_unsupported_operation`，避免与本地设备不存在混淆。

`outcomeUnknown=true` 表示命令可能已进入厂家，结果未确认；不表示“肯定未发送”。“启动失败”、超时、断线均不自动重发。调用方取消等待也不会撤销厂家已收到的命令。下一步先只读查询并与现场核实。没有增加额外审核流程。

## 与当前主工作区的衔接

代码在隔离分支 `feature/sample-workstation-http-readonly`，已包含默认隐藏的 WPF 联调入口，但未合并到 `feature/wpf-ui-layout-optimization`。

主工作区已有签名不同的 `ISampleWorkstationController`、`WorkflowSampleWorkstationWorker`、`SampleWorkstationControlledDriver` 等未提交工作。这里将最小命令端口命名为 `ISampleWorkstationCommands`，避免同名冲突；但不代表整个分支可无冲突覆盖。

后续合并时：

1. 按新增成员合并 Contracts/Application，保留主工作区已有操作请求、工作流控制器和任务管理契约。
2. 将本模块的厂家响应判定、错误透传和超时处理整合进主工作区实际使用的驱动；不要让两套驱动同时注册同一路由。
3. 保留一个 `SampleWorkstationAdapterClient` 和一套服务/路由注册，将两边接口实现合并。工作流通过现有协调层调用最小命令端口，不绕开原有操作管理，也不要在重试循环中反复启动任务。
4. 保留 WPF 测试入口为默认隐藏的诊断能力；正式工作流通过现有协调层执行，不复用 UI 按钮。
5. 编译中控、MES、Adapter，运行两边工作站测试；真机 WPF 验收仅在现场另行授权后发送一次任务。

## 任务表扩展

未来实现 `ImportTasksAsync(deviceId, fileName, Stream, cancellationToken)`，由调用方拥有输入流；导入只创建任务，不隐式启动。拿到厂家样表、字段含义和实际上传格式后，再增加解析、Adapter 上传和 MES 导入路由，并将能力标记改为已支持。现有初始化、启动和查询接口无需改动。

## 离线验证

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --filter FullyQualifiedName~SampleWorkstation
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --filter FullyQualifiedName~SampleWorkstation
dotnet build MesControlAgv.sln --no-restore
```

模块测试仅启动本机临时 HTTP 主机，厂家及 Adapter 上游均为内存响应替身；不访问真实设备。覆盖独立注册/路由、开关、命令确认、错误透传、超时与响应体停滞、禁用能力、配置覆盖及不重试。

本次 WPF 联调入口验证结果：工作站相关 WPF 测试 25/25 通过；排除一个已确认无关的既有根目录用例后，WPF 测试 441/441 通过。若包含该用例则为 441/442，唯一失败是既有 `ShineLabHandoffRehearsalTests` 在链接工作区无法识别仓库根目录。解决方案构建通过，0 警告、0 错误。此前 Adapter 全量 270/270、MES 工作站 14/14 的基线保持不变。本轮自动验证没有启动本地服务，也没有访问真机。
