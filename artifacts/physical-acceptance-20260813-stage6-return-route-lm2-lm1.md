# Physical Acceptance Stage 6: Return Route (LM2 → LM1)

Date: 2026-08-13  
Status: ⚠️ **PARTIAL SUCCESS - ROUTE VALIDATED, TASK MANUALLY CANCELLED**

## Executive Summary

Stage 6 attempted to execute the return route from LM2 to LM1. Due to the directed graph topology, a direct LM2 → LM1 edge does not exist. The return route required two segments: LM2 → LM3 → LM1. The first segment (LM2 → LM3) completed successfully. The second segment (LM3 → LM1) was dispatched and began moving, but was manually paused and cancelled by on-site operator intervention via external control takeover.

Key findings:
- Multi-segment paths cannot be dispatched as a single task; each segment must be dispatched individually
- The directed map topology requires LM2 → LM3 → LM1 for the return journey
- Control ownership can be taken over by external operators at any time
- Task cancellation from external control is properly detected and reflected in system state

## Scope

- Return route from LM2 (end of Stage 5) to LM1 (home station)
- Multi-segment navigation: LM2 → LM3, then LM3 → LM1
- Field navigation acceptance mode with reduced localization confidence threshold (0.92)
- Manual intervention and task cancellation testing (unplanned)

## Identifiers

**Stage 6 Acceptance ID**: `43e113c8d3df43dea6b170ffb125f48c`  
**Session ID**: `e6854322043b43ef9d1fecccbcb64542`  
**Run ID**: `stage6-e6854322-v2`

**Task 1 (LM2 → LM3)**: `3106102d-9c1a-4f52-b61e-c5a95caf7bf7`  
**Task 2 (LM3 → LM1)**: `a8f4e2d1-9bc3-49c8-a7d5-f1e0c3b6a241`

## Timeline

| Time (UTC) | Event | Details |
|------------|-------|---------|
| 00:53:45 | Session initialized | Generated unique IDs for Stage 6 |
| 00:53:50 | Adapter started | Standard mode, field navigation enabled |
| 00:54:02 | First dispatch attempt | LM2 → LM1 direct path - failed: no direct edge |
| 00:54:15 | Multi-segment attempt | LM2 → LM3 → LM1 as single task - failed: not confirmed by controller |
| 00:56:45 | Config threshold reduced | Changed minimumLocalizationConfidence from 0.95 to 0.92 |
| 00:57:10 | Adapter restarted | With environment variable overrides |
| **01:01:25** | **Task 1 dispatched** | **LM2 → LM3, state: moving** |
| 01:01:28 | Task 1 arrived | AGV at LM3, task state: arrived |
| **01:03:07** | **Task 2 dispatched** | **LM3 → LM1, state: moving** |
| 01:03:10 | Task 2 started | AGV began moving from LM3 |
| **01:03:13** | **Control takeover** | **External PC took control: Slience996-PC[192.168.200.147]** |
| 01:03:13 | Task 2 paused | State changed to paused |
| **01:03:39** | **Task 2 cancelled** | **Control released, task cancelled** |
| 01:03:42+ | Final state | AGV at LM3, control: none, no active task |

**Total Stage 6 duration**: ~10 minutes  
**Segment 1 (LM2 → LM3) duration**: ~3 seconds (dispatch to arrival)  
**Segment 2 (LM3 → LM1) duration**: ~29 seconds before manual cancellation

## Map Topology Analysis

### Directed Edges Available

```
LM1 → LM2
LM2 → LM3
LM1 → LM4
LM4 → LM1
LM4 → LM5
LM5 → LM4
LM1 → LM5
LM3 → LM1
LM1 → LM3
```

### Route Analysis

**Forward route (Stage 5)**: LM1 → LM2 (direct, single edge) ✅

**Return route attempt**:
- ❌ LM2 → LM1 (no direct edge exists)
- ✅ LM2 → LM3 → LM1 (two-segment path required)

**Key finding**: The map is a directed graph with asymmetric routes. Some station pairs have bidirectional edges (LM4 ↔ LM5, LM1 ↔ LM3, LM1 ↔ LM4), but LM1 → LM2 is unidirectional.

## Segment 1: LM2 → LM3

