# Approved: one full map validation per stable controller session

The site operator approved this scope on 2026-09-11 and requested a ten-minute
repair/retest window. Continue in the current field workspace, preserve databases
and old Unknown executions, and do not commit a verified version before a full
real workflow passes.

Cache authoritative controller map evidence in the AGV client under a single
async lock. Before reuse, read only current map name and MD5; require both to
match and status/control transport generations to remain unchanged. Missing or
failed identity reads, reconnects, changed identity, and process restart invalidate
reuse. A new full read must succeed before any subsequent admission. Keep the
original map evidence timestamp; live safety preflight gets its own observation
time. Never skip real-time safety, localization, ownership or task checks.

Unknown WPF runs must show manual reconciliation, never promise automatic
continuation. No replay, epoch relaxation, or silent old-run continuation.

Plan: implement cache and truthful warning; test stable reuse, changed identity,
reconnect/read failures and warning; build isolated binaries; deploy only while
devices idle; reconcile arrived old run with audit, then freshly authorize and
retest homing workflow. If time expires, retain a restartable checkpoint.
