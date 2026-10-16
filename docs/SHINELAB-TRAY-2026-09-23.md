# 厂家新版托盘字符串命令联调

## 协议与实现

用户已替换控制电脑厂家新版，并提供协议截图。用户随后明确确认弹出拼写为 **TrayPopup**（末尾p），不采用截图中疑似TrayPopuo的拼写，不轮流试发。

- strMethod仍为Command；body.strAction有值时优先于数字action。
- 本轮仅支持TrayPopup（弹出）、TrayReset（复位），param为空。单通道不带channel，字符串命令不带数字action，尤其不混入启动action=0。
- 原有数字0/2/7行为保留，未开放字符串Init/Start/Stop/Clean/TurnTo。错误拼写直接拒绝，不回退到启动。
- 功能开关EnableTrayCommands默认关闭；启用后仍要求真实Device/UpdateInfo上报的新鲜Idle、无活动样品。认证/心跳、旧连接状态不能代替当前设备状态；并发托盘请求不排队补发。
- 通过设备command入口发送，不能借样品task command入口改变样品状态。请求、应答沿用完整JSON审计；设备级操作TaskUuid和数字Action均为null，不能记为样品启动。

离线验证：新增20项，MES全量681/681、WPF相关74/74通过；结果位于`artifacts/tray-command-verification-20260923/`。未新增托盘WPF按钮。

## 发布与切换

- 工作分支feature/shinelab-dcseq-protocol，独立worktree `.worktrees/shinelab-dcseq`。
- 发布目录`artifacts/tray-command-release-20260923-142744/`；MES DLL SHA256：`209902C00E93E8B811EB025CC83D8633A1F6FFE289ED7794064C7F296C5B99FA`。
- 用户确认无运行样品、待运行行未勾选、运动区域无遮挡且手已离开，并授权先弹出一次。
- 14:28核对结束旧PID35752，新PID **21128**，同一SingleChannel contentRoot，TCP192.168.10.11:5500、HTTP127.0.0.1:5145。
- 停写后一致备份主库/WAL/SHM、配置和旧监听日志，逐项哈希一致，备份路径`artifacts/tray-command-switch-20260923-142842/backup/`。旧程序及日志未覆盖。
- 使用命令行`--ShineLabTcp:EnableTrayCommands true`明确启用，不改变原配置文件。新日志位于上述switch目录的mes.stdout.log、mes.stderr.log。
- 客户端192.168.10.108:62956自动重连，connectionId=`a28be126f0b94f96a45a4ef6482fbbc8`，认证与Device Idle正常。

## TrayPopup已实测成功

14:29:37只发送一次，完整实发JSON：

```json
{"strID":"f24bac9c31254c698ff0fddbc6da42c8","strMethod":"Command","equipmentCode":"STN61_01","body":{"strAction":"TrayPopup","param":""}}
```

完整实收JSON：

```json
{"strID":"f24bac9c31254c698ff0fddbc6da42c8","strMethod":"Command","equipmentCode":"STN61_01","strCode":"","body":{"channel":"","result":"Success","msg":""}}
```

写入完成14:29:37.2567885，接收14:29:37.5464486，约290ms。审计ReplyWithinWait / Matched，无冲突。没有发送Config、启动、Init、另一种弹出拼写或自动重试。

随后用户明确反馈“确认已经弹出了”，因此本项同时具有应答和现场动作证据。不能把此前单独的Success当作到位证明；本次到位结论来自用户观察。

## TrayReset已实测成功

用户明确授权“可以进行复位了”后，14:31:42核对空闲状态，只发送一次：

```json
{"strID":"4ced44a8acae4b99a4cc604b80e8c373","strMethod":"Command","equipmentCode":"STN61_01","body":{"strAction":"TrayReset","param":""}}
```

完整实收JSON：

```json
{"strID":"4ced44a8acae4b99a4cc604b80e8c373","strMethod":"Command","equipmentCode":"STN61_01","strCode":"","body":{"channel":"","result":"Success","msg":""}}
```

写入完成14:31:42.6178832，接收14:31:42.7474738，约130ms；审计ReplyWithinWait / Matched，无冲突。没有重试或附带进样指令。

用户随后明确反馈“确认已经恢复原位”。因此本轮弹出、复位均已完成“匹配应答＋现场动作确认”，原文与时间已入库。当前协议截图未明确独立的托盘到位上报，实际位置结论来自操作员确认，而非仅凭Success或Device=0；尚不等同于无需人工确认的自动到位闭环。

本轮动作验证结束，不继续发送托盘或进样指令。

## WPF入口验证（同日15:00）

新增独立WPF“离子色谱控制”页面后，用户确认在该页面手动点击弹出和复位。审计记录如下，两次均为ReplyWithinWait、Success，未发现应答冲突：

- 15:00:35，TrayPopup，strID=`c8d47d8c55624c84a795bb66fc5d2c59`。
- 15:00:41，TrayReset，strID=`1d6a5347d79741bdb6fe7ea437ba2e4a`。

这些动作来自用户操作，不是自动检查发送；确认了WPF操作至MES及客户端应答链路。此次用户确认的是手动操作来源，不额外推断独立位置反馈。页面接入范围、发布与后续步骤见[WPF接入记录](SHINELAB-WPF-INTEGRATION-2026-09-23.md)。
