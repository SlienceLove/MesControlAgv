# Physical Acceptance Stage 4: Localization Confidence Verification

Date: 2026-08-13  
Status: ✅ **PASS - CONFIDENCE VERIFIED**

## Executive Summary

Stage 4 successfully verified localization confidence after multiple relocation attempts. The final confidence value of **0.9708** exceeds the required minimum of **0.95** by 2.19%. All safety gates are operational, map evidence is controller-authoritative and matches the configuration, and the vehicle is ready for supervised movement testing.

## Scope

- Continued from Stage 3 offline hardening verification
- Three sequential read-only preflight sessions to validate localization confidence after AGV relocation
- No control acquisition, no navigation commands, no AGV movement
- Only read-only vendor TCP APIs were invoked: `1060`, `1110`, `1101`, `1021`, `1300`, `1301`, `1302`, `4011`

## Verification Sessions

### Session 1: Initial Check

**Time**: 2026-08-13 00:36:21 UTC  
**Session ID**: stage4-20260813-04cc9fbf  
**Database**: `artifacts/physical-acceptance/stage4-preflight-20260813-082702.db`

**Result**: ❌ Confidence below threshold

- Localization confidence: `0.9241`
- Gap to target: `-0.0259` (-2.73%)
- Blocking reasons: `adapter_does_not_hold_control`, `localization_confidence_below_threshold`, `automatic_dispatch_disabled`

**Action**: AGV relocation requested

---

### Session 2: First Relocation Check

**Time**: 2026-08-13 00:39:52 UTC  
**Session ID**: stage4-recheck-f22e8bf5  
**Database**: `artifacts/physical-acceptance/stage4-recheck-20260813-083929.db`

**Result**: ❌ Confidence improved but still below threshold

- Localization confidence: `0.9330`
- Gap to target: `-0.0170` (-1.79%)
- Improvement: `+0.0089` (+0.96% from session 1)
- Blocking reasons: `adapter_does_not_hold_control`, `localization_confidence_below_threshold`, `automatic_dispatch_disabled`

**Action**: Second AGV relocation requested

---

### Session 3: Second Relocation Check (Final)

**Time**: 2026-08-13 00:42:11 UTC  
**Session ID**: stage4-recheck3-bccc491b  
**Database**: `artifacts/physical-acceptance/stage4-recheck3-20260813-084150.db`

**Result**: ✅ **PASS - Confidence verified**

- Localization confidence: **`0.9708`**
- Exceeds target by: `+0.0208` (+2.19%)
- Total improvement: `+0.0467` (+5.06% from session 1)
- Blocking reasons: `adapter_does_not_hold_control`, `automatic_dispatch_disabled` (both expected)
- **Hard blocker removed**: `localization_confidence_below_threshold` ✅

---

## Confidence Progression

| Session | Time (UTC) | Confidence | Gap to 0.95 | Change | Status |
|---------|-----------|------------|-------------|--------|--------|
| 1 | 00:36:21 | 0.9241 | -2.73% | Baseline | ❌ Fail |
| 2 | 00:39:52 | 0.9330 | -1.79% | +0.96% | ❌ Fail |
| **3** | **00:42:11** | **0.9708** | **+2.19%** | **+4.06%** | **✅ Pass** |

## Controller Authoritative Evidence

All three sessions returned consistent map evidence from the live controller.

**Map Identity** (vendor TCP APIs `1300`, `1301`, `1302`, `4011`):
- Map name: `guangzhou606`
- Version: `1.0.6`
- MD5: `816e68b9a367d9c8d5eaee9331a7ef58`
- Captured: 2026-08-13 00:42:11 UTC

**Stations** (5 total):
- `LM1`, `LM2`, `LM3`, `LM4`, `LM5`

**Directed Edges** (9 total):
1. LM1 → LM2
2. LM2 → LM3
3. LM1 → LM4
4. LM4 → LM1
5. LM4 → LM5
6. LM5 → LM4
7. LM1 → LM5
8. LM3 → LM1
9. LM1 → LM3

**Map Evidence Verification**: ✅ Controller stations and edges exactly match Profile configuration

---

## Safety Status (Session 3 Final)

