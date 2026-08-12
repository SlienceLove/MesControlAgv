# Physical acceptance stage 0 evidence

Date: 2026-08-12
Status: READY FOR SITE GATES / NO-GO FOR DEVICE CONNECTION

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
- Stage 1 is blocked until the vehicle is powered, the work area is isolated,
  the safety team is present, and written movement authorization is recorded.
