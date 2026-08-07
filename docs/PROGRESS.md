# AGV MES MVP Progress

Last updated: 2026-08-07

## Current status

The `.NET 8 + WPF` MVP is implemented and the current control-center flow is configuration-driven. WPF loads enabled stations and previews the configured route; a task is created as `Created` and explicitly dispatched through MES -> Adapter -> AGV. The default local service ports remain Simulator `5183`, Adapter `5041`, and MES `5045`.

MES owns task state, SQLite persistence, and audit events. Adapter owns device protocol, control ownership, idempotent dispatch, device-confirmed cancellation, and timeout reconciliation. WPF calls MES action APIs; only a Debug build running with `WPF_RUNTIME_MODE=simulator` can call simulator controls.

The WPF dashboard includes dynamic task creation, explicit dispatch, task detail and audit-event timeline loading, task-operation descriptions, explicit exception reasons, `UNKNOWN` recovery, and AGV pause/resume/cancel controls. Release builds hide simulator controls even when Simulator is selected; physical mode never exposes manual arrival or simulator fault injection. The Adapter now also contains a configuration-selected vendor TCP driver; Simulator remains the default. The development Simulator now exposes three virtual AGVs, while the shared Domain layer provides shortest-path planning and multi-AGV assignment.

Task state handling now distinguishes a known execution failure from an unresolved device result. Adapter conflicts such as no available AGV, path conflicts, and duplicate control are persisted as `FAILED` with a Chinese reason and remain retryable. Timeouts and communication failures remain `UNKNOWN` only when the device result cannot be confirmed, and the WPF dashboard shows `系统异常` together with the stored reason instead of `状态未知`.

Simulator arrival controls accept a specific transport operation ID. WPF updates the simulator AGV first and then notifies MES, so the correct AGV is released after arrival and the normal flow can continue through `MOVING_TO_DROPOFF` to `COMPLETED`.

The WPF application now includes experiment workflow management. Workflows can be preset and edited, their nodes can be adjusted through a visual drag-and-drop designer, and definitions are persisted locally as JSON for reuse between runs. The editor can also load MES definitions and versions, save a draft, persist validation, publish an immutable version, and issue a Simulator-safe dry-run admission request. Node parameters and explicit directed edges are preserved across local storage and the MES contract; dry-run remains an auditable next-step decision and does not call an AGV.

## Vendor TCP implementation

The vendor driver follows the supplied integration guide and API reference. It implements the 16-byte TCP frame, channels `19204`, `19206`, `19207`, and `19301`, control ownership (`1060`/`4005`), navigation (`3066`), status query (`1110`), active status push (`19301`/`9300`), pause/resume (`3001`/`3002`), cancellation (`3067`), and emergency-stop handling. The current controller reference documents `3067` as the cancellation API; `3068` was experimentally rejected as a task-specific cleanup mechanism because it returned success without changing the legacy record.

The Adapter maps vendor fatal/error, blocked, emergency-stop, localization-confidence, and forklift automatic-mode signals into dispatch safety gates. Device confirmation, idempotency, timeout reconciliation, and audit behavior remain under the existing Adapter boundary.

## Automated verification

Use the serial shared-compilation workaround when required:

```powershell
dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1
dotnet test MesControlAgv.sln --no-build -p:UseSharedCompilation=false -m:1
```

The Release solution build passed on 2026-08-07 with 0 warnings and 0 errors, and all **210/210 tests passed**. Coverage now includes Profile/API station-catalog mapping for WPF task rows and batch submission, route-preview invalidation and response endpoint validation, serialized refresh state with stale-data reporting, shared WPF action single-flight and AGV result/error guards, real `MesClient` HTTP JSON/error contracts, WPF workflow draft/validate/publish/version/dry-run HTTP contracts and editor state transitions, dropoff pause/resume and final fleet-idle assertions in the local process verifier, Simulator failure/retry and timeout-unknown recovery scenarios, workflow lifecycle audit readback, fleet-aware route selection, and fail-closed physical preflight reasons without opening a device connection. The prior coverage of dynamic station/task parameters, explicit create/dispatch separation, pause/resume state writeback, physical-mode command guards, fleet-status/task correlation, multi-AGV isolation, and the complete Simulator transport flow remains green.

## Live verification

The three service processes were started from the Release output on isolated local ports with fresh temporary MES/Adapter stores for process-level validation. Existing positive and `failure-retry` runs passed, including pause/resume, arrival confirmations, audit evidence, and fleet cleanup. New isolated runs on `5511/5512/5513` passed `timeout-recover` (Simulator `timeout-unknown` -> MES `Unknown` -> same operation recreated -> `ReconciledMoving` -> completed), `5551/5552/5553` passed `multi-agv` (three distinct AGV assignments, fourth task failed closed with `DeviceFailed`, all three tasks completed), `5571/5572/5573` passed `restart-resume` (Simulator kept alive while Adapter/MES restarted and reconciled persisted work), and `5641/5642/5643` passed `workflow-publish-rollback` (three immutable versions, published pointer rollback, and lifecycle audits). The physical-robot run has not been completed.

## 2026-08-07 next-phase handoff

The WPF task form no longer assumes the default `SAMPLE` catalog at runtime. It loads the complete station directory from MES and uses that directory for task-row names, batch-import code/name/AGV-ID resolution, and enabled-station validation. Refresh operations share a single-flight gate; the dashboard exposes whether the last successful snapshot is stale and when it was received. The local process scripts accept per-run URLs, isolated stores, run IDs and state paths, wait for health readiness, and stop only a matching PID/port instance. `docs/LOCAL-VERIFICATION.md` is the repeatable Simulator-only runbook.

The workflow editor to MES lifecycle slice and the first expanded process-verification matrix are now complete for offline verification. The next offline implementation order is:

1. Add a read-only WPF readiness/route/audit pane, including map/profile fingerprint and the actual execution path returned after AGV assignment.
2. Start wiring a published workflow next-step request into transport dispatch, keeping the existing task API as the explicit side-effect boundary and preserving dry-run by default.
3. Keep the local process matrix in CI/release rehearsal and add explicit negative contracts for unavailable fleet/profile changes.

The physical acceptance boundary remains separate and fail-closed: after the vehicle is powered and a fresh read-only preflight is authorized, compare the live map name/version/MD5, station catalog and directed edges with the profile, then confirm automatic mode, control ownership and safety gates. Until that evidence exists, keep `enableAutomaticDispatch=false` and do not connect or move the real AGV.