**Vehicle Status**:
- Online: `true`
- Control owner: `none`
- Current station: `LM1`
- Current task: `null`
- AGV ID: `AGV-01`

**Safety Readiness**:
- Vehicle operating mode: `unknown` (W500-SZ policy: `not-exposed-by-approved-model`)
- Emergency: `false` ✅
- Blocked: `false` ✅
- Fatal count: `0` ✅
- Error count: `0` ✅
- Relocation status: `1` (localized) ✅
- Localization confidence: **`0.9708`** ✅

**Controller Info**:
- Vehicle model: `W500-SZ`
- Controller version: `v3.4.8.0011`

---

## Configuration

**Controller**:
- Host: `<redacted-controller-host>`
- Status port: `19204`
- Command port: `19206`
- Control port: `19207`
- Push port: `19301`

**Adapter**:
- Run mode: `read-only-preflight`
- Acquire control: `false`
- Enable push: `false`
- Minimum confidence: `0.95`

**Profile**:
- Product: `MES-AGV` physical acceptance
- Features: Simulator disabled, automatic dispatch disabled, field navigation disabled, cancellation disabled
- Safety: W500-SZ operating mode policy `not-exposed-by-approved-model`, `requireAutomaticMode=false`

**Executable**:
- Build: Debug
- Adapter build: `MesControlAgv.Adapter.dll` (local Debug build)
- Config: `appsettings.PhysicalAcceptance.json`

---

## API Calls (Session 3)

**Read-only APIs invoked**:
- `1060` - Control ownership query
- `1110` - Active task list
- `1101` - Vehicle detailed status
- `1021` - Dedicated localization status
- `1300` - Map list
- `1301` - Map metadata
- `1302` - Map download
- `4011` - Controller configuration channel (map read)

**Mutation APIs**: ❌ None invoked

**Control acquisition**: ❌ Not attempted  
**Navigation commands**: ❌ Not sent  
**AGV movement**: ❌ Did not occur

---

## Blocking Reasons Analysis

### Session 3 Final Blocking Reasons

1. ✅ `adapter_does_not_hold_control` - **Expected** (read-only preflight does not acquire control)
2. ✅ `automatic_dispatch_disabled` - **Expected** (safety configuration requires manual authorization)
3. ✅ ~~`localization_confidence_below_threshold`~~ - **REMOVED** (confidence now 0.9708 ≥ 0.95)

### Dispatch Permitted

**Session 3**: `dispatchPermitted=false`

This is correct and expected for read-only preflight mode. The two remaining blockers are intentional safety gates that prevent unintended movement during preflight.

---

## Lessons Learned

1. **Relocation effectiveness**: Multiple relocation attempts were required to achieve stable high confidence
2. **Confidence variability**: Initial confidence (0.9241) vs final confidence (0.9708) shows 5.06% variation
3. **Progressive improvement**: Session 2 showed partial improvement (+0.96%), session 3 showed significant jump (+4.06%)
4. **Map evidence stability**: Controller-authoritative map evidence remained consistent across all three sessions

---

## Next Steps

Stage 4 localization confidence verification is complete. The vehicle is now ready for **Stage 5: First Supervised Route (LM1 → LM2)**.

### Stage 5 Prerequisites (to be confirmed before proceeding)

1. Fresh explicit written movement authorization for LM1 → LM2
2. New unique acceptance/permit/task IDs generated
3. Emergency stop and manual takeover confirmed ready
4. Route LM1 → LM2 confirmed isolated
5. Safety observer confirmed on-site and ready
6. Restart Adapter in `standard` mode (not read-only)
7. Acquire control via API `4005`
8. Dispatch single-segment navigation at max_speed=0.3 m/s via API `3066`

Stage 5 will involve **actual AGV movement** and requires a separate authorization and acceptance session.

---

## Acceptance Criteria

✅ Localization confidence ≥ 0.95  
✅ Controller map evidence matches Profile  
✅ All safety gates operational (emergency, blocked, faults clear)  
✅ Vehicle online at known station (LM1)  
✅ No control ownership conflicts  
✅ Read-only APIs only (no mutation)  
✅ AGV did not move  

**Stage 4 Conclusion**: ✅ **PASS**

**Physical Acceptance Status**: Stage 4 complete; Stage 5 awaiting authorization
