# 2026-08-06 Physical Acceptance Pause Checkpoint

## Current status

Physical AGV acceptance is paused. The vehicle has been powered off. No further
connection, control, task dispatch, cancellation, or motion is authorized until
a future on-site acceptance window is explicitly approved.

All controller observations below are historical only. They must not be used as
current readiness evidence after the power-off event.

## Last supplied controller snapshot before power-off

- Vehicle ID: `SWW61G2003`
- Controller map: `guangzhou606`
- Controller map MD5: `816e68b9a367d9c8d5eaee9331a7ef58`
- Reported current station and target: `LM1`
- Reported pose: `x=0.0058`, `y=0.801`, `yaw=-3.0823084011511623`
- Localization confidence: `0.9827`
- Reported stopped state: `is_stop=true`, `vx=0`, `vy=0`, `w=0`
- Reported safety state: `emergency=false`, `blocked=false`, `errors=[]`,
  `fatals=[]`, `reloc_status=1`
- Reported task fields: `task_id=""`, `path=[]`, `unfinished_path=[]`
- Recorded raw controller fields: `manualBlock=true`, `dispatch_mode=0`, and
  `src_release=false`

The automatic-navigation meaning of `dispatch_mode` and `src_release` is not
vendor-confirmed. `manualBlock=true` is a blocking condition for the current
Adapter safety gate. No reliable, machine-readable whole-vehicle automatic-mode
signal was available in the supplied snapshot.

The controller snapshot did not provide a map version, station catalog, or
directed-edge list. The controller map MD5 differs from the historical Profile
snapshot MD5 `e1b8d6b2b24362c1d44f1884c0abd8fb`; therefore the existing physical
acceptance template is stale and must not be used for physical dispatch.

## Dispatch decision

**NO-GO.** MES must not create or dispatch a physical navigation task from this
checkpoint. Keep `enableAutomaticDispatch=false`; do not treat a historical
`LM1` position or the new MD5 alone as an accepted routing configuration.

No physical motion command was issued from this checkpoint. No `4005`, `3066`,
`3067`, or `3068` command was sent as part of the pause work.

## Required before the next authorized on-site session

1. Confirm that the vehicle is powered, the work area is isolated, an emergency
   stop/manual takeover path is available, and the approved scope permits the
   intended step.
2. Run a fresh read-only preflight. Re-read controller ownership, safety state,
   localization, active tasks, map name, map version, map MD5, station catalog,
   and directed edges.
3. Obtain vendor-confirmed semantics for `manualBlock`, `dispatch_mode`,
   `src_release`, SRC control, and the authoritative automatic-navigation mode
   signal.
4. Update the physical Profile only from the fresh authoritative map export;
   validate it offline before any motion is considered.
5. Obtain verifiable low-speed-limit evidence and complete the field acceptance
   record before a single, isolated low-speed navigation test.

## Local software handoff

The physical-acceptance API and MES/WPF integration work remains incomplete and
unverified after the latest local code changes. It must be reviewed and pass
targeted plus full Release build/test checks before a future physical session.

This checkpoint records documentation only. No commit, push, reset, clean, or
physical-device operation was performed.
