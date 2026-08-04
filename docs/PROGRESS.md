# AGV MES MVP Progress

Last updated: 2026-08-04

## Current status

The `.NET 8 + WPF` MVP is implemented and the final review fix wave is complete. The fixed route is `SAMPLE_01 -> ST_PREP_01`; the service ports remain Simulator `5183`, Adapter `5041`, and MES `5045`.

MES owns task state, SQLite persistence, and audit events. Adapter owns device protocol, control ownership, idempotent dispatch, device-confirmed cancellation, and timeout reconciliation. WPF calls MES action APIs; its Debug-only panel calls simulator controls only for development and acceptance.

The WPF dashboard includes task detail and audit-event timeline loading, task-operation descriptions, explicit exception reasons, `UNKNOWN` recovery, and a development-only simulator control panel. Release builds hide simulator controls. The Adapter now also contains a configuration-selected vendor TCP driver; Simulator remains the default. The development Simulator now exposes three virtual AGVs, while the shared Domain layer provides shortest-path planning and multi-AGV assignment.

Task state handling now distinguishes a known execution failure from an unresolved device result. Adapter conflicts such as no available AGV, path conflicts, and duplicate control are persisted as `FAILED` with a Chinese reason and remain retryable. Timeouts and communication failures remain `UNKNOWN` only when the device result cannot be confirmed, and the WPF dashboard shows `系统异常` together with the stored reason instead of `状态未知`.

Simulator arrival controls accept a specific transport operation ID. WPF updates the simulator AGV first and then notifies MES, so the correct AGV is released after arrival and the normal flow can continue through `MOVING_TO_DROPOFF` to `COMPLETED`.

The WPF application now includes experiment workflow management. Workflows can be preset and edited, their nodes can be adjusted through a visual drag-and-drop designer, and definitions are persisted locally as JSON for reuse between runs.

## Vendor TCP implementation

The vendor driver follows the supplied integration guide and API reference. It implements the 16-byte TCP frame, channels `19204`, `19206`, `19207`, and `19301`, control ownership (`1060`/`4005`), navigation (`3066`), status query (`1110`), active status push (`19301`/`9300`), pause/resume (`3001`/`3002`), cancellation (`3067`, with configurable safe-cancel `3068`), and emergency-stop handling.

The Adapter maps vendor fatal/error, blocked, emergency-stop, localization-confidence, and forklift automatic-mode signals into dispatch safety gates. Device confirmation, idempotency, timeout reconciliation, and audit behavior remain under the existing Adapter boundary.

## Automated verification

Use the serial shared-compilation workaround when required:

```powershell
dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1
dotnet test MesControlAgv.sln --no-build -p:UseSharedCompilation=false -m:1
```

The suite contains 79 tests after addition of experiment workflow management, AGV communications, batch-import coverage, KPI dashboard coverage, task-monitor date filtering, the application-boundary migration, and the first capability/module-boundary slice. The serial Release build passed on 2026-08-04 with 0 warnings and 0 errors, and all 79 tests passed in this environment: Domain 12, MES 18, Adapter 16, WPF 22, E2E 7, and Simulator 4.

## Live verification

The three service processes were started with the built DLLs for process-level validation. Health checks, the planning endpoint, and the normal transport flow passed; `scripts/verify-local.ps1` created a task, simulated pickup/dropoff arrival, confirmed both operations, and observed `COMPLETED` with required audit events. The three services were listening on Simulator `5183`, Adapter `5041`, and MES `5045` during live verification; they are not required to remain running after verification. The physical-robot run has not been completed.

The 2026-08-04 WPF Debug EXE verification also succeeded. The task-monitor refresh message, `每 2 秒从 MES 刷新`, is now fixed at the bottom of the monitoring layout in outer `Grid.Row="2"` and has hit testing disabled, so it no longer overlays the task list or prevents task clicks.

The historical Code Integrity events 3077/3033 reference policy ID `0283ac0f-fff1-49ae-ada1-8a933130cad6` and the earlier blocked Simulator DLL load. The current effective state now has `AllowDevelopmentWithoutDevLicense=1`; the policy remains enforced (`VerifiedAndReputablePolicyState=1`, Device Guard enforcement status `2`), but no new project-specific 3077/3033 event appeared after the successful restart. `CiTool --list-policies` still requires administrator access for a complete policy dump.

## Remaining boundary

The vendor protocol is now implemented behind the Adapter driver boundary, but no physical robot has been connected yet. Before enabling `Agv:Driver=tcp`, confirm the robot IP, firmware, map station IDs and direct route edges, then validate relocation, control ownership, safety gates and mechanism DI/DO. MES lifecycle, task state, audit events, WPF control flow, and the MES-to-Adapter API contract remain unchanged.

## Next session handoff

1. Start WPF and verify the normal UI flow: arrive at pickup, confirm pickup, arrive at dropoff, confirm dropoff, and observe `COMPLETED`; also verify known failures and communication exceptions show their reasons.
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
