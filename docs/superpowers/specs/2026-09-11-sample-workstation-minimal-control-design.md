# 开盖分液工作站最小控制与任务导入扩展设计

日期：2026-09-11  
状态：方案已获项目方确认，待书面设计审阅  
目标分支：`feature/sample-workstation-http-readonly`

## 1. 目标

在已经完成的真实设备只读链路上增加最小控制闭环：初始化工作站、启动一个已经存在的任务，并继续使用现有任务状态接口观察执行结果。同时为后续“上传厂家任务表并一键创建任务”保留稳定扩展点；在厂家任务表格式未确认前，不猜测 Excel 字段或实现文件解析。

现场已确认服务地址为 `http://192.168.200.157:8082/Service/`。真实 WCF DLL 表明 `Init` 和 `StartExperiment` 都是 HTTP GET，但 Adapter 和 MES 对上层统一暴露 POST，避免把读取与控制混在一起。

## 2. 本阶段范围

新增两个控制入口：

```text
POST /api/workstations/{deviceId}/initialize
  -> GET /Service/Init

POST /api/workstations/{deviceId}/tasks/{taskNo}/start
  -> GET /Service/StartExperiment?TaskNo={taskNo}
```

控制完成后的状态读取继续使用现有入口：

```text
GET /api/workstations/{deviceId}/status
GET /api/workstations/{deviceId}/errors
GET /api/workstations/{deviceId}/tasks/{taskNo}
GET /api/workstations/{deviceId}/tasks/{taskNo}/state
```

本阶段不实现创建任务、删除任务、机械臂避让、模板导入、轨迹导入，也不从自动测试向真实设备发送命令。

## 3. 代码边界与数据流

在现有 `ISampleWorkstationReader` 之外增加 `ISampleWorkstationController`，提供 `InitializeAsync` 和 `StartTaskAsync`。实际驱动同时实现读取和控制接口；设备控制仍由现有 `DeviceOperationPolicy.EnsureControlEnabled` 判断，配置只需要已有的 `Enabled` 和 `ControlEnabled` 两个开关。

`VendorSampleWorkstationHttpClient` 增加一个共用的请求执行方法。该方法支持厂家定义的 GET 命令，并复用已经验证的 `{ Code, Data }` 解析逻辑。初始化和启动返回统一的 `SampleWorkstationCommandResponse`，包含设备 ID、操作名、厂家业务码、原始 `Data` 和响应时间。

Adapter 执行厂家请求，MES 只代理规范化 POST 和 GET，不拼接厂家 URL。调用链保持为：

```text
调用方 -> MES POST -> Adapter POST -> 厂家 WCF GET -> { Code, Data }
```

## 4. 基本错误处理

- `deviceId` 不存在、设备未启用或 `ControlEnabled=false` 时，不向厂家发送请求。
- `taskNo` 为空时返回参数错误。
- 厂家 HTTP 非成功状态、空响应、无效 JSON 或业务码非 200 时，沿用当前 Adapter 错误映射。
- 控制请求不自动重发。调用方通过现有只读状态接口判断设备和任务的实际状态。

## 5. 后续任务表一键创建扩展

后续稳定入口定义为：

```text
POST /api/workstations/{deviceId}/tasks/import
Content-Type: multipart/form-data
file=<厂家任务表>
```

应用层预留独立的 `ISampleWorkstationTaskImporter`，避免把文件解析塞入控制接口。厂家任务表格式明确后，实现流程为：

1. 接收任务表文件；
2. 根据确认后的规则读取任务号和任务名称；
3. 将原始文件流发送到厂家 `POST /Service/ImportExperimentalTask`；
4. 返回创建出的厂家任务号；
5. 使用现有任务详情和任务状态接口确认任务已创建。

本阶段只让厂家 HTTP 客户端的请求执行与响应解析能够复用于未来 POST 文件流，不创建占位 Excel DTO，不猜测列名。以后增加导入能力时，MES 调用路径、设备配置和状态查询都无需调整。

## 6. 验证

离线测试覆盖：

- 初始化映射到 `GET /Service/Init`；
- 启动映射到 `GET /Service/StartExperiment?TaskNo=...`；
- 两个控制入口在 `ControlEnabled=false` 时被拒绝；
- Adapter 和 MES 返回统一命令响应；
- 厂家业务错误和无效响应保持现有错误映射。

代码测试完成后，真实设备验证分两步人工执行：先初始化并读取设备状态，再选择现场明确允许执行的已有测试任务启动并轮询状态。每一步只发送一次命令。

## 7. 验收标准

- Adapter 和 MES 均提供初始化、启动已有任务两个 POST 入口；
- 默认配置保持 `ControlEnabled=false`；
- 开启控制后能通过真实设备完成一次初始化响应和一次已有测试任务启动响应；
- 任务执行状态可通过现有只读接口持续读取；
- 后续实现任务表导入时无需修改现有初始化、启动和查询接口。
