# D160+ pressure correlation review

Date: 2026-08-19

## Evidence

- Field package:
  `res/d160-pressure-correlation-results/d160-pressure-correlation-result-20260819-094625.zip`
- Package SHA-256:
  `59429392E6AAEA4433144F4B68ECC6B98FDC438CAA3C3726DC0F873FCCC573A7`
- PCAP SHA-256:
  `7E7F5F15371894FD2ED6364EDA92001C759679C51C13BF9738157109B207EF49`
- Capture interval: `2026-08-19T01:46:25.7253984Z` through
  `2026-08-19T01:49:39.4284266Z`
- Display unit: `MPa`
- Result: five annotations, one USBPcap PCAP, no capture failure, and 75
  CRC-valid complete `0x17D4/18` process responses.

The operator subsequently clarified that the stable display reached
`9.8 MPa`; the recorded `9.7 MPa` value was entered before the pressure had
fully stabilized. The original manifest is retained unchanged.

## Correlation

| Phase | Display evidence | Correlated raw | Timing interpretation |
| --- | ---: | ---: | --- |
| Initial pump-off | `0 MPa` | `0` | Nearest response, `+0.427 s` |
| Rising | `4 MPa` | `40` | Response occurred `9.287 s` before terminal entry |
| Stable | corrected stable platform `9.8 MPa` | `98` | Raw `98` began `0.127 s` after the original `9.7` entry and remained for about 25 s |
| Falling | `6.20 MPa` | `62` | Response occurred `4.152 s` before terminal entry |
| Final pump-off | `0 MPa` | `0` | Nearest response, `+0.171 s` |

Switching from ShineLab to the terminal made the rising and falling annotation
timestamps unsuitable for nearest-time regression. The machine-generated
nearest-time fit in `d160-pressure-correlation-analysis-20260819.json` is
therefore rejected. Reviewing the complete raw sequence recovers the exact
values observed before each dynamic entry: raw `40` during rise and raw `62`
during fall.

All five field observations support the same mapping with zero value residual:

```text
pressure MPa = raw 0x17DD / 10
```

The existing 2026-08-18 captures independently show the same raw field rising
with pump activation and falling after pump shutdown. This is sufficient to
expose the normalized read-only pressure together with the original raw value.

## Limits

This evidence establishes a field-correlated display scale and unit for the
current D160+ and ShineLab version. It does not establish a normal operating
range, warning threshold, hard stop threshold, calibration accuracy, firmware
portability, or authorization to activate the pump. No production write route,
automatic pressure interlock, or workflow control may be enabled from this
mapping alone.