The 2026-08-04 WPF Debug EXE verification also succeeded. The task-monitor refresh message, `每 2 秒从 MES 刷新`, is now fixed at the bottom of the monitoring layout in outer `Grid.Row="2"` and has hit testing disabled, so it no longer overlays the task list or prevents task clicks.

The historical Code Integrity events 3077/3033 reference policy ID `0283ac0f-fff1-49ae-ada1-8a933130cad6` and the earlier blocked Simulator DLL load. The current effective state now has `AllowDevelopmentWithoutDevLicense=1`; the policy remains enforced (`VerifiedAndReputablePolicyState=1`, Device Guard enforcement status `2`), but no new project-specific 3077/3033 event appeared after the successful restart. `CiTool --list-policies` still requires administrator access for a complete policy dump.

## Remaining boundary

The vendor protocol is now implemented behind the Adapter driver boundary, but no physical robot has been connected yet. Before enabling `Agv:Driver=tcp`, confirm the robot IP, firmware, map station IDs and direct route edges, then validate relocation, control ownership, safety gates and mechanism DI/DO. MES lifecycle, task state, audit events, WPF control flow, and the MES-to-Adapter API contract remain unchanged.

## Next session handoff

1. Start WPF in Debug + Simulator mode, select configured source/target stations, create and explicitly dispatch a task, then verify pause/resume, arrival, pickup confirmation, dropoff arrival, dropoff confirmation, and `COMPLETED`; also verify known failures and communication exceptions show their reasons.
2. If a future restart produces new project-specific Code Integrity 3077/3033 events, ask the administrator to approve a supplemental WDAC policy or provide a signed development build.
3. Confirm the full physical-robot acceptance boundary before enabling `Agv:Driver=tcp`.
4. Confirm the AGV IP address, firmware version, map name, and actual station IDs.
5. Confirm the direct relationship between `source_id` and the map `id` field.
6. Confirm relocation parameters, control-ownership fields, safety fields, and forklift/lift/roller DI/DO mappings.
7. In an isolated environment, set `Agv:Driver=tcp` and run staged TCP connectivity, read-only status, control-ownership, and movement acceptance checks.
8. Keep Simulator as the default until the vendor values and on-site safety acceptance are complete.

## 2026-08-04 extension: AGV communications and batch task import

The WPF control center now includes an `AGV 通讯与调度` tab. It reads the MES fleet snapshot every two seconds and displays each AGV ID, online state, control owner, current station, and current task. When an AGV is online and has an active task, the operator can send `pause`, `resume`, or `cancel` for that task. These commands travel through WPF -> MES -> Adapter -> Simulator/vendor driver; no unverified free-driving or emergency-stop behavior is exposed in this MVP screen.

The WPF control center also includes a `批量任务导入` tab. CSV and XLSX files are parsed without an additional NuGet dependency. Supported columns include task ID, source station, target station, description, priority, and planned time, with Chinese and English aliases. Valid rows are sorted by priority descending, planned time ascending, and source row number. Import issues are retained for review, priority can be edited in the preview grid, and valid rows can be submitted sequentially to the existing MES task API with the task ID stored as `ExternalId`. Source and target stations accept numeric codes, configured AGV station IDs, or station names.

Task responses now include `Priority`, `Description`, and `ExternalId`; old SQLite databases are upgraded at startup when these columns are missing. The WPF command client supports fleet snapshots and AGV task commands while retaining compatibility with existing test clients.

## 2026-08-04 extension: KPI dashboard

The WPF control center now includes a `KPI 看板` tab. It presents today's task total, running/completed/failed counts and completion rate, plus a native WPF donut chart for task status and a 24-hour created/completed trend chart. The dashboard also shows sample-processing information, consumable remaining status, and instrument/AGV operating status. The existing two-second refresh cycle refreshes KPI data together with the task-monitoring screen.

KPI aggregation is exposed by MES at `GET /api/dashboard/kpi?date=yyyy-MM-dd` and is calculated from the persisted transport-task data. Sample statistics currently describe transport-task status aggregation and include a data-source note. Consumable inventory is explicitly marked `未接入` until a site inventory interface is available; real laboratory instrument status is also not fabricated and is marked as not yet connected. Current instrument/AGV status is sourced from the Adapter/AGV snapshot, and the Simulator remains the default runtime path.

No third-party chart package was added. The donut and trend charts are rendered by WPF controls, keeping the MVP dependency surface unchanged.

## Extension verification

On 2026-08-04, the serial Debug and Release solution builds passed with 0 warnings and 0 errors. All 71 tests passed: Domain 12, MES 17, Adapter 16, WPF 18, E2E 7, and Simulator 4. The WPF XAML was also compiled successfully. Batch import parser coverage includes CSV quoting, UTF-8 BOM, XLSX shared strings/numeric cells, Chinese headers, validation issues, and priority/planned-time sorting.

The extension has been verified against the Simulator path. Physical AGV connection and vendor-specific on-site acceptance remain outstanding; before using `Agv:Driver=tcp`, validate the robot IP, firmware, map/station IDs, control ownership, safety gates, and movement behavior in an isolated acceptance environment.

## 2026-08-04 extension: task monitor date filtering and timestamps

The MES task list endpoint now accepts `GET /api/tasks?date=yyyy-MM-dd`; when the query is omitted it defaults to the current UTC date, so a newly opened WPF task monitor does not load the entire historical task table. The WPF monitor has a date picker with query/refresh actions, and its two-second refresh loop preserves the selected date. KPI data now uses the same selected date as the task list.

Task responses now expose `CreatedAt` and nullable `EndedAt`. The task grid displays both fields; `EndedAt` is populated when a task reaches `Completed`, `Cancelled`, or `Failed`, and is cleared when a failed task is retried. Existing SQLite databases are upgraded at startup with the nullable `EndedAt` column.

After creating a task, WPF refreshes the selected date and selects the newly created task instead of retaining an older moving task. This prevents the common Debug-simulator mistake of sending an arrival control to a stale task. Simulator control failures now preserve the backend JSON `detail`, so an HTTP 409 is shown with the actionable reason rather than only the generic status text.

The date-filter API, timestamp serialization, terminal end-time behavior, WPF selection behavior, KPI date propagation, and simulator error-detail handling are covered by automated tests.

## 2026-08-04 extension: standard platformization refactor preparation

The product direction has been clarified: this project is not intended to be copied into separate customer applications. The current WPF control center is the MVP baseline for a standard, productized control-center platform. Future customers should be able to reuse the standard functions and add site-specific devices, workflows, scheduling rules, reports, and UI modules through configuration, profiles, strategies, and controlled extensions.

