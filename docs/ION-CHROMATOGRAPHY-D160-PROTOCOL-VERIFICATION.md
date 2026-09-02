# CIC-D160+ protocol verification

Date: 2026-08-19

## Source evidence

- Vendor workbook: `res/D160+ general protocol.xlsx` (local evidence; the
  actual filename is Chinese and the `res` directory is intentionally ignored)
- SHA-256:
  `0C86E1B869E6BF21E9DAFAC13C8ACFDA5D7247A2CE9054E40094A4373F8B428F`
- Previous passive captures:
  - `d160-capture-result-20260817-154030.zip`
  - `d160-capture-result-20260818-092523.zip`
  - `d160-capture-result-20260818-100624.zip`

The workbook is a shared D160/D120 family register table and contains entries
for several other models. A row is treated as applicable to the current D160+
only when the workbook identifies it as common/D160+ or live D160+ captures
correlate the address and value. The workbook alone does not establish safe
ranges, firmware compatibility, or an authorized write sequence.

## Confirmed read mapping

| Address | Registers | Meaning | Evidence |
| --- | ---: | --- | --- |
| `0x1770` | 2 | Conductivity, float32 using the observed byte order | Workbook and live UI/capture |
| `0x1776` | 2 | Total conductivity, float32 using the observed byte order | Workbook and live UI/capture |
| `0x17D4` | 1 | Temperature-control state, raw `BIT1 BIT0` | Workbook |
| `0x17D7` | 1 | Column-temperature setpoint | Workbook and capture |
| `0x17D8` | 1 | Actual column temperature | Workbook and live UI/capture |
| `0x17DB` | 1 | Pump-flow setpoint | Workbook and controlled capture |
| `0x17DC` | 1 | Actual pump flow | Workbook and controlled capture |
| `0x17DD` | 1 | Current pressure, `raw / 10 MPa` | Workbook and field correlation |
| `0x17DE` | 1 | Pump mode, enum unresolved | Workbook |
| `0x17DF` | 1 | Pump enabled `BIT0`, protection `BIT1` | Workbook |
| `0x1838` | 1 | Suppressor 1 current setpoint, scaling unresolved | Workbook |
| `0x1839` | 1 | Suppressor 1 actual current, scaling unresolved | Workbook |
| `0x183A` | 1 | Suppressor/eluent current-state raw bits | Workbook |
| `0x183B` | 1 | Fault code 1; only some bits are documented | Workbook |
| `0x1841` | 1 | Fault code 2; `BIT0` is second leak input | Workbook |
| `0x1900` | 24 | Suppressor identity and counters | Workbook and passive capture |
| `0x1918` | 24 | Column identity and counters | Workbook and passive capture |
| `0x1930` | 5 | Eluent serial number and remaining amount | Workbook |

The existing `0x1900/20` read remains valid because it was independently
accepted by the device. The full vendor-defined identity read is `0x1900/24`.

## Confirmed captured writes

These mappings are documented and capture-correlated, but they remain offline
and are not connected to a serial transport, dependency injection, HTTP, or a
workflow executor.

| Address/value | Meaning | Remaining gate |
| --- | --- | --- |
| `0x1389=3500` | Column-temperature setpoint `35.00 C` | Safe range and write/readback policy |
| `0x13DA=700` | Pump-flow setpoint `0.700 mL/min` | Safe range, pressure interlock, and recovery |
| `0x157C=2/0` | Enable/disable observed column-temperature control | Exact bit ownership and state precondition |
| `0x157D=1/0` | Enable/disable pump | Pressure/flow precondition and confirmed idle state |
| `0x13E4=0x5AA5` | Password required by the workbook to query conductivity | Session timing, repetition, and firmware scope |
| `0x157F=0` | Disable suppressor/eluent current bits | Per-bit definition and empty-load authorization |

## Inconsistencies and prohibited assumptions

- The workbook does not specify a safe pressure range or hard stop threshold;
  the field correlation establishes display scaling only.
- It documents only a subset of fault-code bits.
- The `0x157F` bit field is not expanded into independent business meanings.
- The write-side suppressor mode at `0x1400` says `1=CC, 0=CV`, while the
  read-side mode at `0x184F` says `0=CC, 1=CV`. No driver may normalize this
  field until the vendor resolves the contradiction or a controlled readback
  proves the device behavior.
- Device/hardware/software version rows use decimal addresses `4000-4002` but
  omit hexadecimal addresses. Do not add `0x0FA0-0x0FA2` to the production
  allowlist until a live read confirms that interpretation.

## Current implementation decision

- `Flow` now reads actual flow from `0x17DC`.
- `FlowSetpoint` separately reads `0x17DB`.
- `ColumnTemperatureSetpoint` reads `0x17D7`, while
  `ColumnTemperature` continues to represent the actual value at `0x17D8`.
