# DCSeq 发送审计、迟到应答关联与状态恢复

## 现场依据及纠正

13:15:03.985 客户端关闭原 TCP 连接；13:15:07.905 重连后，在认证前补来两条 Config Success：

- 13:15:07.917，strID=`ea9cd7c101a8485aa47812d6ca4dbcb8`。
- 13:15:07.963，strID=`d9a01cd28fd6444189a44e6e0ea64667`。
- body 均为 `{"channel":"","result":"Success","msg":""}`，原文位于 `artifacts/sample-correlation-switch-20260923-112856/mes.stdout.log`。

用户补充此前关闭了仪器通讯。证据应描述为“重连后收到迟到应答”，不再笼统说“客户端不回复”。客户端是否存在积压/刷新条件，仍需厂家确认。旧发送路径没有落库随机strID，**不能按到达顺序把两条Success强配给两次Config**；已有sampleId关联及原始结果不受此限制。

## 本轮实现（与现场运行版本分开）

1. `ShineLabCommandAttempts` 在写入TCP前保存请求strID、方法、设备、托管任务、action、完整JSON、连接及准备时间。记录失败则不发送。写入完成时间与等待结果分别更新，`Prepared`不能冒充实际送达，`Sent`也不等于客户端已接受。
2. `ShineLabCommandReplies` 保存每条Config/Command回包原文、接收连接、时间及匹配结论，包括未知请求、设备/方法不符、格式异常、重复和冲突。只接受已支持的body.result字符串Success/Failed作为明确应答，不把未知值当成功。
3. 严格用请求strID＋方法＋设备匹配；允许重连后的连接标识不同。进程重启不丢请求映射，HTTP等待超时不删除持久化记录。不会自动重发Config、Command或Init。
4. 同一请求的重复应答不重复写任务接受事件；内容冲突保留首次应答和全部原文，转人工核对。旧请求不能覆盖该任务较新的请求状态。
5. Config迟到Success只在仍处于该次配置阶段时恢复Configured；不倒退Running、Error、Stopping或已收齐结果的状态。Command成功只代表接受/停止请求待观察，不声称动作完成。
6. 唯一sampleId的上报已证明执行时，可解除“Config超时导致的未知”；全部结果状态为`AwaitingCompletion / AllResultsReceivedAwaitingCompletion`，没有自动Completed，也不清除其他故障或停止不确定性。历史超时事件保留。
7. 若发生“回包已入库、任务状态还未更新”的进程中断，启动时仅补应用当前请求尚未应用的已存应答，不重发、不用曾经接受的历史应答猜测设备现在是否仍运行。
8. 旧库补表和两个可空字段`ConfigRequestId / CommandRequestId`，不伪造历史发送strID、不删除样品映射或检测结果。

进样编号关联已由样品605734568现场验证；本轮迟到应答恢复是新增逻辑，其现场验收须在切换新版后另做，不能把离线通过混称为全部实机完成。

## 只读查询

```text
GET /api/shinelab/tasks/{taskUuid}/command-audit
GET /api/shinelab/devices/{equipmentCode}/command-replies?limit=100
GET /api/shinelab/devices/{equipmentCode}/push-evidence?taskUuid={taskUuid}&method=Result&limit=100
```

任务审计最多返回最近100次请求、500条回复；设备回复查询包含未知请求。push-evidence先筛选再取limit，避免报警刷屏挤出目标任务Result。数据库原记录不因此删除。

## 验证与边界

`ShineLabCommandJournalTests`新增18项，覆盖Config/启动/停止应答超时、重启迟到回包、真实回环TCP断开重连后在认证前补回Config、错误设备/方法/编号、格式不符、重复和冲突、旧请求隔离、结果先到后应答、状态不倒退、取消等待、审计失败不发送、旧库升级、已存应答未应用的恢复，以及报警超过500条后的结果筛选。原连接管理测试明确校验异常携带的strID与发送帧一致，未放宽断线隔离要求。

- 本轮没有修改现场数据库、替换PID38020或厂家DLL，也没有下发新样品/仪器命令。
- 之前两条缺发送记录的迟到回包无法自动追溯；它们的原文继续留存，任务不强制改为成功。
- 不承诺多个MES实例同时操作同一仪器的跨进程调度；当前仍要求单监听进程。
- WPF单通道导入界面和正常/终止/整序列结束语义仍是独立未完成项。
- 托盘弹出/复位仍等待厂家新版及明确协议值，禁止猜测action或把现有0/2/7复用为托盘动作。

