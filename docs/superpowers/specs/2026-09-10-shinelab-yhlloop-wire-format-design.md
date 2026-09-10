# ShineLab YhLoop 实际线协议兼容设计

## 背景与证据

现场控制电脑 `192.168.10.108` 已连接到中控 `192.168.10.11:5500`。在不向客户端发送任何数据的原始捕获中，YhLoop 发送了连续的换行分隔 JSON，而不是当前 MES 假设的 `55 AA` 长度帧。

典型心跳帧（UTF-8，末尾一个 LF `0A`）：

```json
{"strID":"7503612046576062464","strMethod":"Heart","equipmentCode":"STN61_01","strCode":"","body":{"type":"ping","time":"110947000"}}
```

现场还捕获到厂家确认的绑定帧：

```json
{"strID":"7503612046865469440","strMethod":"BindModule","equipmentCode":"STN61_01","strCode":"","body":{"chan":""}}
```

心跳约每 0.5 秒一条；每条帧的最后字节为 `0A`，没有 `55 AA` 头和 NUL 结尾。`res/下游表协议 - 序列进样新.docx` 明确规定 TCP 使用换行作为 JSON 粘包/半包分帧，客户端主动保持连接，10 秒无读写视为不可用。该表没有单列 `Heart` 和 `BindModule`，但它们符合通用 JSON 外壳；`BindModule` 由厂家另行确认。

当前 `ShineLabTcpServer` 及 `ShineLabTcpConnectionManager` 只使用 55AA 编解码。因此真实字节会一直停留在“等待完整原生帧”状态，不能登记设备或刷新心跳。

## 目标

1. 在同一 TCP 端口上可靠接收现场实际的 LF-JSON YhLoop 流。
2. 保留现有 55AA 编解码能力，避免破坏已有离线 fixture 和潜在旧客户端。
3. 按每条连接实际识别出的格式发送响应或后续请求，禁止混用格式。
4. `Heart`、`BindModule` 只做解析、连接登记和审计，不主动回包，不触发泵、进样、停止或方法控制。
5. 只有客户端主动发送合法 `Certification` 时，才按协议表返回同格式的 `Success` 响应；不主动伪造认证或绑定消息。
6. 在离线测试中覆盖半包、粘包、心跳、绑定帧、格式识别和连接超时。

## 非目标与安全边界

- 本阶段不推断或实现 `BindModule` 的服务端响应；现场捕获证明客户端在无回包捕获窗口内仍发送 Heart/BindModule，完整业务响应规则仍以厂家确认为准。
- 不发送 `Config`、`Command`、泵、进样、停止或方法控制报文。
- 不修改现场 AQ 配置、仪器数据库或串口驱动。
- 不删除现有 55AA codec；它仍作为兼容协议和离线测试组件保留。

## 方案

### 连接级格式识别

新增有限状态的连接读取器，状态为 `Unknown`、`LineJson` 或 `Native55Aa`：

1. 在 `Unknown` 状态累计有界字节缓冲。
2. 若前两个有效字节为 `55 AA`，切换到现有长度帧读取器。
3. 若数据表现为 UTF-8 JSON（跳过仅用于探测的 ASCII 空白后首字节为 `{` 或 `[`），按 LF 增量读取器处理；允许单次读取包含多行，也允许一行被拆成多个 TCP 读取。
4. 每条 LF 行必须在最大长度内、以严格 UTF-8 解码并通过 JSON 解析；空行可忽略，超长或无法解码的行计入有限错误预算。协议主分隔符是 LF；为兼容常见 Windows 发送端，解析时可去掉 LF 前的单个 CR，但发送端仍只追加 LF。
5. 一旦格式锁定，连接剩余生命周期不再在两种格式间切换。格式未知时禁止发送任何主动业务消息。

读取器输出统一的 `ShineLabMessage`，上层不再关心线格式或 55AA 格式。连接记录同时保存 `WireFormat`，供响应/命令编码使用。

### 消息处理

- `Heart`：刷新 `LastSeenAtUtc`、连接状态和设备编码；不改变泵/进样任务状态，不回包。
- `BindModule`：刷新连接登记并保留 `body.chan`（包括现场捕获的空字符串）；不回包、不猜测响应。
- `Certification`：按现有字段校验；使用该连接的 `WireFormat` 返回 `strID`、`strMethod`、`equipmentCode`、`body.result=Success`、`body.msg=""`。只有收到该消息才响应。
- 已有 `Device`/`UpdateInfo`/任务事件继续交给状态 Hub；其余未知方法只记录摘要，不触发控制动作。
- `strCode` 作为可选字符串保留在内部消息模型或审计中；缺失时不拒绝与现有协议表一致的消息。

### 出站行为

`ShineLabConnectionManager` 在格式锁定后使用对应编码器：LF-JSON 追加一个 LF，55AA 使用现有长度/NUL 帧。若格式仍为 `Unknown`，命令发送返回“尚未识别线格式”，避免向真实客户端发送猜测报文。生产默认仍关闭连接探测开关。

## 错误与生命周期

- 10 秒内没有任何完整 LF 行或 55AA 帧时，按现有 stale timeout 断开；收到 Heart 即刷新超时。
- 连接被替换时，旧连接的待处理命令以“连接被替换”结束，不重试物理动作。
- 连续达到错误预算后关闭连接并记录格式、错误原因、长度和哈希前缀；不记录敏感正文。
- 解析失败不得调用设备控制服务。

## 测试计划

1. LF 读取器：单字节拆分、多个心跳粘包、LF 后下一帧、空行、单个 CRLF 兼容、超长行和无效 UTF-8。
2. 55AA 回归：现有编解码、半包、粘包、噪声恢复测试全部保留。
3. 自动识别：首帧分别为现场 Heart、BindModule 和 55AA Certification 时锁定正确格式；未知格式不得产生出站字节。
4. 消息行为：Heart/BindModule 不回包且刷新在线时间；Certification 仅在收到后用同格式回复；`strCode` 缺失/存在均可解析。
5. 连接级集成测试：现场样本连续 20 秒 Heart 不超时，BindModule 后设备登记为 `STN61_01`，没有任何泵/进样调用。
6. 运行现有 MES 与 Contracts 测试，并保存一份脱敏的真实帧 fixture（不包含现场账号、地址或控制参数）。

## 发布与回滚

- 先以被动模式发布，`SendCertificationOnConnect=false`，只观察 Heart/BindModule 登记和日志。
- 离线测试通过后再在现场启动；首轮联调不启用 Config/Command API。
- 若出现解析异常，恢复旧二进制即可；保留 55AA codec 和旧配置，不改数据库。