- The read-only driver uses the complete `0x1900/24` identity range and records
  raw evidence for `0x1838/10`.
- The normalized status preserves the raw temperature-control, pump, pressure,
  suppressor/eluent, and two fault-code values. Pressure and fault meanings are
  not inferred from those raw values. Normalized pressure is separately exposed
  as `PressureRaw / 10` MPa from the field-correlated mapping.
- Mapping confidence is now `VendorDocumentAndFieldValidated` for decoded
  fields supported by both sources.
- All state-changing routes and serial writes remain disabled.

## Field read gate

Run `Test-D160ProtocolReads.ps1` on the control computer with ShineLab closed.
It performs three rounds of these exact function `0x04` reads:

1. `0x1900/24`
2. `0x1770/12`
3. `0x17D4/18`
4. `0x1838/10`

The script validates slave address, function, byte count, frame length, and CRC
and writes progress to one JSON file after every query. No function `0x06`
frame exists in the script.

## Field result: 2026-08-18 15:19 CST

- Result: `res/d160-protocol-read-validation.json`
- Result SHA-256:
  `09C122E7A41125F4D7219C22A300A6092225F221C8640411A41E63080E9D12E8`
- Endpoint: `COM4`, `115200 8N1`, slave `1`
- Outcome: `12/12` successful function `0x04` reads; all request and response
  CRCs, functions, byte counts, and lengths passed an independent recheck.
- Stability: each of the four query ranges returned one identical response
  across all three rounds.
- Latency: identity `106-125 ms`, detector `13-17 ms`, process `19-20 ms`,
  suppressor `7-8 ms`.

The validated snapshot contained:

| Field | Value |
| --- | ---: |
| Observed identifier | `YA7261078` |
| Conductivity | `45.4674225` |
| Total conductivity | `45.4674225` |
| Column-temperature setpoint | `35.00 C` |
| Actual column temperature | `30.01 C` |
| Flow setpoint | `0.700 mL/min` |
| Actual flow | `0.700 mL/min` |
| Pressure | raw `0` |
| Temperature-control state | raw `0` |
| Pump state | raw `0` |
| Suppressor/eluent state | raw `0` |
| Fault code 1 / 2 | raw `0` / `0` |

The read-only script did not send `0x13E4=0x5AA5`, yet the detector range
returned a stable nonzero conductivity value in this session. This proves the
current session can be read without the script sending the password; it does
not prove whether a cold boot, firmware change, or prior ShineLab session can
alter that requirement. The gateway therefore remains read-only and does not
send the password automatically.

Register `0x17DE` returned raw `3500`, although the workbook labels it as pump
mode. That value is not credible as a simple mode enum and may indicate a
firmware-specific layout or stale workbook mapping. It remains uninterpreted.

## Field read gate result: 2026-08-28 13:16 CST

- Result: `artifacts/ion-chromatography/d160-protocol-read-validation-20260828.json`
- Result SHA-256:
  `B2F883A4C2C14637FCF890FFC776274CECACE55BB3556CF363E9287F55C11F44`
- Endpoint: `COM4`, `115200 8N1`, slave `1`
- Outcome: `12/12` successful function `0x04` reads; an independent recheck of
  every request/response found no CRC, length, address, or function errors.
- Stability: all four query ranges returned byte-identical responses in all
  three rounds; no `0x06` or `0x10` frame was sent.
- Observed identifier: `YA7261078` (same as the prior validated session).
- Read-only snapshot: conductivity `359.155151`, column setpoint `35.00 C`,
  actual column temperature `29.68 C`, flow setpoint/actual `0.100/0.100 mL/min`,
  pressure raw `0`, pump state raw `0`, and fault-code raw values `0/0`.

The snapshot confirms that the COM4 read path remains repeatable on the current
control computer. It is not an activation or safety-limit approval; the raw
pump-mode and suppressor fields remain subject to the existing interpretation
limits. The complete field JSON is retained without modification.

## 2026-08-28 ShineDataAcquisition lifecycle capture (not a read-only run)

During a supervised USBPcap session the operator clicked Run once, Pause twice,
Resume once, and Terminate once on the window `ShineDataAcquisition -
D160+-test111`. The capture showed ten D160+ `0x06` writes with exact echoes,
including a transition of the process-state raw value at `0x17DF` from `0` to
`1`; the final captured process response still had pressure raw `9` and
process-state raw `1`. No matching disable write was captured. These buttons
must not be assumed to be a hardware pump stop; the site must confirm the
instrument's physical safe state using its approved stop/emergency procedure.