## 下一步顺序

13:35检查点：MES全量 **661/661**、WPF ShineLab相关 **74/74** 通过，`git diff --check`通过。结果位于 `artifacts/command-journal-verification-20260923/mes-command-journal-release.trx` 和 `wpf-shinelab.trx`。
独立Debug发布包：`artifacts/command-journal-release-20260923-133545/`。
MES DLL SHA256：`B863E7EC093D9C61AD82EE8A6BE944492BF491249110BDD5CE9474B5337CCBD2`。
尚未部署、提交、合并或推送；现场13:35只读状态为online=true、status=0、Idle。

1. 确认现场空闲，保留当前主库/WAL/SHM、配置、旧程序和日志后，切换独立发布目录；新日志必须使用唯一文件名。
2. 先核对认证/Device/历史结果查询与新增审计接口。若需要现场应答复测，仅一次明确的Config测试，用户确认AQ新增行并取消勾选，不要求再跑25分钟样品。
3. 再由用户替换厂家程序，先核对版本和新协议，离线适配托盘动作后，再逐次实测弹出、到位反馈、复位。接受应答与实际位置分开验收。

## 已部署及一次正常应答实测通过（13:37—13:39）

用户明确要求“切换后做一次应答验证”。发送前及切换前均确认online=true、status=0、Idle；未启动进样。

- 核对并结束旧PID38020，新PID **35752** 运行 `command-journal-release-20260923-133545`；DLL哈希与上述已验证发布包一致。
- 停写后将数据库主库/WAL/SHM、三份配置、此前监听stdout/stderr复制到 `artifacts/command-journal-switch-20260923-133749/backup/`，逐项SHA256一致。旧程序及原日志不覆盖，其他设备进程不动。
- 新日志：`artifacts/command-journal-switch-20260923-133749/mes.stdout.log`、`mes.stderr.log`。沿用SingleChannel、192.168.10.11:5500、127.0.0.1:5145、认证Result和60秒连接超时。
- 客户端192.168.10.108:63022自动连接，connectionId=`a4ed4cfeb39946c2a9a460c8e19dbd65`，认证后Device正常。旧任务605734568的4条完整Result仍可通过任务/方法筛选查询，关联MesSampleId不变；旧任务没有虚构发送审计。
- 新任务 **DCSEQ-ACKCHECK-20260923-03**，sampleId **986140724**，名称 **STD-25_AckCheck_03**；创建请求保存在本switch目录的`task-create.json`。
- 只发送一次Config。完整发送JSON：

```json
{"strID":"2de7cf48e8934fd1a1ee6e7e95d4dadd","strMethod":"Config","equipmentCode":"STN61_01","body":{"sampleData":[{"sampleId":"986140724","sampleName":"STD-25_AckCheck_03","type":0,"position":1,"mPos":"1"}]}}
```

完整实收JSON：

```json
{"strID":"2de7cf48e8934fd1a1ee6e7e95d4dadd","strMethod":"Config","equipmentCode":"STN61_01","strCode":"","body":{"channel":"","result":"Success","msg":""}}
```

- 请求准备13:38:57.8664057，写入完成13:38:57.9539846，回复接收13:38:58.2620960（北京时间）；写入完成到接收约308 ms。
- 审计1次请求、1条回包，`waitOutcome=ReplyWithinWait`、`correlation=Matched`、`responseConflict=false`，两端strID/方法/设备一致且成功关联上述任务。
- 任务`Configured / ConfigAcceptedAwaitingVerification`、lastError=null、startedAtUtc/completedAtUtc均null；状态只证明应答接受，新增行实际落地仍待用户核对。
- 13:38:58设备仍Idle，未发送Command/Init。本轮通过正常应答及持久化关联的实机验证；迟到/重启恢复仍以离线回放通过为准，本轮未人为制造新的超时。
- 可以进入厂家新版准备阶段；更新/托盘试验前先确认新增及其他待运行测试行取消勾选、无运行样品，再取得新动作定义。不得把本次通过宣称为厂家发送队列问题已根治。
