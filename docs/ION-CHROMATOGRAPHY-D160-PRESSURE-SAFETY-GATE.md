# CIC-D160+ pressure safety gate

Date: 2026-08-19

## Current decision

**Read-only pressure mapping: accepted. Physical activation: NO-GO.**

The current field evidence supports the display mapping:

```text
pressure MPa = register 0x17DD raw value / 10
```

The observed stable platform of `9.8 MPa` is a correlation point, not a normal
operating limit, warning threshold, hard stop, calibration certificate, or
pressure-interlock setting.

## Evidence accepted so far

- Device: CIC-D160+, observed identifier `YA7261078`.
- Field endpoint: `COM4`, `115200 8N1`, Modbus slave `1`.
- Passive correlation package:
  `res/d160-pressure-correlation-results/d160-pressure-correlation-result-20260819-094625.zip`
- Package SHA-256:
  `59429392E6AAEA4433144F4B68ECC6B98FDC438CAA3C3726DC0F873FCCC573A7`
- The package contains 75 CRC-valid complete process responses and five
  timestamped display annotations.
- Reviewed pairs include `0/0`, `4/40`, `6.20/62`, and stable `9.8/98` MPa/raw;
  the raw value `98` remained stable for approximately 25 seconds.
- Existing captures independently show the raw pressure rising while the pump
  is enabled and returning to zero after shutdown.

The timing-shifted nearest-sample regression is rejected. The accepted mapping
comes from the complete sequence review and the operator's stable-value
correction documented in
`artifacts/ion-chromatography/d160-pressure-correlation-review-20260819.md`.

## Software policy boundary

`CicD160PlusPressureSafetyPolicy` is an offline, fail-closed decision model.

- `Enabled=false` and no configured hard stop are the defaults.
- Raw values are converted with the field-correlated `raw / 10` mapping.
- An enabled policy requires an explicit hard stop and a vendor/site evidence
  reference.
- The hard stop must map exactly to an integer raw tenth of an MPa.
- The policy does not infer a limit from `9.8 MPa` or from any historical peak.
- The policy is not registered in DI and has no serial, HTTP, MES, WPF, or
  workflow entry point.

This model may support a future preflight, but it does not authorize or perform
a pump, temperature, suppressor, injection, run, reset, or shutdown command.

## Required evidence before physical activation

Each item must be documented for the current D160+ firmware and reviewed by the
field owner and the vendor/site authority before a physical gate can be opened.

| Item | Required confirmation | Status |
| --- | --- | --- |
| Pressure range | Normal operating range and expected transient range | Pending |
| Hard stop | Authoritative hard-stop value, unit, raw encoding, and who may change it | Pending |
| Interlock | Device-side pressure protection behavior and reset semantics | Pending |
| Pump state | Complete meaning of `0x17DF` and enable/disable preconditions | Pending |
| Temperature state | Complete meaning of `0x17D4`/`0x157C`, including safe transitions | Pending |
| Faults | Complete meanings and severity for `0x183B` and `0x1841` | Pending |
| Suppressor | Individual `0x157F` bit meanings and safe empty-load state | Pending |
| Setpoints | Approved flow/temperature ranges, ordering, and dwell times | Pending |
| Fluid path | Approved liquid, column/load condition, waste routing, and leak check | Pending |
| Supervision | Named operator, local stop method, power isolation, and exclusion zone | Pending |
| Unknown write | Manual reconciliation and recovery after timeout or lost response | Pending |

An observed value or a valid Modbus echo does not satisfy any of the pending
items above.

## Future staged sequence

After every required item is evidenced and explicitly authorized, development and
field work must proceed in this order:

1. Exercise the policy and one-action preflight entirely through a fake
   transport, including mismatch, timeout, exception, and recovery states.
2. Add a separately disabled local operator-only harness on the COM4 host. It
   must perform a fresh read-only preflight, show raw and normalized pressure,
   and require per-action confirmation.
3. Validate one physical action in an empty, supervised setup. Send at most one
   write, require an exact echo and immediate readback, and verify the device UI
   and raw state after the action.
4. Stop for manual reconciliation on any timeout, mismatched echo, unexpected
   state, pressure excursion, fault, or disagreement between UI and raw data.
5. Repeat the gate separately for each action. Do not bundle pump, temperature,
   suppressor, injection, start, stop, or reset operations.
6. Only after all single-action evidence is accepted may a production adapter,
   MES task state, or workflow transition be designed for review. Unattended
   execution remains prohibited until a separate safety approval.

## Prohibited until the gate is complete

- No replay of `0x13E4=0x5AA5` outside a specifically approved session.
- No `0x157F` suppressor write, method load, autosampler movement, injection,
  run start/stop, reset, or automatic rollback.
- No production write HTTP route, MES mutation endpoint, workflow worker, or
  unattended command retry.
- No use of the `9.8 MPa` observation as a hard stop or pressure interlock.

## References

- `docs/ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md`
- `artifacts/ion-chromatography/d160-pressure-correlation-review-20260819.md`
- `artifacts/ion-chromatography/d160-pressure-correlation-analysis-20260819.json`
- `src/MesControlAgv.InstrumentGateway/CicD160PlusPressureSafetyPolicy.cs`
- `tests/MesControlAgv.InstrumentGateway.Tests/CicD160PlusPressureSafetyPolicyTests.cs`
