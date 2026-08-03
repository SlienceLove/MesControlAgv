# AGV MES MVP Progress

Last updated: 2026-08-03

## Current status

The `.NET 8 + WPF` MVP is implemented and the final review fix wave is complete. The fixed route is `SAMPLE_01 -> ST_PREP_01`; the service ports remain Simulator `5183`, Adapter `5041`, and MES `5045`.

MES owns task state, SQLite persistence, and audit events. Adapter owns device protocol, control ownership, idempotent dispatch, device-confirmed cancellation, and timeout reconciliation. WPF calls MES action APIs only.

## Automated verification

Use the serial shared-compilation workaround when required:

```powershell
dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1
dotnet test MesControlAgv.sln --no-build -p:UseSharedCompilation=false -m:1
```

The suite contains 43 tests after removal of the no-op E2E test and addition of cancellation, error persistence, retry validation, and recovery coverage. The build passed with 0 warnings and 0 errors. Adapter focused tests passed 10/10 in this environment; Windows application-control policy blocked DLL loading for the Simulator, MES, and E2E focused runs.

## Live verification

Live service and WPF processes were not started during this fix wave. Existing process-level validation covers health checks, the normal `SAMPLE_01 -> ST_PREP_01` flow, failure retry, timeout reconciliation, and MES restart recovery.

## Remaining boundary

When the vendor protocol and control-owner rules are confirmed, replace only the Adapter device client. MES lifecycle, task state, audit events, WPF control flow, and the MES-to-Adapter API contract remain unchanged.