The current architecture is suitable for continuing MVP validation and physical AGV integration, but it is not yet platform-grade for deep customer customization. The main coupling points identified are fixed stations/maps and workflow assumptions in the Domain/MES boundary, the broad responsibility of the WPF `MainViewModel`, duplicated API/device/UI DTO shapes, direct service registration without a module registry, and the workflow editor being persisted locally without a versioned execution contract.

The agreed target is an incremental “shared platform + standard modules + customer extensions” structure:

```text
Platform Core / Contracts / Application / Device Abstractions
        -> WPF Shell + Standard Modules
        -> Customer Profile + Customer Workflow/Driver/UI Modules
        -> Infrastructure Drivers for Simulator, Vendor TCP, instruments and PLCs
```

The current five-project split remains usable as an intermediate structure. Physical project splitting will follow after interface boundaries stabilize. The dependency rule is that Domain and Application do not depend on WPF, databases, or vendor protocols; WPF calls application use cases; device protocols remain behind driver interfaces; and customer differences are not implemented as customer-specific branches in core services.

### Agreed extension boundaries

- `IAgvDriver`: connection, snapshot, dispatch, pause, resume, cancel, and vendor-error conversion.
- Device capabilities: UI and application services check supported operations instead of testing vendor names.
- `IWorkflowDefinition`: versioned, validated workflow definitions for standard and customer processes.
- Scheduling strategy: pluggable AGV selection and route policy.
- `IControlCenterModule`: service, view, command, menu, and permission registration for standard and customer modules.
- Profile/configuration: driver selection, AGV IDs, stations, maps, feature flags, timeouts, permissions, and display options.

The workflow editor must eventually feed a runtime workflow executor. Saving a draggable JSON definition alone is not sufficient for a production template; definitions require identity, version, validation, publish status, migration behavior, and auditable execution.

The first boundary implementation is now in place. `src/MesControlAgv.Contracts` owns the shared task, KPI, station, AGV snapshot, command, and planning response/request records. `src/MesControlAgv.Application` owns the use-case interfaces `ITaskApplicationService`, `IKpiDashboardApplicationService`, and the normalized AGV gateway ports. MES implements those application interfaces; Adapter remains the HTTP/TCP infrastructure implementation of the AGV ports; WPF deserializes shared contracts and maps them to UI models.

The current dependency direction is: `Application -> Contracts + Domain`; `MES -> Application + Contracts + Domain`; `Adapter -> Contracts + Domain`; and `WPF -> Contracts + Domain`. The MES-side `AdapterClient` implements the Application gateway ports while the Adapter service remains the device-protocol boundary. The boundary is intentionally incremental: a complete workflow executor, plugin loader, profile system, and physical-device protocol acceptance are still future work.

The next platformization slice is also in place. `AgvCapabilitiesResponse` is now part of the shared device snapshot contract; Adapter normalizes capability metadata for fleet and single-snapshot responses, and WPF gates pause/resume/cancel commands from the declared capabilities instead of assuming every AGV supports every command. The WPF shell now has a `ControlCenterModuleRegistry` with standard module IDs for task monitoring, AGV communications, batch import, KPI, and workflow design. It is currently a registration boundary, while view/service composition remains the next step.

### Refactor preparation backlog

#### P0 — before the first deep customer customization

- [x] Establish the first shared Contracts boundary for tasks, KPI, stations, device snapshots, commands, and planning responses. Error/workflow/audit contracts remain to be expanded.
- [x] Introduce the Application/use-case layer for task and KPI boundaries; WPF no longer owns MES state transitions.
- [ ] Move fixed stations, map data, device parameters, and timeouts toward Profile/configuration or persistence.
- [ ] Split `MainViewModel` into task-monitor, AGV, batch-import, KPI, workflow, and future alarm/device modules.
- [x] Establish the first device capability model and WPF module registry; vendor-specific `IAgvDriver` implementation remains.
- [ ] Add workflow version, validation, publish status, and runtime execution entry points.

#### P1 — platform capability enhancement

- [ ] Add workflow executor, scheduling strategy, unified alarms, and richer audit contracts.
- [ ] Add specialized abstractions for instruments, barcode scanners, PLCs, and other site devices.
- [ ] Add API/plugin compatibility versions and a customer Profile model.
- [ ] Add contract tests shared by Simulator and vendor drivers.

#### P2 — customer delivery readiness

- [ ] Define extension packaging, compatibility checks, configuration migration, and database migration.
- [ ] Provide a standard-module and customer-extension sample.
- [ ] Define production plugin whitelist/signing, audit, rollback, and release procedures.
- [ ] Complete physical-device, instrument, workflow, and safety acceptance checklists.

### Platformization acceptance criteria

1. Adding another AGV vendor requires a new driver without changing Domain, standard task services, or WPF pages.
2. Adding a customer workflow requires a workflow definition, strategy, or customer module rather than a copied application.
3. Customer pages are loaded through module registration, Profile, and permissions.
4. Station names, map, AGV count, device endpoints, timeouts, and feature flags can change without editing platform core code.
5. Platform upgrades preserve or migrate customer profiles, workflow versions, and data, with a rollback path.
6. Device commands remain capability-checked, idempotent, timeout-aware, and auditable.

This entry records architecture preparation only. No production refactor has been claimed complete, and the Simulator remains the default until physical AGV acceptance is finished.

## 2026-08-05 platformization pause checkpoint (local only)

按推荐顺序并发推进的平台化重构已完成第一轮 P0 独立切片，当前按用户要求暂停，**本次不提交、不推送**。

### 已落地的并发切片

1. **AGV Driver boundary**
   - 新增 `IAgvDriver`、`IAgvDriverFactory`、`DriverRegistry`、`AgvDriverOptions`、`AgvDriverException`。
   - 新增 `SimulatorDriver` 与 `VendorTcpDriver` 适配骨架，均复用现有 `IAgvDeviceClient`，没有替换现有 `TcpAgvClient`。
   - 新增统一调度/控制命令契约。
   - 已补充 Driver Registry 测试；最终全量验证需在恢复后执行。

2. **Profile/configuration**
   - 新增 `ProductProfile`、`AgvProfile`、`StationProfile`、`FeatureFlags`、`TimeoutOptions` 和 `ProfileConfiguration`。
   - 新增 JSON loader、验证器、验证结果和加载异常契约。
   - Domain Profile 定向测试已通过：16 项。

