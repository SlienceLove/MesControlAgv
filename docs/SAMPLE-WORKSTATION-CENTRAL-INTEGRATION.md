# 开盖分液工作站：中控接入说明

日期：2026-09-14。范围：最小通讯模块的接入准备，不包含本次主工作区合并、工作流自动执行或真实设备启动。

## 当前结论

现场 HTTP 读取和命令往返已经打通。当前远程启动阻塞的证据指向厂家主程序：提供的新版 EXE 中 `Cmd_LoadForm(int)` 是空方法，远程任务分支还会跳过定时器恢复。详见 [厂家程序诊断](diagnostics/2026-09-14-workstation-remote-start.md)。现场实际运行文件与配套 DLL 仍需厂家确认。

这不等于真实控制闭环验收通过：初始化已有现场成功反馈；任务启动、运行和完成仍待厂家修复后验证。

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

代码在隔离分支 `feature/sample-workstation-http-readonly`，未合并到 `feature/wpf-ui-layout-optimization`。

主工作区已有签名不同的 `ISampleWorkstationController`、`WorkflowSampleWorkstationWorker`、`SampleWorkstationControlledDriver` 等未提交工作。这里将最小命令端口命名为 `ISampleWorkstationCommands`，避免同名冲突；但不代表整个分支可无冲突覆盖。

后续合并时：

1. 按新增成员合并 Contracts/Application，保留主工作区已有操作请求、工作流控制器和任务管理契约。
2. 将本模块的厂家响应判定、错误透传和超时处理整合进主工作区实际使用的驱动；不要让两套驱动同时注册同一路由。
3. 保留一个 `SampleWorkstationAdapterClient` 和一套服务/路由注册，将两边接口实现合并。工作流通过现有协调层调用最小命令端口，不绕开原有操作管理，也不要在重试循环中反复启动任务。
4. 编译中控、MES、Adapter，运行两边工作站测试；厂家修复后再人工验证一次启动到完成的真实闭环。

## 任务表扩展

未来实现 `ImportTasksAsync(deviceId, fileName, Stream, cancellationToken)`，由调用方拥有输入流；导入只创建任务，不隐式启动。拿到厂家样表、字段含义和实际上传格式后，再增加解析、Adapter 上传和 MES 导入路由，并将能力标记改为已支持。现有初始化、启动和查询接口无需改动。

## 离线验证

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --filter FullyQualifiedName~SampleWorkstation
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --filter FullyQualifiedName~SampleWorkstation
dotnet build MesControlAgv.sln --no-restore
```

模块测试仅启动本机临时 HTTP 主机，厂家及 Adapter 上游均为内存响应替身；不访问真实设备。覆盖独立注册/路由、开关、命令确认、错误透传、超时与响应体停滞、禁用能力、配置覆盖及不重试。

本次验证结果：Adapter 全量 270/270 通过（其中工作站 38 项）；MES 工作站 14/14 通过；解决方案构建通过，0 警告、0 错误。MES 全量为 288 通过、16 失败，保留原有工作流 `UnknownReason` 问题，没有在本次工作站优化中修改。单独复核 `WorkflowSimulatorDispatcherTests.Dispatcher_marks_an_ambiguous_dispatch_unknown_and_never_retries_it` 仍在 `WorkflowRuntimeRecordPersistence.cs:214` 抛出 `Unknown outcome requires a controlled UnknownReason`，该逻辑及相应测试未改动。