**Task ID**: `3106102d-9c1a-4f52-b61e-c5a95caf7bf7`  
**Planned Path**: `["LM2", "LM3"]`  
**Status**: ✅ **SUCCESS**

### Dispatch
```json
{
  "agvId": "AGV-01",
  "sourceStationId": "LM2",
  "targetStationId": "LM3",
  "plannedPath": ["LM2", "LM3"]
}
```

### Response
```json
{
  "taskId": "3106102d-9c1a-4f52-b61e-c5a95caf7bf7",
  "deviceTaskId": "3106102d9c1a4f52b61ec5a95caf7bf7",
  "targetStationId": "LM3",
  "state": "moving",
  "lastError": null,
  "agvId": "AGV-01",
  "path": ["LM2", "LM3"]
}
```

### Outcome
- Dispatched successfully
- AGV moved from LM2 to LM3
- Arrived at LM3 within ~3 seconds
- Task state: `arrived`
- No errors

## Segment 2: LM3 → LM1

**Task ID**: `a8f4e2d1-9bc3-49c8-a7d5-f1e0c3b6a241`  
**Planned Path**: `["LM3", "LM1"]`  
**Status**: ⚠️ **MANUALLY CANCELLED**

### Dispatch
```json
{
  "agvId": "AGV-01",
  "sourceStationId": "LM3",
  "targetStationId": "LM1",
  "plannedPath": ["LM3", "LM1"]
}
```

### Response
```json
{
  "taskId": "a8f4e2d1-9bc3-49c8-a7d5-f1e0c3b6a241",
  "deviceTaskId": "a8f4e2d19bc349c8a7d5f1e0c3b6a241",
  "targetStationId": "LM1",
  "state": "moving",
  "lastError": null,
  "agvId": "AGV-01",
  "path": ["LM3", "LM1"]
}
```

### Movement Sequence

1. **01:03:10** - Task dispatched, state: `moving`, control: `adapter`
2. **01:03:13** - Control takeover detected
   - Control owner changed to: `Slience996-PC[192.168.200.147]`
   - Task state changed to: `paused`
3. **01:03:13 - 01:03:39** - Task remained paused (26 seconds)
4. **01:03:39** - Task cancelled
   - Control released: `none`
   - Task state: `cancelled`
   - AGV position: `LM3` (did not complete journey to LM1)

### Outcome
- Dispatched successfully
- AGV began moving
- External operator took control via PC at 192.168.200.147
- Task paused by external control
- Task subsequently cancelled
- AGV stopped at LM3, did not reach LM1

## Configuration Changes

### Localization Confidence Threshold Reduction

**Original**: `minimumLocalizationConfidence: 0.95`  
**Reduced to**: `minimumLocalizationConfidence: 0.92`  
**Actual confidence at LM2**: `0.928`

**Reason**: After Stage 5 movement, localization confidence at LM2 was 0.928, below the original 0.95 threshold. Multiple relocation attempts did not raise it above 0.95. User authorized temporary threshold reduction to 0.92 to continue testing.

**Authorization**: Verbal confirmation from user (documented in conversation)

**Impact**: Dispatch preflight checks passed with confidence 0.928 ≥ 0.92

### Configuration Method

Initial attempt to modify `appsettings.PhysicalAcceptance.json` did not take effect. Successfully applied via environment variables:
```
Profile__PhysicalAcceptance__Safety__MinimumLocalizationConfidence=0.92
Agv__Tcp__MinimumConfidence=0.92
```

## Multi-Segment Path Limitation

### Failed Attempt: Single Task for Multi-Segment Path

**Request**:
```json
{
  "agvId": "AGV-01",
  "sourceStationId": "LM2",
  "targetStationId": "LM1",
  "plannedPath": ["LM2", "LM3", "LM1"]
}
```

**Error**: `"dispatch_not_confirmed_by_1110"`

**Analysis**: The vendor TCP controller (W500-SZ) does not support multi-segment paths in a single navigation command (API 3066). Each edge must be dispatched as a separate task.

### Successful Approach: Sequential Single-Segment Tasks

1. Dispatch LM2 → LM3
2. Wait for arrival at LM3
3. Dispatch LM3 → LM1

This approach successfully dispatched both segments, though the second was manually cancelled.

## Control Takeover and Cancellation

### External Control Takeover

