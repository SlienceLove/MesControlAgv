# AGV command-channel recovery and interrupted reset disposition

Date: 2026-09-14
Status: implementation details approved by the user on 2026-09-14; implementing.

## Goal and approved scope

The user approved fixing command-channel recovery, handling the unconfirmed
return-to-LM1 record, then continuing one real WPF homing test. RoboShop's device
connection has been disconnected by the user. Preserve the existing commissioning
simplifications and never automatically replay an uncertain device command.

This is not a four-device acceptance, unattended recovery, batch execution or a
general-purpose workflow reauthorization feature. All three arm nodes continue
to use `回原点.pro`. Do not modify that controller program.

## Evidence and current state

The previous WLAN-interrupted WPF run
`84a52310-c2a7-4f4f-859a-01f25474f336` has been manually closed as failed with
the original evidence retained. The subsequent authorized reset acceptance
`88a254fb-df7a-495d-877f-d10bb28be76b` attempted LM7 -> LM6 -> LM1 once at
14:22:02. The command write failed with a remote-host TCP reset. Exact-task
queries still report `dispatch_not_confirmed_by_1110`. No replay or replacement
workflow was submitted. At 14:23 AGV was online, idle at LM7 and owned by Adapter;
the arm was Stopped, Automatic and Normal. There were no new WLAN events since
14:21. These observations must be refreshed before any subsequent physical action.

`TcpAgvClient` has separate status, command, control and other channels.
`TcpApiChannel.EnsureConnectedAsync` reuses any non-null stream. A status read
can recover its own connection without refreshing an idle command connection.
This is an identified recovery gap consistent with the failure; the exact cause
of the TCP reset and earlier WLAN driver disconnect remains unproven.

Evidence directory: `artifacts/physical-acceptance/homing-simplified-20260914-0950/`.

## Approaches considered

1. **Recommended: fresh command connection before each new command request.**
   Close an idle command stream and connect before the first write, within the
   existing channel lock. Low implementation complexity; adds a TCP connection
   setup for each navigation/pause/resume/cancel request. These commands are
   infrequent in the single-run commissioning flow.
2. **Reconnect only when another channel's generation changes.** Fewer TCP
   connections, but requires cross-channel state coordination and does not by
   itself cover a command-only idle disconnect. Unnecessary for this deadline.
3. **Restart Adapter manually after each network loss.** Temporary workaround
   only; changes readiness sessions and leaves the recovery defect unresolved.

## Design A: command-channel lifetime

- Add a narrowly scoped fresh-connection-before-write behavior to the existing
  TCP channel, enabled only for `_commandChannel`.
- Under its existing semaphore, reset the previous command connection before
  beginning a new mutating request, then connect and write once.
- Do not reset the control-ownership channel, status channel, map cache or
  readiness store. Do not add repeated map downloads or whole-Ready checks while
  observing an already dispatched task.
- Navigation, pause, resume and cancel inherit the command-channel behavior.
  Existing task-identity, ownership, emergency and protection-stop boundaries
  remain unchanged.
- Connection establishment failure before the write callback does not claim
  that a navigation command was sent. Once a write may have started, any error
  remains uncertain and cannot trigger another mutation attempt.
- Existing read-only retries remain read-only. Preserve same-task idempotency
  and distinguish command-connection setup from command replay in tests/audit.

## Design B: explicit closure of this kind of standalone reset

Add a small, audited manual-disposition operation for a standalone field
navigation acceptance in Unknown. This is bookkeeping after explicit operator
approval, not a device cancellation acknowledgement or a successful arrival.

- Use a request ID, actor and reason through the existing MES admin permission
  model. The MES service coordinates with Adapter; do not perform direct SQL
  edits or use a new database to bypass history.
- Restrict the operation to an exactly matched standalone acceptance and its
  Adapter task. Workflow-linked unknown nodes keep their existing resolution
  mechanism; this change must not resume them or replace their authorization.
