# DCSeq 单样品 Config 实测（2026-09-21）

用户授权认证成功后的下一步单条Config验证。使用独立分支feature/shinelab-dcseq-protocol及现场PID22844，未发送Init/Command。

## 请求和结果

- 本地任务：`dcseq-config-20260921-1430-01`
- 发送前：STN61_01在线，真实Device持续上报status=0，状态Idle；lastSeen小于3秒。
- 预览通过后，仅发送一次Config；未自动重试。
- 14:30:29.566开始调用，14:30:29.787收到155字节LF-JSON回复。
- 响应明确为Failed，msg为空；本地记录Failed/ConfigRejected，非超时Unknown。

TCP请求内容（依据与实际发送相同的构造器预览、固定请求参数及响应回显strID还原，非独立抓包）：

```json
{
  "strID": "dfaae074263e4caf9ddd4fcfeb0b1ee2",
  "strMethod": "Config",
  "equipmentCode": "STN61_01",
  "body": {
    "channel": "A",
    "sampleData": [
      {
        "sampleId": "1",
        "sampleName": "DCSeq_ConfigOnly_20260921",
        "type": 0,
        "position": 1,
        "mPos": "1"
      }
    ]
  }
}
```

客户端原始回包（诊断RX日志原文）：

```json
{"strID":"dfaae074263e4caf9ddd4fcfeb0b1ee2","strMethod":"Config","equipmentCode":"STN61_01","strCode":"","body":{"channel":"","result":"Failed","msg":""}}
```

响应后TCP保持连接，Device仍上报空闲，无已知活动任务。失败不能自动认定客户端无任何局部配置变化，重试前应核对AQ样品表。

## 补充包静态检查：下一步核对仪器绑定

只在内存读取res/补充.zip的DLL，不执行或修改厂家程序。

- ShDeviceFamily.dll `IDCTCPCmd_Config::Do`（首选VA 0x10138BD0）将设备编码、channel及样品数据构造成ShTaskCmd_Add，派发内部事件，随后根据事件结果返回Success/Failed；失败消息来源于内部事件，本次为空。
- ShCDS.dll `ShForOther::AddTask`（0x10181B60）先调用GetInstrument，未找到则返回false。
- `ShForOther::GetInstrument`（0x101815B0）调用`ShInstrumentMgr::GetInstrument_DC`（0x100497D0）。后者枚举运行仪器，要求`IsConnected()`为真并且`IsDC(code,channel)`匹配。
- `ShInstrument::IsDC`（0x1003E9B0）遍历仪器启用的DC子模块，比较其设备编码；非空channel还须匹配仪器通道标识。空channel存在跳过通道匹配的路径，但不以此擅自改空或扩大路由范围。

由此，重点应核对AQ中实际D160+仪器是否已连接、其使用的DC子模块是否绑定STN61_01、实际通道标识是否为A。上述检查提示前置条件，不代表已证明现场失败就是其中某一项；也可能内部事件处理未成功，需要AQ日志确认。

下一步让现场核对这些设置和样品表是否出现本测试名称；在确认前不重复Config，不发送启动或Init。

## 证据位置

- 请求文件：本worktree `artifacts/dcseq-auth-probe/runtime/config-test-20260921-01.json`。
- 原始回包日志：`artifacts/dcseq-auth-probe/runtime/mes.stdout.log`，搜索上述strID。
- 任务及事件：`GET http://127.0.0.1:5145/api/shinelab/tasks/dcseq-config-20260921-1430-01`，数据库为同目录mes.db。

## 厂家确认单通道省略channel后复测成功（14:54）

厂家微信明确：channel不要写，是多通道使用。单通道报文应省略该字段，不是强行填写A；本次也未发送空字符串channel。

新增显式模式`ShineLabTcp:PayloadProfile=DcSeqSingleChannel`，保留Rike与DcSeq（有通道）模式。单通道Config/Command均不输出通道字段；若HTTP请求仍传入非空通道则拒绝，避免悄悄忽略调用者意图。

