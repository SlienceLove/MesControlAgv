# 中控系统 LIMS 实验任务接口协议 v1.0（草案）

- 文档状态：对外评审草案
- 适用对象：LIMS / SDL 自驱动实验平台与中控系统的内网集成
- 编写日期：2026-09-20
- 当前实现状态：仅文档提案，尚未修改中控接口、代码或数据库
- 目标：让 LIMS 以稳定、简单、可追踪的方式批量导入实验任务，并查询中控执行状态和实验结果

> 本文面向双方接口评审。协议确认后，另行安排接口实现、联调和发布；本文中的接口地址不代表当前系统已经提供生产接口。

## 1. 设计目标

LIMS 负责样品、批次和实验任务来源；中控系统负责：

1. 校验实验方案和已发布流程版本；
2. 接收并持久化 LIMS 批量任务；
3. 根据方案编排 AGV、工作站、仪器和实验流程；
4. 记录任务状态、步骤状态、审计信息和执行结果；
5. 向 LIMS 提供任务状态、结果摘要和结果文件引用。

设计原则：

- 内网优先，使用普通 HTTP REST/JSON，不引入消息队列或专用 SDK。
- 提交接口采用异步语义，不等待设备执行完成。
- 一次请求可以提交多个任务，每条任务独立接受或拒绝。
- 使用 requestId、externalBatchId、externalTaskId 实现幂等。
- 所有响应和错误都带 requestId，便于审计和联调。
- LIMS 只面向实验任务和结果，不直接调用 AGV、AUBO、仪器或厂商协议。
- 结果采用结构化摘要加文件引用，不把大文件嵌入任务响应。
- unknown 表示执行结果无法确认，必须人工核验，不允许 LIMS 自动重试。

## 2. 通信架构

~~~text
LIMS / SDL
   |
   | HTTP REST + JSON UTF-8
   | X-Api-Key（可配置）
   v
中控系统对外集成接口
   |
   +-- 方案和流程版本校验
   +-- 样品与参数校验
   +-- 批量实验任务接收
   +-- 任务排程和设备编排
   +-- 执行状态与审计持久化
   +-- 结果摘要和文件引用
   ^
   |
   +-- LIMS 定时查询状态和结果
~~~

第一版采用单向发起、双向查询：

- LIMS 主动调用中控的提交和查询接口；
- 中控不要求 LIMS 提供回调端口；
- 中控不主动访问 LIMS 网络地址；
- 后续如确有实时性需求，再增加可选回调，不作为 v1 依赖。

## 3. 传输约定

| 项目 | 约定 |
| --- | --- |
| 协议 | HTTP REST |
| 数据格式 | JSON |
| 字符集 | UTF-8 |
| 时间格式 | ISO 8601，建议带时区偏移；服务端统一保存 UTC |
| 基础路径 | /api/integration/v1/lims |
| 认证 | X-Api-Key 请求头 |
| 请求体大小 | 建议不超过 1 MB |
| 单批任务数 | 默认不超过 100 条 |
| 结果文件 | 通过下载地址获取，不内嵌大文件 |
| 接口版本 | URL 固定版本号，向后兼容时不改变 v1 |

示例请求头：

~~~http
POST /api/integration/v1/lims/batches HTTP/1.1
Host: mes-control-center.local
Content-Type: application/json; charset=utf-8
Accept: application/json
X-Api-Key: <configured-api-key>
~~~

API Key 不能放在 URL、查询参数或业务 JSON 中，也不能写入普通业务日志。

## 4. 对外接口清单

| 方法 | 路径 | 用途 | 是否必须 |
| --- | --- | --- | --- |
| GET | /capabilities | 查询协议版本、服务状态和可用能力 | 建议 |
| GET | /plans?status=published | 查询可导入的已发布实验方案 | 必须 |
| POST | /batches | 批量提交实验任务 | 必须 |
| GET | /batches/{externalBatchId} | 查询批次接收和执行汇总 | 必须 |
| GET | /tasks/{externalTaskId} | 查询单条任务状态 | 必须 |
| GET | /tasks/{externalTaskId}/result | 查询结果摘要和结果文件 | 必须 |

