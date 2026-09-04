# 现场料盘流程取消与权限释放记录 — 2026-09-03

## 本次运行

- Workflow execution: `a6a6e311-616a-466e-9dbf-bb208d057973`
- Request: `0860fa63-b245-4c8c-8058-9f9971078fcb`
- Operator: `33206`
- Safety observer: `admin`
- Cancellation reason: 现场流程按计划停止，明日继续
- Final workflow status: `Cancelled`
- First Move (`LM1 -> LM7`): `Succeeded` / arrived
- First AUBO program (`取料盘`): `Succeeded`, runtime returned `Stopped`
- Second Move (`LM7 -> LM2`): device task was cancelled and the linked
  acceptance ended as `cancelled`

The complete machine-readable record is
`material-wpf-20260903-172710-cancel-record.json`.

## 权限与停机

- The Adapter control-release endpoint was called once after cancellation.
  The controller status channel was unavailable at that instant, so ownership
  could not be confirmed and no retry was issued.
- The last successful read before shutdown showed no active task; the reported
  owner was an external controller session (`Slience996-PC`), not this Adapter.
- The MES worker process and the Adapter process used for this run were stopped
  by exact PID. WPF was already closed.
- Local ports `5141` and `5145` have no listeners after shutdown.
- No automatic retry, new workflow, AUBO load/run, or AGV command was sent after
  cancellation.

Before tomorrow's run, perform a fresh read-only preflight and explicitly
confirm that the controller owner is `none` or the newly authorized Adapter;
do not reuse this execution ID, acceptance ID, or permit.
