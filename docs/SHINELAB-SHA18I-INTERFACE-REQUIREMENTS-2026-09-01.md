# ShineLab—中控 TCP 接口需求清单

## 1. 对接目标

本次不再由中控直接打开 D160+/SHA-18i 串口，继续由 ShineLab 负责设备和串口控制。中控只负责：

```
创建样品任务 → 下发样品/通道/检测方法 → 启动 ShineLab → 接收状态、完成、异常和结果
```

沿用《下游表协议》的 TCP + JSON + 换行分帧方式。本次角色确定为：ShineLab 是常驻 TCP Client，我方中控是 TCP Server。

由中控生成全局唯一 `task_uuid`，ShineLab 在所有响应和上报中原样返回；每条请求另带唯一 `strID`，响应必须原样回传该 `strID`。

### 

按照“下游仪表=ShineLab 软件、主控=中控”的定义，确定：

```text
ShineLab（开机常驻）= TCP Client
中控                         = TCP Server
```

ShineLab 启动后主动连接中控并保持长连接；中控负责监听、接收连接、下发命令和接收上报。中控可以随时主动关闭连接，但 ShineLab 必须自动重连并重新发送 `Certification`。该方式符合原下游表协议，也避免在 ShineLab 电脑上开放入站端口。

## 2. 双方职责边界

### ShineLab 负责

- SHA-18i、D160+ 串口和设备状态机；
- 样品位置、通道、方法参数到设备协议的转换；
- 实际进样、检测、清洗、停止和异常处理；
- 设备状态、样品完成、任务完成、错误和检测结果上报。

### 中控负责

- 样品编号、样品名称、样品类型、盘位/位置、通道和检测方法管理；
- 任务创建、任务编号和 `strID` 关联；
- 向 ShineLab 下发配置和启动/停止指令；
- 展示任务状态和结果，并保存接口日志；
- 断线重连后的任务状态恢复和人工处理。

## 3. 必须提供的接口

### 3.1 TCP 连接与通用协议

请 ShineLab 团队按以下已确定的方式提供实现和配置：

| 项目 | 需求 |
|---|---|
| TCP 角色 | **ShineLab 为 Client、中控为 Server**；ShineLab 主动连接中控监听端口 |
| IP/端口 | ShineLab 端提供可配置的中控 IP/端口；中控提供开发、测试、现场端口 |
| 分帧 | 每条 JSON 以 `\\n` 结束，建议同时兼容 CRLF |
| 编码 | UTF-8，无 BOM |
| 长连接 | ShineLab 断线后自动重连，建议 1/3/5/10 秒退避；每次重连重新认证 |
| 认证 | 每次建立/重连必须发送 `Certification`，双方确认认证 body |
| 版本 | ShineLab 提供协议版本号和能力查询方式 |
| 并发 | 中控只保留一个有效 ShineLab 控制连接，连接内请求串行处理 |

当前协议文档说明下游仪表是 TCP Client、平台是 Server。按本次定义，这对应 ShineLab Client、中控 Server。不能直接使用历史 5556/5558 内部 ZMQ 端口；ShineLab 需要提供专用连接配置和测试端口。

连接生命周期要求：

1. ShineLab 开机常驻后主动连接中控；
2. 中控接受连接后，ShineLab 发送 `Certification`；
3. 认证成功后进入状态上报和命令交互；
4. 中控关闭连接、网络中断或进程重启后，ShineLab 自动重连；
5. 每次重连必须重新发送 `Certification`，不能沿用旧连接状态；
6. 同一时间中控只接受一个有效 ShineLab 控制连接，旧连接应被标记为失效。

## 3A. ShineLab 技术团队需要交付给中控的内容

请不要只提供一组 JSON 示例，需要提供可实际运行的 Client 版本或明确的开发接口：