完整路径为：

/api/integration/v1/lims/{relative-path}

现有系统的 /api/experiment-plans、/api/experiment-jobs、/api/experiment-runs 和 /api/samples 等接口属于中控内部业务接口，不要求 LIMS 直接依赖其字段和生命周期。

## 5. 查询能力和实验方案

### 5.1 查询能力

~~~http
GET /api/integration/v1/lims/capabilities
~~~

响应示例：

~~~json
{
  "protocolVersion": "1.0",
  "service": "mes-control-center",
  "serverTime": "2026-09-20T01:30:00Z",
  "acceptsBatchSize": 100,
  "supportsPolling": true,
  "supportsCallback": false,
  "resultFileMode": "download-url",
  "plans": [
    {
      "planCode": "CIC_STANDARD",
      "planVersion": 3,
      "name": "离子色谱标准实验",
      "status": "published",
      "description": "标准样品检测流程",
      "estimatedDurationMinutes": 45,
      "workflowSummary": [
        {
          "order": 1,
          "stepCode": "MOVE_TO_INSTRUMENT",
          "name": "样品转运",
          "estimatedDurationMinutes": 8
        },
        {
          "order": 2,
          "stepCode": "CIC_ANALYSIS",
          "name": "仪器分析",
          "estimatedDurationMinutes": 30
        }
      ],
      "parameterDefinitions": [
        {
          "name": "injectionVolume",
          "type": "number",
          "required": false,
          "unit": "uL"
        }
      ]
    }
  ]
}
~~~

### 5.2 查询已发布方案

~~~http
GET /api/integration/v1/lims/plans?status=published
~~~

v1 只允许 LIMS 引用已发布、可执行的方案版本。草稿、已归档或校验失败的版本不能用于任务提交。

对外使用稳定的 planCode + planVersion；中控内部的 PlanId、WorkflowId 和节点 ID 不要求 LIMS 保存或填写。

## 6. 批量提交实验任务

### 6.1 请求

~~~http
POST /api/integration/v1/lims/batches
~~~

请求字段：

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| requestId | string | 是 | 本次 HTTP 请求唯一号，用于幂等 |
| externalBatchId | string | 是 | LIMS 批次业务号 |
| source | string | 是 | 调用方标识，例如 LIMS |
| submittedAt | datetime | 是 | LIMS 提交时间 |
| tasks | array | 是 | 实验任务数组，默认最多 100 条 |
| tasks[].externalTaskId | string | 是 | LIMS 单条任务号，批次内唯一 |
| tasks[].sampleId | string | 是 | 样品号 |
| tasks[].sampleBatchId | string | 是 | 样品批次号 |
| tasks[].planCode | string | 是 | 已发布方案编码 |
| tasks[].planVersion | integer | 否 | 方案版本；不填时使用约定的默认版本 |
| tasks[].priority | integer | 否 | 优先级，默认 0 |
| tasks[].parameters | object | 否 | 方案允许的参数覆盖 |
| tasks[].metadata | object | 否 | LIMS 扩展信息 |
| tasks[].requestedAt | datetime | 否 | 期望执行时间 |
| tasks[].remark | string | 否 | 备注 |

请求示例：

~~~json
{
  "requestId": "LIMS-20260920-000001",
  "externalBatchId": "BATCH-20260920-001",
  "source": "LIMS",
  "submittedAt": "2026-09-20T09:30:00+08:00",
  "tasks": [
    {
      "externalTaskId": "SAMPLE-001",
      "sampleId": "SAMPLE-001",
      "sampleBatchId": "BATCH-20260920-001",
      "planCode": "CIC_STANDARD",
      "planVersion": 3,
      "priority": 50,
      "parameters": {
        "injectionVolume": "10"
      },
      "metadata": {
        "operator": "lims-user",
        "sourceFile": "lims-export-20260920.csv"
      },
      "remark": "常规检测"
    }
  ]
}
~~~

