# Physical acceptance stage 3 offline hardening

Date: 2026-08-12
Status: OFFLINE HARDENING VERIFIED / PHYSICAL ACCEPTANCE NO-GO

## Scope

- Continued from source commit `3726aeba8db872501429e882b05960a74e256a7f`
  on branch `feat/wpf-map-export` with uncommitted Stage 3 safety changes.
- No Adapter connected to the physical controller. Existing local processes
  were left running and no process was stopped or restarted.
- No `4005`, `4006`, `3066`, `3001`, `3002`, `3067`, or `9300` request was
  sent or exercised against a physical AGV.

## Hardening result

- Physical dispatch, cancellation, and manual control release use one shared
  physical-session gate so lifecycle writes cannot cross an in-flight dispatch.
- Control acquired by a field-navigation attempt is released only when that
  same attempt can prove it acquired ownership and stopped before a possible
  navigation write. Unknown transport outcomes remain fail-closed.
- Physical acceptance rejects pause and resume until a separate lifecycle
  authorization exists. Both direct task endpoints and the aggregate AGV
  command endpoint reject before control acquisition or a device write.
- The aggregate pause/resume gate now also runs before fleet snapshot lookup,
  so a rejected physical lifecycle command performs no device query.
- Disabled cancellation rejects before task lookup. Unknown task cancellation
  returns without acquiring control or sending a cancellation request.
- The read-only run mode continues to reject all mutation endpoints before any
  controller mutation call.
- `LocalPortContractTests` can now locate the repository from both normal and
  isolated test output directories.

## Verification

- Aggregate physical pause/resume regression selection: `40/40` passed.
- Full Adapter Release tests: `148/148` passed.
- Isolated-output `LocalPortContractTests`: `1/1` passed.
- Full Release solution tests: `420/420` passed:
  Domain 37, MES 49, Adapter 148, WPF 159, E2E 12, Simulator 5, and Workflow
  Contract 10.
- Full Release solution build: `0 warnings / 0 errors`.
- `git diff --check`: passed; only Git line-ending conversion notices were
  emitted.

## Build hashes

| Artifact | SHA-256 |
| --- | --- |
| `MesControlAgv.Adapter.dll` | `709A4E08EAF6B660245D1ACBCFB4E93B97C24B3020AD381FB020257113F24856` |
| `MesControlAgv.Adapter.Tests.dll` | `2F00E9C29DA72CCE04CAE15913A515C8B089074615023CFDC30F8AA648606314` |
| `MesControlAgv.E2E.Tests.dll` | `C0B151C8D389E7E88BB6281D1158F57411362179915B887439140251A81E34DA` |

## Physical gate

- The last controller-authoritative confidence remains `0.9482`, below the
  configured minimum `0.95`. A rounded display value is not accepted as gate
  evidence.
- Stage 3 therefore remains **BLOCKED / NO-GO**. Before any operation that may
  acquire control or dispatch movement, obtain a fresh authorized read-only
  preflight, renewed movement authorization, and a new unique acceptance/task
  identifier, then request explicit operator authorization immediately before
  the first possible control command.
