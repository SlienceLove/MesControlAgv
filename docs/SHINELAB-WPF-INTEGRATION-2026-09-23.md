# 离子色谱WPF接入记录（2026-09-23）

## 范围与当前状态

- 分支：`feature/shinelab-dcseq-protocol`；工作目录：`D:\Project\Github\Mes\.worktrees\shinelab-dcseq`。
- 已实现并发布独立验证版WPF，“任务与设备 → 离子色谱控制”为操作入口。未合入develop/master或覆盖另一会话的UI版本。
- 接入现有MES HTTP `http://127.0.0.1:5145/`，TCP `192.168.10.11:5500`，设备`STN61_01`，单通道`DcSeqSingleChannel`。
- 15:19只读复查：设备在线、Idle、无活动任务；MES PID21128、WPF PID14796仍运行。该状态仅为检查时快照。

## 已提供功能

1. 状态与托盘：显示监听及设备状态；弹出/复位分别确认，执行前刷新设备状态，不自动重试；Success只表明应答，实际到位仍需现场确认。
2. 导入与单条预检：本机CSV/XLSX解析；准备选中行仅创建中控任务及唯一通讯sampleId、展示实际Config预览，不发送给仪器；单独确认后才发送一条Config。不使用RPA或共享目录。
3. 任务与检测结果：显示源编号与通讯编号映射、结果及冲突信息，保留原始任务详情。结果收齐不自动等于整个任务正常完成。

示例文件：`res/DCSeq-SingleChannel-Wpf-Test.csv`，也已复制到发布目录。示例使用源编号STD-25、未知样品type=0、position=1、mPos=1、25uL、处理方法09220922_TCP_TEST。方法名称和体积仅作核对元数据，当前Config并不发送这些字段；须在AQ确认实际设置。

单通道不填写channel；当前仅允许未知样品、循环次数1。Config会修改AQ上次运行序列，且运行队列可能自动处理新行，发送前必须核对序列及勾选状态。本页面未提供自动启动进样。

## 验证证据

- 新页面相关14项测试通过；WPF全量638/638、MES全量681/681通过。
- TRX：`artifacts/wpf-control-verification-20260923/wpf-control-ui.trx`、`mes-ui-contracts.trx`。
- 用户明确确认15:00:35和15:00:41在新页面手动点击托盘弹出、复位；两次客户端均Success，审计ReplyWithinWait，无冲突。
- 两条strID分别为`c8d47d8c55624c84a795bb66fc5d2c59`、`1d6a5347d79741bdb6fe7ea437ba2e4a`。这些不是自动检查动作。
- WPF导入、Config及结果展示已具备实现与离线测试；本轮尚未完成它们的全套现场UI验收，不把之前API联调证据算作页面验收。

## 发布及启动

发布目录：`artifacts/wpf-shinelab-control-20260923-145900/`。

WPF DLL SHA256：`7ED30CD76D3543739F1F3CFE24BFD9DA7AFE1DEA92C322C07C73FB0DAAE283FC`。

从本worktree启动（已有窗口时无需重复启动）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Start-ShineLabControl.ps1 -PublishedDirectory .\artifacts\wpf-shinelab-control-20260923-145900
```

脚本只设置子进程环境，连接已有MES，不管理或重复启动现场服务；不改变用户/机器执行策略。当前MES继续使用`artifacts/tray-command-release-20260923-142744/`版本。

独立验证窗口未接入真实AGV Adapter，使用空闲回环端口15999；平台通用AGV状态/就绪监督可能显示不可用，不能据此判断离子色谱MES断线。离子色谱页面及其HTTP/TCP状态应分别检查；正式整合时需与集成会话协调统一服务地址和状态提示。

## 下一步验收顺序

1. 在页面导入示例CSV，核对解析行；点击“准备选中行（不发送）”，验证源编号、生成通讯编号与预览一致。此步骤只改变中控任务记录，不改变仪器。
2. AQ空闲并核对序列后，人工确认发送单条Config；核对Success、实际新增行和通讯sampleId，避免重复创建。
3. 使用已验证的方法运行一条样品，检查同一通讯sampleId的生命周期和Result是否在页面关联正确；启动仍按现场确认执行。
4. 由指定集成会话将本分支成果合入develop验证，再进入统一中控版本；本记录不构成合并、推送或额外现场动作授权。

## 现场页面导入及Config验证（15:40—15:45）

用户从页面导入示例CSV，解析字段与源表一致；15:40:51准备任务：

- taskUuid：`wpf-dcseq-20260923-154051-dc4f9655c85f4bfab9b54bae7ad29d87`。
- 源编号STD-25 → 通讯编号`322979395`；样品`STD-25_WPF_TEST`。
- 准备后后台Created、ConfigAttempted=false、命令审计为空，符合“不发送”约定。
- 15:45:27用户刷新后确认发送一次Config，strID=`ac57ef8308db4a6da9579607dfbb0ae6`；写入至应答约303ms，Success、Matched、ReplyWithinWait，无冲突。
- 中控状态Configured / ConfigAcceptedAwaitingVerification；用户截图显示AQ新增同编号同名样品，新增行未勾选。该阶段尚未启动或收到结果。

## 缓存过期点击无反应修复

现场发现：准备后等待超过60秒，CanExecute使用页面缓存的FreshIdle直接拒绝执行，导致无法进入内部刷新，也没有确认弹窗或错误提示。用户手动刷新后发送成功，后台记录与此一致。

最小修复将“允许开始预检”与“允许实际发送”分离：Config按钮不再用缓存时间作为点击入口条件；托盘按钮不再因缓存时间流逝拒绝预检。配置能力、忙碌标志、任务已准备和不重复下发等限制保留。确认前后依然分别读取最新状态，要求新鲜在线Idle才能发送；未发送的预检失败与发送后结果未知采用不同提示。没有添加自动重试、自动进样或周期性发命令。

新增11个回归用例，页面相关25/25、WPF全量649/649通过；TRX位于`artifacts/wpf-preflight-verification-20260923/`。本轮未改MES逻辑，未重跑MES全量；此前681/681是前一检查点的结果。

修复版发布目录：`artifacts/wpf-preflight-fix-20260923-155500/`（目录是发布标识，不用作现场动作时间）。WPF DLL SHA256：`E2377F95446F53A26739B6DD98BDDD213003814542DABBE137B33A9D94E46BB7`。

已正常关闭本会话旧WPF PID14796，启动修复版PID40904；MES PID21128未重启。15:53复查设备仍在线Idle，设备发送审计总数仍6，当前任务仍只有上述一次Config；切换未发送任何设备命令。旧发布目录和现场记录保留。新窗口的导入缓存不会跨进程保留，已下发任务请在“任务与检测结果”查看，不要为同一现场样品重新导入并发送。

下一轮使用现有编号322979395验证运行及Result关联；实验方法和现场条件需核对，启动另行确认。缓存过期修复已经过离线回归和新窗口启动检查，尚未再次用现场Config做重复验证，以免额外新增样品。