### 6.2 响应

校验和接收成功后返回 HTTP 202 Accepted。202 只表示中控已接收并持久化任务，不表示实验已经完成。

~~~json
{
  "requestId": "LIMS-20260920-000001",
  "externalBatchId": "BATCH-20260920-001",
  "receivedAt": "2026-09-20T01:30:03Z",
  "acceptedCount": 1,
  "rejectedCount": 0,
  "tasks": [
    {
      "externalTaskId": "SAMPLE-001",
      "status": "accepted",
      "centerTaskId": "optional-internal-trace-id",
      "message": null
    }
  ]
}
~~~

批次允许部分成功。单条任务被拒绝时，不影响同一批次中其他合法任务。

## 7. 任务状态查询

### 7.1 查询批次

~~~http
GET /api/integration/v1/lims/batches/BATCH-20260920-001
~~~

响应字段建议包括：

- externalBatchId
- totalCount
- acceptedCount
- runningCount
- completedCount
- failedCount
- cancelledCount
- unknownCount
- tasks：每条任务的外部任务号、状态、当前步骤和最后更新时间

### 7.2 查询单条任务

~~~http
GET /api/integration/v1/lims/tasks/SAMPLE-001
~~~

响应示例：

~~~json
{
  "externalTaskId": "SAMPLE-001",
  "externalBatchId": "BATCH-20260920-001",
  "sampleId": "SAMPLE-001",
  "sampleBatchId": "BATCH-20260920-001",
  "planCode": "CIC_STANDARD",
  "planVersion": 3,
  "status": "running",
  "currentStep": {
    "order": 2,
    "stepCode": "CIC_ANALYSIS",
    "name": "仪器分析",
    "deviceId": "CIC-D160"
  },
  "createdAt": "2026-09-20T01:30:03Z",
  "startedAt": "2026-09-20T01:35:20Z",
  "updatedAt": "2026-09-20T01:40:10Z",
  "completedAt": null,
  "resultAvailable": false,
  "error": null,
  "requestId": "LIMS-20260920-000001"
}
~~~

### 7.3 对外状态定义

| 状态 | 含义 | 是否终态 | LIMS 行为 |
| --- | --- | --- | --- |
| accepted | 已接收且通过基本校验 | 否 | 继续查询 |
| queued | 已进入排程或执行队列 | 否 | 继续查询 |
| running | 正在执行 | 否 | 继续查询 |
| waiting_manual | 等待人工确认、人工核验或设备恢复 | 否 | 展示告警，等待中控处理 |
| completed | 执行完成，结果可查询 | 是 | 查询结果并结束轮询 |
| failed | 有明确失败原因 | 是 | 查询错误详情，不自动重提 |
| cancelled | 已取消 | 是 | 结束轮询 |
| unknown | 执行结果无法确认 | 是 | 必须人工核验，不得自动重试 |

状态顺序原则上为：

~~~text
accepted -> queued -> running -> completed
                         |       -> failed
                         |       -> cancelled
                         |       -> waiting_manual -> running/completed/failed
                         \-------> unknown
~~~

unknown 表示中控无法证明设备动作是否完成，不等同于普通失败。LIMS 不得仅凭网络超时再次提交同一个实验任务。

## 8. 实验结果查询

### 8.1 查询结果

~~~http
GET /api/integration/v1/lims/tasks/SAMPLE-001/result
~~~

任务未完成时建议返回 HTTP 409，并使用 RESULT_NOT_READY。

任务完成后响应示例：