64项ShineLab专项及603项MES全量通过。单通道配置/命令预览测试保证没有通道字段，也没有向连接写入数据。

14:53:47替换本会话PID22844，启动PID28632（需以实时进程为准）：

- 程序：`artifacts/dcseq-auth-probe/bin-single/MesControlAgv.Mes.dll`
- 配置环境：SingleChannel，文件`runtime/appsettings.SingleChannel.json`
- 本机HTTP5145、TCP5500保持，客户端自动重连并恢复Device=0。
- 日志改为`runtime/mes-single.stdout.log`及`mes-single.stderr.log`，保留旧日志。

复测任务：`dcseq-config-20260921-single-02`。样品编号、名称、类型、位置、mPos与首次测试完全一致，线上body只删除channel；使用新的请求strID避免混淆两次独立操作。

请求（由同一发送构造器预览及响应ID还原）：

```json
{
  "strID": "eca794596e8a49ad90d3caf84f616b0b",
  "strMethod": "Config",
  "equipmentCode": "STN61_01",
  "body": {
    "sampleData": [
      {"sampleId":"1","sampleName":"DCSeq_ConfigOnly_20260921","type":0,"position":1,"mPos":"1"}
    ]
  }
}
```

14:54:45.983收到客户端原始回复：

```json
{"strID":"eca794596e8a49ad90d3caf84f616b0b","strMethod":"Config","equipmentCode":"STN61_01","strCode":"","body":{"channel":"","result":"Success","msg":""}}
```

本地状态Configured/ConfigAccepted；没有发送Command或Init，设备继续Device.status=0。结果验证单通道省略channel的Config已被客户端接受，但尚需现场核对AQ中实际生成的样品条目，不能等同于真实进样、方法/体积校验或结果闭环已经完成。

请求文件`runtime/config-test-20260921-02.json`；任务和响应审计已落同一mes.db。后续启动前需核对样品表及仪器运行条件，不能因Config成功自动启动。

## 用户新建序列后第三次Config（15:03）

用户已在DA看到上一次条目，原序列test111有历史样品。随后确认已新建序列，方法和进样体积使用软件默认值，授权继续配置。

- 任务：`dcseq-config-20260921-newsequence-03`。
- 样品：sampleId=1，sampleName=`DCSeq_NewSeq_20260921_1503`，type=0，position=1，mPos="1"。
- 单通道不携带channel，也未下发方法或体积字段。
- 15:03:38.262开始配置，客户端返回Success，任务记Configured。
- strID：`a0b147abac664d1ca1e0cafcdf78026f`。
- 原始响应body：`{"channel":"","result":"Success","msg":""}`。
- 请求证据：`runtime/config-test-20260921-03.json`；完整响应在mes-single.stdout.log及任务事件中。
- 没有发送Command/Init。仍需现场确认条目确实进入新序列，而非旧序列：协议Config没有序列名称字段，中控无法凭Success推断客户端当前目标序列。

### 新序列定位纠正及第四次定向观察（15:10）

用户截图证实第三次样品仍进入旧test111，而新建的DCSeq_Test_20260921此前仅为序列方法。随后用户在AQ分析控制打开新方法空表，要求再试；截图标签已为新方法，但顶部“当前状态”仍显示test111，实际运行序列切换尚不能仅凭打开标签判定。

15:10:14.911在设备真实上报Idle且lastSeen新鲜时发送一次新的Config：

- 任务：`dcseq-config-20260921-sequencecheck-04`。
- sampleId=`4`，sampleName=`DCSeq_SequenceCheck_04`，type=0，position=1，mPos="1"，省略channel及方法/体积。
- strID=`9e7906dc160341929b1b12fcedec8af3`。
- 客户端约300ms内返回body `{"channel":"","result":"Success","msg":""}`，本地Configured。
- 没有发送Command/Init；等待用户核对新样品进入新表还是旧序列。Success本身不证明目标序列正确。
- 请求文件：`runtime/config-test-20260921-04.json`。

### 第五次序列定位准备：尚未发送（15:17）

