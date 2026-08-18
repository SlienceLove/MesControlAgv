# 开盖分液工作站中控集成计划

更新日期：2026-08-17
协议依据：`res/开盖分液工作站Http接口文档V1.0.pdf`（V1.0，2026-08-10）

## 1. 结论

开盖分液工作站是与 CIC-D160+、AGV、机械臂和视觉设备并列的新设备。中控不得直接调用厂家 HTTP 接口，也不得把它并入离子色谱仪串口网关。

建议扩展现有 `MesControlAgv.Adapter` 协议宿主，在其中增加独立的开盖分液工作站模块。无需新增网关进程；MES 仍只访问 Adapter 的规范化接口：

```text
WPF 中控
  -> MES 业务 API / 工作流运行时
  -> MesControlAgv.Adapter / SampleWorkstation 模块
  -> 厂家 HTTP API（/Service/...）
  -> 开盖分液工作站
```

职责边界：

- WPF：显示状态、任务和审计信息，提交经过权限校验的业务命令，不接触厂家 URL。
- MES：拥有中控任务、工作流、操作 ID、权限、审批、审计和跨设备联锁。
- Adapter 工作站模块：独占厂家接口的写入权，转换协议，执行幂等保护、超时判断和重启恢复。
- 厂家客户端：只负责厂家字段和 `/Service/...` 路由，不向上泄露不稳定的数据结构。

## 2. 协议初步结论

协议使用 HTTP、UTF-8 和 JSON。文档给出的默认端口是 `8808`，但示例普遍使用 `8082`，现场接入前必须确认实际 IP、端口和基础路径。

设备状态值：

| 厂家值 | 文档含义 | 中控初步映射 |
| --- | --- | --- |
| 0 | 空闲 | `Idle` |
| 1 | 运行 | `Running` |
| 2 | 暂停 | `Paused` |
| 3 | 故障 | `Faulted` |
| 4 | 初始化 | `Initializing` |
| 5 | 离线/软件未连接 | `Offline` |
| 其他或解析失败 | 未定义 | `Unknown` |

可用于第一阶段只读验证的接口包括：

- `GetInstrumentStatus`
- `GetErrorInformation`
- `GetTaskInformationList`
- `GetTaskDetails`
- `GetTaskState`
- 实验流程、物料类型、模块类型、平台布局、轨迹参数和溶剂参数等目录查询

以下接口会改变设备或数据，首期全部禁止：

- `Init`
- `ArmAvoidance`
- `StartExperiment`
- `ImportExperimentalTask`
- `AddExperimentalTask`
- `AddTaskTrajectoryParameter`
- 所有参数或模板导入
- `DeleteTaskRunState`
- `DeleteTaskRecord`

文档中的主要风险：

- 多个写操作使用 HTTP GET，存在误触发、缓存和错误重试风险。
- `Init` 异步返回，但没有操作 ID 或明确的完成关联方式。
- `StartExperiment` 的请求格式、任务关联和重复调用行为不明确。
- `ArmAvoidance` 会真实移动 X 轴，不能当作状态查询。
- 未说明认证、TLS、控制权、并发、幂等、超时和重启恢复。
- `Code=200` 被描述为业务成功，但 HTTP 状态码和失败响应结构不完整。
- `Data` 在不同接口中可能是字符串、数值、布尔值、列表或文件数据。
- `EquipmentNo` 的必填说明和示例不一致，部分响应示例疑似复制错误。
- 文件导入同时出现 multipart、原始请求体和自定义 Base64 拼接等互相矛盾的描述。
- 未提供可靠的暂停、继续、停止、急停和故障复位契约。

## 3. 中控规范化契约

不要照搬厂家接口。MES 和 Adapter 之间使用稳定、符合业务含义的接口：

