# Real AGV TCP Adapter

This project keeps the Simulator as the default driver. The vendor TCP driver is selected with `Agv:Driver = tcp` in the Adapter configuration.

## Vendor protocol mapping

The implementation follows `MES-WMS 对接 AGV 机器人技术指南.docx` and `机器人API2023(5).pdf`:

| Capability | Port | API |
|---|---:|---:|
| Control owner query/acquire | 19204 / 19207 | 1060 / 4005 |
| Fixed route navigation | 19206 | 3066 |
| Task status reconciliation | 19204 | 1110 |
| Pause/resume | 19206 | 3001 / 3002 |
| Clear navigation path | 19206 | 3067 |
| Status push configuration | 19207 | 9300 |
| Status push stream | 19301 | 19301 |

Every request channel is serialized. Packets use the vendor 16-byte header, big-endian payload length and API number, followed by UTF-8 JSON. Responses must use the request API number plus `10000` and a non-zero `ret_code` is retained as an AGV error.

MES sends the fixed route source and target to Adapter. Adapter sends all three required 3066 fields: `task_id`, `source_id`, and `id`. The real client does not infer a source station from a nearest-point status.

## Configuration

Set the following in the Adapter environment-specific settings only after the robot network information is confirmed:

```json
{
  "Agv": {
    "Driver": "tcp",
    "Tcp": {
      "Host": "192.168.1.100",
      "StatusPort": 19204,
      "CommandPort": 19206,
      "ControlPort": 19207,
      "PushPort": 19301,
      "NickName": "MesControlAgv.Adapter",
      "AcquireControl": true,
      "EnablePush": true,
      "PushIntervalMs": 500,
      "MinimumConfidence": 0.0,
      "RequestTimeoutMs": 3000,
      "ConnectTimeoutMs": 3000,
      "CancelApiId": 3067
    }
  }
}
```

`CancelApiId` defaults to `3067`, which clears the current 3066 route. PDF revisions that support task-specific safe clearing can use `3068`; that API receives the current `task_id` and does not clear the current movement.

## State mapping

The vendor task states map to the existing Adapter contract as follows:

| Vendor status | Adapter state |
|---:|---|
| 0 / 1 | `accepted` |
| 2 | `moving` |
| 3 | `paused` |
| 4 | `arrived` |
| 5 | `failed` |
| 6 | `cancelled` |
| 7 / 404 | `unknown` |

On a request timeout the Adapter queries API 1110 before deciding `unknown`. It never generates a new robot task ID for an unresolved operation.

Before 3066, the real client checks available safety fields from the push stream or API 1101: emergency, blocked, fatal/error arrays, relocation status, localization confidence and fork automatic mode. Missing optional fields are left to the site-specific acceptance procedure; present failing fields block dispatch.

## Hardware handoff checklist

- Confirm robot IP, firmware version, map name and the actual station IDs.
- Confirm `source_id` and `id` are directly connected in the loaded map.
- Confirm Adapter can acquire control with API 4005 and that RoboShop is not also controlling the robot.
- Verify relocation (2002/1021/2003), emergency/fatal/error gates, automatic fork mode and status push.
- Verify one pickup and one dropoff with the actual institution action and DI/DO confirmation.
- Verify timeout, disconnect, robot restart, pause, cancel and retry with the same `task_id`.

The current implementation deliberately does not invent fork, jack, roller, DI/DO or station-specific operation parameters. Those belong in the device-specific client after the robot model and mechanism interface are confirmed.
