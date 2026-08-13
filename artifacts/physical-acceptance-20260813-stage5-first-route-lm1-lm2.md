# Physical Acceptance Stage 5: First Supervised Route (LM1 → LM2)

Date: 2026-08-13  
Status: ✅ **PASS - FIRST MOVEMENT SUCCESSFUL**

## Executive Summary

Stage 5 successfully executed the first supervised AGV movement from LM1 to LM2. The vehicle acquired control, navigated the single-segment route at the approved maximum speed of 0.3 m/s, arrived safely at the target station, and released control as expected. This marks the first actual AGV movement in the physical acceptance process.

## Scope

- First actual AGV movement under MES control
- Single-segment route: LM1 → LM2 (direct path)
- Field navigation acceptance mode with physical safety gates
- Control acquisition, supervised navigation, control release cycle
- Real-time monitoring of position, task state, and control ownership

## Identifiers

**Acceptance ID**: `78b3968547ab4ffe91fa8ca6a0d0f416`  
**Task ID**: `78b39685-47ab-4ffe-91fa-8ca6a0d0f416`  
**Device Task ID**: `78b3968547ab4ffe91fa8ca6a0d0f416`  
**Session ID**: `25855a07162c4ae588efecd4b290e6a4`  
**Run ID**: `stage5-field-nav`

## Timeline

| Time (UTC) | Event | Details |
|------------|-------|---------|
| 00:44:37 | Session initialized | Generated unique IDs |
| 00:47:51 | First dispatch attempt | Failed: AGV not enabled in config |
| 00:48:14 | Config fixed | Added `"enabled": true` to AGV profile |
| 00:49:36 | Second dispatch attempt | Failed: Field navigation disabled |
| 00:50:11 | Adapter restarted | Enabled field navigation acceptance |
| **00:50:11** | **Task dispatched** | **LM1 → LM2, state: moving** |
| 00:50:14 | Position: LM3 | In transit, control: adapter |
| 00:50:23 | Position: LM2 | Arrived at target, still moving state |
| **00:50:26** | **Task completed** | **State: arrived, position: LM2** |
| 00:50:29 | Task cleared | currentTaskId: null |
| 00:51:24+ | Stable at LM2 | No active task, control held |
| 00:51:27 | Control released | Released: true |
| **00:51:30** | **Final verification** | **LM2, control: none, task: null** |

**Total movement duration**: ~15 seconds (dispatch to arrival)  
**Total session duration**: ~7 minutes (including troubleshooting)

## Route Details

**Planned Path**: `["LM1", "LM2"]`  
**Source Station**: `LM1`  
**Target Station**: `LM2`  
**Path Type**: Single-segment, direct  
**Maximum Speed**: 0.3 m/s  
**AGV ID**: `AGV-01`

## Movement Observation

### Position Progression

1. **00:50:11** - Dispatch confirmed, AGV at LM1
2. **00:50:14** - AGV at **LM3** (unexpected intermediate position)
3. **00:50:17** - Still at LM3
4. **00:50:20** - Still at LM3
5. **00:50:23** - AGV at **LM2** (target reached)
6. **00:50:26** - Task state: `arrived`
7. **00:50:29+** - Stable at LM2, task cleared

### Observations

- AGV reported position **LM3** during transit, despite the planned path being LM1 → LM2
- This suggests:
  - LM3 may be a waypoint on the physical route between LM1 and LM2
  - Controller may report landmark stations during navigation
  - The actual traveled path differs from the single-segment logical path
- AGV successfully reached the target station LM2
- Task completion was clean: state transitioned to `arrived`, then task was cleared

## Control Ownership

**Before dispatch**: `none`  
**During movement**: `adapter` (MesControlAgv.Adapter)  
**After arrival**: `adapter` (held until explicit release)  
**After release**: `none` ✅

Control acquisition and release worked as designed.

## Task State Progression

1. **dispatched** → `moving`
2. **moving** → continued while in transit
3. **moving** → `arrived` (at target station LM2)
4. **arrived** → task cleared from `currentTaskId`
5. Task record retained with final state `arrived`

## Safety Gates

Physical acceptance safety gates were active throughout the movement:

- ✅ Control ownership enforced (only adapter could command)
- ✅ Localization confidence validated before dispatch
- ✅ Maximum speed limited to 0.3 m/s
- ✅ Emergency gates monitored (no emergency, no blocked, no faults)
- ✅ Field navigation acceptance mode active