1. TCP Client 连接配置：中控 IP、端口、连接超时和重连间隔；
2. `Certification` 的完整请求和响应示例；
3. `UpdateInfo`、`AlarmInfo` 的完整上报字段和频率；
4. `Config`、`Command` 的完整请求/响应示例；
5. `action` 与 SHA18i 实际动作的映射表；
6. 样品编号、样品名称、类型、盘位、位置、通道、进样方法、检测方式、处理方法和进样体积的字段定义；
7. `SampleFinish`、`TaskFinish`、`TaskError`、`Result` 的完整上报示例；
8. 状态枚举、异常码、停止/急停/清洗语义和设备忙时的返回规则；
9. 结果文件路径、共享方式、文件命名和保留策略；
10. 断线自动重连、重复 `task_uuid` 防重复执行和重连后状态恢复说明；
11. 可供中控联调的测试版本、模拟器或测试账号；
12. 接口负责人、协议版本和计划完成日期。

ShineLab Client 的最低行为要求：开机常驻、主动连接中控、每次重连重新认证、每秒发送状态、收到中控命令后返回响应，并持续上报任务进度和结果。

### 3.2 `Certification`：建立连接

每次建立连接或重连后由 ShineLab Client 向中控 Server 发送一次，并返回明确的成功/失败响应。

需要确认：

- `strID` 的生成规则；
- `equipmentCode` 的 SHA-18i 设备编码，例如 `SHA18I`、`AS18` 或 `SHA-18iA`；
- 认证 body 是否需要中控编号、软件版本或其他字段；
- 认证失败后的错误码和重连间隔；
- 中控 Server 主动断开连接后，ShineLab 是否立即进入重连。

### 3.3 `UpdateInfo`：状态和心跳

建议 ShineLab 至少每 1 秒上报一次，沿用协议中的状态定义：

```text
status = 0 空闲
status = 1 运行中
status = 2 异常
```

SHA18i 需要补充以下字段，不能只返回通用状态：

```json
{
  "task_uuid": "任务号",
  "sampleID": "样品编号",
  "sampleName": "样品名称",
  "channel": "A",
  "position": 11,
  "stage": "Idle|Preparing|Injecting|Detecting|Washing|Completed|Error",
  "progress": 0,
  "errorCode": "",
  "errorMsg": ""
}
```

### 3.4 `AlarmInfo`：异常上报

至少包含：

- `task_uuid`；
- 样品编号和通道；
- 异常码；
- 异常文本；
- 发生时间；
- 是否需要停止任务；
- 建议处理方式。

### 3.5 `Config`：样品和任务配置

中控需要一次下发一个任务或一批样品。沿用协议的 `sampleData`，并固定字段含义：

| 字段 | 类型 | 说明 |
|---|---|---|
| `sampleID` | string | 中控样品唯一编号 |
| `sampleName` | string | 样品显示名称 |
| `type` | int/string | 样品类型，需提供枚举表 |
| `position` | int | 物理样品瓶位置 |
| `mPos` | string | 盘位/机械位置，需明确与 `position` 的区别 |
| `channel` | string | `A` 或 `B` |
| `instrumentMethod` | string | SHA-18i/AS 进样方法编号或名称 |
| `processingMethod` | string | 色谱结果处理/积分方法编号或名称 |
| `volume` | number | 进样体积及单位 |
| `dilution` | object/null | 稀释参数，无则为空 |

`instrumentMethod` 和 `processingMethod` 必须分开。前者控制 ShineLab/自动进样器的进样参数，后者用于检测结果积分和数据处理，不能使用一个字段混用。

`Config` 响应必须返回：

```json
{
  "task_uuid": "ShineLab任务号",
  "result": "Success|Failed",
  "errorCode": "",
  "msg": ""
}
```

### 3.6 `Command`：启动、停止和检测

现有协议示例中 `action` 定义为 0 启动取样、1 停止、2 启动检测，但需要 ShineLab 团队明确 SHA18i 的实际映射。

至少需要支持：

| 操作 | 说明 |
|---|---|
| `Prepare`/初始化 | 回零、检查设备和准备任务 |
| `StartInjection` | 按指定样品、通道、方法执行进样 |
| `StartDetection` | 按检测方式/检测方法启动对应检测流程 |
| `Stop` | 正常停止，明确与急停的区别 |
| `Wash` | 任务后或人工触发洗针 |
| `QueryStatus` | 查询指定任务当前状态 |