3. **WPF module composition boundary**
   - 模块注册器已扩展 View、ViewModel、Service、Command、Permission 注册描述。
   - 支持模块启停、重复注册校验、排序、权限查询和访问判断。
   - 既有 WPF 定向测试已通过：22 项。

4. **Workflow contract**
   - 新增版本化 Workflow Contracts：定义、节点、参数、版本状态、发布状态、校验结果和执行请求。
   - 新增 Domain Workflow Validator 与 Application `IWorkflowApplicationService` 边界。
   - Workflow 独立测试已通过：4 项。

5. **WPF ViewModel gradual split**
   - 新增 `TaskMonitorViewModel`、`AgvCommunicationViewModel`、`BatchImportViewModel` 和 `ControlCenterViewModel` facade。
   - `MainViewModel` 的子 VM 聚合接入正在进行中，当前已暂停，恢复后必须先完成编译和行为回归，再继续扩展。

### 暂停时验证状态

- 在最后一次 `MainViewModel` 渐进式接入前，Release solution build 已通过：0 warnings、0 errors。
- 已通过的定向测试：Domain 16、MES 18、Adapter 18（含新增 Driver Registry 测试）、WPF 22、E2E 7、Simulator 4、Workflow Contract 4。
- 最新的 `MainViewModel` 子 VM 接入修改尚未重新执行最终构建，因此恢复工作后的第一步是重新编译和运行全量测试。
- 当前工作树包含此前所有未提交 MVP/平台化改动；保留既有 `stash@{0}`、`stash@{1}`，未清理、未回滚。

### 恢复后的推荐顺序

1. 先编译并运行全量测试，修正 `MainViewModel` 子 VM 接入的兼容性问题。
2. 将 Driver Registry、Profile loader、Workflow validator 纳入统一组合根/DI（不让 WPF 直接 new 业务实现）。
3. 完成 MainViewModel 到模块 ViewModel 的状态和命令迁移，保留 XAML 兼容代理作为过渡。
4. 再实现 Workflow Runtime Executor、调度策略和真实 AGV Driver contract tests。
5. 最后更新验证记录，由用户明确决定是否提交和推送。

本 checkpoint 只写入本地文档，未执行 commit 或 push。

## 2026-08-05 platformization pause checkpoint 2 (local only)

按用户要求暂停当前平台化执行。本次不提交、不推送，不清理或回滚现有工作树改动。

### 本次已落地并完成定向验证

1. **WPF ViewModel 收口**
   - 修复 `MainViewModel` 批量导入代码中的损坏字符串和 `_batchParser` 接入问题，改由 `BatchImportViewModel` 处理。
   - 保留任务创建、取货/放货确认、重试、取消、异常原因、刷新、模拟器控制和 AGV 能力门禁。
   - WPF 构建通过：0 warnings、0 errors；WPF 定向测试 22/22 通过。

2. **Profile/configuration 收口**
   - 补齐默认七站点、`SAMPLE_01 -> ST_PREP_01` 地图边、地图/站点/设备/超时/Features 校验。
   - JSON loader 支持 `Profile` 包装结构，并补充 Profile 与站点测试。
   - Domain 全量测试 16/16 通过，Profile/站点定向测试 6/6 通过；三个服务的 `appsettings.json` 可解析。

3. **Workflow Runtime 收口**
   - 增加已发布/已校验版本门禁、参数解析、下一步请求、审计结果和请求级幂等。
   - 修复 Application Workflow 接口重复定义，补充版本、校验、参数、审计和幂等契约测试。
   - Application 编译通过：0 warnings、0 errors；Workflow 定向测试 9/9 通过。

4. **Adapter 组合根初步接线**
   - 新增 `AdapterCompositionRoot`，默认使用 Simulator，仅显式配置 `Agv:Driver=vendor-tcp` 时选择 Vendor TCP。
   - Adapter 入口已改为调用 `builder.Services.AddServices(...)`，并补齐 Workflow validator 的命名空间。
   - Adapter 定向测试使用现有产物通过 16/16。

### 暂停时的未完成项与验证限制

- Adapter 和 MES 指定构建受到遗留 `dotnet.exe` 锁定输出 DLL 影响，尚未取得本次改动后的可靠构建结果。
- 隔离输出构建暴露 `ProfileConfigurationValidator.cs:122` 的 `CS8602` nullable 错误，恢复后必须优先修复。
- MES 测试 DLL 尚未生成，因此 MES 定向测试未运行；全量 solution build/test 也尚未在上述切片合并后执行。
- MES 尚未接入 Workflow Runtime 的真实持久化/版本读取实现；当前没有伪造该注册。
- WPF 仍使用现有桌面组合方式直接构造客户端，未强行引入 Web DI。
- 真实 AGV 物理连接、Vendor TCP 现场验收、控制权、安全门禁和 DI/DO 验证仍未完成。

### 恢复后的执行顺序

1. 检查并释放遗留构建进程造成的 DLL 锁，修复 `ProfileConfigurationValidator.cs:122`。
2. 重新构建 Adapter、MES、WPF，并运行对应定向测试。
3. 运行 `dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1` 和全量测试，确认跨切片依赖。
4. 验证 `AdapterCompositionRoot` 的运行时默认驱动、Profile 加载和端口契约；必要时补充 DI 测试。
5. 全量验证通过后再更新 README/设计状态，并由用户明确决定是否 commit 或 push。

本 checkpoint 只写入本地文档，未执行 commit 或 push。

## 2026-08-05 Workflow Runtime persistence second-round completion (local only)

本轮在同一 worktree 中完成此前遗留的 MES Workflow Runtime 缺口；未执行 commit、push、reset 或 clean，也未停止现有的三个 dotnet 服务。

### 本轮完成

1. **MES-backed version persistence/read**
   - 新增 `WorkflowVersions` 持久化表和 `WorkflowApplicationService`。
   - 以 `(WorkflowId, Version)` 为复合键保存不可变定义 JSON 快照、版本生命周期、发布状态、校验结果、创建/发布操作者和时间。
   - 支持创建下一版本 Draft、仅编辑未发布 Draft、版本列表/读取、显式校验、仅成功校验后发布。
   - 发布新版本会将同一 Workflow 的旧 Published 版本标记为 `Archived/Superseded`；已发布版本的定义载荷不被改写。

