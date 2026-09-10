# 离子色谱 ShineLab 现场联调进度 · 2026-09-10

## 结论

首次拿到**真实现场流量**（`192.168.10.108` → 中控 `192.168.10.11:5500`）。
连接层打通并稳定；业务层仍未打通，且阻塞点在厂商侧。

在此之前所有"验证"都是回环构造：`mes-shinelab-certfix.stdout.log` 中两条连接
是 `127.0.0.1:57249/57250`、strID 为 `local-cert-1`/`local-cert-2`，全文
`192.168.10.*` 与 `STN61_01` 各出现 0 次。本次是第一份现场证据。

## 已打通

| 项 | 证据 |
|---|---|
| LF-JSON 帧识别 | 连接首字节锁定 LineJson，全程无解码错误 |
| Certification 握手 | MES 回 `body.result=Success`，客户端接受并继续推送 |
| 连接稳定性 | 修复后 1 次连接 / 0 次断开，`lastSeenAtUtc` 持续刷新 |
| 多连接隔离 | 辅助连接 `58804` 建立后 0 字节、被 stale 超时回收，**未踢掉主连接** |
| 设备注册寻址 | 启用兜底后 `knownDeviceCount:1 / onlineDeviceCount:1` |

## 现场协议事实

客户端**每一帧的 `equipmentCode` 和 `strCode` 都是空串**，连接上没有 `Heart`，
也没有 `BindModule`，只有 `Certification` 与 `UpdateInfo`：

```json
{"strID":"7503733687394111488","strMethod":"Certification","equipmentCode":"","strCode":"","body":{}}
{"strID":"7503733688270721024","strMethod":"UpdateInfo","equipmentCode":"","strCode":"","body":{"status":99}}
```

`UpdateInfo` 约每 0.8 秒一条，`body` 恒为 `{"status":99}`，观测期内从未变化。

这与 `docs/SHINELAB-YHLLOOP-NATIVE-FRAME.md` 中更早记录的、带
`equipmentCode":"STN61_01"` 的 `Heart` 样本**不一致**。该样本来自更早的被动
抓包，不代表当前部署的客户端。

## 本次修复

三次提交，全部由现场证据驱动：

- `b4346f0` — 连接按 `equipmentCode` 索引，替代原先的单连接持有；pending 命令
  按 `connectionId` 隔离，避免一台设备掉线连带判死另一台在飞的命令。
- `bc8c8fa` — 空 `equipmentCode` 不再按协议违规断连，并按原文记录报文样本。
- `62343f5` — 新增 `FallbackEquipmentCode`；未知 `status` 不再落回 `Idle`。

### 一次由本方引入的回归（已修复，记录备查）

`bc8c8fa` 之前的一版把空 `equipmentCode` 当作协议违规，8 帧即断连。现场客户端
每帧都是空码，于是被持续踢下线并立即重连：**三分钟内 33 次连接 / 32 次断开**。
证据：`artifacts/ion-chromatography/field-reconnect-loop-20260910-1633.log`。

该逻辑的前身是一段永不触发的死代码（共用的 `invalidFrameCount` 在每个结构合法
帧上被重置）。把死代码改成"能触发"时，未先确认现场是否真会触发——教训是：
限流/断连类策略在拿到现场报文前不应启用。

### 一处误导性显示（已修复）

`ShineLabStatusHub` 原先只识别 `status` 0/1/2，其余全部落回 `Idle`。现场上报
99，因而显示为「空闲」，操作员会据此认为仪器就绪可进样。现改为未知码显示
`Unknown(<code>)`。已确认当时没有任何调度门禁消费该字段，故为显示误导而非
安全闸门失效。

## 仍未打通 —— 能寻址 ≠ 能下发

兜底只解决了寻址。业务闭环一步未动：

1. **状态看板无内容**：`UpdateInfo` 只有 `status` 一个字段，缺 task、sample、
   channel、position、stage、progress、errorCode。除在线/离线外无可显示项。
2. **`status` 码表缺失**：99 含义未知，厂商未提供。
3. **`Config` / `Command` 未验证**：2026-09-04 符合性分析的 20 项 P0 一条未解决，
   见 `docs/SHINELAB-下游表协议-需求符合性分析-2026-09-04.md`。
4. **`strID` 重复**：同一会话中 3 条 `Certification` 有两条 `strID` 完全相同
   （`7503733687536717824`）。`strID` 是响应关联的唯一键，重复会导致关联错乱。

本阶段仍不发送任何 `Config`、`Command`、泵、进样或方法控制报文。

## 需要厂商回答

1. 客户端为何不填 `equipmentCode`？是配置项还是缺陷？正式设备编码是什么？
2. `status` 完整码表，特别是 99 的含义。
3. `UpdateInfo` 是否有携带任务/样品/通道/进度字段的完整版本？触发条件是什么？
4. 为何连发 3 条 `Certification` 且 `strID` 重复？
5. 本次连接上未见 `Heart` 与 `BindModule`，与更早抓包不符——客户端版本/模式差异？

## 复现方式

```powershell
cd src\MesControlAgv.Mes
$env:ShineLabTcp__Enabled = 'true'
$env:ShineLabTcp__FallbackEquipmentCode = 'STN61_01'   # 临时方案，厂商修复后移除
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5145'
dotnet bin\Debug\net8.0\MesControlAgv.Mes.dll
```

客户端需手动点「连接」；服务端重启后不会自动重连。

查看：

```powershell
Invoke-RestMethod http://127.0.0.1:5145/api/shinelab/devices/status
Invoke-RestMethod http://127.0.0.1:5145/api/shinelab/server/status
```

## 证据文件

| 文件 | 内容 |
|---|---|
| `artifacts/ion-chromatography/field-reconnect-loop-20260910-1633.log` | 断连循环，33 次连接 / 32 次断开 |
| `artifacts/ion-chromatography/field-payload-capture-20260910-1650.log` | 逐字节原始报文，含 5 条完整样本 |
| `artifacts/ion-chromatography/field-fallback-registered-20260910-1657.log` | 兜底生效、设备注册上线 |

## 测试

ShineLab 专项 35/35；MES 全量 284/287。

余下 3 项为运行方式导致，非代码缺陷：这些用例从程序集目录向上查找仓库根读取
`appsettings.json`，为不中断现场连接而将构建输出重定向到临时目录，路径被打断。
停服务后按正常输出目录构建即全绿（本次改动前基线为 285/285）。

## 下一步

短期最有价值的动作：**趁连接活着，在 ShineLab 客户端上执行一个动作，观察
`status` 是否从 99 变化**——这能直接反推码表，无需等厂商。