```text
GET  /api/workstations/{deviceId}/status
GET  /api/workstations/{deviceId}/errors
GET  /api/workstations/{deviceId}/tasks
GET  /api/workstations/{deviceId}/tasks/{taskNo}
GET  /api/workstations/{deviceId}/catalogs/{catalogType}

POST /api/workstations/{deviceId}/initialize
POST /api/workstations/{deviceId}/tasks
POST /api/workstations/{deviceId}/tasks/{taskId}/start
```

后三个命令端点只作为未来设计保留。厂家确认、现场只读验证和空载授权完成前，不注册路由或始终返回明确的 `403 OperationDisabled`。删除、机械臂避让、原始坐标和原始厂家请求不对 MES/WPF 开放。

状态响应至少包含：

- `DeviceId`、`EquipmentNo`、`Online`
- 规范化设备状态和原始厂家状态值
- 当前厂家任务号和规范化任务状态
- 当前错误代码、错误文本和是否需要人工处理
- `ObservedAtUtc`、数据是否过期、最后成功通讯时间
- 只读/控制策略、当前允许的操作
- 控制权状态和是否检测到外部任务

任务命令至少包含：

- MES 任务 ID 和稳定的 `OperationId`
- 厂家 `TaskNo`（创建成功后保存）
- 已批准的流程/模板 ID 和版本
- 源/目标条码与位置
- 液体代码和分液体积
- 操作员、审批人、请求时间和关联 ID

MES 不应允许操作员在生产任务中直接输入运动坐标、夹盖距离、速度、吸液高度等底层参数。此类参数只能来自经过批准并带版本的厂家模板快照。

## 4. Adapter 工作站模块设计

建议在现有 `MesControlAgv.Adapter` 中新增 `SampleWorkstationAdapterModule`，不要扩展 D160+ 专用的 `MesControlAgv.InstrumentGateway`。厂家 HTTP 客户端、任务协调和路由都由该设备模块注册。主要组成：

- `VendorWorkstationHttpClient`：厂家 HTTP 路由、查询参数和不稳定响应结构的唯一所有者。
- `WorkstationStatusNormalizer`：将厂家状态、错误和任务状态转换为中控枚举。
- `WorkstationCommandPolicy`：默认只读，按操作逐项开放。
- `WorkstationOperationRepository`：持久化操作 ID、请求指纹、厂家任务号、最后确认状态和原始证据摘要。
- `WorkstationCommandCoordinator`：先持久化再发送，控制单写者、超时和恢复。
- `WorkstationPollingService`：有界轮询状态和任务，避免 WPF 每次刷新直接打到设备。

统一模块架构不等于强制所有设备运行在同一个进程。HTTP 工作站和 TCP AGV 在同一 Adapter 主机可访问时可以共同加载；D160+ 串口模块必须部署在连接实际 COM 口的控制电脑上，可以继续使用独立宿主，但应逐步复用相同的模块契约、操作策略和审计模型。

关键规则：

1. Adapter 工作站模块同一时间只有一个控制所有者和一个设备写入队列。
2. 命令发出前先保存 `OperationId` 和请求指纹。
3. 超时或断线后状态进入 `Unknown`，先查询厂家任务再决定结果，禁止自动重发 `StartExperiment`。
4. 同一操作 ID 的重复请求返回已保存结果，不重复执行物理动作。
5. 检测到厂家软件或人工创建的外部任务时，中控进入只读并提示人工确认。
6. 原始厂家响应可作为诊断证据受限保存，但不得成为 WPF 的公开契约。
7. 厂家写操作即使内部使用 GET，中控侧仍只通过 POST 命令调用，并设置禁止缓存。

配置建议：

```json
{
  "Devices": {
    "SampleWorkstation": {
      "Enabled": false,
      "ControlEnabled": false,
      "BaseUrl": "http://127.0.0.1:8082/Service/",
      "EquipmentNo": "",
      "RequestTimeout": "00:00:03",
      "PollInterval": "00:00:02",
      "StaleAfter": "00:00:10"
    }
  }
}
```

