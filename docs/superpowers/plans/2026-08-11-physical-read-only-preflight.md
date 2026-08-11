# Physical AGV read-only preflight and acceptance plan

> Status: read-only map, localization, and device-identity evidence are complete,
> the Profile is synchronized to the authorized controller snapshot, and the
> two-phase control gate is implemented. The first supervised route attempt was
> stopped without motion after a pre-dispatch `1110 NotFound` handling defect;
> the defect is repaired offline, but a new read-only preflight and separately
> authorized low-speed attempt are still required.

## Goal

Collect a current, controller-authoritative snapshot without sending a control
or movement command, then use that evidence to decide whether a later isolated
low-speed acceptance can be scheduled.

## Ordered work

1. **Offline contract and startup gate**
   - Keep `Adapter:RunMode` startup-only.
   - Keep the physical template in `read-only-preflight` with simulator,
     automatic dispatch, field navigation and cancellation disabled.
   - Preserve HTTP `405` and TCP mutation guards with negative tests.

2. **Controller map evidence source**
   - Implement only a documented vendor read-only API or vendor-approved export
     reader for map name, version, MD5, station catalog and direct directed
     edges.
   - Record source, UTC observation time and raw response/export checksum.
   - Never use a local `.smap` file as a substitute for controller evidence.
   - Completed offline with vendor APIs `1300`, `1301`, `1302`, and `4011`,
     including fake-controller and fail-closed contract coverage.

3. **On-site read-only session**
   - Isolate the area and obtain written authorization before powering on.
   - Start the Adapter in `read-only-preflight`; verify `/health`.
   - Call `/physical/preflight` and save API evidence, safety status, control
     owner, active tasks, automatic mode and timestamps.
   - Stop on any timeout, active task, alarm, block, mismatch or unavailable map
     evidence. An unknown mode is also a blocker unless the named approved
     model policy applies and API `1000.model` matches.
   - Completed on 2026-08-11 after a power cycle: `4011` map evidence,
     dedicated `1021` localization, and read-only `1000` model/version all
     passed. The panel and live protocol both showed no mode field; the profile
     records `not-exposed-by-approved-model` for `W500-SZ`, while explicit
     manual mode remains a hard blocker.

4. **Evidence review and profile update**
   - Completed: the Profile now matches the current MD5 and all nine direct
     directed edges; committed controller addresses remain placeholders.
   - Keep `enableAutomaticDispatch=false` until all gates and the acceptance
     procedure are approved.

5. **Separate supervised movement acceptance**
   - Confirm the area is isolated, the vehicle is empty, and an emergency-stop /
     manual-takeover observer is in position; record the operator authorization.
   - Switch to standard mode only after the profile/model policy and all current
     read-only gates are confirmed.
   - Perform the non-ownership preflight, acquire control, then repeat the full
     preflight before any navigation request.
   - Acquire control explicitly, use one unique task ID and one approved direct
     route at the approved low-speed, empty-load limit. Include `max_speed=0.3`
     on every `3066` segment.
   - Monitor position, task status, emergency stop, obstacle stop, alarms,
     localization and arrival. Preserve the complete request/response timeline.
   - Release control only after the operator and site owner confirm the final
     state; any anomaly leaves the result `NO-GO`.
   - First attempt recorded on 2026-08-11: control acquisition succeeded, but
     the Adapter misclassified pre-dispatch `1110 status=404` as an existing
     task and returned `unknown` before `3066`. The vehicle stayed at `LM1`,
     the operator released control, and `1060` confirmed `locked=false`.
   - Offline repair completed: NotFound permits only the first command attempt;
     every post-attempt empty/404 result is explicit `unknown`, with no resend,
     and allowlisted mutation logs preserve `3066 ret_code/err_msg` evidence.

## Out of scope

This plan does not authorize automatic batch dispatch, cancellation experiments,
unknown vendor APIs, map write-back, `.smap` replacement, or unattended motion.
