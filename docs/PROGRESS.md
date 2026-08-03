# AGV MES MVP Progress

Last updated: 2026-08-03

## Current status

The `.NET 8 + WPF` MVP is implemented and the final review fix wave is complete. The fixed route is `SAMPLE_01 -> ST_PREP_01`; the service ports remain Simulator `5183`, Adapter `5041`, and MES `5045`.

MES owns task state, SQLite persistence, and audit events. Adapter owns device protocol, control ownership, idempotent dispatch, device-confirmed cancellation, and timeout reconciliation. WPF calls MES action APIs; its Debug-only panel calls simulator controls only for development and acceptance.

The WPF dashboard includes task detail and audit-event timeline loading, `UNKNOWN` recovery, and a development-only simulator control panel. Release builds hide simulator controls.

## Automated verification

Use the serial shared-compilation workaround when required:

```powershell
dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1
dotnet test MesControlAgv.sln --no-build -p:UseSharedCompilation=false -m:1
```

The suite contains 44 tests after addition of the WPF detail and recovery coverage. The full suite passed with 0 failures; the build passed with 0 warnings and 0 errors.

## Live verification

Live service and WPF processes were not started during this fix wave. Existing process-level validation covers health checks, the normal `SAMPLE_01 -> ST_PREP_01` flow, failure retry, timeout reconciliation, and MES restart recovery.

## Remaining boundary

When the vendor protocol and control-owner rules are confirmed, replace only the Adapter device client. MES lifecycle, task state, audit events, WPF control flow, and the MES-to-Adapter API contract remain unchanged.