`BaseUrl` 只是占位，不能依据文档示例直接用于现场。

## 5. 状态机

设备运行状态建议使用：

```text
Offline -> Connecting -> Uninitialized -> Initializing -> Idle
Idle -> Preparing -> Ready -> StartPending -> Running -> Completed
任意非终态 -> Faulted / Unknown / ManualInterventionRequired
```

中控任务状态建议使用：

```text
Draft -> Validated -> Queued -> Creating -> Created -> StartPending
StartPending -> Running -> Completed
任意执行态 -> Failed / Unknown / ManualInterventionRequired
```

约束：

- 厂家只有“等待、运行、完成”等粗粒度任务状态，中控不得虚构更细的设备事实。
- MES 请求超时不等于厂家失败，必须记为 `Unknown`。
- `Unknown` 不允许直接重新开始；必须通过任务号、任务列表和任务详情进行对账。
- 厂家没有确认取消/停止语义前，只允许取消尚未发送到设备的 MES 任务。
- `Init` 完成必须通过状态从 `Initializing` 变为 `Idle` 确认，且需要厂家给出最大时长和失败条件。

## 6. 工作流接入

现有工作流运行时只自动处理 `Move`。建议在枚举末尾追加通用的 `DeviceOperation` 节点，保持现有枚举数值兼容，并增加可注册的设备步骤执行器：

```text
IWorkflowStepExecutor
  - AgvMoveStepExecutor
  - SampleWorkstationStepExecutor
  - future: RobotArmStepExecutor
  - future: VisionStepExecutor
  - future: IonChromatographyStepExecutor
```

`DeviceOperation` 节点保存 `DeviceId`、`OperationCode`、已批准模板 ID/版本以及业务参数。节点发布前由相应设备执行器进行强类型验证，不能依赖任意字符串直接生成厂家命令。

建议的首个跨设备流程：

```text
AGV 到达上料位
  -> 人工确认物料交接（机械臂协议确认后替换）
  -> 条码/位置确认
  -> 工作站就绪检查
  -> 创建工作站任务
  -> 经审批后启动
  -> 轮询并确认完成
  -> 确认工作站运动部件处于安全位
  -> 人工确认卸料（机械臂协议确认后替换）
  -> AGV 取走
```

跨设备联锁必须由 MES 管理：工作站运行或运动部件未回安全位时，禁止机械臂进入；机械臂占用冲突区时，禁止调用工作站运动命令；物料条码、托盘位置和任务绑定不一致时，流程失败关闭。

## 7. 分阶段实施顺序

### 阶段 A：厂家澄清与离线契约

- 确认 IP、真实端口、基础路径和 `EquipmentNo`。
- 获取完整成功/失败响应、错误码、任务样例和接口版本。
- 使用本地假服务器完成 JSON 兼容、超时、异常响应和状态映射测试。
- 输出一个自包含的只读测试包，现场只传输一次，结果统一导出为 JSON/ZIP。

完成条件：所有只读响应均可解析；任何未知值都失败关闭而不是误判为空闲。

### 阶段 B：现场只读验证

- 只查询设备状态、错误、任务列表、任务详情、任务状态和目录数据。
- 连续验证在线、空闲、运行、故障/离线场景中的真实返回。
- 确认厂家控制软件同时运行时是否允许查询，以及是否存在控制权冲突。
- 不调用 `Init`、`ArmAvoidance`、启动、创建、导入或删除接口。

完成条件：固定设备身份，连续查询稳定，并形成真实响应样本和状态映射证据。

### 阶段 C：中控状态页

- 实现 Adapter 工作站模块的只读路由和 MES 代理。
- 增加独立“开盖分液工作站”页面，显示设备、任务、错误、数据新鲜度和操作策略。
- `ControlEnabled=false`，界面不显示可执行命令。

### 阶段 D：任务创建但不启动

