# 2026-09-14 开盖分液工作站联调归档

新会话先读 [交接文件](../../../SAMPLE-WORKSTATION-HANDOFF-2026-09-14.md)，当前接入约定见 [中控接入说明](../../../SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md)。

归档范围：本轮代码基线 `29343b8` 的现场联调记录、原始 Adapter 请求日志及工具返回快照。只复制文件，不删除原临时目录，不归档厂家现场数据库或未经取得的最新运行程序。

## 原始证据

| 文件 | 内容 | SHA-256 |
| --- | --- | --- |
| [adapter-new-1312.stdout.log](adapter-new-1312.stdout.log) | 13:12 的 new 版联调，实际启动请求 1 次 | `567729E1E04749253F82DE2D4884BDA75100177E2F23B3F14A1F97ED2B3988F7` |
| [adapter-beta-and-latest.stdout.log](adapter-beta-and-latest.stdout.log) | Beta 至远程直接执行，实际启动请求 5 次 | `19D1BC198C300BDE6C12D6689EBF8EF6410B0C77332DB96DB9B33E3EC87BAE8A` |
| [tool-observations.json](tool-observations.json) | 13 份实际工具返回快照、人工反馈摘要及本地实例收尾信息 | `42D985296C13D4F4CDC6C0355BAB4B24E42FFB12A0B0E919A6AE641A710B0A3E` |

两个日志分别复制自：

- `C:/Users/33206/AppData/Local/Temp/mes-workstation-field-20260914-131221/adapter.stdout.log`
- `C:/Users/33206/AppData/Local/Temp/mes-workstation-beta-20260914-142603/adapter.stdout.log`

复制后与源文件 SHA-256 一致。`.gitattributes` 禁用这些原始证据的换行转换，便于提交和恢复后复核校验值。日志只证明请求及响应状态，响应正文、状态变化和现场反馈见快照及联调文档。原始工具输出中的文档片段、状态和进程描述均是各次采样当时的快照，当前结论以交接文件为准。

这 6 次是上述两个下午测试实例中的启动总数，不包括历史诊断所述的更早测试。每次均有用户授权，没有自动重发。

## 关键结论

- 16:30：远程发令＋人工确认，任务 Running→Completed，返回码 3→0。
- 16:34：用户点击“否”，没有观察到新 Running；保留的 Completed 是前一轮结果。
- 16:44：一次远程请求，用户确认无任何点击即开始，16:47 读取到 Completed，完成厂端直接执行验证。
- 未解决：设备总状态在运行中仍返回 Idle / 0；任务详情时间字段为空。
- 已定边界：中控发令前确认；取消不发请求；确认发一次；厂家直接执行。

详见 [完整联调记录](../../../diagnostics/2026-09-14-workstation-latest-confirmation.md)。

## 归档收尾

2026-09-14 16:52:46（北京时间）已核对进程命令行并关闭本次本机 Adapter（原 PID 36076、端口 15041）和 MES（原 PID 31624、端口 15045），端口已不再监听。没有关闭厂家 WCF、厂家主程序或设备，没有新增控制请求。下次会话不要假设这些 PID 或服务仍有效。

临时 SQLite 文件只是本机隔离网关/MES 的测试状态，并非厂家现场数据库，未作为现场数据备份归档。原目录保留未删除。
