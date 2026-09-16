# AGV homing workflow forward-observation fix

Approved by the site operator after the observed LM1-to-LM7 failure on 2026-09-11.

## Evidence and scope

Run 595c1fe9-2eba-44aa-8427-07dc874b1dff dispatched exactly one Move. Acceptance
072254f3-0468-4c8c-b60a-43519232f6da arrived at LM7. The running workflow became
Unknown because its ordinary observation reused the new-dispatch Ready gate.
The AGV epoch remained 4 and the readiness supervisor instance did not change.

## Selected repair

- Leave global dispatch admission unchanged.
- During forward observation only, accept a busy snapshot solely attributable
  to the linked acceptance: matching supervisor, epoch, AGV task, vendor task
  when present, and destination; current online/preflight state; no other
  blockers or reauthorization requirement.
- When arrival precedes the idle snapshot, wait for Ready before advancing or
  final control release. Never redispatch the Move.
- Restart recovery, mismatched tasks, disconnection and safety blockers retain
  conservative reconciliation. Preserve the linked acceptance in error output.
- WPF computes expiration after confirmation. Its minimum is 31 minutes, with
  the server's 30-minute minimum unchanged and an explicit submission margin.

## Verification and field retest

Test own-task motion, delayed idle after arrival, duplicate polling, wrong task,
obstacle, epoch change, reauthorization and restart without replay. Test WPF's
30-minute rejection and 31-minute acceptance. Build outside running binaries.
Retain all old run evidence. Check devices idle before any service restart.
Never resume or overwrite an Unknown result silently; use audited run controls.
Only AGV and ARM-01 are in scope; all arm programs remain 回原点.pro. No vendor-
blocked devices may execute. No verified-version commit before field success.

## Approved next phase: historical arrival and termination

The operator approved a separate WPF reconciliation action after reconnect made
the old authorization stale. ConfirmedArrivedAndCancel requires both resolve and
cancel permissions, a consumed persisted arrival with exact run/node/operation/
AGV/destination linkage and vendor task ID, and no other active or Unknown work.
It records the Move as successful but the run as Cancelled, retains evidence and
audit, cancels remaining unstarted nodes, and performs no dispatch, release or
continuation. Old epoch validity is not a prerequisite for recording history;
every subsequent physical run still needs new, current authorization.

## Field follow-up: arrival stability window

Run b5a6cda6-66b4-4196-9746-3f3db1802e2f persisted arrival at LM7 at
08:16:08.4547417Z, then entered Unknown at 08:16:08.7572909Z with
device_not_ready. Its epoch stayed 1 and the map evidence was reused unchanged.
The operator requested repair before any further physical actions.

Complete the already approved arrival-wait behavior: while the linked acceptance
is arrived, the AGV is idle at the exact destination, full preflight is valid,
the epoch/map/supervisor are unchanged, no reauthorization is required, and no
safety blockers exist, Stabilizing is a wait, not Unknown. A Ready snapshot that
arrives between the two readiness reads also waits for the next normal gate
validation. Do not shorten the five-second window or advance/release before Ready.
Restart recovery and unsafe/mismatched states retain manual reconciliation.

Regression tests first reproduced three failures (two final-Move busy/arrival
paths and one Move-to-AUBO path). Cover repeated stability polls without writes,
one-time completion after Ready, and negative safety/epoch/restart cases. Bind
the WPF warning title to actual PhysicalGateStatus, as the old static title still
promised automatic recovery for Unknown/Cancelled runs. No physical dispatch is
authorized by these offline tests; retain the Unknown field evidence.

## Offline continuation: Adapter result correlation

The operator requested continued offline repair and optimization. Complete the
existing exact-task correlation requirement at the MES arrival reconciliation
boundary as well as the forward readiness gate. No physical connections or
field startup are permitted in this phase.

