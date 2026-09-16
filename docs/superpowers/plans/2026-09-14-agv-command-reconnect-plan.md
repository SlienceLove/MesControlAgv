# Approved AGV recovery implementation plan

Spec: ../specs/2026-09-14-agv-command-reconnect-design.md

Work in the current workspace, preserving previous field fixes and unrelated
changes. Use isolated build outputs; do not modify deployed processes/databases
until tests and review pass. No validation commit before field success.

1. [ ] Baseline: targeted TCP, Adapter and field-navigation MES tests; record
   pre-existing relevant diff for scoped review.
2. [ ] Command connection: add failing stale-connection / no-replay tests;
   rebuild only the command channel before each new write under its lock.
3. [ ] Adapter disposition: exact all-segment absence evidence; persist audited
   manually-closed state, serialize against dispatch/read, preserve old IDs.
4. [ ] MES disposition: existing admin permission, exact standalone linkage,
   idempotent coordination with Adapter; preserve errors and terminal closure.
5. [ ] Regression: stale TCP, no mutation replay, no side-channel reset;
   closure negative cases, restart, concurrency and lost-ack reconciliation.
6. [ ] Scoped independent code review, fix findings, run commissioning suites.
7. [ ] Deploy using original data; close only the authorized uncertain return
   if fresh evidence meets the approved conditions. Never substitute an empty DB.
8. [ ] Return once to LM1, execute one full WPF homing flow and record receipts
   plus WLAN events. Report truthfully; commit only if the user's success condition
   is met and unrelated edits can be excluded safely.

No new feature-design or workspace-creation approval is required for this plan.
If field evidence fails the agreed closure conditions, stop new motion and report
the concrete blocker; do not change those conditions merely to complete the test.

## Execution update — 2026-09-14 15:03 +08:00

Steps 1–7 completed. Scoped review found an opaque active-task-ID omission in
the manual-closure idle check; reproduced with failing tests and fixed. The
related Adapter suite passed 198/198, MES 156/156; existing-database schema
upgrade (including repeated initialization) is covered. Release Adapter/MES
builds completed with zero warnings/errors.

Deployed from `artifacts/command-recovery-build` using the original databases,
backed up while both service writers were stopped. Adapter PID 41248, MES 47356;
existing WPF PID 40532 retained. No other user application was stopped.

Unconfirmed return 88a254fb-df7a-495d-877f-d10bb28be76b was manually closed at
15:00:37 through MES, admin request 58cbd997-5c7b-4966-acb9-3dfaa3c1ab22. Both
exact route segments returned 404; fresh unfiltered state was idle at LM7.
The separately authorized return 990cb01a-d121-45c9-916d-86bdd247d06b dispatched
once at 15:01:07 and subsequently arrived at LM1 without a write error.

New full run 39fca078-edfc-4a0f-85c5-44348dcb69ab was accepted from actual WPF
at 15:02:28 (request 543c8a15-4796-458f-8d69-6f01a3fd4eea). First LM7 arrival
completed at 15:03:09; first arm homing completed at 15:03:19; LM2 move started.
Step 8 remains in progress. No validation commit yet.

Evidence: `artifacts/physical-acceptance/command-recovery-20260914-1500/`.

## Closeout update — 2026-09-14 16:49 +08:00

Step 8 did not complete: LM7, first arm homing and LM2 succeeded (3/7 nodes).
Run 39fca078-edfc-4a0f-85c5-44348dcb69ab was paused at 15:07:07 for network
diagnosis; the second arm command was not sent. AGV was subsequently powered
off by the user for charging. The run remains Paused, not passed or closed.

The AGV-targeting AP Ping watchdog is strongly supported as the source of the
periodic WLAN loss. User disabled it; actual observation lasted 27m34s with
zero WLAN disconnects/reconnects, then the user ended monitoring. This is not
a completed 35-minute observation or a successful full physical workflow.

User requested evidence archival and a next-session handoff; physical workflow
reverification is deferred. Start with `NEXT-SESSION.md` at repository root.
Do not rerun historical deployment/return scripts or reuse old motion authority.
No field-validation-version commit has been made.
