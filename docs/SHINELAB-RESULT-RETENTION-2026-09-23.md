# DCSeq Result 完整留存修复（2026-09-23）

## 现场事实与本轮范围

- DA 最新截图显示测试组分 `TEST_ION_01`：保留时间 5.055000 min、峰面积 4.386941、浓度 1.354831 mg/L。
- 测试处理方法为 `09220922_TCP_TEST`，使用模拟浓度 1.000000 的单点曲线，仅用于通讯验证，不构成正式定量方法或检测报告。
- MES 日志摘要显示 09:50:26.951～09:50:27.107 收到 4 条 Result，每条 429 字节。第一条 strID 为 `7508342016984616960`。
- 现场旧版本在同一连接每类消息仅记录前三条完整 JSON，配额此前已用完；现场数据库也没有本分支新增的证据表，无法还原本次完整 Result。
- **DA 数值不等于 TCP 实收字段。** 本轮不拼造报文，不宣称已核对线上组分、浓度字段；测试代码中的对应数值是明确标注的模拟输入。
- 本轮仅修改独立工作目录、使用临时数据库和随机回环 TCP 端口测试。未替换现场二进制、未重启现场 MES、未下发 Config/Command/Init。

## 修复内容

1. DCSeq / DcSeqSingleChannel 的生命周期、Result、报警证据保存解码后的完整 JSON、原始 body、strID、连接标识、帧格式及 MES 接收 UTC ticks。没有 task_uuid 也保存，不按重复使用的 sampleId 猜测任务归属。
2. `RawJson` 是接收时解码的 JSON 信封，包含 strCode 和未知扩展字段，不是事后用 body 拼装，也不包含 TCP 包头、LF 分隔符或 55AA 包头。
3. 诊断采样打开时，Result / SampleFinish / TaskFinish / EndMission / TaskError 不再受前三条限制；高频 Device 等仍有限额。数据库留存不依赖 `CaptureProtocolSamples`。
4. 重复 Result 原文全部保留。设备、方法、sampleId、testDate 及规范化 body 一致时标记重复；strID 可不同。浓度、日期或样品编号改变不会被合并；缺少标识时只按同 strID 与 body 识别重复。
5. 明确 task_uuid 且设备匹配时，重复 Result 只生成一次任务观察事件，不据此推断整单完成。此过滤不承诺跨连接并发的 exactly-once。
6. 新库建表，旧库补表/索引，已有 body-only 表追加可空字段；历史 RawJson 保持 null，不伪造缺失数据。

查询入口：

```text
GET /api/shinelab/devices/STN61_01/push-evidence?limit=100
```

按接收时间倒序，最多 500 条；HTTP 返回的 `rawJson` 是 JSON 字符串，需要解码一次查看完整报文。

## 离线验收

测试入口：`tests/MesControlAgv.Mes.Tests/ShineLabResultRetentionTests.cs`。

- 同一连接连续 6 条 Result，含非空/空 compounds、相同 body 不同 strID、未知信封字段及原始空白。
- 分别关闭/开启诊断采样，所有结果均入 SQLite，开启时第 4～6 条完整日志仍存在。
- 关闭测试服务并使用同一临时库重启，API 返回的全部证据字段与重启前一致。
- 旧库无证据表、旧证据表缺新增列两种升级路径均覆盖；既有任务和旧 body 保留。
- 结果变更、日期变更、样品变更、缺失标识不误合并；明确关联时只去重任务观察，仍保留两份原文和原接收时间。
- 被动监听不主动发送任何控制帧。

最终结果：MES 全量 **629/629** 通过（含本轮新增 10 项），WPF ShineLab 相关 **74/74** 通过；`git diff --check` 通过。
测试结果位于 `artifacts/result-retention-verification-20260923/mes-result-retention-final.trx` 及 `wpf-shinelab.trx`。
已生成独立的 Debug 发布包 `artifacts/result-retention-release-20260923-101930/`，尚未运行或部署；不是已启动的现场监听。
源码和文档尚未提交、合并或推送。

