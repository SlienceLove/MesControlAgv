# YhLoop LF-JSON/55AA 线协议兼容实施计划

关联设计：[2026-09-10-shinelab-yhlloop-wire-format-design.md](../superpowers/specs/2026-09-10-shinelab-yhlloop-wire-format-design.md)

## 实施边界

- 生产现场的 YhLoop 实际格式是 LF 结尾 JSON；保留 55AA 编解码作为兼容路径。
- 默认被动接收；不主动发 Heart、BindModule、Certification、Config、Command、泵或进样命令。
- 只在收到合法 Certification 后按已接收格式回复 Success。
- 不改现场数据库、AQ 配置或串口驱动；不覆盖工作区已有安全改动。

## Task 1：共享 LF-JSON 编解码与连接级格式识别

范围：

- `src/MesControlAgv.Contracts/ShineLabTcpFrameCodec.cs`
- 新增同目录的 LF 增量 reader/codec（或在现有文件内保持清晰边界）。
- 对应 `tests/MesControlAgv.Mes.Tests/ShineLabTcpFrameCodecTests.cs`。

动作：

1. 保留并冻结现有 55AA codec 的行为与测试。
2. 增加严格 UTF-8、LF 分隔、最大行长、半包/粘包、可选单个 CR 处理。
3. 增加 `Unknown/LineJson/Native55Aa` 格式状态和有限缓冲；格式锁定后拒绝切换。
4. 输出统一的 JSON payload/frame abstraction，避免服务层重复解析。
5. 对未知格式或超限输入返回有界错误，不产生任何出站数据。

完成判据：LF Heart、BindModule 和 55AA fixture 均能逐字节拆分、一次多帧读取，并能在噪声/错误后安全恢复。

## Task 2：MES Server 接入真实 LF 流

范围：

- `src/MesControlAgv.Mes/Services/ShineLabTcpServer.cs`
- `src/MesControlAgv.Mes/Services/ShineLabTcpOptions.cs`
- 可能的统一消息/连接记录类型。

动作：

1. 将单一 55AA 读取循环改为连接级格式识别读取循环。
2. 日志明确记录 `WireFormat`、方法名、strID、设备编码、长度和脱敏摘要。
3. Heart 刷新连接活动时间；BindModule 登记设备和 `body.chan`，不回包。
4. Certification 仅在收到后用同一格式返回 Success；保持主动探测默认关闭。
5. 将可选 `strCode` 作为兼容字段读取，不因缺失拒绝通用协议表消息。
6. 保持现有任务事件和状态 Hub 路由；未知方法只审计，不调用控制层。

完成判据：现场样本连续 20 秒 Heart 不超时，`STN61_01` 可在线登记，日志出现 BindModule，且无任何控制动作调用。

## Task 3：连接管理器和 dispatcher 出站格式

范围：

- `src/MesControlAgv.Mes/Services/ShineLabConnectionManager.cs`
- `src/MesControlAgv.ShineLabDispatcher/ShineLabTcpProtocol.cs`
- 对应测试。

动作：

1. 连接记录保存检测到的 WireFormat。
2. 未锁定格式前禁止发送命令并返回明确的未识别错误。
3. 已锁定后，响应/命令使用同格式编码；本阶段仍不从现场流程触发控制命令。
4. 旧 `DecodeLegacyLine` 重新标记为正式 LF fixture 路径，避免语义混淆。

完成判据：模拟客户端分别使用 LF 和 55AA 时，命令编码与入站格式一致；未知格式没有出站字节。

## Task 4：行为与安全回归测试

新增/调整测试：

- LF 单字节半包、多个 Heart 粘包、Heart+BindModule 粘包。
- Heart/BindModule 不回包且刷新在线时间。
- Certification 只在收到后回复，且回复使用同一格式。
- `strCode` 存在/缺失、空 `body.chan`、设备编码 `STN61_01`。
- 55AA 原有测试全部保留。
- 10 秒 stale timeout、连接替换、错误预算和未知格式无出站数据。
- 任务/泵/进样控制服务未被 Heart/BindModule 调用。

## Task 5：验证、文档与交付

1. 运行定向 Contracts/MES 测试。
2. 运行 MES 全量测试（单线程，记录已知锁定例外）。
3. 构建 Debug/Release 相关项目，确认监听端口仍为 5500、主动探测默认关闭。
4. 更新协议说明，明确“现场 YhLoop 实际为 LF-JSON；55AA 为兼容路径”，不再把静态推断写成现场事实。
5. 保留脱敏真实 fixture：Heart、BindModule，删除/忽略临时捕获脚本和现场原始日志。
6. 现场首轮只观察在线/心跳/绑定；通过后才另行设计 Config/Command 联调。

## 回滚点

- 每个 Task 独立提交；若线协议回归失败，可回滚 Server/codec 提交而保留设计和测试文档。
- 不使用破坏性 Git 操作，不触碰用户已有未提交安全文件。