2. **Runtime composition and execution boundary**
   - 新增真实 MES `MesWorkflowVersionReader`，由 EF Core `MesDbContext` 读取已保存版本；`WorkflowRuntimeExecutor` 通过组合根获得该 reader 和已注册的 `WorkflowValidator`。
   - 新增 `/api/workflows`、版本读取/列表、Draft 创建/更新、校验、发布和 `/api/workflows/execute` 入口。
   - Runtime 只做已发布版本的校验、参数解析和下一步请求准备，不调用 AGV、不发送运动/控制/调度指令，不改变既有 MES Transport Task 流程。

3. **Idempotency and audit**
   - 新增 `WorkflowExecutions`，以 `RequestId` 持久化请求指纹、请求/结果 JSON 和执行结果；同一请求跨服务实例重放返回原执行结果，不同 payload 重用同一 RequestId 返回 `WORKFLOW_REQUEST_ID_REUSED`。
   - 新增 `WorkflowAudits`，记录 Draft 创建/更新、校验、发布/替代和 Runtime 接受/拒绝结果。
   - 失败结果保留稳定拒绝码和审计详情；未发布、未校验、校验失败、缺少必需参数和不支持分支等边界继续由 Application Runtime 返回。

4. **DI、兼容性和测试**
   - MES 组合根注册 `WorkflowValidator`、持久化 version reader、`WorkflowRuntimeExecutor` 以及 `IWorkflowApplicationService`。
   - 使用启动时 `CREATE TABLE IF NOT EXISTS` 补齐旧 SQLite 数据库的三张 Workflow 表和索引；不依赖破坏性迁移，不改变既有 TransportTasks 表数据。
   - `MesControlAgv.WorkflowContract.Tests` 已安全加入 `MesControlAgv.sln`，并补充 MES SQLite 持久化/API 测试。

### Release 验证结果

- `dotnet build MesControlAgv.sln -c Release --no-restore -p:UseSharedCompilation=false -m:1`：成功，0 warnings，0 errors。
- `dotnet test MesControlAgv.sln -c Release --no-build -m:1`：全量 **97/97 通过**：Domain 16、MES 21、Adapter 18、WPF 22、E2E 7、Simulator 4、Workflow Contract 9。
- `dotnet test tests/MesControlAgv.WorkflowContract.Tests/MesControlAgv.WorkflowContract.Tests.csproj -c Release --no-restore -p:UseSharedCompilation=false -m:1`：**9/9 通过**。

### 仍存限制

- Runtime 仍是 MVP admission/planning 边界，只产生第一条 `WorkflowNextStepRequest`；后续节点执行、AGV 调度、任务状态联动、分支选择、循环和完整恢复编排不在本轮范围内。
- SQLite 兼容处理使用安全的启动建表 SQL，而非 EF migration history；若未来需要跨数据库部署或复杂 schema 演进，仍应引入正式迁移流程。
- 当前 RequestId 幂等在持久化主键和指纹基础上实现；尚未扩展为分布式锁/队列或完整通用 Workflow 平台。
- 未连接真实 AGV `192.168.200.151`，未使用地图 `20260805111440651.smap` 发起任何连接或控制；真实参数仍只记录在 `docs/AGV-TCP-ADAPTER.md`，Simulator 默认配置保持不变（`Agv:Driver=simulator`）。

本 checkpoint 仅更新本地进度文档，未执行 commit 或 push。
## 2026-08-05 physical AGV live integration checkpoint

本节记录首次基于控制器实时地图的物理 AGV 联调结果，并作为此前“尚未连接真实 AGV”记录的最新状态。现场联调只使用控制器当前数据，不再使用本地旧 `.smap` 文件作为地图依据。

### 当前已确认

- AGV 地址：`192.168.200.151`；RoboshopPro 进程 PID：`22872`。
- 控制器地图：`guangzhou606`，版本 `1.0.6`，MD5 `e1b8d6b2b24362c1d44f1884c0abd8fb`。
- 控制器站点：`LM1`、`LM2`、`LM3`、`LM4`、`LM5`。
- 已确认的有向路径：`LM1 -> LM2`、`LM2 -> LM3`、`LM1 -> LM4`、`LM4 -> LM1`、`LM4 -> LM5`、`LM5 -> LM4`、`LM1 -> LM5`。没有直接的 `LM5 -> LM1`。
- 控制权由 `MesControlAgv.Adapter` 持有，控制器报告来源地址为 `192.168.200.142`。
- 实时状态正常：定位成功，置信度约 `0.98`，无 `emergency`、`blocked`、`errors` 或 `fatals`。

### 已完成的实车通信和导航测试

1. 通过 TCP 16-byte vendor frame 验证 API `1060`、`1100`、`1101`、`1110` 的读取链路和控制权状态。
2. 从 `LM5` 到 `LM1` 采用控制器已配置的连续链路，向 API `3066` 发送了一个批量请求。控制器要求外层字段为 `move_task_list`，不能发送裸数组：

   ```json
   {
     "move_task_list": [
       {"task_id": "bed2cab8b9794ac780b88feb8973b79f", "source_id": "LM5", "id": "LM4"},
       {"task_id": "e398e0309ea54dd7afe0f604c5671ec0", "source_id": "LM4", "id": "LM1"}
     ]
   }
   ```

3. API `3066` 返回 `ret_code=0`；两个任务查询状态均为 `4 (Completed)`。AGV 最终位于 `LM1`，`running_status=0`，速度为零，现场观察与接口结果一致。

### 历史任务清理结论

- 遗留任务 ID：`91e0218e544b452e937f8d67060b5b86`，查询状态为 `0 (StatusNone)`，不是 `Waiting=1`，且不在当前运行任务中。
- 按任务取消 API `3068` 返回 `ret_code=0`，但该记录状态未改变；当前控制器 API 参考未将 `3068` 列为标准接口。
- 按参考文档执行标准取消 API `3067`，返回 `ret_code=0`，但该 `StatusNone` 历史记录仍在 `1110` 列表中。当前 AGV 没有活动任务、没有运动，也没有安全报警。
- 当前控制器协议没有历史记录删除 API，因此不能把该记录强制改成 `Canceled=6` 或从历史列表删除。后续应在 RoboshopPro/控制器历史记录管理界面处理显示清理，不得继续试探未知 API。

### 下一阶段调试和开发计划

#### P0: 先固化物理联调边界