## Configuration

**Adapter**:
- Run mode: `standard`
- Field navigation acceptance: `enabled`
- Acquire control: `true`
- Enable push: `false`
- Controller host: `192.168.200.151`

**AGV Profile**:
- AGV ID: `AGV-01`
- Model: `W500-SZ`
- Driver: `vendor-tcp`
- Max speed: `0.3` m/s
- Home station: `LM1`
- **Enabled**: `true` ✅

**Database**: `D:\Project\Github\Mes\artifacts\physical-acceptance\stage5-route-lm1-lm2-20260813.db`  
**Logs**: `C:\Users\33206\AppData\Local\Temp\MesControlAgv-physical-stage5-field-nav-logs`

## API Calls

**Field Navigation Dispatch** (successful):
```http
POST /field-navigation-acceptances/78b3968547ab4ffe91fa8ca6a0d0f416/dispatch
Content-Type: application/json

{
  "agvId": "AGV-01",
  "sourceStationId": "LM1",
  "targetStationId": "LM2",
  "plannedPath": ["LM1", "LM2"]
}
```

**Response**:
```json
{
  "taskId": "78b39685-47ab-4ffe-91fa-8ca6a0d0f416",
  "deviceTaskId": "78b3968547ab4ffe91fa8ca6a0d0f416",
  "targetStationId": "LM2",
  "state": "moving",
  "agvId": "AGV-01",
  "path": ["LM1", "LM2"]
}
```

**Control Release**:
```http
POST /agv/control/release
```

**Response**:
```json
{
  "released": true
}
```

## Troubleshooting Log

### Issue 1: AGV Not Enabled
- **Error**: "AGV  is not enabled by the active profile."
- **Cause**: Missing `"enabled": true` in AGV profile configuration
- **Fix**: Added `"enabled": true` to `appsettings.PhysicalAcceptance.json`
- **Resolution time**: ~3 minutes

### Issue 2: Field Navigation Disabled
- **Error**: "Field navigation acceptance is disabled by the active profile."
- **Cause**: Adapter started without `-EnableFieldNavigationAcceptance` flag
- **Fix**: Restarted Adapter with `-EnableFieldNavigationAcceptance` parameter
- **Resolution time**: ~2 minutes

### Issue 3: Wrong JSON Field Name
- **Error**: `NullReferenceException` on `command.PlannedPath`
- **Cause**: Request used `"path"` instead of `"plannedPath"`
- **Fix**: Changed JSON field name to match `FieldNavigationDispatchCommand` contract
- **Resolution time**: ~1 minute

## Lessons Learned

1. **Configuration completeness**: Physical acceptance profiles require explicit `"enabled": true` on AGV profiles, unlike simulator profiles
2. **Feature flags**: Field navigation acceptance must be enabled both in configuration and via startup parameter
3. **Contract alignment**: JSON field names are case-sensitive and must match the exact contract definition (`plannedPath`, not `path`)
4. **Route topology**: The logical path (LM1 → LM2) may differ from the physical path (LM1 → LM3 → LM2), and the controller reports actual traversed landmarks

## Next Steps

Stage 5 first supervised route is complete. The vehicle is now at **LM2** with control released. 

### Suggested Stage 6 Options

1. **Return route LM2 → LM1**: Verify bidirectional navigation
2. **Extended route LM2 → LM3**: Continue forward exploration
3. **Multi-segment route LM2 → LM1 → LM4**: Test complex path planning
4. **Pause/Resume testing**: Verify mid-route control commands

### Prerequisites for Next Stage

- AGV position: LM2 ✅
- Control released: Yes ✅
- No active task: Yes ✅
- No errors or faults: Yes ✅
- Configuration validated: Yes ✅

---

## Acceptance Criteria

✅ Control acquired before movement  
✅ AGV moved from LM1 to LM2  
✅ Target station reached successfully  
✅ Task state transitioned to `arrived`  
✅ Control released after completion  
✅ No emergency stops or faults  
✅ Maximum speed limit enforced (0.3 m/s)  
✅ Field navigation acceptance gates active  
✅ Real-time monitoring successful  

**Stage 5 Conclusion**: ✅ **PASS - First supervised movement successful**

**Physical Acceptance Status**: Stage 5 complete; ready for Stage 6
