# Physical acceptance stage 0 evidence

Date: 2026-08-12
Status: STAGE 1 SITE GATES CONFIRMED / READ-ONLY PREFLIGHT AUTHORIZED

## Frozen inputs

- Source commit: `3d20cad445ef958d68bc497e781a7f16b70bdc07`
- Branch: `feat/wpf-map-export`
- Candidate offline session ID: `d48eebc9-5882-4a23-b9e6-9e6294010cf8`
- Physical acceptance configuration remains read-only and dispatch-disabled.
- The configuration file is the repository's redacted example only; no
  controller address, credential, process ID, or temporary absolute path is
  recorded here.

## SHA-256

| Artifact | SHA-256 |
| --- | --- |
| `adapter.physical-acceptance.example.json` | `4FA5A3CA2E5D5781672523E94DFB5B8527DC3916264AF1BFEBD88EB7C82054F8` |
| Release `MesControlAgv.Adapter.dll` | `0B7556258C7A37F10F94E1E4F44584696CE1D959996816B540C6F6255C8BD487` |
| Release `MesControlAgv.Mes.dll` | `8FACF91E8D3A93298AD4AED4BF875E05EA2FB4D4CE30C556C6D2C30C6DB67AFA` |
| Release `MesControlAgv.Wpf.dll` | `7B20FE287C518188D6F54FC2AE628B555793C714524F9321852EC7ECA99BA252` |
| Release `MesControlAgv.Launcher.dll` | `1A2047C549A08CDA078BFB15B591C9D861AD0966AF466C5637A35BD21D3C0E8E` |
| Release `MesControlAgv.Domain.dll` | `A77D4ADADDF0E4A146B73E5C3E7BCE581807BD9F52C4BF529BDBC6B1DEC8D34A` |
| Release `MesControlAgv.Contracts.dll` | `3B667D4F4316236B797F1BFC388D563E7E6E830F37B6B55EAA1EBD9F8A426865` |

## Offline verification

- Full Release solution build: `0 warnings / 0 errors`.
- Full Release solution tests: `381/381` (Domain 37, MES 49, Adapter 109,
  WPF 159, E2E 12, Simulator 5, Workflow Contract 10).
- `git diff --check`: passed.
- No controller connection, control acquisition, task write, cancellation,
  pause, resume, or movement was attempted.

## Known blockers

- The remote branch is still one commit behind because the current environment
  cannot connect to GitHub; the frozen commit remains available locally.
- The WPF test project required explicit project-level `System.IO` and
  `System.Net.Http` global usings in the frozen build; that correction is now
  included in commit `3d20cad` and the clean full-solution Release gate passes.
- Stage 1 site gates were confirmed by the operator on 2026-08-12: the AGV is
  powered and idle at `LM1`; the route is isolated; a safety observer,
  emergency stop, and manual takeover are available; written movement
  authorization exists; fresh unique acceptance, permit, and task identifiers
  exist; and the frozen version, configuration, route, and `0.3 m/s` limit were
  confirmed. The identifiers remain in the controlled site record and are not
  copied into repository evidence.
- Stage 2 is limited to a fresh `read-only-preflight` session. No control
  acquisition, task write, cancellation, pause, resume, dispatch, or movement
  is authorized in this stage.
- Stage 2 could not be started in this shell because no protected runtime
  configuration source was present (`ASPNETCORE_ENVIRONMENT`, AGV host, and
  isolated Adapter database settings were absent). The repository example host
  is a placeholder and was not used. No controller connection was attempted.

## Stage 2 read-only preflight result

- A fresh isolated Release Adapter was started with the site-injected
  controller address, a temporary SQLite store, and
  `Adapter:RunMode=read-only-preflight`. The controller address and temporary
  path are intentionally omitted.
- `GET /health`: `200`, `runMode=read-only-preflight`.
- State-changing HTTP probes were fail-closed with `405`: dispatch, control
  release, field-navigation dispatch, and AGV command. No mutation request was
  sent to the controller.
- `GET /physical/preflight`: `200`, `DispatchPermitted=false`.
- Snapshot: online, control owner `none`, current station `LM1`, no active task.
- Readiness: model `W500-SZ`, controller version observed, localization status
  `1`, confidence `0.9482`, emergency clear, blocked clear, Fatal/Error `0/0`.
- Controller-authoritative map evidence: `guangzhou606`, version `1.0.6`, the
  expected MD5, five stations, and nine directed edges. The evidence matches
  the committed Profile snapshot.
- Blocking reasons: `adapter_does_not_hold_control`,
  `localization_confidence_below_threshold`, and
  `automatic_dispatch_disabled`. The confidence gate is a hard failure because
  `0.9482 < 0.95`; the physical result remains **NO-GO**.
- The newly owned Adapter process was stopped after capture. No control
  acquisition, task write, cancellation, pause, resume, dispatch, or movement
  occurred.

## Stage 3 supervised movement gate

- Stage 3 was authorized for evaluation, but its mandatory fresh read-only gate
  was rechecked before any standard-mode startup. The isolated Adapter again
  reported `read-only-preflight`; the process was stopped after capture.
- The live snapshot remained online and idle at `LM1`, with control owner
  `none`, no active task, localization status `1`, emergency/blocked clear,
  and Fatal/Error `0/0`. Model `W500-SZ`, controller version, and the
  controller-authoritative map evidence remained available and matched the
  Profile.
- Localization confidence remained `0.9482`, below the mandatory `0.95`
  threshold. `DispatchPermitted=false` with blockers
  `adapter_does_not_hold_control`, `localization_confidence_below_threshold`,
  and `automatic_dispatch_disabled`.
- Stage 3 is therefore **BLOCKED / NO-GO**. No standard-mode Adapter was
  started; no `4005`, `3066`, `3001`, `3002`, `3067`, or `9300` was sent; no
  control was acquired and the AGV did not move. A new authorized read-only
  preflight is required after the site restores confidence to at least `0.95`.
