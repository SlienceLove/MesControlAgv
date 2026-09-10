# SHA-18i / ShineLab 现场抓包证据 · 2026-09-10

首次现场流量记录。链路：控制电脑 `192.168.10.108` → 中控 `192.168.10.11:5500`。

原始 `.log` 按 `.gitignore` 策略（`/artifacts/ion-chromatography/*.log`，
"retain curated Markdown evidence only"）保留在本地不入库，文件名见文末。
本文摘录的是判断所依据的原文。

---

## 1. 帧格式与握手

连接首字节即锁定 **LineJson**（LF 分帧 UTF-8 JSON），全程无解码错误、无格式回退。

```
Accepted ShineLab TCP client 192.168.10.108:54729 as connection 8b767f7df36141c0b96d843cbf41e07b.
Received first ShineLab TCP payload on connection 8b767f7df36141c0b96d843cbf41e07b;
  length=114, sha256=74CB26FBF49C5730, prefix=7B227374724944223A223735
Replied to ShineLab Certification 7503738441591558144 for STN61_01 using LineJson framing.
```

`prefix` 解码为 `{"strID":"75`，与 LF-JSON 判定一致。

---

## 2. 客户端不发设备标识（核心阻塞）

该连接上**没有 `Heart`，也没有 `BindModule`**，只有 `Certification` 与
`UpdateInfo`，且每一帧的 `equipmentCode` 与 `strCode` 均为空串。

前 5 条 `UpdateInfo` 原文（服务端逐字节记录）：

```json
{"strID":"7503733688270721024","strMethod":"UpdateInfo","equipmentCode":"","strCode":"","body":{"status":99}}
{"strID":"7503733689105387520","strMethod":"UpdateInfo","equipmentCode":"","strCode":"","body":{"status":99}}
{"strID":"7503733689940054016","strMethod":"UpdateInfo","equipmentCode":"","strCode":"","body":{"status":99}}
{"strID":"7503733690770526208","strMethod":"UpdateInfo","equipmentCode":"","strCode":"","body":{"status":99}}
{"strID":"7503733691605192704","strMethod":"UpdateInfo","equipmentCode":"","strCode":"","body":{"status":99}}
```

`Certification` 原文形状：

```json
{"strID":"7503733687394111488","strMethod":"Certification","equipmentCode":"","strCode":"","body":{}}
```

观测要点：

- `UpdateInfo` 约每 0.8 秒一条，`body` 恒为 `{"status":99}`，观测期内从未变化。
- `body` 不含 task、sample、channel、position、stage、progress、errorCode 中的
  任何一个字段。
- 单条连接累计忽略帧数达 800+ 仍保持 `{"status":99}`。

此结果与本目录更早记录的、带 `equipmentCode":"STN61_01"` 的 `Heart` 样本**不符**。
该样本来自更早的被动抓包，不代表当前部署的客户端。

---

## 3. `strID` 重复

同一次会话中客户端连发 3 条 `Certification`，其中两条 `strID` 完全相同：

```
Replied to provisional ShineLab Certification 7503733687394111488 ...
Replied to provisional ShineLab Certification 7503733687536717824 ...
Replied to provisional ShineLab Certification 7503733687536717824 ...
```

`strID` 是响应关联的唯一键，重复会导致关联错乱。需厂商确认是否为缺陷。

---

## 4. 断连循环（本方引入的回归，已修复）

将空 `equipmentCode` 按协议违规处理的那一版，导致现场客户端每 8 帧被踢一次并
立即重连。**三分钟内 33 次连接 / 32 次断开**：

```
Accepted ShineLab TCP client 192.168.10.108:58766 as connection fde90c71522d4c0db0ee85e4dc1d3784.
System.IO.InvalidDataException: Too many ShineLab messages without an equipment code.
Accepted ShineLab TCP client 192.168.10.108:58767 as connection 60b3f0af2e8e4a36b058ffd39decc14f.
System.IO.InvalidDataException: Too many ShineLab messages without an equipment code.
...
Accepted ShineLab TCP client 192.168.10.108:58798 as connection cabc27f33bad42e0b5baf16320baf849.
Accepted ShineLab TCP client 192.168.10.108:58799 as connection de3c553ba9ab41e58a70a3093c4f1dc5.
```

源端口从 `58766` 单调递增到 `58799`，即客户端在持续重建连接。

修复后同一客户端：**1 次连接 / 0 次断开**。

---

## 5. 多连接隔离验证

主连接 `58802` 保持在线期间，客户端另建了一条连接 `58804`，该连接建立后未发送
任何字节：

```
Accepted ShineLab TCP client 192.168.10.108:58804 as connection b34308eee951430e9138f40b94ef77e3.
ShineLab client connection b34308eee951430e9138f40b94ef77e3 timed out after 10s
  without a complete message; observed 0 reads/0 bytes and format Unknown.
```

该连接被 stale 超时正常回收，**未影响主连接 `58802`**。这验证了按
`equipmentCode` 索引连接的修复在真实现场生效。

---

## 6. 兜底注册生效

启用 `ShineLabTcp:FallbackEquipmentCode=STN61_01` 后：

```
Attributing unidentified ShineLab frames on connection 8b767f7df36141c0b96d843cbf41e07b
  to configured fallback equipment code STN61_01. This is a stand-in for a client that
  does not populate equipmentCode; it assumes a single instrument on this listener.
```

该 Warning 每条连接只打一次。设备随即注册上线：

```json
{"enabled":true,"role":"server","listenAddress":"0.0.0.0","port":5500,
 "staleAfterSeconds":10,"commandTimeoutMs":10000,
 "knownDeviceCount":1,"onlineDeviceCount":1}

[{"equipmentCode":"STN61_01","deviceName":"STN61_01","online":true,"status":99,
  "state":"Idle","hasActiveTask":false,"taskUuid":null,"sampleId":null,
  "sampleName":null,"channel":null,"position":null,"stage":null,"progress":null,
  "alarmCode":null,"alarmMessage":null,
  "lastSeenAtUtc":"2026-09-10T09:07:23.5797879+00:00",
  "taskStartedAtUtc":null,"taskFinishedAtUtc":null}]
```

上面这份快照中的 `"state":"Idle"` 即后来判定为误导性显示的表现：`status` 99 属
未知码，却落回了 `Idle`。该快照由修复前的进程输出，保留原样作为问题证据。
修复后同样输入应显示 `Unknown(99)`。

注意除 `equipmentCode`、`online`、`status`、`lastSeenAtUtc` 外，其余字段全为
`null` —— 状态看板无内容可显示，根因是 `UpdateInfo` 只有 `status` 一个字段。

---

## 本地原始日志

| 文件 | 内容 |
|---|---|
| `field-reconnect-loop-20260910-1633.log` | 断连循环，33 次连接 / 32 次断开 |
| `field-payload-capture-20260910-1650.log` | 逐字节原始报文，含 5 条完整样本 |
| `field-fallback-registered-20260910-1657.log` | 兜底生效、设备注册上线 |

## 关联文档

- `docs/SHINELAB-FIELD-PROGRESS-2026-09-10.md` —— 本次联调进度与结论
- `docs/SHINELAB-YHLLOOP-NATIVE-FRAME.md` —— 帧格式与实现边界契约
- `docs/SHINELAB-下游表协议-需求符合性分析-2026-09-04.md` —— 20 项 P0 差距清单
