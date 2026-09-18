# 样品核对现场验收与合并接续记录（2026-09-18）

## 当前结论

待厂家更新后进行真实验收，尚未发送本轮设备启动请求，尚未合并。
用户已授权完成真实验收后合入日常开发分支，并明确目标为
`feature/wpf-ui-layout-optimization`，不是 `master`。
验收与合并授权继续有效；恢复时先核对现场就绪和本轮真实样品信息。

## 版本与工作区

- 源工作区：`D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`。
- 源分支：`feature/sample-workstation-http-readonly`。
- 本轮构建代码：`3bb9a4e3fd5ad391f840d3cb30a8d67cac1f8819`。
- 目标分支当前提交：`201dbb4c0424565affcc7e5e6db40e2254f44ece`。
- 目标工作区：`D:\Project\Github\Mes`，存在未提交的 WPF / 数字孪生代码及资料；须保留。
- 已执行 `git fetch origin`；远程默认分支仍为 `origin/master`，其版本为 `ba47a99`，不是本轮合并目标。
- `git merge-tree --write-tree --name-only HEAD feature/wpf-ui-layout-optimization` 预检返回冲突：36 个文件，涵盖工作站 Adapter/契约、工作流执行、MES 和 WPF。
- 预检仅生成 Git 对象，未变更分支、索引或工作区。实际合并前须重新读取目标最新提交和未提交状态。

## 现场只读检查（2026-09-18 08:11 左右）

- Adapter：`127.0.0.1:15041`，PID `15244`。
- MES：`127.0.0.1:15045`，PID `5996`，仍运行旧的 `mes-optional-manual-gate-runtime` 版本。
- Adapter 的工作站 status、errors、`TEST-001/state` 三个 GET 均返回 HTTP 503。
- MES `GET /api/schedule` 返回 200；上一轮任务 Completed，`activeLeases=[]`。
- MES `GET /api/experiment-samples` 返回 404，现场旧进程尚未升级到样品核对版本。
- 本轮没有启动 WPF，没有重启 MES/Adapter，没有写入现场数据库或发送设备命令。
- 用户明确说明真实验收需等待厂家更新软件。

历史数据位置：`C:\Users\33206\AppData\Local\Temp\mes-workstation-formal-20260916-130610\mes.db`。
检查时数据库有 WAL/SHM；升级备份须使用 SQLite 一致性备份，或确认无活动运行并停稳 MES 后复制，不能只复制运行中的主 db 文件。

## 已准备的程序

- 独立构建目录：`C:\Users\33206\AppData\Local\Temp\mes-sample-field-20260918-build`。
- MES 发布目录：`C:\Users\33206\AppData\Local\Temp\mes-sample-field-20260918-runtime\mes`。
- WPF 发布目录：`C:\Users\33206\AppData\Local\Temp\mes-sample-field-20260918-runtime\wpf`。
- MES DLL SHA-256：`59B5F9EBD875D11477CB4BC700E7E07BE58C5358ED6F8DD4C5BC7878A4293563`。
- WPF DLL SHA-256：`19A87EF893894665254EF5539DDB193C01506DDC16C7D863656A76383B6AB6DB`。

MES Release publish 成功。WPF 首次发布因独立输出目录未还原隐式本地服务项目失败；随后完成 solution restore，再以
`SkipLocalServiceBuild=true`、`SkipLocalServiceCopy=true` 成功发布 WPF。
本验收包使用外部 MES/Adapter，不携带自动托管的本地服务，启动时显式设置：

```text
WPF_RUNTIME_MODE=physical
WPF_MANAGE_LOCAL_SERVICES=false
WPF_MANAGE_LOCAL_MES=false
MES_BASE_URL=http://127.0.0.1:15045/
ADAPTER_BASE_URL=http://127.0.0.1:15041/
```

本轮只做构建准备，没有重跑已通过的功能测试，也没有把发布成功记作真实验收。
既有代码验证结果见最终修复报告：MES 352/352、WPF 定向 38/38、WorkflowContract 74/74。

## 厂家更新后的执行顺序

1. 记录厂家程序版本/文件校验值，复核目标 `192.168.200.157:8082`、设备 `CYC-001-1000`、任务 `TEST-001`。确认现场无人调试，设备状态可读且适合执行。
2. 核对实际瓶身条码、位置、顺序与厂家现有任务；录入中控的数据必须由现场提供/核对。当前没有任务表上传能力，不能把中控核对状态当作仪器已接收该表。
3. 复查当前 MES 无活动任务/租约，完成数据库一致性备份；替换为已准备的新 MES。先关闭工作站执行 worker 验证迁移/接口及历史数据，再在本轮单次真机验收就绪时启用。保留旧程序和数据库备份以便回退。
4. 打开新版 WPF；建立本轮独立批次/任务，检查未核对时不可准入、保存后的行信息、页内无弹窗核对、未保存身份编辑阻断及保存变化后失效。人工目视核对必须由现场操作员完成。
5. 就绪后经正式任务排程的运行准入发送一次启动，记录本轮 request/job/schedule/run/node/operation/verification ID、revision 和 hash。观察本轮 Running，随后 Completed、设备 Idle、错误码 0、单次设备操作及租约释放。请求不自动重试。
6. 将 WPF 现场反馈、厂家状态和 MES 审计/时间线对应归档。真实验收通过后才开始合并；异常或 Unknown 时先查明，不以旧 Completed 判成功。
7. 重新读取 `feature/wpf-ui-layout-optimization` 最新提交，在隔离整合环境逐项解决冲突，保留目标已有能力及未提交改动。尤其保留本工作站链路“已有任务、单次启动、Running 证据、只读恢复”语义。
8. 对最终整合版本执行相关测试和构建，复核冲突涉及的工作站执行链路；若整合改变已验收行为，应补相应真实验证，再完成目标分支合并和交接。

## 接续入口

- [正式工作流交接](../SAMPLE-WORKSTATION-FORMAL-WORKFLOW-HANDOFF-2026-09-16.md)
- [中控集成说明](../SAMPLE-WORKSTATION-CENTRAL-INTEGRATION.md)
- [样品核对离线验收](2026-09-17-sample-verification-acceptance.md)

下一步待厂家软件更新与现场样品信息就绪。无需重新确认合并目标；用户已确定为日常开发分支。