用户截图确认第四次样品仍进入test111。随后顶部状态变成“--”，用户要求再发一次观察。

15:16:40初查状态0；准备预览和本地任务后，15:17:21发送前复核收到Device.status=99，因此未调用/config下发接口。15:17:45复核仍为99；TCP保持Established（192.168.10.108:63852），代表DCSeq至中控链路尚在，而新版协议将99定义为仪器离线。

- 待发送本地任务：`dcseq-config-20260921-sequencecheck-05`，状态Created，仅TaskCreated事件，没有Configuring/ConfigAccepted/ConfigRejected。
- 待配置样品：sampleId=5，sampleName=`DCSeq_SequenceCheck_05`，位置1。
- 请求准备文件：`runtime/config-test-20260921-05.json`。
- 待现场恢复AQ仪器通讯后，重新检查真实状态和任务是否仍未发送；不另建重复任务，也不绕过就绪检查。

### 仪器通讯恢复后第五次Config已发送（15:28）

用户确认恢复仪器通讯并要求再测试。15:27:40读取真实Device为status=0/Idle，原05任务仍Created且只有TaskCreated事件；预览再次确认DcSeqSingleChannel、不含channel。

15:28:11.620只调用一次原05任务的/config接口，约310ms收到Success，状态Configured。strID=`003f9edcc4b545a2953fc37460a005a2`，响应body=`{"channel":"","result":"Success","msg":""}`。

待用户核对样品`DCSeq_SequenceCheck_05`（编号5、位置1）实际进入哪个序列。未新建重复任务，未发送Init或Command；响应后仪器仍Idle。

### 用户反馈第五条样品不可见：Success不能证明添加成功

用户明确表示新旧列表均未找到该样品。核对本机任务事件，只有一次Configuring及一次ConfigAccepted；原始RX日志确认15:28:11.933收到对应strID的Success，不是误读旧回复。本轮不重发，也不发送启动。

再次直接从补充.zip在内存分析ShCDS.dll，发现：

1. `ShInstrument::AutoCreateTask`（首选VA `0x1003A5D0`）首先调用`GetLastSeq()`（`0x1003A530`）。后者按仪器对象`+0x5D0`保存的标识调用`IC_SeqMgr/GetObjByMain`查找序列，并不是读取当前UI打开的标签页。
2. `0x1003A618`发现无LastSeq时进入错误分支，记录`GetLastSeq None`，在`0x1003A6B0`返回false。`GetSeqMethodBranch_Reload()`为空也返回false。
3. 正常分支才会执行`NewTask`、`AutoPasteTask`、`UpdateModify`及PostMessage刷新。
4. 外层`ShForOther::AddTask`（`0x10181B60`）在`0x10181C86`调用AutoCreateTask后，没有test/cmp保存或检查其bool返回值，直接析构临时对象并处理下一样品；末尾`0x10181CA1 mov al,1`统一返回true。

因此该二进制存在“内部创建失败但外层仍报告成功”的明确代码路径，能够解释本次现象；不能据此断言远端此次一定是GetLastSeq为空，需现场日志验证。顶部显示“--”及未找到样品与此路径相容。

建议厂家核对：

- 15:28:11附近是否有`GetLastSeq None`或序列方法加载失败日志；仪器记录的LastSeq标识是什么。
- 新建/打开序列方法后，如何显式建立并切换仪器LastSeq，不要求用户为设置目标而运行旧样品。
- AddTask应检查并向上传递AutoCreateTask失败，返回明确msg，最好返回目标序列ID及成功样品ID；避免中控误把客户端Success当作已落库。

本地Configured目前只是对客户端Success的投影，本测试验收仍标记“样品落地未确认”。临时反汇编脚本已删除，无解压DLL残留。

### 厂家明确上次运行序列；用户运行基线后第六次追加（16:43）

厂家微信说明“插入的是上次运行的序列”“新序列需要先运行个标样”“先做个曲线”。用户表示在新序列已运行基线，并明确要求再试添加。本次仅验证样品追加，不将基线运行等同于标样/校准曲线已完成。

