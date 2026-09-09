# YhLoop TCP 原生帧接入实施计划

关联设计：[2026-09-09-yhlloop-tcp-framing-design.md](../designs/2026-09-09-yhlloop-tcp-framing-design.md)

## 全局约束

- 生产 TCP framing 必须是 `55 AA + 2 字节大端 length + UTF-8 JSON + 00`。
- `length = JSON UTF-8 字节数 + 1`，不能按字符数计算。
- 不发送任何未经厂商确认的 YhLoop/IRAY 业务控制报文。
- 认证响应只能在收到合法 `Certification` 后生成；诊断探测默认关闭。
- 现场网线断开期间只做本机单元测试、回放和构建。
- 不把换行 JSON 作为生产兼容协议；旧 fixture 必须显式标记为 legacy。
- 不修改与本任务无关的现有工作区文件。

## Task 1：共享原生帧编解码

文件范围：`src/MesControlAgv.Contracts` 与对应测试。

新增无网络依赖的消息/帧 codec 和增量 reader，包含严格 UTF-8、最大长度、
半包/粘包、前导噪声重同步、缺少 NUL 和无效长度处理。测试覆盖 ASCII、中文、
空 body、无换行完整帧和错误帧恢复。

## Task 2：MES Server 与连接管理器迁移

文件范围：
`src/MesControlAgv.Mes/Services/ShineLabTcpServer.cs`、
`ShineLabConnectionManager.cs`、`ShineLabTcpOptions.cs`。

用 `NetworkStream.ReadAsync` 加增量 reader 替换 `ReadLineAsync`；连接管理器改
用原生帧 writer，并保留现有状态 hub、任务事件和响应关联。认证回复、空闲超时、
连接替换、异常清理和摘要日志均覆盖测试。

## Task 3：MES 测试迁移与回放矩阵

文件范围：`tests/MesControlAgv.Mes.Tests/ShineLabTcpServerTests.cs` 及新增
codec 测试。

把现有换行测试改为原生帧；增加半包、粘包、坏长度、旧换行拒绝、认证闭环、
状态上报、命令关联和空闲超时用例。测试不得访问真实串口或现场 IP。

## Task 4：dispatcher 路径隔离/迁移

文件范围：`src/MesControlAgv.ShineLabDispatcher/ShineLabTcpProtocol.cs` 及其
直接测试/文档引用。

优先复用共享 codec，使真实客户端路径不再写 LF；若该旧路径没有生产调用者，
保留 API 兼容但明确标记 legacy，并增加原生帧读写测试，防止现场误用旧 framing。

## Task 5：离线验证与交付记录

运行定向测试、MES 全量测试、解决方案构建，检查发布配置仍监听 5500 且默认
关闭自动诊断探测。更新协议文档说明“协议表换行描述”和 DLL 原生 framing 的
差异，并记录现场联调前只允许被动认证观察。