Five offline counterexamples showed the old reconciler accepting an arrived
response with the wrong TaskId, AGV, target station, changed vendor task ID, or
missing vendor task ID. Validate TaskId against the acceptance ID, AGV/target
against their existing case-insensitive identifiers, and any previously bound
vendor task ID with ordinal equality. A response must include a vendor task ID;
an initially unbound acceptance may learn it only from an otherwise matching
response. Preserve the original binding when any identity check fails.

Persist a mismatched response as Unknown with one structured audit containing
expected and observed identity. Do not let subsequent ordinary polls silently
clear that identity conflict. Unconsumed permits are not pollable dispatched
work. Keep existing transient missing/read-error/paused/unknown handling for
already confirmed motion; a short read failure is not an identity conflict.

Expose an internal bounded single reconciliation pass to the test assembly while retaining the
feature switch and read-only gateway behavior. The full offline homing test now
feeds in-memory Adapter task history through the real scoped reconciliation
service instead of writing arrival state directly. It still does not emulate
controller TCP/RPC protocols or establish field success. Regress valid state
mapping, duplicate audit prevention, mismatch lock, ordinary transient recovery,
unconsumed/disabled behavior, and whole-run no-continuation/no-release on mismatch.

## Offline follow-up 2026-09-12: arrival observation ordering

The continued offline check reproduced four failures in the existing arrival
wait behavior. The supervisor can observe idle at the target before the separate
reconciler persists Arrived. Also, a full-preflight query can observe idle after
the preceding snapshot/task queries observed motion, causing the old task ID or
old task-read error to be attached to an idle snapshot.

Keep the approved wait-only behavior independent of polling order. For a
consumed, vendor-bound Accepted/Moving acceptance in the same AGV/epoch/supervisor
session, permit waiting at the exact target with no active task, valid full
preflight and no safety blockers during Stabilizing. Do not write progress,
advance, release or redispatch from this exception. A Ready transition between
gate reads also waits for a normal gate pass. Ready alone is not proof of arrival:
the persisted correlated Arrived record is still required before completion.
Unknown, changed identity/session, unconsumed/unbound permits, other stations,
safety failures and startup recovery do not gain this exception.

Project task metadata from a consistent task identity: if the later preflight
snapshot changes CurrentTaskId, discard details/read errors obtained for the
earlier ID. A new non-null task ID still blocks readiness and has no fabricated
details until a later probe reads that task. An unchanged active task retains
its details and read-error blocking. This does not change any device command,
retry, dispatch permission, safety threshold or five-second stability window.

Tests use in-memory gateways and SQLite only: both polling orders through all
four navigation/three homing steps, read-error-to-idle transition, changed and
unchanged active tasks, no completion from Ready alone, no duplicate progress
during the wait exception, and invalid binding/safety/restart counterexamples.

## Offline follow-up 2026-09-14: continuous arm completion evidence

Continue the approved offline control-chain repair without new UI features or
physical connections. A failed read, Offline response or Unknown runtime must
break the AUBO continuous Stopped observation window. Previously a sequence of
Stopped, unavailable, Stopped could count the unobserved interval toward the
terminal stability threshold and complete too early.

Reset only the stopped-since clock on those unavailable paths. Keep the existing
bounded read-only retry, overall observation timeout, program-name correlation,
controller safety/mode checks and supervisor/epoch requirements. Do not replay
load/run or change any safety threshold. A real session/epoch change remains
Unknown and requires reconciliation; the short read-recovery tests intentionally
keep the readiness session current.

Ten deterministic in-memory cases exercise healthy completion, temporary read
failure/Offline/Unknown, permanent read failure, paused-to-stopped,
paused-to-running-to-stopped, protective stop, wrong loaded program and epoch
invalidation during the terminal stability window. The
physical dispatcher and readiness store run against SQLite :memory: and an
injected gateway/clock. Assert exactly one correlated load/run, observation
counts, durable operation outcome and completion timestamp. Red tests reproduced
three premature completions; the repaired implementation passes all ten.

This is additional offline regression, not evidence that either controller has
executed the repaired full workflow. Preserve historical Unknown runs and defer
all field startup, queries and dispatch until separately authorized.