- 增加独立的 physical-acceptance 配置/启动说明，真实 IP、地图指纹和控制器版本只作为现场配置，不改变 `Simulator` 默认驱动。
- 为 Adapter 增加真实协议回归测试：`move_task_list` 外层封装、连续任务校验、`task_status` 数值映射、`3067` 取消、超时对账、重复 `task_id` 幂等和 `StatusNone` 非活动记录处理。
- 明确取消策略：默认只使用文档确认的 `3067`；移除或标记 `3068` 的实验性配置，避免把 `ret_code=0` 误认为历史记录已清除。
- 增加控制器地图快照和路径快照记录，至少保存地图名、版本、MD5、站点、直接有向边和读取时间；禁止以本地旧地图替代控制器数据。
- 在隔离区域完成低速空载安全回归：控制权抢占/释放、定位和置信度、自动模式、急停、障碍停障、恢复、到站停止、方向/角度和 DI/DO；每项保留请求、响应和现场结果。
- 将真实 API 请求、响应、任务 ID、状态时间线、地图指纹和操作者写入可审计联调记录，避免只依赖终端输出。

#### P1: 接入应用层和操作界面

- 在 Adapter/MES 之间补齐物理任务的状态对账和取消结果语义：`accepted`、`moving`、`arrived`、`failed`、`cancelled`、`unknown`，禁止通信超时后盲目重发。
- WPF AGV 通讯页显示控制器地图、控制权、当前站点、当前任务、任务状态和 `StatusNone` 警告；取消按钮只对确认中的活动任务启用。
- 将站点、地图指纹、AGV 参数、端口和安全门禁纳入 Profile；Profile 与控制器快照不一致时阻止真实派单。
- 完成真实 Vendor TCP driver contract tests，再评估把 physical-acceptance 结果接入 MES 审计和任务详情页面。

#### P2: 平台化和流程运行时

- 在物理协议和安全门禁稳定后，继续 Workflow Runtime 的后续节点执行、AGV 调度策略、分支/循环和恢复编排。
- 完成多厂商 `IAgvDriver` 合同测试、能力模型、统一报警、配置迁移和客户 Profile 扩展；保持 Domain/Application 不依赖 WPF 或 vendor protocol。
- 将真实 AGV 验收结果纳入发布前检查清单，形成可回滚的地图、Profile、驱动和数据库版本记录。

### 下次现场调试执行顺序

1. 先读取控制权、地图指纹、实时安全状态和 `1110` 活动任务；任何不一致都只读排查，不发运动命令。
2. 只选择控制器已确认的直接有向边，使用唯一 `task_id`，一次只下发一个连续任务批次。
3. 下发后持续轮询任务状态和实时位置；出现定位、急停、阻挡、报警、路径或到站异常立即停止后续动作。
4. 任务完成后保存状态时间线和现场确认，再由用户明确决定是否释放控制权。

本 checkpoint 仅更新本地进度文档，未执行 commit 或 push。

## 2026-08-06 offline verification completion (local only)

The interrupted continuous-route batch-dispatch change has now been reviewed and verified offline. The first segment preserves the parent operation ID; later segments use deterministic derived IDs; the parent `DeviceTaskId` remains the parent ID; the complete route is forwarded to the driver; route status is aggregated conservatively; and cancellation returns `cancelled` only after `1110` confirms every segment is terminal with at least one cancelled segment. An empty `1110` result is handled idempotently without sending `3067`.

Verification completed on 2026-08-06:

- Release solution build: 14 projects, 0 warnings, 0 errors, using an external temporary output directory so legacy service processes could remain untouched.
- Release tests: 134/134 passed after the E2E repository-root contract test was rerun from the worktree Release output. Breakdown: Domain 19, MES 22, Adapter 51, WPF 22, E2E 7, Simulator 4, Workflow Contract 9.
- TCP route-focused tests: 13/13 passed. AdapterService and composition-root tests: 19/19 passed. The complete Adapter test project: 51/51 passed.
- Domain/Profile tests: 19/19 passed, including direct-edge route validation and physical-profile checks.

The only issue observed during the first full temporary-output test run was the E2E test's intentional repository-root lookup from `AppContext.BaseDirectory`; a system-temp assembly cannot find `MesControlAgv.sln`. It was an output-layout issue, not a product or port failure, and the same Release E2E project passed 7/7 from the worktree output.

No physical AGV was connected, commanded, or moved during this verification. Existing MES/Simulator/Adapter processes were left untouched, and no commit, push, reset, or broad clean was performed. The next work can proceed with offline WPF physical-state display, MES audit integration, and the platformization backlog; physical acceptance remains gated by explicit authorization, isolation, read-only preflight, and map comparison.

## 2026-08-06 offline handoff before workstation restart (local only)

车辆现场已断电。本轮后续工作只做离线代码、配置、文档和本地模拟测试；没有在本节期间连接、控制或移动实体 AGV。未执行 `commit`、`push`、`reset`、`clean`，也没有手动停止既有服务进程。

### 本轮已完成或已落盘

1. **物理验收配置和门禁**
   - 已新增 `docs/physical-acceptance/adapter.physical-acceptance.example.json`、README 和 `FIELD-ACCEPTANCE-RECORD.md`；示例不含真实控制器地址或凭据。
   - Profile 已记录控制器地图快照、直接有向边和安全阈值；Adapter 组合根对 physical Profile 强制 `vendor-tcp`、匹配控制客户端昵称、`AcquireControl=true` 和最低定位置信度。
   - 离线 JSON 解析通过；`ProfileConfigurationTests` 通过 7/7。

2. **Driver/组合根离线合同覆盖**
   - 已新增 `AgvDriverContractTests.cs`（Simulator/Vendor 驱动连接、快照、派单、控制、AGV ID、能力与 Vendor 协议异常归一化）和 `AdapterCompositionRootTests.cs`（默认 Simulator 与 physical Profile 门禁）。
   - 合同测试在此前隔离构建产物中通过 14/14；组合根测试完成隔离编译，但测试宿主启动遇到 `OutOfMemoryException`，尚未在当前工作树确认断言结果。
   - 合同测试已补充“路径不得在 Driver 边界丢失”的断言，但此最新断言尚未重新编译运行。

3. **已中断、不可宣称完成的连续路径批量派单改造**
   - `TcpAgvClient`、`AdapterService`、`ISimulatorClient`、`VendorTcpDriver`、`SimulatorDriver` 和 Adapter TCP 测试中已出现路径透传、多段 `move_task_list`、分段状态聚合和取消对账的未验证改动。
   - 这些改动在重启前被主动中断，**不得**视为完成或可部署；尚未完成全量构建/测试。
   - 恢复时首先审阅并修正以下协议不变量：
     1. 第 0 段必须保留父 `operationId.ToString("N")`；后续段才使用确定性派生 ID。
     2. 上游 `DeviceTaskId` 保持父任务 ID，不能泄露以逗号拼接的子任务列表。
     3. 路径必须至少两站、站点非空、首尾匹配 source/target；经 Profile/地图验证后才允许下发。
     4. 派发超时或重复 `operationId` 时，先以全部子 ID 查询 `1110`，不能盲目重发 `3066`。
     5. `3067` 前先确认活动任务属于该父任务；仅所有分段终态且至少一段为 `6` 时返回 `cancelled`，否则稳定返回 `unknown/cancel_not_confirmed_by_1110`。
     6. 多段聚合不得把“前段已完成、后段仍在运行”误判为 `arrived`。