16:42和发送前的真实Device为status=1/Running，连接及上报新鲜。根据用户这次明确的运行中追加测试要求，只放行一次Config；没有沿用先前必须Idle的试验条件，也没有发送任何启动、停止、初始化指令。

- 本地任务：`dcseq-config-20260921-afterbaseline-06`。
- sampleId=`6`，sampleName=`DCSeq_AfterBaseline_06`，type=0，position=1，mPos="1"，不携带channel、方法或体积。
- 16:43:34.898开始调用，16:43:35.281本地记录ConfigAccepted/Configured。
- strID=`dd5eecefb54244a68d49299799778d1d`。
- 客户端响应body=`{"channel":"","result":"Success","msg":""}`。
- 请求文件`runtime/config-test-20260921-06.json`；原始回包在mes-single.stdout.log。
- 待现场确认该条目实际出现且位于新序列；在此之前仍不判定样品落地成功。

### 第六条已确认进入新序列，AQ自动接续执行

用户随后截图确认编号6、`DCSeq_AfterBaseline_06`进入`DCSeq_Test_20260921`。
AQ自动勾选并在前一条基线结束后运行该样品；用户也明确确认“AQ会自动运行下一条样品”。
本次MES没有发送Init或Command，但Config在运行中的序列里仍可能引起后续实际执行。
因此后续不得把Config称为无动作预览，也不得仅以“未发启动命令”判断没有发生进样。

### 2026-09-22 类型映射与自动勾选验证

根据协议表，`type`定义为：0未知样、1标准样、2基线、3空白样、4质控样、5加标样。
本次在设备空闲时追加了`type=2`的`DCSeq_Baseline_Check_20260922`，客户端返回Config Success，
但AQ显示为“未知样品”并自动勾选。随后一次Config追加`type=0、1、3、4、5`五条不同sampleId：
`T0/T1/T3/T4/T5`，客户端均返回Success；AQ界面五条仍全部显示“未知样品”，且全部自动勾选。

结论：中控预览和实际构造均保留了整数`type`，不是MES丢字段；当前AQ/DCSeq客户端对Config的type枚举不做界面映射，
或该版本不支持通过Config设置样品类型。Config Success只代表客户端接受请求，不能证明类型已生效。
新增行会自动勾选，不能把Config当作纯离线导入；多条测试行未授权启动前必须保持序列停止。
厂家已确认：当前版本所有样品均按“未知样品”处理。故障不在中控构造，暂不再重复追加type测试；
后续按未知样品执行受控通讯验证，待厂家后续版本再验证类型映射。

补充：厂家确认托盘弹出/复位尚未加入DCSeq通讯协议；AQ中点击该按钮不会产生下游TCP报文，当前仅能作为本地仪器操作。

现场事件（MES接收时间，与body中的仪器时间分开记录）：

| MES接收时间 | 方法 | sampleId | 仪器finishDate | 解释边界 |
| --- | --- | --- | --- | --- |
| 16:50:30.559、16:50:30.618 | TaskFinish，两次不同strID | 空 | 2026-09-21 16:50:36 | 与手动基线结束相近；不能归给MES编号6任务 |
| 16:51:37.231、16:51:37.289 | SampleFinish，两次不同strID | 6 | 2026-09-21 16:51:42 | 样品仍在分析时已收到，不能等同全分析完成 |

用户随后手动终止编号6，截图显示“终止”。该观察阶段没有发现新的明确终止上报、Result或EndMission；
不能根据Device回到0或没有新事件，反推成功完成或协议已确认停止。
仪器时间领先接收时间约5–6秒，两个时钟不能混用作因果排序依据。

16:58:04旧运行版本API出现status=0但state/stage仍为SampleFinished、progress=100的残留；
这不是仪器最新完成证明。已安排离线修正，不回写历史测试任务，也不自动部署到现场。

下一阶段与厂家问题见[状态与闭环下一阶段记录](SHINELAB-DCSEQ-NEXT-PHASE-2026-09-21.md)。
