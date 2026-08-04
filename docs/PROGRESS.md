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

The suite contains 69 tests after addition of experiment workflow management, AGV communications, batch-import coverage, and KPI dashboard coverage. The serial build passed on 2026-08-04 with 0 warnings and 0 errors, and all 69 tests passed in this environment across Domain, MES, Adapter, Simulator, E2E, and WPF.

## Live verification

The three service processes were started with the built DLLs for process-level validation. Health checks, the planning endpoint, and the normal transport flow passed; `scripts/verify-local.ps1` created a task, simulated pickup/dropoff arrival, confirmed both operations, and observed `COMPLETED` with required audit events. All three services are currently listening on Simulator `5183`, Adapter `5041`, and MES `5045`. The physical-robot run has not been completed.

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

On 2026-08-04, the serial solution build passed with 0 warnings and 0 errors. All 69 tests passed: Domain 12, MES 16, Adapter 16, WPF 14, E2E 7, and Simulator 4. The WPF XAML was also compiled successfully. Batch import parser coverage includes CSV quoting, UTF-8 BOM, XLSX shared strings/numeric cells, Chinese headers, validation issues, and priority/planned-time sorting.

The extension has been verified against the Simulator path. Physical AGV connection and vendor-specific on-site acceptance remain outstanding; before using `Agv:Driver=tcp`, validate the robot IP, firmware, map/station IDs, control ownership, safety gates, and movement behavior in an isolated acceptance environment.