### 重启后的离线恢复顺序

1. 确认旧 `dotnet.exe` 输出锁已随重启释放；不清理工作树。
2. 完成并代码审查连续路径批量派单改造及其 TCP/Adapter/Driver 合同测试。
3. 依次运行 Adapter 定向测试、Profile/组合根测试、再运行：

   ```powershell
   dotnet build MesControlAgv.sln -c Release --no-restore -p:UseSharedCompilation=false -m:1
   dotnet test MesControlAgv.sln -c Release --no-build -p:UseSharedCompilation=false -m:1
   ```

## 2026-08-06 MES/Adapter offline transport loop

- MES binds the active Profile for station catalog and pre-dispatch route planning; the legacy fixed `2 -> 4` restriction is removed.
- The MES pre-dispatch plan is optional input to Adapter. Adapter validates it against its current Profile, AGV position, reservations, control ownership, readiness, and device state before sending `3066`.
- Adapter responses are carried back through MES with the active AGV id, device task id, and current execution path for API/WPF display.
- MES Recovery performs startup reconciliation and periodic active-task polling. Pickup and dropoff still require explicit operator confirmation.
- Offline verification: Adapter 51/51, MES 23/23, WPF 22/22, and the real Adapter Web + fake Vendor TCP + MES service path 1/1. The integration exercised `1060`, `4005`, `1101`, `3066`, and `1110`, including two planned legs and both manual confirmations.
- No physical AGV was connected or controlled. Template/platform refactoring remains deferred until this offline loop is accepted.

### Industrial dispatch boundary refinement

- MES now records a `PathPlanned` audit event immediately before each leg enters dispatch. The event captures the planning source, observed AGV station, target, candidate path, cost, and observation time.
- Adapter treats the MES path as a proposal. Before any new dispatch it enforces the active profile's automatic-dispatch switch, control ownership, online state, idle state, known current station, and profile-map path validation. A duplicate request returns the persisted operation without acquiring control again.
- The Vendor TCP driver remains the final protocol safety gate: it rechecks controller readiness and only then sends `3066`; timeout handling reconciles task state before any retry. The current physical profile still requires a fresh read-only controller map comparison because the vendor API integration does not yet query the controller map fingerprint.

4. 仅在离线全量验证通过后，更新本进度文档的测试计数和完成状态；继续评估 WPF 物理状态展示、MES 审计接入及平台化 backlog。
5. 实车工作继续留到车辆通电、现场隔离和明确授权之后：先只读预检/地图比对，再低速安全回归，最后才进行真实全链路验收。

## 2026-08-06 final offline verification after dispatch-gate fix (local only)

- 修复了 Vendor TCP 首次派单的控制权顺序：Adapter 现在在选择 AGV 和读取可派状态前先获取控制权，并在实时安全检查后、发送 `3066` 前再次确认控制权。
- Release solution build：14 个项目，0 warnings，0 errors。
- Release tests：**141/141 通过**：Domain 19、MES 24、Adapter 55、WPF 22、E2E 8、Simulator 4、Workflow Contract 9。
- Adapter 定向测试：55/55；MES 定向测试：24/24；Vendor TCP + fake controller + MES 完整离线链路：8/8。完整链路覆盖 `1060`、`4005`、`1101`、`3066`、`1110`，包括连续路径两段派发和取货/卸货人工确认。
- 本轮未连接、控制或移动实体 AGV；未执行 `commit`、`push`、`reset`、`clean`。模板化改造继续后置，实体验收仍需现场隔离、明确授权、只读预检和地图比对。

## 2026-08-06 physical read-only preflight and protocol correction (local only)

- 车辆通电且现场确认后，只执行了只读 TCP 查询。独立 physical Adapter 使用单独端口和临时数据库；读取完成后已停止。既有 Simulator Adapter 未修改。
- API `1100` 确认当前地图为 `guangzhou606`，MD5 为 `e1b8d6b2b24362c1d44f1884c0abd8fb`，与 Profile 快照一致；当前位置为 `LM1`，车辆停止，电量约 `0.93`，定位置信度 `0.9859`，无急停、阻塞、错误或致命告警，`reloc_status=1`。
- API `1101` 按厂商协议使用 `{"return_laser":false}`。响应再次确认 `LM1`、置信度 `0.9859`、无急停/阻塞/错误/致命告警、速度和角速度为零、`path=[]`、`running_status=0`。
- 当前响应没有 `fork_auto_flag` 或其他已确认语义的自动模式信号；API 也没有返回地图版本和直接有向边。`dispatch_mode=0` 仅作为原始观测值记录，未映射成自动模式。
- `TcpAgvClient` 已修正 `1101` 请求体，并在 physical gate 缺失自动模式信号时返回明确阻断原因。Adapter 测试 **56/56** 通过。
- 最新 Release solution build：14 个项目，0 warnings，0 errors。Release tests：**142/142** 通过：Domain 19、MES 24、Adapter 56、WPF 22、E2E 8、Simulator 4、Workflow Contract 9。
- 结论：只读通信与部分安全状态通过，但物理派单验收仍被自动模式、地图版本和直接有向边三项证据阻断。未发送 `4005`、`3066`、`3067` 或 `3068`，实体 AGV 未移动；physical Profile 继续保持 `enableAutomaticDispatch=false`。

## 2026-08-06 physical acceptance paused after map change (local only)

现场 AGV 已断电，实体导航验收暂停。本节之后不得将断电前的控制器数据视为当前就绪状态；除非未来现场重新通电、隔离并明确授权，否则不连接、不控制、不下发、不取消实体 AGV 任务。