## 切换及下一条样品验证

1. 通知用户离线验证完成，确认 AQ 当前没有运行样品，再安排现场服务切换；本轮不自动执行切换。
2. 切换前保留旧程序、配置、数据库一致性备份和已有日志；新版本使用新发布目录及唯一的新日志文件名，禁止覆盖旧 stdout。
3. 核对单通道配置、认证回复及 60 秒超时保持现场已验证值。确保只有一个服务占用 `192.168.10.11:5500`，确认新证据查询接口可访问后，再通知用户点击连接。
4. 沿用已配置的处理方法和曲线，用户确认仅勾选目标测试行后运行一条；不自动追加或启动队列。
5. 收到 Result 后导出完整 rawJson，对照该次 DA 的组分名称、峰面积和浓度，并区分 MES 接收时间与仪器 testDate。不能预设数值必须等于上一条结果。
6. 分别报告通讯留存、DA 对照、重复上报以及任务关联结论。尚无唯一任务关联时，不宣称中控任务闭环已完成。

## 边界

已有日志缺失不可由本次修复补回。Result 留存不等同于迟到 Command 响应留存，后者仍待实现。磁盘容量、归档保留周期及跨连接并发去重不属于本次现场验收结论。

## 已授权现场切换（2026-09-23 10:22）

用户明确确认没有样品运行、允许切换后执行；以下状态更新取代上文“尚未部署”的离线检查点。

- 已核对并结束旧 PID `16960`，其他 MES / Adapter / 工作站进程未动。首次退出检查仍见进程，脚本按保护条件停止；再次只读检查确认已退出且端口释放后，才继续备份和启动，没有启动两个监听进程。
- 停止写入后将 `mes.db`、`mes.db-wal`、`mes.db-shm`、三份 appsettings 和旧 stdout/stderr 一并复制到 `artifacts/result-retention-switch-20260923-102200/backup/`，逐个 SHA256 比对一致。SQLite 回退时必须使用这组一致备份，不可只取主库并混用新 WAL。
- 新 PID `13012` 运行 `artifacts/result-retention-release-20260923-101930/MesControlAgv.Mes.dll`；DLL SHA256：`8E641635E765B7C0880DF4785FF4058B26745508446C21905F0B813E229CC90E`。
- 保持原 contentRoot `artifacts/dcseq-auth-probe/runtime`、环境 `SingleChannel`、HTTP `127.0.0.1:5145`、TCP `192.168.10.11:5500`、认证回复 `Result`、超时 60 秒及禁用的执行 worker 配置。
- 新日志为 `artifacts/result-retention-switch-20260923-102200/mes.stdout.log` 和 `mes.stderr.log`；旧 stdout 保留为 211513264 字节，没有覆盖。检查时新 stderr 为空。
- `push-evidence?limit=10` 返回 HTTP 200、空数组，数据库新表及字段可读；尚无新 Result，空数组不代表留存失败。
- 客户端 `192.168.10.108:62603` 自动重连，连接标识 `24b4956e323742988871967db4d29e74`。收到认证 `7508350160519434240` 并沿用 `body.result=Success` 回复；随后持续 Device 上报，最新 `status=0`、`Idle`。同一连接保持超过 30 秒，端口均由 PID 13012 独占。
- 重连初始还收到两条客户端 Command 报文：`2401a849ba4e4322b4a6a6b45827d48d` / Failed、`a9c8b3c3489848de8914f356836f100c` / Success。本轮没有发送对应命令，不能归因于本轮启动/停止操作；原文在新日志保留，历史归属待核实。
- 本轮只重启监听并回复客户端认证，没有下发 Config、Command、Init。现在可由操作员仅运行目标测试行，再核对完整 Result 与该次 DA 结果。