每个命令请求还应能携带 `detectionMethod`、`instrumentMethod`、`processingMethod` 和 `channel`。每个命令响应必须包含 `strID`、`task_uuid`、`action`、`result`、`errorCode` 和 `msg`。命令应支持幂等判断：同一个 `task_uuid` 重复提交不能造成重复进样。

### 3.7 `SampleFinish`：单个样品完成

需要上报：

- `task_uuid`；
- `sampleID`；
- 完成时间；
- 通道和位置；
- 成功/失败；
- 失败原因；
- 是否继续下一个样品。

### 3.8 `TaskFinish`：整批任务完成

需要上报：

- `task_uuid`；
- 任务开始时间、结束时间；
- 总样品数、成功数、失败数；
- 任务最终状态；
- 是否生成结果文件；
- 任务失败原因或中止原因。

### 3.9 `TaskError`：任务异常

需要与 `AlarmInfo` 区分：

- `AlarmInfo`：设备实时报警；
- `TaskError`：导致任务失败、暂停或需要人工处理的任务级异常。

建议增加 `errorCode`、`recoverable`、`requiresOperator` 和 `lastKnownStage` 字段。

### 3.10 `Result`：检测结果

结果上报至少需要：

```json
{
  "task_uuid": "任务号",
  "sampleID": "样品编号",
  "testDate": "2026-09-01 10:30:00",
  "instrumentMethod": "进样方法",
  "processingMethod": "结果处理方法",
  "detectionMethod": "检测方式",
  "result": "Success|Failed",
  "data": [
    {
      "testItem": "Li",
      "value": 3.2,
      "unit": "mg/L",
      "quality": "OK"
    }
  ],
  "file": {
    "name": "结果文件名",
    "path": "共享目录相对路径",
    "sha256": "文件校验值"
  }
}
```

请明确数值单位、空值表示、异常值表示、结果文件共享目录和文件保留时间。

## 4. 建议在原协议基础上补充的能力

以下不是第一天联调的阻塞项，但建议一并纳入版本规划：

1. `GetCapabilities`：查询 SHA18i 是否支持双通道、稀释、洗针和连续样品；
2. `QueryTask`：中控断线重连后按 `task_uuid` 查询最终状态；
3. `CancelTask`/`PauseTask`/`ResumeTask`：明确正常停止和急停边界；
4. `ProtocolVersion`：协议版本和字段版本协商；
5. `ResultQuery`：结果上报丢失时由中控主动补取；
6. `CommandAck`：命令已接收、已开始、已完成分阶段确认；
7. 统一错误码表和状态枚举表；
8. 连接断开后自动重连，并保证不会重复下发进样命令。

## 5. 我方中控 TCP Server 需要完成的事项

收到 ShineLab 可联调版本后，中控侧完成：

1. 监听固定 TCP 端口，接受 ShineLab Client 连接；
2. 按 `\\n` 处理 JSON 分帧、粘包和半包，统一使用 UTF-8；
3. 同时只保留一个有效 ShineLab 控制连接，旧连接自动失效；
4. 接收并响应 `Certification`，记录连接、协议版本和软件版本；
5. 接收 `UpdateInfo`/`AlarmInfo`，维护在线、空闲、运行、异常和离线状态；
6. 生成 `task_uuid` 和 `strID`，向 ShineLab 下发 `Config`、`Command`；
7. 将中控表单中的样品编号、盘位、位置、通道、进样方法、检测方式和处理方法转换为协议字段；
8. 接收 `SampleFinish`、`TaskFinish`、`TaskError`、`Result` 并更新任务；
9. 保存原始请求、响应、上报报文、时间和错误信息；
10. 处理 10 秒无消息超时、连接断开、自动重连后的重新认证和状态恢复；
11. 按 `task_uuid` 去重，避免重复下发导致重复进样；
12. 在 WPF 展示任务状态、设备状态、异常和检测结果；
13. 对停止、异常和未知状态提供人工确认入口，不在通讯超时后自动重复启动。

中控 Server 不负责打开 SHA18i/D160+ 串口，所有设备动作由 ShineLab 完成。