The same capture's SHA-18i endpoint contained only periodic `0x04` reads, so it
does not add SHA-18i action evidence. Detailed frame lists and the original ZIP
are retained in `artifacts/ion-chromatography/
SHA18I-CAPTURE-ANALYSIS-20260828-132249.md` and
`SHA18iA-capture-20260828-132249.zip`.

## Next gate: current-value rewrite only

Prepared package:
`artifacts/ion-chromatography/d160-noop-write-validation.zip`
(SHA-256
`106071B93FC45F1BB1FF1992952CF9EAD683F9BD79012A50F10D59E17C916017`).

The next package is deliberately separate from the read-only package. It may
write only these two exact values already present in the validated snapshot:

| Register | Required preflight value | Fixed write |
| --- | ---: | ---: |
| Pump-flow setpoint `0x13DA` | `700` | `700` |
| Column-temperature setpoint `0x1389` | `3500` | `3500` |

Before opening the write sequence, `Test-D160NoOpWrites.ps1` requires:

- exact instrument identifier `YA7261078`;
- raw temperature-control, pump, suppressor/eluent, pressure, and both fault
  states equal to zero;
- the two setpoints already equal to the fixed values above;
- ShineLab closed, COM4 selected, an operator ID, and the exact authorization
  phrase `I-AUTHORIZE-D160-NOOP-WRITES`.

Each write is sent once with no automatic retry. It must receive an exact
function `0x06` echo, then a function `0x04` readback must show the same
setpoint and no activation or pressure. The package contains no pump enable,
temperature enable, password, `0x157F`, start, stop, or automatic rollback
write. This gate validates the direct write transport and readback discipline;
it does not authorize later physical activation.

## Current-value rewrite result: 2026-08-18 15:48 CST

- Result: `res/d160-noop-write-validation.json`
- Result SHA-256:
  `29C85FD9E2F6F8F44FC99C715EE309597621BF0C74C11C915E85190855B4EAFA`
- Operator: `operator-20260818`
- Outcome: completed successfully with exactly two write records and no
  retries.
- `0x13DA=700` received exact echo `010613DA02BCAC64`.
- `0x1389=3500` received exact echo `010613890DAC5849`.
- Every request and response passed an independent CRC, slave, and function
  recheck.
- The process response was byte-identical before both writes and after both
  readbacks. Flow remained `700/700`, column setpoint remained `3500`, raw
  temperature-control/pump/pressure remained `0/0/0`, and no activation was
  observed.
- The final suppressor/eluent state and both fault codes remained raw zero.

This establishes the direct function `0x06` request/echo path and the
function `0x04` write/readback discipline for the two tested setpoint
registers. It does not establish safe physical enable behavior, pressure
limits, automatic retry, or recovery after an ambiguous write timeout.

No additional no-op disable replay is required. Pump-disable and
temperature-disable encodings already have passive ShineLab capture evidence,
while another physical write would add little evidence. The next development
stage is an unregistered, disabled-by-default controlled write session with
raw-state preflight and audit. Physical enable remains blocked until pressure
scaling, safe limits, fluid-path setup, supervision, and emergency recovery
are explicitly defined.

## Controlled current-value session implementation

The local code now contains a controlled session for the two field-validated
current-value rewrites. It is a development boundary, not a production write
entry point:

- its exact-value policy defaults disabled;
- it accepts only `SetPumpFlow` and `SetColumnTemperature`, and only when the
  requested raw value is already the current raw setpoint;
- it requires identifier `YA7261078` and raw temperature-control, pump,
  pressure, suppressor/eluent, and both fault values to be zero before writing;
- it sends one frame through a transport method whose contract permits one
  attempt only, requires a byte-exact echo, then repeats identity, process,
  suppressor, and setpoint validation;
- timeout, cancellation during the write exchange, or I/O loss is reported as
  an outcome-unknown exception with an explicit no-automatic-retry rule;
- its success result retains preflight/readback states and all read/write frame
  evidence for audit.

There is deliberately no concrete controlled serial transport, dependency
injection registration, HTTP route, workflow worker, or WPF write control.
The running gateway remains GET/HEAD-only and uses only
`SerialReadOnlyModbusTransport`.

Offline verification passed with InstrumentGateway `50/50` tests, full
solution `536 passed / 5 skipped / 0 failed`, and a Release build with
`0 warnings / 0 errors`. No local test opened `COM4` or sent a physical frame.

## Next physical-control gate

Do not implement or run pump/temperature activation yet. Before preparing a
new field package, obtain and review:

1. pressure normal range, hard stop limit, and the authoritative pressure
   interlock; display scaling is field-correlated but does not supply limits;