- 厂家确认精确请求和幂等行为后，先开放单个已批准模板的任务创建。
- 使用空载/仿真数据验证任务号关联、重复请求和重启恢复。
- 禁止原始轨迹、坐标和参数输入。

### 阶段 E：空载受控启动

- 现场授权后，逐项开放初始化、单任务启动和完成确认。
- 每个动作独立审批、单次执行，并保留前后状态与厂家响应证据。
- `ArmAvoidance`、删除和未确认的停止/复位继续禁用。

### 阶段 F：跨设备联调与生产准入

- 先人工交接，再接机械臂和视觉，最后接 AGV 自动交接。
- 完成碰撞区、条码、托盘位置、安全位、急停和断电恢复验证。
- 通过小批量、有人值守和故障注入后，才评估自动连续运行。

## 8. 必须向厂家确认

1. 实际 IP、端口是 `8808` 还是 `8082`，基础路径是否固定为 `/Service/`。
2. 是否需要认证、来源 IP 白名单或特定请求头。
3. 每个接口的 HTTP 方法、请求编码、必填参数和完整响应 JSON Schema。
4. HTTP 状态码、业务 `Code`、错误代码和错误文本的完整对应关系。
5. `EquipmentNo` 的格式、唯一性和所有接口是否都必须携带。
6. `Init` 如何判断完成、最大时长、失败状态和重复调用行为。
7. `StartExperiment` 如何指定任务，重复调用是否会启动两次，超时后如何查询结果。
8. 是否存在官方的暂停、继续、停止、取消、复位和急停状态查询接口。
9. 厂家软件与中控能否同时连接，如何申请/释放控制权，如何识别外部任务。
10. `ArmAvoidance` 的单位、范围、零点、限位、碰撞联锁和安全前置条件。
11. 所有坐标、距离、速度、体积、液位参数的单位、范围和精度。
12. 文件/模板导入究竟使用 multipart、原始二进制还是自定义 Base64 格式。
13. 设备、厂家软件或中控重启后，运行中任务如何恢复和对账。
14. 完成状态是否代表所有运动部件已回安全位，何时允许机械臂进入。
15. 是否提供仿真服务、测试模式、正式 OpenAPI/Swagger 或更新版协议。

## 9. 当前准入结论

离线只读客户端和 Adapter/MES 查询接口已经完成，但仍不能调用任何可能引起运动、创建、启动、导入、删除或参数修改的接口。第一项现场工作应是确认网络参数并采集只读真实响应，而不是直接尝试启动任务。

## 10. 只读实现状态

Adapter 多设备模块基础和首个工作站 HTTP 模块已经完成：

- `SampleWorkstationAdapterModule` 已注册为独立设备模块，传输类型为 HTTP。
- 设备目录公开 `Enabled` 和 `ControlEnabled`；工作站默认均为 `false`。
- 启用工作站时必须配置 `EquipmentNo`、绝对 HTTP(S) `BaseUrl` 和有界请求超时。
- 当前构建强制 `ControlEnabled=false`；配置为 `true` 会导致 Adapter 启动失败。
- Adapter 仅实现状态、错误、任务列表、任务详情和任务状态五类 GET 路由。
- MES 已提供相同规范化只读路由，WPF 和其他业务调用方无需接触厂家地址。
- 厂家 `Code`、变化的 `Data` 类型、未知状态和错误示例冲突均采用失败关闭或显式 `Unknown` 处理。
- 没有实现 `Init`、`ArmAvoidance`、任务创建、启动、导入、删除、原始厂家请求或任意 POST 命令。
- 通用设备操作持久化将在首个经过厂家确认和现场授权的写命令之前实现；只读阶段不创建无实际语义的操作记录。

当前代码和测试未访问任何现场 IP，也未向工作站发送请求。下一现场步骤仍是确认端口、基础路径和 `EquipmentNo` 后进行只读采样。