WPF 当前提供“设备状态查询”和“ShineLab 任务下发（实验）”页面：状态页只从 MES 获取 ShineLab 推送的设备列表并查看在线状态、任务进行中、当前样品/通道/位置、阶段、进度和报警；实验任务页只通过 MES 的高层 Config/Command 接口下发业务 JSON，不打开串口、不发送任意原始帧。

建议中控 Server 作为独立后台连接服务运行（不要把 TCP 监听和读写循环写在 WPF 窗体代码中）：

```text
ShineLab Client → CentralTcpServer/ConnectionManager → 任务服务/数据库 → WPF
```

实验阶段可以与 WPF 同机运行，生产阶段建议作为 MES 或中控后台服务运行。WPF 只订阅连接状态、任务状态和结果，避免关闭页面导致 TCP 连接和任务状态丢失。

目前项目已增加初版中控 TCP Server、状态缓存和查询接口；仍需根据 ShineLab 最终字段和错误码完成联调适配，不再使用中控直连串口方案。

中控 Server 的实验配置示例：

```json
{
  "ShineLabTcp": {
    "Enabled": true,
    "ListenAddress": "0.0.0.0",
    "Port": 5500,
    "StaleAfterSeconds": 10,
    "CommandTimeoutMs": 10000
  }
}
```

该配置只开启 ShineLab TCP 状态接收，不会打开 SHA18i 或 D160+ 串口。

中控服务端已预留高层业务下发接口（不暴露原始串口报文）：

```text
POST /api/shinelab/devices/{equipmentCode}/config
POST /api/shinelab/devices/{equipmentCode}/command
```

接口会通过当前 ShineLab Client 长连接发送 `Config`/`Command`，按 `strID` 等待响应；设备未连接、超时或断线时返回服务不可用，WPF 不直接操作 TCP Socket。

MES 任务闭环接口：

```text
POST /api/shinelab/tasks
GET  /api/shinelab/tasks
GET  /api/shinelab/tasks/{taskUuid}
POST /api/shinelab/tasks/{taskUuid}/config
POST /api/shinelab/tasks/{taskUuid}/command
```

任务和事件写入 SQLite 的 `ShineLabTasks`、`ShineLabTaskEvents` 表。`task_uuid` 是唯一键：相同内容重复创建返回原任务，不会重新执行；相同 `task_uuid` 携带不同样品/方法时返回冲突。ShineLab 推送的 UpdateInfo、SampleFinish、Result、TaskFinish、TaskError 会自动更新任务状态和事件时间线。

Config/Command 在发送后发生超时或断线时，任务进入 `Unknown`，禁止自动重发；必须先通过 ShineLab 状态/任务查询确认实际执行结果。若发送前设备尚未连接，任务保留为 `Created`，连接恢复后可以人工重试。

MES 启动恢复时，`Configuring`、`Commanding`、`Accepted`、`Running`、`Stopping` 等未闭环任务统一转为 `Unknown` 并写入恢复事件，避免进程重启后误判任务未执行并重复进样。

实验阶段也可用以下 HTTP 请求验证下发链路（`equipmentCode`、字段枚举和 `action` 以 ShineLab 最终版本为准）：

```powershell
$config = @{
  taskUuid = 'task-001'
  sampleData = @(@{ sampleId = 'S-01'; sampleName = '标准样'; type = '1'; position = 11; mPos = '1'; channel = 'A'; instrumentMethod = 'AS18-M01'; processingMethod = 'IC-P01'; detectionMethod = 'Normal'; injectionVolume = 25; injectionVolumeUnit = 'uL' })
} | ConvertTo-Json -Depth 8

Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5045/api/shinelab/devices/SHA18I/config `
  -ContentType 'application/json' -Body $config

$command = @{ taskUuid = 'task-001'; action = 0; sampleId = 'S-01'; channel = 'A' } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri http://127.0.0.1:5045/api/shinelab/devices/SHA18I/command `
  -ContentType 'application/json' -Body $command
