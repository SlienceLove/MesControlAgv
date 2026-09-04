# Phase 2 现场流程后统一修复清单（2026-09-04）

本清单记录本轮现场执行前后暴露的问题。按现场要求，先保留证据并完成本次流程，流程收尾后集中修复；本文件不代表本轮已修改产品代码。

## 1. PowerShell 5.1 多值参数绑定不稳定

- 现象：调用 `start-physical-acceptance-adapter.ps1` 时，将程序白名单数组直接传给嵌套 `powershell -File`，后续值被误绑定到 `StartupTimeoutSeconds`；改用逗号分隔字符串后才成功。
- 影响：启动命令容易在参数解析阶段失败，增加现场操作风险。
- 证据：本目录中失败启动命令的会话记录；成功 RunId 为 `phase2-std-20260904-093754`。
- 建议修复：为启动脚本提供明确的单字符串/JSON 白名单参数或统一参数转发包装器，并增加 PowerShell 5.1 回归测试。

## 2. Windows PowerShell `ConvertTo-Json` 日期格式与 API 不兼容

- 现象：`DateTimeOffset` 直接经 Windows PowerShell 5.1 `ConvertTo-Json` 输出为 `/Date(...)/`，MES JSON 模型绑定返回 HTTP 400。
- 影响：一键执行请求在入口被拒绝；该请求未创建 WorkflowExecution，未触发设备动作。
- 证据：`workflow-execution-request.json` 与 `workflow-execution-error.json`（第一次执行尝试目录）。
- 建议修复：现场工具统一使用 ISO-8601 字符串或 .NET `System.Text.Json` 序列化，并增加端到端日期格式契约测试。

## 3. Windows PowerShell `Invoke-WebRequest -Body` 的 UTF-8 传输问题

- 现象：通过 `Invoke-WebRequest -Body` 发布含中文节点/程序名的工作流后，服务端收到 `?`，导致 `WORKFLOW_PHYSICAL_TEMPLATE_REQUIRED`（HTTP 422）。
- 影响：工作流虽可校验/发布，但模板语义被破坏；该拒绝请求未创建可执行运行，未触发设备动作。
- 证据：`workflow-publish-response.json`（修复前的发布响应）和 `execution-attempt-2/workflow-execution-error.json`；UTF-8 修正版见 `workflow-correction-utf8-3/workflow-publish-response.json`。
- 建议修复：仓库提供统一 UTF-8 HTTP 客户端/现场脚本；所有中文契约增加字节级传输回归测试，禁止依赖 Windows PowerShell 默认编码。

## 4. MES 进程启动时标准输出重定向方式易造成管道阻塞

- 现象：首次用 `ProcessStartInfo.RedirectStandardOutput=true` 但未消费管道，随后改为文件重定向并重启 MES。
- 影响：长时间 worker 日志可能填满管道导致进程停滞。
- 证据：`mes.stdout.log`、`mes.stderr.log` 及 `standard-session-state.json`；首次 PID 14624，实际运行 PID 23980。
- 建议修复：统一服务启动器，明确使用文件重定向或异步消费 stdout/stderr，并记录准确 PID/停止命令。

## 5. WPF 导航 UI Automation 事件契约不够稳定

- 现象：`SelectionItemPattern.Select()` 能改变自动化选择状态，但不触发应用依赖的 `RadioButton.Click` 路由事件；键盘/鼠标注入在非前台环境也不稳定。
- 影响：现场自动化难以可靠地完成“从 MES 加载”及按钮点击；本轮执行请求工具保持一次性标记，避免重复 POST。
- 证据：WPF 进程 `42172` 的 UI Automation 只读树检查记录；未产生设备写入。
- 建议修复：为 WPF 提供受权限保护的测试/现场操作入口或标准 UIA Invoke 契约，避免依赖坐标和路由事件副作用。

## 6. 工作流图文档到 MES 合同缺少正式 UTF-8 导入工具

- 现象：新 MES 数据库需要把已批准的 `mes.workflow.graph` 转为 legacy `WorkflowDefinition` 后再创建/校验/发布；当前只能使用临时脚本转换。
- 影响：现场准备步骤长，容易出现节点类型、ID、边或编码错误。
- 证据：`publish-workflow.ps1`、`workflow-definition-request.json` 及 `workflow-correction-utf8-3/`。
- 建议修复：增加正式、可审计的图文档导入/发布命令，自动生成新 ID、校验九节点模板并使用 UTF-8 .NET HTTP 客户端。

## 7. 本轮不应重试的请求

- `f825e8bf-8b22-4650-9bde-c1857d3ac6fd`：HTTP 400，未找到执行记录。
- `c0cd5360-3489-4d02-b180-f4ce4c8a4508`：HTTP 422 `WORKFLOW_PHYSICAL_TEMPLATE_REQUIRED`，未产生设备动作。
- 上述请求 ID、相关 correlation ID 和一次性 marker 均保留，不得复用；最终成功尝试必须使用全新 request ID。

## 8. 临时后台监控脚本的 PowerShell 数组处理

- 现象：初版 `monitor-physical-batch.ps1` 将 JSON 数值/数组作为 `System.Object[]` 传入状态映射，产生重复的只读监控报错；初版汇总脚本也把数组包成了带 `value/Count` 的单对象。
- 影响：临时显示器没有及时打印状态，MES worker 和设备执行不受影响；最终改用原始 JSON 展开后得到准确的 7 个可执行节点、7 个设备操作和 4 张验收单。
- 建议修复：将现场监控工具迁移到 .NET `System.Text.Json` 或统一显式数组展开/标量转换，并增加真实 HTTP JSON 响应回放测试。

## 9. 执行前出现一条未关联的 AUBO load 请求

- 现象：MES 日志在最终 workflow 受理前记录了 1 次独立的 `POST /api/robot-arms/ARM-01/program/load`（HTTP 200）；它没有对应本次 workflow 的 `WorkflowDeviceOperation`。最终受理运行关联的 3 个 AUBO 节点各自仅有 1 次 load + 1 次 run。
- 影响：严格的“全会话无额外 load”无法仅凭当前日志断言；本次用户确认的九节点流程本身无中途错误并已完成。该独立请求可能与 WPF 前台/UI 自动化交互有关，需在流程后核对控制器审计和调用来源。
- 证据：`mes.stdout.log` 中约第 42942 行的 `program/load` 请求，以及本文件对应的最终 workflow 7 条设备操作记录。
- 建议修复：为 AUBO 写接口加入强制 workflow/run correlation 与操作员审计；无关联的手工 load 应在标准运行会话中拒绝或单独告警。