~~~json
{
  "externalTaskId": "SAMPLE-001",
  "resultStatus": "available",
  "completedAt": "2026-09-20T02:12:33Z",
  "summary": {
    "overall": "pass",
    "method": "CIC_STANDARD",
    "sampleId": "SAMPLE-001",
    "items": [
      {
        "name": "chloride",
        "value": "12.30",
        "unit": "mg/L",
        "status": "pass"
      }
    ]
  },
  "files": [
    {
      "fileId": "result-file-001",
      "fileName": "SAMPLE-001-result.pdf",
      "contentType": "application/pdf",
      "sizeBytes": 182034,
      "downloadUrl": "http://mes-control-center.local/api/integration/v1/lims/files/result-file-001",
      "expiresAt": "2026-09-27T02:12:33Z",
      "sha256": "hex-encoded-sha256"
    }
  ],
  "requestId": "LIMS-20260920-000001"
}
~~~

### 8.2 结果约定

- summary 只返回双方约定的业务摘要，不暴露厂商私有协议字段。
- files 返回文件元数据和下载地址；下载地址应具备有效期和校验值。
- 无结果文件时，files 返回空数组，不暴露本地文件系统路径。
- 结果还未采集完成时，resultAvailable 为 false。
- 结果不完整但任务已结束时，必须明确 resultStatus 为 partial 或 failed。
- 文件下载失败不能改变已经确认的实验执行状态；LIMS 可按文件地址重新下载。

## 9. 幂等、超时和重试

### 9.1 幂等规则

- requestId 标识一次提交请求。
- externalBatchId 标识 LIMS 批次。
- externalTaskId 标识 LIMS 单条实验任务。
- 同一 externalTaskId 重复提交且业务字段一致：返回原任务接收结果，不重复创建。
- 同一 externalTaskId 重复提交但方案、样品或关键参数不一致：返回 HTTP 409 和 TASK_ID_CONFLICT。
- LIMS 应在网络超时后使用原 requestId 重试，不能生成新任务号盲目重提。

### 9.2 推荐重试策略

建议只重试 HTTP 408、429、502、503、504，或 TCP 连接失败/响应超时；重试必须使用相同 requestId。

不建议自动重试 HTTP 400、401、403、404、409、422，以及业务状态为 unknown 的任务。

建议退避时间为 5 秒、15 秒、30 秒，最多 3 次；超过次数转为人工告警。

## 10. 错误响应和错误码

统一错误响应：

~~~json
{
  "requestId": "LIMS-20260920-000001",
  "code": "PLAN_NOT_PUBLISHED",
  "message": "实验方案未发布或版本不可用",
  "details": {
    "externalTaskId": "SAMPLE-001",
    "planCode": "CIC_STANDARD",
    "planVersion": 3
  }
}
~~~

| HTTP | code | 含义 |
| --- | --- | --- |
| 400 | INVALID_REQUEST | JSON、字段格式或必填字段错误 |
| 401 | UNAUTHORIZED | API Key 缺失或无效 |
| 403 | FORBIDDEN | 调用方没有该能力权限 |
| 404 | TASK_NOT_FOUND / PLAN_NOT_FOUND | 外部任务或方案不存在 |
| 409 | DUPLICATE_REQUEST | 请求已处理，返回原结果 |
| 409 | TASK_ID_CONFLICT | 同一任务号的业务字段发生变化 |
| 409 | RESULT_NOT_READY | 任务尚未形成可读取结果 |
| 422 | PLAN_NOT_PUBLISHED | 方案未发布或不可执行 |
| 422 | PLAN_VERSION_NOT_FOUND | 方案版本不存在 |
| 422 | SAMPLE_INVALID | 样品信息不合法或重复 |
| 422 | PARAMETER_INVALID | 参数不符合方案定义 |
| 409 | RESOURCE_UNAVAILABLE | 当前资源或排程不满足执行条件 |
| 500 | INTERNAL_ERROR | 中控内部处理异常 |
| 503 | SERVICE_UNAVAILABLE | 中控或依赖设备服务暂不可用 |

## 11. 认证和内网部署

最小部署方式：

- 中控开放一个内网 HTTP 端口；
- LIMS 使用固定主机名或 IP 访问；
- 每个调用方配置独立 API Key；
- 中控按来源 IP、API Key 和接口权限进行审计；
- API Key 支持禁用和轮换；
- 生产环境建议启用 HTTPS，但不把证书体系作为接入硬性前置条件。

