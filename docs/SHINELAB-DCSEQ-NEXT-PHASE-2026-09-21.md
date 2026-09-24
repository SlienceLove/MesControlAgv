# DCSeq 状态与闭环下一阶段（2026-09-21）

## 范围与验收

用户已同意按“证据整理 → 离线修正 → 讨论版协议对照 → 单样品受控验证 → 批量/WPF闭环”推进。
`res/鲲达自动化-物联系统-对接工作安排-讨论版.docx`已列出`SampleFinish`、`TaskFinish`、`Result`、`EndMission`、`TaskError`等客户端上报消息，
并将`Command action=2`列为停止动作；但停止当前样品还是整个序列，仍必须通过现场受控实测确认。
本轮离线阶段不发送Config、Command、Init，不重启现场服务，不修改现场数据库。
工作分支`feature/shinelab-dcseq-protocol`，目录`.worktrees/shinelab-dcseq`。

## 已实现的最小修正（尚未部署）

- DCSeq/DcSeqSingleChannel的当前状态由Device/UpdateInfo中的明确status驱动，缺失状态不推断；
  0=Idle、1=Running、2=Error、99=InstrumentOffline。TCP在线不等于仪器在线。
- 完成/结果/错误事件通过`lastProtocolEvent`单独保留方法、strID、sampleId、MES接收时间和仪器finishDate；
  不直接改成Completed、100%或填TaskFinishedAtUtc。当前Device不携带的样品/阶段信息不沿用旧完成事件。
  明确TaskError仍显示故障、保留错误内容，但不据此宣称已停止或最终失败闭环。
- 新表`ShineLabPushEvidence`保存DCSeq生命周期、结果及报警的原始body、请求ID、关联结论、重复标记与接收UTC ticks。
  高频Device/UpdateInfo不全量写入该表。旧数据库启动时仅补建新表和索引，不重算旧任务。
- 新只读接口：`GET /api/shinelab/devices/{equipmentCode}/push-evidence?limit=100`，按接收时间倒序，最多500条。
- 只有明确task_uuid且equipmentCode匹配才关联任务；空编号、只有sampleId、未知task_uuid及设备冲突都留证，
  不猜测归属。当前真实DCSeq上报只有sampleId，因此仍留在未关联证据里，不冒充已实现任务闭环。
- 同一设备/方法/finishDate/规范化body的成对完成事件（即使strID不同）仅生成一次任务观察记录，
  原始两条证据都保留。没有finishDate时只对相同strID和body去重；没有二者不作去重猜测。
  这是接收顺序下的重复过滤，不是跨连接并发事务的exactly-once承诺。
- DCSeq Config成功仍兼容保留Configured，但stage为ConfigAcceptedAwaitingVerification；
  该状态不证明目标序列正确或样品创建成功。WPF提示明确需核对，且运行中追加可能自动执行。
- 修正停止动作2误按1处理的问题；停止/清洗应答不填写启动时间；DCSeq启动应答也仅为Accepted。
- 旧Rike的完成协议行为保留；跨设备task_uuid隔离修正适用于全部模式。

## 当前仍未完成

