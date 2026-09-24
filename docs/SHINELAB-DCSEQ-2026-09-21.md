# DCSeq 协议适配及现场认证记录（2026-09-21）

## 范围与分支

- 工作目录：`D:\Project\Github\Mes\.worktrees\shinelab-dcseq`
- 分支：`feature/shinelab-dcseq-protocol`，基于 `develop/ad2d958`。
- 最新厂家文件：主工作目录 `res/下游表协议 - 序列进样(3).docx`。
- 文件 SHA256：`1F356ACF92409314CC808A0AB41B4C54351CA3952A95DADEA49D7D55A7EE8998`。
- 本轮完成 Config/Command 格式适配和无设备写入的预览；不是完整进样闭环验收。

## 当前现场实例

- MES PID：8900；启动程序集版本：`1.0.0+ad2d95873ab1f4599d785448aa853723c20a624e`。
- TCP：`192.168.10.11:5500`；本机 HTTP：`127.0.0.1:5145`。
- 独立数据和日志：主工作目录 `artifacts/ion-chromatography/dcseq-20260921-130921/`。
- 首次联调临时设置 `StaleAfterSeconds=60`；命令超时仍为10秒。
- 13:26 检查：`192.168.10.108:53021` 仍保持同一条 Established 连接。
- DCSeq 以约0.5秒间隔发送122字节 LF-JSON Certification，设备编号 `STN61_01`。
- 中控每次回显请求 strID、strMethod、equipmentCode，body 为 `{"result":"Success","msg":""}`；没有 strCode 字段。
- 尚未观察到 Device、Heart、BindModule；仅能证明TCP和解析链路正常，不能证明客户端认证通过。
- 接口中的 `Connected/status=0` 是认证处理写入的本地状态，不是仪器实际空闲上报。
- 另有一条无报文辅助连接60秒超时，与持续认证的连接不同。
- 本轮没有发送 Init、Config、Command，没有重启现场实例。

## 已实现的离线适配

`ShineLabTcp:PayloadProfile` 可选择 `Rike`（默认）或 `DcSeq`。新增模式必须显式启用，未知模式启动验证失败；不依据设备编号猜测协议。

DCSeq Config body：

```json
{
  "channel": "A",
  "sampleData": [
    { "sampleId": "S1", "sampleName": "sample", "type": 0, "position": 1, "mPos": "1" }
  ]
}
```

- 与旧版区别：`chan` → `channel`；`sampleID` → `sampleId`；移除样品内 `Channel`。
- 本地任务、方法和体积元数据不附加到厂家报文。
- 拒绝单条 Config 混合通道、空/重复 sampleId 和超出0至5的样品类型。
- Command body 为 `{"channel":"A","action":0}`；2停止、7清洗沿用。
- action=1 因厂家描述含糊仍被拒绝；Init 尚未实现下发，不随连接或配置自动发送。
- 旧 Rike 的默认报文格式保持不变。

离线预览接口（POST，仅构造JSON，不创建任务、登记响应或写入连接）：

- `/api/shinelab/preview/config`：接收现有 `ShineLabConfigRequest`。
- `/api/shinelab/preview/command`：接收现有 `ShineLabCommandRequest`。
- 返回有效 `payloadProfile`、`strMethod`、`body`，不伪造实际发送用 strID。
- `/api/shinelab/server/status` 增加 `payloadProfile`。
- 这些接口目前只存在于本分支构建，正在监听5500的旧实例尚未提供。

示例预览请求：

```json
{
  "taskUuid": "offline-dcseq",
  "sampleData": [
    { "sampleId": "S1", "sampleName": "sample", "type": "0", "position": 1, "mPos": "1", "channel": "A" }
  ]
}
```

注意：上面是中控HTTP请求格式，`type` 在请求中为字符串，实际TCP body转为整数。当前WPF本地预览仍默认Rike，尚未加入DCSeq模式选择；DCSeq验证必须使用本分支的服务端预览，不能将旧WPF预览当作新报文。

## 剩余协议与代码差异

1. Certification 的完整成功响应仍缺失，需厂家样例或对应版本DLL确认。不能仅凭猜测添加 strCode=200 或 success=1。
2. Init 的实际动作、副作用、与 Config 的先后顺序待确认。
3. action=0 与1的区别待确认。
4. Device.params 含压力、流量、温度、电导等；本轮未增加展示或持久化。
5. Result.compoundData 改为 compounds，sampleid/samplename 改为 sampleId/sampleName；本轮尚未建立新结果字段的完整业务映射。
6. 最新协议仍包含 TaskFinish 和 EndMission；当前代码将 TaskFinish 也映射为整任务完成，序列进样前必须修正/确认单样与整序列语义。
7. 厂家声明 strID 串联下发/响应/上报；现有任务仓储依赖 body.task_uuid/taskUuid/taskId。须补充发送strID与本地任务的关联，不能认定Result/EndMission已经能够关闭本地任务。
8. 厂家版本仍有注释、中文标点和示例缺逗号，作为语义说明使用，禁止直接复制为线上JSON。