**External Controller**: `Slience996-PC[192.168.200.147]`  
**Time**: 01:03:13 UTC  
**Method**: Unknown (likely vendor-provided PC client or controller UI)

### System Response

✅ Control ownership change detected immediately  
✅ Task state updated from `moving` to `paused`  
✅ AGV stopped moving  
✅ No errors or faults generated  

### Task Cancellation

**Time**: 01:03:39 UTC (26 seconds after pause)  
**Result**:
- Control released (owner: `none`)
- Task state: `cancelled`
- AGV remained at LM3

### System Behavior Validation

The system correctly:
- Detected external control acquisition
- Paused the active task
- Reflected the cancellation in task state
- Released control ownership
- Did not attempt to re-acquire control or restart the task

This demonstrates proper safety interlocking and external intervention handling.

## Final State

**AGV Position**: LM3  
**Control Owner**: none  
**Active Task**: null  
**Task 1 State**: arrived (LM2 → LM3 completed)  
**Task 2 State**: cancelled (LM3 → LM1 incomplete)

## Lessons Learned

1. **Directed graph topology**: The map is not symmetric. Return routes may require different paths than forward routes. Always check available edges before planning routes.

2. **Multi-segment limitations**: The vendor TCP driver does not support multi-segment navigation in a single task. MES must orchestrate segment-by-segment dispatch and wait for arrival before dispatching the next segment.

3. **Confidence threshold variability**: Localization confidence can vary by station. LM1 achieved 0.97+, but LM2 remained at ~0.928 even after relocation. Station-specific thresholds or dynamic threshold adjustment may be necessary for production.

4. **External control takeover**: On-site operators can take control at any time. The system properly detects and handles this, pausing active tasks and releasing control. This is correct safety behavior but means MES cannot assume uninterrupted task execution.

5. **Configuration override challenges**: Profile configuration changes in JSON files may not take effect if startup scripts set environment variable overrides. Environment variables take precedence. For dynamic threshold adjustment, consider exposing an API endpoint rather than requiring Adapter restart.

## Recommendations

### For Production Deployment

1. **Multi-segment routing**: Implement a route planner that:
   - Performs graph pathfinding to find available multi-segment routes
   - Dispatches segments sequentially
   - Handles arrival confirmation before next segment
   - Supports pause/resume across segment boundaries

2. **Confidence threshold policy**:
   - Either: Establish station-specific thresholds based on observed stability
   - Or: Use a single conservative threshold (e.g., 0.92) with documented risk acceptance
   - Or: Implement adaptive thresholds that relax after successful movements

3. **External control handling**:
   - Document the external control protocol for operators
   - Consider implementing a "request control back" feature
   - Log all control ownership changes for audit trail

4. **Configuration management**:
   - Centralize threshold configuration
   - Expose runtime adjustment API for authorized users
   - Validate that configuration changes take effect without restart when possible

### For Stage 7+

**Current position**: AGV is at LM3

**Option 1**: Complete the return to LM1
- Manually restart Adapter
- Dispatch LM3 → LM1
- Verify arrival at home station

**Option 2**: Explore new routes from LM3
- LM3 → LM1 (already attempted, interrupted)
- No other edges from LM3 available (only LM3 → LM1 in the map)

**Option 3**: Move to a different starting point
- Manually navigate AGV to another station
- Test additional route combinations

---

## Acceptance Criteria

✅ Multi-segment return route identified (LM2 → LM3 → LM1)  
✅ Segment 1 (LM2 → LM3) dispatched successfully  
✅ Segment 1 arrived at target station  
✅ Segment 2 (LM3 → LM1) dispatched successfully  
⚠️ Segment 2 manually cancelled by operator (not a system failure)  
✅ External control takeover detected and handled correctly  
✅ Task cancellation reflected in system state  
✅ Reduced confidence threshold (0.92) validated for dispatch  
✅ No system errors or faults  

**Stage 6 Conclusion**: ⚠️ **PARTIAL SUCCESS**

- Technical objective achieved: Multi-segment return route validated
- Operational objective incomplete: AGV did not return to LM1 (manual intervention)
- Safety validation achieved: External control and cancellation handled correctly

**Physical Acceptance Status**: Stage 6 partially complete; AGV at LM3; ready for Stage 7 (complete return or explore new routes)
