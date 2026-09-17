# Task 1 Report: Sample Workstation Business Chain

## Implementation

Added `ExperimentSampleWorkstationBusinessChainTests.cs`, a single end-to-end API integration test. It boots a physical-profile test host (`UseSimulator=false`) with `SAMPLE-WORKSTATION-01` enabled, controllable, and configured for `sample-workstation.start-existing-task`. Its in-process fake implements both workstation ports and supplies the required Idle/Completed, Running/code 3, and Idle/Completed observations; no network client or field service is used.

The test creates/publishes the Start -> sample-workstation.execute-existing-task -> End workflow, then creates, validates, publishes, schedules, and formally admits the single-step experiment plan for `workstation/SAMPLE-WORKSTATION-01`. It explicitly invokes one dispatcher cycle and verifies admission, execution, node, job, schedule, lease, operation, and schedule-activity terminal state.

## Files

- `tests/MesControlAgv.Mes.Tests/ExperimentSampleWorkstationBusinessChainTests.cs` (new)

`MesWebApplicationFactory.cs` was briefly explored for a reusable profile override, but the final implementation leaves it behaviorally unchanged and uses a test-local physical factory to retain xUnit fixture compatibility.

## RED

Command:

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --filter "FullyQualifiedName~ExperimentSampleWorkstationBusinessChainTests"
```

Initial result: failed during compilation with `CS1503` in the newly added assertion (`WorkflowResourceLeaseRecord` passed to `Assert.Single`). This was a test-authoring error, not a production behavior gap.

## GREEN

Same focused command after correcting the assertion: passed, 1/1 tests.

## Full suite

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore
```

Passed: 321/321, 0 failed, 0 skipped (5 s).

## Self-review

- Scope is test-only; no production code changed.
- The test replaces both workstation interfaces with one in-process fake, so it cannot contact `192.168.200.157` or start a field service.
- It uses the formal admission endpoint and calls the dispatcher exactly once explicitly.
- Assertions cover both admission-time active lease binding and terminal release/activity visibility.
- Independent review identified and the implementation now addresses two gaps: the fake records/asserts the dispatched device/task values, and the schedule activity is asserted `Succeeded` with a non-null actual end.

## Concerns

No production behavior gap found. The initial RED run was a test compilation error rather than a behavioral failure; the brief permits an unchanged product path when the completed test passes. The final focused test passed 1/1 and the final full suite passed 321/321.
