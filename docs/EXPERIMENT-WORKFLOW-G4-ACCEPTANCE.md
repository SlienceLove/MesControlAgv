# 实验流程 G4 验收记录

> 状态：G4-A、G4-B、G4-C 已通过项目方验收；G4-D 已实现并通过自动化门禁，等待项目方人工验收。未进入 G5。

日期：2026-08-21

## 1. 交付范围

### G4-A：运行记录基础

- 增加 `NodeExecution`、`DeviceOperation` 和运行时间线只读契约。
- 增加对应 SQLite 表、原位升级、兼容双写和 pre-G4 稳定 ID 投影。
- 增加运行、节点、设备操作和时间线只读 API，保留旧执行读取接口。

提交：`d6da0fd feat(workflows): add G4 runtime record foundation`

### G4-B：Simulator 节点执行器

- 将既有 Simulator Move 和 Timed Wait 调度、对账和恢复迁移到持久节点记录。
- Move 复用持久操作 ID；Timed Wait 使用持久开始时间且不创建设备操作。
- 暂停状态下允许当前运行节点继续对账，但阻止后续节点声明。

提交：`51e6dff feat(workflows): migrate simulator worker to node records`

### G4-C：只读运行监控

- 增加独立运行监控页，按运行 ID 读取固定流程版本、节点尝试、设备操作和时间线。
- 运行画布复用 Nodify Runtime 视图，不修改已发布定义；刷新保留同一画布实例和视口。
- 严格拒绝跨运行、跨版本和未关联证据；Unknown 使用独立警示状态且没有 Retry 按钮。
- 修正验收时发现的 G3 Timed Wait 类型化输入投影兼容问题。

提交：`b8cf387 feat(workflows): add read-only runtime monitor`、`29bbd74 fix(workflows): project typed wait runtime inputs`

### G4-D：受控运行操作

- 增加暂停、恢复、取消和 Unknown 人工处置 API；每个请求必须包含请求 ID、操作者和理由。
- 增加由 MES 配置提供的动作级授权，JSON 请求不能自报权限。
- `workflow.pause` 控制暂停和恢复，`workflow.cancel` 控制取消，`workflow.resolve-unknown` 控制 Unknown 处置。
- 同请求 ID、同内容返回幂等重放；同请求 ID 被不同动作或内容复用时返回冲突。
- 操作审计为追加式记录，保存操作者、理由、权限、请求指纹、原状态和处置结果。
- WPF 增加操作者、理由、MES 权限校验、禁用原因、二次确认及独立 Unknown 处置面板。
- Unknown 只允许“现场确认成功”或“确认失败并终止”，两者均只更新既有证据，绝不重发设备命令。

G4-D 由本验收记录所在提交交付。

## 2. 状态与安全语义

| 操作 | 允许的运行状态 | 结果与约束 |
| --- | --- | --- |
| 暂停 | `Prepared`、`Running` | 转为 `Paused`，仅阻止后续节点 claim，不向设备发送暂停命令 |
| 恢复 | `Paused` | 有 Running 节点时恢复为 `Running`；有 Ready/待执行节点时恢复为 `Prepared` |
| 取消 | `Prepared`、静止的 `Paused` | 仅取消待执行节点；存在 Claimed/Running/Unknown 节点或 Accepted/Running/Unknown 设备操作时拒绝 |
| Unknown 确认成功 | `Unknown` 且选中匹配节点 | 既有节点/设备证据改为成功并按固定发布版本推进，不重发命令 |
| Unknown 确认失败 | `Unknown` 且选中匹配节点 | 既有节点/设备证据改为失败并终止，不重发命令 |

pre-G4 的 Prepared、Running 或 Unknown 兼容记录会先回填稳定节点和设备证据，再执行运行控制。G4-D 没有增加实体设备控制、串口访问、协议字段或 CIC-D160+ 写入路径。

## 3. API 与权限

```text
GET  /api/workflow-run-controls/permissions?actor={actor}
POST /api/workflow-runs/{id}/pause
POST /api/workflow-runs/{id}/resume
POST /api/workflow-runs/{id}/cancel
POST /api/workflow-runs/{id}/unknown-resolution
```

本地隔离验收配置的操作者为 `local-operator`，拥有：

```text
workflow.pause
workflow.cancel
workflow.resolve-unknown
```

服务端仍独立执行授权和状态校验；WPF 的按钮禁用只用于提前解释原因，不构成安全边界。

## 4. 自动化验证

最终 Release 门禁于 2026-08-21 执行：