- Before closure, query fresh controller evidence: AGV online, no active task,
  currently at the recorded source station, and every exact route segment
  explicitly reported absent by vendor query 1110. Generic Unknown, an empty or
  malformed response, a read failure, or a mismatched task is not absence proof.
- If any segment exists, is moving, has completed, or cannot be read, leave the
  record unchanged and report the evidence for operator reconciliation.
- Persist a distinct terminal **manually closed** outcome with the reason and
  evidence in both layers. It must never be reported as a device-confirmed
  cancellation, failed controller task, or successful arrival. Preserve prior
  dispatch errors, route, IDs and audits.
- Serialize against dispatch and task refresh. Make repeated disposition
  requests idempotent and recover partial MES/Adapter acknowledgement by
  querying the same task/disposition, never by sending another device command.
- The old task ID and consumed permit remain non-reusable across restarts.
  Polling must not overwrite the manual terminal record or reactivate it.
  Release only this task's scheduler reservation after persisted closure.
- Closure sends no navigation, cancellation, control acquisition or release
  commands. A separately authorized new return can be created only after closure.
- No new WPF editing surface is required for this narrow reset recovery. The
  normal complete test must still be started and monitored through actual WPF.

## Verification

### Offline

- Reproduce a successful command followed by a stale/closed idle connection;
  the next distinct command connects freshly and is observed exactly once.
- Demonstrate that command-channel renewal does not reconnect ownership/status
  channels or fetch the map again.
- Drop a mutation acknowledgement after the server receives the request:
  assert no replay, including a repeat of the same task ID.
- Fail connection establishment: assert no navigation-write evidence is set.
- Cover manual disposition success with exact all-segment absence, actor/reason
  validation, wrong task/route, workflow-linked rejection, active task, source
  mismatch, completed segment, incomplete evidence and transient read failure.
- Cover duplicate and conflicting request IDs, refresh/disposition races,
  service restart, partial response loss and no resurrection of manually closed
  records. Assert zero physical mutation calls during disposition.
- Run the relevant Adapter and MES tests, then the existing commissioning
  regressions. Preserve unrelated dirty-worktree changes.

### Supervised field comparison

After code review and passing offline tests, deploy using the same databases.
Read current status and exact reset-task evidence first. The user's approval
covers this reset disposition and one new complete comparison; do not fabricate
normal completion if the closure preconditions are not met.

Once the old reset is properly closed, authorize one new LM7 -> LM1 return and
confirm its exact arrival. Then submit one new WPF run with current authorization:
LM1 -> LM7 -> homing -> LM2 -> homing -> LM7 -> homing -> LM1 -> release.
Monitor node receipts and WLAN events without adding repeated full-map polling.
Stop progression on uncertain outcomes without replay. Record what actually
completed. A passing comparison alone cannot prove RoboShop caused the earlier
wireless loss.

## Delivery and exclusions

No driver upgrades, Wi-Fi settings changes, broad refactoring, automatic
Unknown-to-success conversion, epoch edits or program resends. Do not terminate
other user applications. Keep the current services untouched during design.
Honor the user's existing condition: do not designate or commit a validated
version before a complete, bug-free field verification. Therefore this proposed
design document is not committed separately in advance.

## Work checklist

- [x] Explore current code, tests, recent commits and field evidence.
- [x] Record the user's goal, limits and permission to fix the issue.
- [x] Consider alternatives and present the smallest recommended approach.
- [x] Write proposed design and perform inline scope/consistency review.
- [x] User review of these implementation details.
- [x] Implementation plan after approval (writing-plans skill is not available;
      use a concise explicit plan as fallback if it remains unavailable).
- [x] Preserve the existing dirty field-workspace; isolate build outputs, do not
      omit uncommitted field fixes by creating a clean-HEAD worktree.
- [ ] Implement, test, review, deploy and conduct the authorized field test.

No visual companion is needed: this is a protocol-lifetime and audit change,
not a layout or other visual decision.