## 验证结果

- ShineLab专项：57/57通过（包含新DCSeq测试及旧Rike回归）。
- MES全量：596/596通过。
- WPF直接导入/任务预览相关回归：7/7通过；这验证旧行为兼容，不表示WPF已支持DCSeq模式。
- 新工作目录首次WPF构建缺少Adapter/Simulator还原资产；补齐这两个项目的NuGet还原后重跑通过。
- `git diff --check`通过；本轮变更尚未提交、合并或部署到5500现场实例。

## 下一步

先确认认证响应，再在本分支修复及离线回放；通过后替换本会话启动的MES实例并通知用户点击连接。收到稳定 Device 状态后再安排 Config 单条预检。进样、初始化或清洗需按现场明确授权执行。超时记Unknown，不自动重发。

本地未在已检查的 res/Input/下载/资源目录找到新版DCSeq DLL。若厂家回复慢，可让用户提供控制电脑当前版本的DCSeq DLL及依赖进行静态检查。

## 现场补充：用户授权认证回复格式试验（13:34—13:39）

用户明确授权尝试常见认证回复，并要求参考旧模块。已核对历史提交557c1a0及9月11日记录：旧模块使用原strID/strMethod/equipmentCode和body.result=Success，无strCode，随后出现UpdateInfo。当前DCSeq使用同一默认回复仍持续认证。

为完成试验，结束本会话旧PID 8900，启动独立分支发布程序PID 22844（随后请按实际进程重新核实），监听地址保持192.168.10.11:5500，本机API仍为127.0.0.1:5145。

- 发布目录：本worktree `artifacts/dcseq-auth-probe/bin`。
- 配置、独立数据库、完整收发日志：`artifacts/dcseq-auth-probe/runtime`。
- 模式配置：`appsettings.Production.json` 的 `ShineLabTcp.CertificationReplyMode`，在下一条认证请求上热更新；每条请求只回一条LF-JSON。
- 第一次服务替换后DCSeq自动重连为192.168.10.108:58555；后续所有变体都在同一连接上完成，没有逐项重启或踢连接。
- 保持真实设备编号STN61_01、回显请求ID和Certification方法名；未发送Init/Config/Command。

已试变体（本地日志截至13:39:22，数量仅表示对应模式下的响应条数，不是独立连接数量）：

| 模式 | strCode | body | 时间窗口 | 回复数 |
|---|---|---|---|---:|
| 历史基准 Result | 不带 | result=Success,msg="" | 13:12开始持续测试 | 远超数百 |
| StringCode200Result | 字符串"200" | result=Success,msg="" | 13:34:47—13:35:08 | 46 |
| NumericCode200Result | 数字200 | result=Success,msg="" | 13:35:09—13:35:39 | 61 |
| SuccessOne | 不带 | success=1 | 13:35:40—13:36:31 | 101 |
| StringCode200SuccessOne | 字符串"200" | success=1 | 13:36:31—13:37:19 | 94 |
| NumericCode200SuccessOne | 数字200 | success=1 | 13:37:19—13:38:00 | 80 |
| StringCode200Only | 字符串"200" | 空对象 | 13:38:00—13:38:34 | 68 |
| NumericCode200Only | 数字200 | 空对象 | 13:38:35—13:39:22 | 94 |

各窗口均仅观察到持续Certification，无Device/Heart/BindModule。发送记录成功完成，TCP保持Established。不能由此证明客户端已解析回应，也不能断言客户端重复认证一定由回复字段造成。试验结束恢复Result默认格式。

实际认证请求样本：

```json
{"strID":"7507673687970156544","strMethod":"Certification","equipmentCode":"STN61_01","strCode":"","body":{"channel":""}}
```

特别注意channel为空：这可能与客户端尚未绑定仪器/通道有关，尚无证据确认因果；不擅自替它填A。下一步需要客户端接收日志、DCSeq DLL，或厂家确认认证成功条件与状态上报前置条件。

代码增加可显式选择的认证回复模式（默认保持原格式），可选收发诊断日志；58项ShineLab专项、597项MES全量通过。热切换集成测试验证在同一TCP连接中切换8种模式、保持关联字段、每次只响应一条且无主动控制报文。认证试验已在现场运行，但Config/Command新格式仍只有离线验证，WPF模式选择及结果闭环尚未完成。
