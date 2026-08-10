# WPF UI Automation offline verification

`scripts/verify-wpf-ui.ps1` is an interactive Windows-only smoke driver for
the complete offline path:

`WPF UIA -> MES -> Adapter -> Simulator -> MES -> WPF UIA`

It starts a fresh Simulator/Adapter/MES process run with temporary SQLite
stores, launches the Debug WPF client with `WPF_RUNTIME_MODE=simulator`,
selects a configured non-default route, and drives the task through:

1. station selection and route preview;
2. task creation with priority, description, and external id;
3. explicit dispatch and AGV/device/path correlation;
4. pause and resume from the AGV communication tab;
5. Simulator pickup arrival, pickup confirmation, dropoff arrival, and
   dropoff confirmation;
6. `Completed`, required audit events, and idle MES/Adapter fleet status.

The driver intentionally does not modify `verify-local.ps1`, the WPF source,
or any physical-device profile. It is not a CI test: it requires an
interactive Windows desktop, the .NET UI Automation assemblies, a built Debug
WPF DLL, and an available desktop session. After attaching, it asserts through
`WindowPattern` that the main WPF window is maximized. It never connects to a
real AGV.

## Run

Build the Debug WPF client and Release service binaries first:

```powershell
dotnet build MesControlAgv.sln -c Debug --no-restore -p:UseSharedCompilation=false -m:1
dotnet build MesControlAgv.sln -c Release --no-restore -p:UseSharedCompilation=false -m:1
```

Then run the smoke from an interactive PowerShell window. The default
`2 -> 3` route is deliberately different from the historical `2 -> 4`
sample route:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\verify-wpf-ui.ps1 `
  -RunId wpf-ui-20260807-a `
  -SimulatorUrl http://localhost:5611 `
  -AdapterUrl http://localhost:5612 `
  -MesUrl http://localhost:5613
```

Use `-SourceStationCode` and `-TargetStationCode` for another enabled profile
route. The script rejects `2 -> 4` unless the driver is intentionally changed
for a separate baseline check. `-KeepServices` leaves the isolated service
run available for inspection; otherwise the WPF window and matching service
PIDs are stopped in `finally`. SQLite files are retained under the temporary
run directory for audit inspection.

When UI discovery changes, use `-DumpTree`. The failure output includes each
visible control's `ControlType`, `Name`, `AutomationId`, class, enabled state,
and bounds. A command-enable timeout also reports the AGV command-gate text
and the name/enabled state of the pause, resume, and cancel buttons. The
driver resolves workflow controls by stable `AutomationId`; localized captions
and visual layout are not part of the automation contract.

| Role | AutomationId |
|---|---|
| Source station ComboBox | `TaskSourceStationCombo` |
| Target station ComboBox | `TaskTargetStationCombo` |
| Priority TextBox | `TaskPriorityTextBox` |
| External id TextBox | `TaskExternalIdTextBox` |
| Operator TextBox | `TaskOperatorTextBox` |
| Description TextBox | `TaskDescriptionTextBox` |
| Route preview button | `PlanRouteButton` |
| Create button | `CreateTaskButton` |
| Dispatch button | `DispatchTaskButton` |
| Simulator arrival | `SimulateArrivalButton` |
| Pickup confirmation | `ConfirmPickupButton` |
| Dropoff confirmation | `ConfirmDropoffButton` |
| AGV pause | `PauseAgvButton` |
| AGV resume | `ResumeAgvButton` |
| AGV command-gate diagnostic | `AgvCommandGateStatusText` |
| Task monitor tab | `TaskMonitorTab` |
| AGV communication tab | `AgvCommunicationTab` |
| Task grid | `TaskGrid` |
| Task status/detail | `TaskStatusText` |
| Audit event list | `TaskEventsList` |

## Physical acceptance boundary

This smoke is Simulator-only. It must not be pointed at a physical Adapter or
used to bypass the current physical NO-GO gate. Physical work remains limited
to an explicitly authorized, isolated, read-only preflight after power is
restored, followed by map MD5, station, direct-edge, automatic-mode, control
ownership, and safety-gate comparison. No physical dispatch, cancellation,
low-speed movement, or TCP command is part of this script.
