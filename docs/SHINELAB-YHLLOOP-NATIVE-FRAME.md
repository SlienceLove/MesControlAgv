# ShineLab YhLoop TCP 帧记录

更新时间：2026-09-10

## 现场确认的 YhLoop 格式

此前根据二进制符号作出的 `55 AA` 判断不能代表当前部署的 YhLoop 客户端。
在中控 `192.168.10.11:5500` 的被动捕获中，控制电脑发送的是 UTF-8 JSON，
每条消息以 LF（`0A`）结束：

```json
{"strID":"7503612046576062464","strMethod":"Heart","equipmentCode":"STN61_01","strCode":"","body":{"type":"ping","time":"110947000"}}
```

厂家确认的绑定消息为：

```json
{"strID":"7503612046865469440","strMethod":"BindModule","equipmentCode":"STN61_01","strCode":"","body":{"chan":""}}
```

心跳约每 0.5 秒一条。TCP 可能拆分或合并多条 JSON，读取器必须按 LF 增量
分帧，不能把一次 `ReadAsync` 当作一条消息。`Heart` 和 `BindModule` 在本阶
段只登记设备、刷新在线时间并记录摘要，不主动回包。

## 2026-09-10 现场实测：客户端不发设备标识

上面带 `equipmentCode":"STN61_01"` 的样本来自更早的被动抓包。**2026-09-10
从 `192.168.10.108` 接入的实际客户端与之不符**：该连接上没有 `Heart`，也
没有 `BindModule`，只有 `Certification` 与 `UpdateInfo`，且**每一帧的
`equipmentCode` 和 `strCode` 都是空串**。逐字节样本：

```json
{"strID":"7503733687394111488","strMethod":"Certification","equipmentCode":"","strCode":"","body":{}}
{"strID":"7503733688270721024","strMethod":"UpdateInfo","equipmentCode":"","strCode":"","body":{"status":99}}
```

`UpdateInfo` 约每 0.8 秒一条，`body` 恒为 `{"status":99}`，观测期内从未变化，
也不含 task、sample、channel、position、stage、progress、errorCode 中的任何
一个字段。证据：`artifacts/ion-chromatography/field-payload-capture-20260910-1650.log`。

同一次会话中客户端连发 3 条 `Certification`，其中两条的 `strID` 完全相同
（`7503733687536717824`）。`strID` 是响应关联的唯一键，重复会导致关联错乱，
需厂商确认是否为缺陷。

`status = 99` 的含义**未知**，厂商未提供码表。不得假定它表示空闲或就绪。

## 兼容的 55AA 格式

旧版或离线 fixture 仍可能使用：

```text
55 AA [length_hi] [length_lo] [UTF-8 JSON] 00
```

`length` 是 JSON 字节数加末尾 NUL，按大端序写入。MES 保留该 codec，并在
每条 TCP 连接的首个有效字节上自动锁定 LF-JSON 或 55AA；锁定后不在两种格式
之间切换。未知格式、超长行和无效 UTF-8 不产生出站数据。

## 当前实现边界

MES 默认被动监听，只在收到 `Certification` 后按收到的格式返回
`body.result=Success`、`body.msg=""`。不会主动伪造 `Heart`、`BindModule`、
`Certification`，也不会在本阶段发送 `Config`、`Command`、泵、进样或方法控制
报文。二进制静态符号只能作为辅助证据，协议行为以现场抓包和厂家确认优先。

### 空 equipmentCode 的处理

空 `equipmentCode` 是当前部署的**常态**，不是协议违规：

- 这类帧**不会**导致断开连接。曾按协议违规处理，结果是现场客户端每 8 帧被踢
  一次并立即重连，三分钟内 33 次连接 / 32 次断开。证据：
  `artifacts/ion-chromatography/field-reconnect-loop-20260910-1633.log`。
- 每条连接的前 5 条此类报文按原文完整记入日志作为协议证据，其余每 200 帧
  汇总一次，避免按心跳频率刷屏。
- 未配置兜底时，这类连接保持连接但**不注册设备**，因此无法寻址、无法下发。

### FallbackEquipmentCode（临时替代方案）

`ShineLabTcp:FallbackEquipmentCode` 把空 `equipmentCode` 的帧归属到配置的设备码。

- **默认为空即关闭。** 启用它等于断言该监听器上只有一台仪器——所有无标识帧
  都会被记到这一个设备名下。
- 每条连接首次使用时打一条 Warning，写明这是替代方案，不静默生效。
- **回包仍按客户端原样回**，`equipmentCode` 照旧回空串。兜底是 MES 侧的归属
  判断，不把我们的假设写回线上。
- 厂商客户端一旦正确填写 `equipmentCode`，应移除此配置。

启用后设备可注册上线（证据：
`artifacts/ion-chromatography/field-fallback-registered-20260910-1657.log`），
但这只解决**寻址**，不构成业务闭环——见下节。

### 设备状态码映射

`status` 只有 0/1/2 是已确认的编码，分别映射为 Idle / Running / Error。
其余一律映射为 `Unknown(<code>)`。**不得让未知码落回 `Idle`**：现场上报的
99 曾因此显示为「空闲」，操作员会据此认为仪器就绪可进样。

## 尚未打通（能寻址 ≠ 能下发）

- `UpdateInfo` 只有 `status` 一个字段，状态看板除在线/离线外**无内容可显示**。
- `status` 码表缺失，99 含义未知。
- `Config` / `Command` 的字段与动作映射仍是 2026-09-04 符合性分析中未解决的
  20 项 P0，见 `docs/SHINELAB-下游表协议-需求符合性分析-2026-09-04.md`。
- 本阶段仍不发送任何 `Config`、`Command`、泵、进样或方法控制报文。
