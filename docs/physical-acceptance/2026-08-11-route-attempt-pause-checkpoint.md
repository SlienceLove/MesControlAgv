# Supervised route attempt pause checkpoint

## Safety state at pause

- The temporary standard-mode and post-route read-only Adapters are stopped.
- No further controller connection, control acquisition, task write,
  cancellation, pause, or resume was made during offline diagnosis.
- The operator released control in the robot test software.
- The last read-only `1060` result was `{"locked":false,"ret_code":0}`.
- The last verified vehicle state was stopped at `LM1`, with no active task.
- Do not perform another live connection from this checkpoint without a fresh
  site check and authorization.

## Field attempt evidence

- Acceptance ID: `9fea739a-6f1e-402d-b8c2-fd70f4977c5e`.
- Approved route: `LM1 -> LM2`.
- Configured limit: `0.3 m/s`.
- Automatic batch dispatch and Push remained disabled.
- Both preflight phases passed and Adapter control acquisition succeeded.
- The vehicle did not move. A task-specific `1110` query returned
  `404 (NotFound)` and the global task list was empty.
- No cancellation and no second dispatch were sent.
- The PowerShell HTTP client raised a local `NullReferenceException` while
  reading the response, but this is not controller-response evidence.

The old Adapter log did not retain raw mutation request/response data. Do not
state that a particular `3066 ret_code` was observed in this session.

## Offline root cause

The controller returns a non-empty `1110.task_status_list` item with
`status=404` for a requested task ID that does not exist. `TcpAgvClient`
previously treated any non-empty list as an idempotency hit, returned
`unknown`, and exited before the `3066` write. The temporary SQLite sequence,
`dispatching -> unknown` with no device error, is consistent with this path.

The vendor PDF separately confirms:

- `3066` uses a `move_task_list` wrapper.
- Each item requires `task_id`, `source_id`, and `id`.
- The referenced `3051.max_speed` item field is optional and uses `m/s`.
- Control release is no-payload API `4006` on port `19207` and can release only
  control owned by the caller.

Therefore `max_speed=0.3` was not the cause of this pre-command exit.

## Implemented but not yet fully verified

- Pre-dispatch all-`404` now means absent and permits one first `3066` attempt.
- A navigation attempt is registered before the command-channel request.
- Post-attempt empty/404 status returns `unknown` with
  `dispatch_not_confirmed_by_1110`.
- A repeated call with the same task ID reconciles only and cannot resend
  `3066`.
- Persisted `dispatching`, `accepted`, `moving`, or `paused` Adapter state
  becomes explicit `unknown` if the device task disappears.
- Mutation audit logs use an allowlist. `3066` keeps segment task/station IDs,
  configured speed, and raw response `ret_code`, `err_msg`, and `create_on`;
  the controller host and unfiltered payload are excluded.

Files changed for this repair:

- `src/MesControlAgv.Adapter/Services/TcpAgvClient.cs`
- `src/MesControlAgv.Adapter/Services/AdapterService.cs`
- `tests/MesControlAgv.Adapter.Tests/TcpAgvClientTests.cs`
- `tests/MesControlAgv.Adapter.Tests/AdapterServiceTests.cs`
- `docs/AGV-TCP-ADAPTER.md`
- `docs/physical-acceptance/FIELD-NAVIGATION-ACCEPTANCE.md`
- `docs/PROGRESS.md`

## Verification completed

- `TcpAgvClientTests`: `20/20` Debug.
- All Adapter tests: `96/96` Debug.
- `VendorTcpTransportAcceptanceTests`: `2/2` Debug.

These runs were offline and used loopback fake controllers only.

## Remaining work for the next session

1. Review the repair diff and run `git diff --check`.
2. Update `README.md`, `docs/physical-acceptance/README.md`, and
   `FIELD-ACCEPTANCE-RECORD.md` with the attempt, diagnosis, manual release,
   and audit boundary. Verify all docs avoid the real controller address.
3. Run the complete Debug solution. The previous baseline was `352/352`; two
   new Adapter tests have been added.
4. Run an isolated Release Adapter test suite with no site host in environment
   or configuration.
5. Recheck branch/worktree changes and preserve unrelated user edits.
6. Before any second physical attempt, obtain a fresh site confirmation, start
   a new read-only-preflight process, verify control owner `none`, map/model,
   localization, alarms, idle state, and stop the read-only process.
7. Obtain renewed explicit movement authorization and use a new unique
   acceptance/task ID. Confirm the new mutation audit captures the real
   `3066` request and response before judging controller acceptance.
8. Only after the physical-acceptance stage is complete, commit and push the
   existing `feat/wpf-map-export` branch as previously requested.

No commit or push was made at this checkpoint.
