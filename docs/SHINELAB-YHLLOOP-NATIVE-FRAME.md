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

## 兼容的 55AA 格式

旧版或离线 fixture 仍可能使用：

```text
55 AA [length_hi] [length_lo] [UTF-8 JSON] 00
```

`length` 是 JSON 字节数加末尾 NUL，按大端序写入。MES 保留该 codec，并在
每条 TCP 连接的首个有效字节上自动锁定 LF-JSON 或 55AA；锁定后不在两种格式
之间切换。未知格式、超长行和无效 UTF-8 不产生出站数据。

## 当前实现边界

MES 默认被动监听，只在收到合法 `Certification` 后按收到的格式返回
`body.result=Success`、`body.msg=""`。不会主动伪造 `Heart`、`BindModule`、
`Certification`，也不会在本阶段发送 `Config`、`Command`、泵、进样或方法控制
报文。二进制静态符号只能作为辅助证据，协议行为以现场抓包和厂家确认优先。