| 检查 | 结果 |
| --- | --- |
| `dotnet build MesControlAgv.sln --configuration Release --nologo` | 0 警告，0 错误 |
| Domain | 39/39 通过 |
| Workflow Contract | 54/54 通过 |
| MES | 93/93 通过 |
| WPF | 212/212 通过 |
| Adapter | 176/176 通过 |
| Instrument Gateway | 50/50 通过 |
| Simulator | 5/5 通过 |
| E2E | 19 通过，5 个既有用例跳过，0 失败 |
| 全方案合计 | **648 通过，5 跳过，0 失败** |

聚焦覆盖包括权限拒绝、必填理由、状态冲突、请求幂等、暂停后对账、静止取消、两种 Unknown 结论、pre-G4 回填、HTTP 状态/错误详情、WPF 禁用原因、二次确认以及无 Retry 绑定。

## 5. 隔离人工验收环境

当前环境使用 Release 输出、Simulator-only Profile 和独立 SQLite：

| 服务 | 地址 |
| --- | --- |
| Simulator | `http://localhost:5183` |
| Adapter | `http://localhost:5041` |
| MES | `http://localhost:5045` |

运行标识：`g4d-acceptance-20260821153327`

状态文件：`%TEMP%\MesControlAgv-local-g4d-acceptance-20260821153327-pids.json`

WPF 已使用 `MES_BASE_URL=http://localhost:5045/` 启动。验收数据均位于临时 SQLite，不是共享或生产数据。

### 待验证运行 ID

| 用途 | 运行 ID | 初始预期 |
| --- | --- | --- |
| 暂停 | `6c7b2955-7052-48b0-af71-577a7edf9026` | Running，24 小时 Timed Wait 节点为 Running |
| 恢复 | `e94ab416-5c41-48ee-bb62-e2daa4fae787` | Paused，24 小时 Timed Wait 节点仍为 Running |
| 取消 | `5723597b-c4c6-4d8c-9a81-eb534c17492d` | Paused，Wait 已成功，后续 Move 为 Ready，无设备操作 |
| Unknown 确认成功 | `07984ede-c82a-4605-8567-44451dc8ba2c` | Unknown，节点和唯一设备操作均为 Unknown |
| Unknown 确认失败 | `42b30280-65b9-426e-bce5-81723fc62062` | Unknown，节点和唯一设备操作均为 Unknown |

24 小时等待流程 ID 为 `daab83d8-87d7-4737-9f3a-42ce6efd1dbd`。两个 Unknown 候选共用已发布流程 `dbe67dae-601e-4d56-98fb-39cd61d38798`，但拥有各自独立的运行、节点和设备操作 ID。

## 6. 推荐人工验收

1. 打开“流程运行监控”，操作者保持 `local-operator`。先输入 `unconfigured-operator` 并校验权限，确认三个权限均未授予、危险按钮保持禁用且显示原因；再切回 `local-operator`。
2. 加载暂停候选，填写原因并校验权限。确认仅“暂停”可用；点击后出现“只阻止后续调度、不向设备发送暂停命令”的二次确认。确认后运行变为 Paused，时间线包含操作者和理由。
3. 加载恢复候选，填写原因并校验权限。确认“恢复”可用，而“取消流程”因仍有 Running 节点而禁用。恢复后运行变为 Running，既有 Timed Wait 节点继续运行。
4. 加载取消候选，填写原因并校验权限。确认“取消流程”可用；二次确认明确该操作不可撤销且不会发送设备取消命令。确认后运行变为 Cancelled，Ready Move 变为 Cancelled，设备操作仍为 0。
5. 加载 Unknown 成功候选并选择 Unknown 节点。确认独立面板明确“禁止自动重试”，界面没有 Retry 按钮。填写原因后选择“记录现场已成功”，确认二次提示明确不会重发设备命令。运行应完成，节点和原设备操作变为 Succeeded，设备操作总数仍为 1。
6. 加载 Unknown 失败候选并选择 Unknown 节点。填写原因后选择“确认失败并终止”。运行、节点和原设备操作应变为 Failed，设备操作总数仍为 1。
7. 对每条已操作运行执行刷新，确认状态、节点证据和时间线保持一致；时间线可看到操作者、理由和动作结果。

项目方 G4-D 验收结论：**待确认**。

## 7. 兼容、回退与边界

- 旧 `/api/workflow-executions/...` 读取接口和 legacy run 镜像继续保留；节点记录存在时以节点记录为运行证据权威来源。
- 回退应在干净分支上使用 `git revert`，不得使用 `reset`、`clean`、`stash` 或覆盖当前工作树中的用户文件。
- G4-D 只控制 MES 运行调度状态，不等同于暂停或取消现场设备动作。
- G5 的方案、排程和资源租约尚未开始；G4-D 验收通过前不进入 G5。