2. complete meanings for temperature state `0x17D4`, pump state `0x17DF`, and
   fault codes `0x183B`/`0x1841` on this firmware;
3. vendor-approved flow and column-temperature ranges plus ordering and dwell
   requirements;
4. the required fluid-path/load condition, local supervision, manual stop,
   power isolation, and recovery procedure after an unknown write outcome;
5. explicit per-action authorization for a single supervised activation and
   shutdown sequence.

Only after these inputs are recorded should the next package add a separately
disabled serial implementation and a local operator-only command surface. It
must first perform a fresh read-only preflight, execute one action at a time,
verify physical/UI state and raw readback after each action, never retry an
ambiguous write, and stop for manual reconciliation on any mismatch.
Password `0x13E4`, suppressor/eluent write `0x157F`, method loading, injection,
run start/stop, reset, MES task admission, and unattended execution remain out
of scope.

## Empirical pressure-correlation result

The pressure scaling at `0x17DD` could not be obtained from the vendor. It was
therefore established by correlating the raw register with the pressure value
already displayed by ShineLab. This evidence does not authorize new pump
operations.

Offline replay of the three existing USBPcap sessions found `15`, `21`, and
`22` CRC-valid complete `0x17D4/18` responses. The first session stayed at raw
pressure zero. In the two controlled-operation captures, raw pressure rose
from `0` to `99` while pump state was `1`, and later fell from `89` to `0`
after pump state returned to `0`. This independently supports `0x17DD` as a
dynamic pressure signal, but those sessions have no timestamped ShineLab
display values and cannot prove a scale or unit.

Passive-correlation package used:
`artifacts/ion-chromatography/d160-pressure-correlation-kit.zip`
(SHA-256
`4E3271FF727916319EE8FDDC74967FF9AC85A588DFD691447ADC65F08D85ED2C`).
It contains only a USBPcap capture script, an elevated launcher, and a README;
it does not include or require another protocol-tester deployment. USBPcap
must already be installed from the previous capture stage.

The script leaves ShineLab running, never opens `COM4`, never sends Modbus
data, and collects five timestamped operator entries containing the displayed
pressure, displayed unit, and optional flow/state note. Use it only during an
already approved and supervised ShineLab operation where pressure naturally
reaches stable points. Do not start a pump, change flow, or alter a method only
to create correlation samples. Return only the generated
`d160-pressure-correlation-result-*.zip` file.

Field result:
`res/d160-pressure-correlation-results/d160-pressure-correlation-result-20260819-094625.zip`
(SHA-256
`59429392E6AAEA4433144F4B68ECC6B98FDC438CAA3C3726DC0F873FCCC573A7`).
The package contains five annotations, one 2,994,235-byte PCAP, no capture
failure, and 75 CRC-valid complete process responses.

The terminal timestamps for rising and falling samples lagged the display
observations because the operator had to switch windows. Nearest-time linear
regression is therefore rejected. Reviewing the complete response sequence
correlated display/raw pairs `0/0`, `4/40`, `6.20/62`, and final `0/0`. The
recorded stable entry was `9.7 MPa` at raw `98`; the operator clarified that
the display subsequently stabilized at `9.8 MPa`, while raw `98` remained
stable for about 25 seconds. All reviewed points support exactly:

```text
pressure MPa = raw 0x17DD / 10
```

The original annotations remain unchanged. The manual timing review and limits
are recorded in
`artifacts/ion-chromatography/d160-pressure-correlation-review-20260819.md`;
the machine extraction is retained in
`artifacts/ion-chromatography/d160-pressure-correlation-analysis-20260819.json`.
The mapping is now enabled for normalized read-only pressure while preserving
`PressureRaw`. It does not define a safe range, warning threshold, calibration
accuracy, or automatic pressure interlock, and it does not authorize physical
activation.

## Pressure safety policy model

The code now contains `CicD160PlusPressureSafetyPolicy` as a fail-closed model
for a future activation gate. It is intentionally not registered with
dependency injection, the serial transport, an HTTP route, MES, WPF, or a
workflow worker.

- The default policy is disabled and unconfigured. It rejects activation at
  every pressure, including raw zero.
- An enabled policy must carry a vendor/site evidence reference and an explicit
  hard-stop value that maps exactly to a raw tenth of an MPa.
- The observed `9.8 MPa` platform is never copied into the hard-stop value
  automatically. It is field evidence for display scaling only.
- Decisions retain both `PressureRaw` and normalized MPa and distinguish an
  unconfigured policy from a configured limit exceedance.

The implementation is a preparation boundary only. A physical action remains
blocked until the evidence and authorization checklist in
`docs/ION-CHROMATOGRAPHY-D160-PRESSURE-SAFETY-GATE.md` is complete.