后续可选增强：HTTPS 双向证书、OAuth 2.0、IP 白名单、网关限流、回调通知、OpenAPI 文件和官方 SDK。

## 12. 联调流程

1. 双方确认 planCode 与中控已发布方案版本的映射表。
2. 中控提供测试地址、API Key、可用方案和参数定义。
3. LIMS 调用 capabilities，确认协议版本和可用方案。
4. LIMS 使用 1～3 条测试任务调用 batches。
5. 中控返回每条任务的 accepted/rejected 结果。
6. LIMS 使用 externalTaskId 轮询任务状态。
7. 任务完成后调用 result，下载结果文件并校验 sha256。
8. 双方验证重复提交、参数错误、方案不可用、执行失败和 unknown 场景。
9. 通过测试后再切换生产地址和生产 API Key。

## 13. 与当前中控模型的映射

本协议只定义稳定的外部视图，内部实现可以继续使用现有模型：

| LIMS 字段 | 中控内部概念 |
| --- | --- |
| externalBatchId | 实验批次/外部关联号 |
| externalTaskId | ExperimentJob 的外部关联号或集成映射 |
| sampleId | ExperimentJob.SampleId / 样品记录 |
| sampleBatchId | ExperimentJob.SampleBatchId |
| planCode + planVersion | 已发布 ExperimentPlan |
| workflow steps | 方案中的 WorkflowSteps |
| parameters | ExperimentJob.Parameters |
| currentStep | 当前 ExperimentStepRun / WorkflowStepId |
| result summary | 运行结果和设备操作结果的业务投影 |
| files | 结果文件引用，不暴露本地路径 |
| requestId | 集成请求审计号 |

中控内部的 PlanId、WorkflowId、WorkflowRunId、设备任务号和厂商任务号，不要求 LIMS 作为提交条件。需要排障时可以作为可选 trace 字段返回，但不应成为 LIMS 业务主键。

## 14. 明确不在 v1 范围内

- LIMS 直接控制 AGV、AUBO、仪器或工作站；
- LIMS 直接提交底层 workflow 节点；
- LIMS 传入内部设备任务号；
- 使用 WebSocket、MQTT、AMQP 等消息协议替代 REST；
- 强制要求 LIMS 提供回调服务；
- 在接口中传输大体积原始数据文件；
- 对 unknown 任务自动重试或自动重新派发；
- 为 LIMS 单独开发必须安装的 SDK/Python 包；
- 修改当前中控已有 API、数据库结构或设备安全策略。

## 15. 版本和变更规则

- v1 字段新增必须保持向后兼容；删除字段或改变语义必须升级版本。
- 新增状态时，LIMS 应将未知状态按非终态、需继续查询处理，除非协议明确声明为终态。
- 新增错误码不应改变已有错误码语义。
- 方案和参数的变更通过 capabilities/plans 查询发现，不通过接口猜测。
- 每次协议变更应更新版本号、示例、错误码和联调用例。

## 16. 双方确认项

- [ ] 中控服务地址和端口
- [ ] API Key 分配、轮换和保管方式
- [ ] planCode 与方案版本映射
- [ ] 单批最大任务数
- [ ] 样品号和批次号是否全局唯一
- [ ] 参数名称、类型、单位和必填规则
- [ ] 结果摘要字段和判定规则
- [ ] 结果文件保存期限和下载权限
- [ ] 轮询间隔和超时策略
- [ ] unknown、失败和人工确认的处理方式
- [ ] 测试环境、测试样例和验收时间

## 17. 实施说明

本文只是一份外部接口协议草案。本次变更不修改：

- 中控现有 HTTP 路由；
- WPF、MES、Adapter、Simulator 代码；
- 数据库表和迁移；
- 设备控制流程和安全门禁；
- 当前工作区中的现场验证文件。

协议评审通过后，建议另开独立实现分支，先实现只读 capabilities/plans 和批量接收，再补充状态查询、结果查询、鉴权和集成测试。