- 断电前最后提供的控制器状态报告：地图 `guangzhou606`，MD5 `816e68b9a367d9c8d5eaee9331a7ef58`，当前位置/目标 `LM1`，定位置信度 `0.9827`，车辆静止，无急停、阻塞、错误或致命告警。
- 该 MD5 与 physical Profile 的历史快照 `e1b8d6b2b24362c1d44f1884c0abd8fb` 不一致。控制器响应未提供可信的地图版本、站点清单或直接有向边，故不能据此更新 Profile 或启用真实派单。
- 原始字段 `manualBlock=true`、`dispatch_mode=0`、`src_release=false` 已记录；其中 `manualBlock=true` 继续作为 Adapter 阻断条件，`dispatch_mode`、`src_release` 和 SRC 控制模式均没有厂商确认语义，整车自动导航模式仍为 `unknown`。
- 当前结论为 **NO-GO**：保持 `enableAutomaticDispatch=false`，MES 不得创建或下发实体导航任务。断电前的 `LM1`、速度和安全状态只作历史记录，不能作为下一次验收放行证据。
- 下次现场首先做新的只读预检和地图比对，取得当前地图名称/版本/MD5、站点和直接有向边，以及自动模式、控制权与低速限制的厂商可验证证据；随后才可评估一次隔离的低速导航测试。
- 当前 physical-acceptance API、MES/WPF 接入脚手架在最新本地改动后仍未完成全量验证，不得部署或用于实车。详见 `docs/physical-acceptance/2026-08-06-pause-checkpoint.md`。

本 checkpoint 仅更新文档；未执行 `commit`、`push`、`reset`、`clean`，也未在断电后连接、控制或移动实体 AGV。

## 2026-08-07 WPF fleet status and configurable offline loop (pushed)

- `448c85c` 将 WPF `AGV 通讯与调度` 页面接入 `/api/agvs/fleet/status`，每行展示 MES 任务状态、设备状态、目标站点、执行路径和错误；`MainViewModel.UpdateAgvs` 现在消费完整 fleet status，而不是只显示基础 AGV 快照。
- 同一提交修复了历史活动任务误关联：MES fleet status 优先按 Simulator snapshot 的 `CurrentTaskId` 与当前 transport operation 精确匹配，无法确认时不静默选择旧任务。新增的 MES/WPF 回归覆盖了派发、暂停、恢复状态对账。
- `0aa94ef` 将 `scripts/verify-local.ps1` 的路线改为 `-SourceStationCode` / `-TargetStationCode` 参数，并跟随 MES 返回的 `activeAgvId` 发送暂停、恢复和到站控制；进程校验要求临时 MES/Adapter SQLite 存储，避免历史活动任务污染结果。
- 备用端口隔离进程验证已通过：Simulator `5361`、Adapter `5362`、MES `5363`。默认 `2 -> 4` 与可配置 `2 -> 3` 路线均完成创建、派发、fleet 状态对账、暂停、恢复、取货/卸货到站确认并达到 `COMPLETED`。
- 新增 WPF 状态化回归覆盖动态创建、显式派发、暂停、恢复、两次到站、人工取货/放货确认和完成；MES 回归补充多 AGV 独立对账及取货后按卸货 operation 取消。
- 最新 Release solution build 为 0 warnings、0 errors；Release 全量测试 **172/172 通过**（Domain 19、MES 38、Adapter 57、WPF 36、E2E 9、Simulator 4、Workflow Contract 9）。本轮仍只使用 Simulator，未连接、控制或移动实体 AGV。

真实 AGV 继续保持 **NO-GO**：车辆断电，断电前地图 MD5、`manualBlock=true` 和自动模式 `unknown` 均为历史/未确认信息。下一次现场工作必须在隔离和明确授权后，从重新通电的只读预检开始，并重新比对地图、站点、直接有向边、自动模式和控制权；这些现场条件不阻塞当前离线 WPF 调度开发。

## 2026-08-07 WPF control-center complete UIA loop (local only)

- WPF 创建任务不再依赖固定的 `SAMPLE_01 -> ST_PREP_01` 默认值；界面从 MES 加载启用站点集合，并按操作员实际选择的起点、终点、优先级、描述和外部单号创建任务。Workflow 默认模板中的运输站点也改为空值，发布前按当前 MES 站点目录解析和校验。
- Simulator 完整闭环已从 WPF 中控界面通过：动态参数创建任务、显式派发、暂停、恢复、取货到站确认、放货到站确认，最终进入 `Completed`。AGV 页命令要求 adapter 控制权以及 MES task/operation 精确关联；取消统一走 MES 任务取消接口，避免设备与 MES 状态分叉。
- 默认最大化窗口下的 Windows UI Automation 运行 `wpf-ui-20260807-maximized` 已通过；随后加入运行时 `WindowPattern` 最大化断言，并由 `wpf-ui-20260807-runtime-maximized` 再次通过。两次验证结束时 MES `/api/agvs/fleet/status` 无活动任务，Adapter fleet 的 `currentTaskId` 全部为空，确认任务完成后 MES 与 Adapter 均恢复 fleet idle；第二次使用自定义 `StatePath`，清理脚本按该路径停止并删除状态文件。
- 修复 WPF 命令状态更新：刷新开始、刷新完成及 AGV 状态恢复时会触发 `CanExecuteChanged`，关联的 `Paused` 任务在成功刷新后恢复按钮保持可执行；后台刷新期间保留上一轮有效 task/fleet 展示。
- 修复站点集合刷新：MES 站点目录未变化时不再清空并重建 `AvailableStations`，从而保留已选站点、路线预览和 ComboBox/UIA 元素；UIA 脚本同时按 Windows PowerShell 5 语义展开顶层 JSON 数组，避免将整个 fleet 数组误判为单条活动记录。
- WPF 定向测试 Debug **70/70**、Release **70/70** 通过，覆盖动态建单、完整状态流转、task/operation 关联、刷新快照保持、`CanExecuteChanged`、退出竞态和物理派单 fail-closed 门禁。Recover API 对未知任务/不可对账设备状态分别返回稳定的 404/409，WPF HTTP 合约保留错误详情。Release solution build 为 0 warnings、0 errors；Release 全量测试 **234/234** 通过（Domain 19、MES 49、Adapter 72、WPF 70、E2E 10、Simulator 5、Workflow Contract 9）。
- WPF 主窗口已默认最大化显示，同时保留标准窗口边框和还原/最小化能力；该启动状态已经过上述最终 UIA 完整闭环复验。

真实 AGV 继续保持 **NO-GO**。本轮没有重新通电，也没有执行实体设备连接、控制、派单或移动验证；断电前地图 MD5、`manualBlock=true` 和自动模式 `unknown` 仍不得作为当前放行证据。现场验证继续跳过，不阻塞 Simulator/WPF 离线闭环开发。