```

### 无真实设备联调方式

在中控电脑启用 MES ShineLab TCP Server：

```powershell
$env:ShineLabTcp__Enabled = 'true'
$env:ShineLabTcp__ListenAddress = '0.0.0.0'
$env:ShineLabTcp__Port = '5500'
dotnet run --project src\MesControlAgv.Mes --urls http://0.0.0.0:5045
```

再运行仓库提供的 ShineLab Client 模拟脚本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Test-ShineLabTcpPush.ps1 `
  -ServerAddress 127.0.0.1 -Port 5500 -RunningSeconds 5 -HoldSeconds 30
```

查询状态：

```powershell
Invoke-RestMethod http://127.0.0.1:5045/api/shinelab/server/status
Invoke-RestMethod http://127.0.0.1:5045/api/shinelab/devices/status
```

脚本只模拟 TCP JSON 推送，不读取或控制任何串口设备。

如需验证 Config/Command 和任务持久化，启动可响应命令的模拟 Client：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\Run-ShineLabTcpSimulator.ps1 `
  -ServerAddress 127.0.0.1 -Port 5500 -RunSeconds 120
```

然后在 WPF“ShineLab 任务下发（实验）”页面创建任务，依次发送 Config 和 Command；模拟 Client 会返回 Success，并推送 UpdateInfo、SampleFinish、Result、TaskFinish，MES 最终任务应进入 `Completed`。

## 6. 联调验收标准

### 第一轮：连接和只读

- 连接成功后收到 Certification 响应；
- 心跳/状态连续接收 10 分钟无断线；
- 状态可区分空闲、运行、异常和离线；
- 中控能显示 ShineLab 当前连接状态和设备状态。

### 第二轮：单样品任务

- 中控创建一条样品任务；
- ShineLab 正确收到样品编号、位置、通道和方法；
- ShineLab 返回任务号和接收结果；
- 中控下发启动；
- 中控收到样品完成、任务完成和结果；
- 请求和响应通过同一个 `strID`/`task_uuid` 关联。

### 第三轮：异常和恢复

- ShineLab 忙时拒绝第二个任务；
- 停止、设备异常、缺瓶和通信中断能够上报；
- TCP 断开后自动重连并重新 Certification；
- 重连后中控可查询任务最终状态；
- 重复发送相同任务不会重复进样。

## 7. 时间计划建议

时间从 ShineLab 团队提供“可连接的测试端口、字段说明和模拟/现场程序”开始计算：

| 时间 | 目标 | 交付物 |
|---|---|---|
| 1 个工作日 | 协议确认 | 角色、IP/端口、设备编码、字段和错误码确认表 |
| 2～3 个工作日 | 基础联调 | Certification、心跳、状态、Config、Command 响应 |
| 3～5 个工作日 | 单样品闭环 | 启动、单样品完成、任务完成、异常和结果回传 |
| 1～2 周 | 实验版本 | WPF 任务表单、日志、重连、重复消息和人工恢复 |
| 2～4 周 | 生产版本 | MES 任务持久化、结果归档、网络部署、稳定性和现场验收 |

如果 ShineLab 只能提供串口动作而不能提供稳定 TCP 服务，时间将取决于其软件改造周期；请技术团队在会议后明确“协议确认日期、测试版本提供日期、现场联调日期和正式版本日期”。

## 8. 请 ShineLab 技术团队回复的清单

1. 确认 ShineLab 按 TCP Client 开机常驻运行，中控按 TCP Server 监听；
2. 提供中控 Server 的开发/测试 IP、端口和防火墙要求；
3. 提供 ShineLab Client 的连接配置、自动重连和重认证行为；
4. 提供 SHA18i 的 `equipmentCode`；
5. 提供 Certification 完整示例和认证失败处理；
6. 提供 Config、Command 的完整请求/响应示例；
7. 提供 `action` 到 SHA18i 实际动作的映射；
8. 提供方法、通道、盘位、位置、体积和检测方式字段定义；
9. 提供状态、错误码、停止、急停和清洗语义；
10. 提供 SampleFinish、TaskFinish、TaskError、Result 完整示例；
11. 提供结果文件路径、共享方式和保留策略；
12. 提供模拟器或测试账号/测试设备、协议版本、接口负责人和计划完成日期。