1. 讨论版已确认这些是客户端上报/中控下发的协议方向，但在实测前不自动将SampleFinish、TaskFinish或EndMission推进为整单完成；手动停止也不凭Idle推断。
2. 不根据重复使用的数字sampleId自动关联；需要厂家回传唯一请求/任务关联，或确认可稳定携带的唯一样品标识。
3. 未增加人工“强制完成”接口、补发/自动重试、批量投递或动作指令。
4. WPF导入目前仍有旧Rike通道必填/预览路径，尚未适配DCSeq单通道；不得用于宣称DCSeq全界面验收完成。
5. push证据仅在新版实际部署后开始积累；此前原始证据仍在现场日志，不做自动导入。
6. 2026-09-22实测确认AQ将Config追加的`type=0~5`均显示为“未知样品”且自动勾选；在厂家确认前不再追加类型行，也不启动当前已勾选队列。
7. 厂家已确认当前DCSeq版本统一按“未知样品”处理；类型映射属于客户端版本能力，后续以未知样品完成通讯闭环即可。
8. 2026-09-22基线测试中AQ显示“终止”。2026-09-23复核纠正：此前引用的Success对应启动请求，不能作为停止请求的应答证据，亦不能确认所谓“迟到停止Success”。厂家确认action=2停止整个序列；现场仅启用一条样品，不足以独立验证后续队列行为。
9. 同日正常运行一条基线：09:27:21收到成对`SampleFinish(sampleId=6)`，09:42:22收到成对`TaskFinish(sampleId=6)`，之后Device=0/Completed；未收到Result或EndMission。顺序已留证，但单样品不足以证明TaskFinish的序列级语义。
10. 厂家确认Result触发前提是AQ提前选择处理方法；本次STD-25未选处理方法，因此无Result属于预期。下一阶段先验证“已选处理方法 → Result”链路。
11. 处理方法`0922`选中后复测：SampleFinish、TaskFinish、Result均出现4条重复帧；Result包含`sampleId=6`、`sampleName=STD-25`、`compounds=[]`，MES投影为ResultAvailable。空数组仅表示上报的组分数组为空，不能据此断言色谱图没有峰。
12. 厂家补充注意事项：需先建立/选择有效校准曲线，Result才会有可用检测记录；操作手册需同时说明处理方法、曲线和样品类型的前置关系。
13. 厂家确认托盘弹出/复位尚未加入DCSeq通讯协议；AQ按钮属于仪器本地操作，不产生TCP报文，MES当前不能远程控制或验证该动作。

## 本轮离线验证结果

以下为早期检查点；2026-09-23完整Result留存修复及下一条样品验证安排见[专项记录](SHINELAB-RESULT-RETENTION-2026-09-23.md)。

- MES全量：619/619通过（包含原有测试和新增16项DCSeq回放/留证/数据库升级用例）。
- WPF ShineLab相关：74/74通过；不是WPF全量测试，也不表示DCSeq单通道界面已贯通。
- SQLite旧库升级测试：保留既有任务、补建证据表、保存重复/未关联事件、只读接口排序及条数限制均通过。
- 回放覆盖：空编号基线、SampleFinish后仍Running、随后Idle、仪器99与TCP断开区分、重复不同strID、
  同sampleId多任务不猜归属、跨设备拒绝关联、停止动作2、Success不等于开始、故障不等于终止。
- `git diff --check`通过；改动尚未提交、未合并、未部署。没有向现场设备发送测试动作。

## 给厂家的微信询问（可直接复制）

今天新模块认证、Device上报和Config追加已跑通，单通道不带channel。还有几个闭环点麻烦确认：

1. Config目前追加到“上次运行序列”，打开新序列方法不生效；运行中追加还会自动接着运行。能否明确指定/返回目标序列ID，并提供“仅添加不自动运行”的方式？此前出现Success但没有样品，麻烦检查AddTask是否正确传递AutoCreateTask的失败及msg。
2. 基线结束收到两次TaskFinish，sampleId为空；编号6分析过程中已收到两次SampleFinish。请明确SampleFinish、TaskFinish、EndMission分别代表进样动作、单样品分析还是整个序列结束；正常结束、手动终止、故障分别上报什么？重复两次是否正常？
3. 上报能否回传Config请求ID/中控任务ID，以及稳定的sampleId、序列ID？目前空ID或重复数字编号无法可靠归属任务。
4. Command的0、1、2、7分别是什么动作？停止2针对当前样品还是整个队列，会不会继续下一条？回复Success只是接受，还是动作执行结束？
5. Result什么时候发送，终止是否有结果，是否带同一样品/序列ID？断线重连能否补发或查询最终状态？仪器时间的时区能否明确？

## 后续执行顺序

1. 本轮完成离线回放、MES全量和受影响WPF测试，保留未部署状态。
2. 由操作员在AQ中删除或取消当前自动勾选的测试行，只保留一条安全的未知样品测试行；确认序列处于停止状态。
3. Result链路已验证；下一轮只需用厂家确认会产生峰的标准样确认`compounds`非空，并继续核对重复帧去重和结果持久化，不重复action=2或type映射测试。
   需要点击连接前通知用户。不把“用户曾允许空位试跑”视为当前批量自动执行授权。
4. 对齐Config实际落地、样品开始、停止/正常结束、Result四类证据后，才扩展批量及WPF导入。
