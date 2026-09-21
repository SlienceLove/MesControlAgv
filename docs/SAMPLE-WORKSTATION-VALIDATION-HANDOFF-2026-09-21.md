# 开盖分液第三轮验证交接（2026-09-21）

> 13:05经用户明确授权试验，已确认条码更新使用POST＋URL参数＋空请求体，成功回执也变更；无需再等厂家提供这一接口的参数放置说明。生产代码尚未适配，任务未启动。详细实测见文末。

## 结论

厂家更新后的**Excel小数换算和双来源保留均通过真实导入回读验证**。
后续条码更新接口按现有文档GET调用返回HTTP405，新版服务声明Allow: POST；等待确认POST参数格式及启动接口是否同步改变。
未修改生产代码，未猜测或自动切换请求方法，未初始化或启动设备。完整运行闭环仍未验收通过。

代码为已离线回归、审查的 `d62b602`，工作分支 `feature/workstation-ml-template-validation`；
工作区 `D:/Project/Github/Mes-worktrees/sample-workstation-http-readonly`，当轮开始HEAD `f639c47`。
前两轮见[9月20日交接](SAMPLE-WORKSTATION-ML-VALIDATION-HANDOFF-2026-09-20.md)。

## 部署和身份

沿用用户已明确批准的独立验收库方式，新建本轮服务和数据库，旧记录/数据库/其他进程原样保留。
没有通过清理Unknown或修改准入代码继续同库任务。

| 项目 | 值 |
| --- | --- |
| 验证编号 | FIELD-ML-20260921-1150 |
| 厂家任务 | WS8173C2D6208D3E6D25，主表R_No=30 |
| 厂家地址 | http://192.168.200.157:8082/Service/ |
| 设备标识 | SAMPLE-WORKSTATION-01，EquipmentNo=CYC-001-1000 |
| Adapter | http://127.0.0.1:15050，启动PID20572 |
| MES | http://127.0.0.1:15051，启动PID22356 |
| Workflow | 30dfeaf9-7656-4198-9fe5-8648fb5c0ae9 v1 |
| Plan | a8d331ea-6134-4201-89a7-52ddb30d94ef v1 |
| Job | 403e9198-12e3-4ac9-96aa-94650adc96ec |
| Preparation | 7fc4c0df-8606-4353-b443-21aeebea2810 |

证据/配置/运行数据库目录：`artifacts/workstation-ml-field-20260921-1150/`，ignored，仅保留本机。
两个新进程均为Production，contentRoot分别为该目录adapter/mes；Excel单位明确Milliliters，HTTP体积仍µL。
运行输出仍是前一天的 `artifacts/workstation-ml-validation-release/bin/`，生产代码未变，无需重复构建覆盖运行程序。
启动PID仅为历史记录，后续操作前重新确认命令行及路径。

## 实测过程

1. 11:47只读检查在线空闲、错误码0；原test为等待运行，来源CYC-001-1000和CYC-002-1000各8孔。
2. 新库完整创建/发布流程及计划，创建作业、排程，登记用户授权的中控测试来源ID并冻结核验快照。
3. 1号瓶测试ID `SRC-260921-1150-01` 绑定CYC-001-1000/(1,1)→FYB-001八孔；2号瓶 `SRC-260921-1150-02` 绑定CYC-002-1000/(1,1)→FYB-002八孔。
4. 两组孔位均为(1,1),(1,2),(1,3),(1,4),(2,1),(2,2),(2,3),(2,4)，默认枪头QT-001/(0,0)。每孔50µL，文件0.05mL，总800µL；测试来源ID不是扫码结果。
5. 从冻结快照重建文件，校验hash；独立读取XLSX单元格验证两个来源各8孔、每孔0.05mL。
6. 11:49:29唯一一次POST ImportExperimentalTask成功，随后下载模板全部字段与冻结模板一致，Adapter正式返回ReadbackVerified。
7. MES随后调用条码同步，Adapter向厂家发出GET UpdateTaskBarcode，收到405；Preparation因此为Unknown，LastError为“Sample workstation returned HTTP 405.”。
8. 直接HTTP轨迹查询返回R_No=99..114共16条、每条1个TargetData目标。按来源和目标展开后，16个目标的来源/坐标/目标/体积/顺序完全相同，每孔50µL。不同于旧版2条各8孔的分组形式，但没有内容差异，不应当作故障。
9. Job的workflowRunId仍为空，设备Idle/0，错误码0。出站计数：导入1、条码更新尝试1（GET405）、Init0、StartExperiment0。

上传XLSX SHA256：`D827D1F53E6DB18DB861C17F4A800C59608211546485B3F54F86E6001D343067`。
详细对比见 `trajectory-verification.json`：orderedFieldsMatch=true，两个来源各8，totalMicroliters=800。

## 新阻塞：条码接口HTTP方法与文档不一致

仓库res的《开盖分液工作站Http接口文档V1.02.pdf》第24页明确标注：
`2.11.更新任务表中的样品瓶条码[Method = "GET"]`；第25页列TaskNo、SampleBarcode1、SampleBarcode2。
当前代码按此约定调用，未临时改动：

```text
GET /Service/UpdateTaskBarcode?TaskNo=WS8173C2D6208D3E6D25&SampleBarcode1=SRC-260921-1150-01&SampleBarcode2=SRC-260921-1150-02
→ HTTP 405

OPTIONS /Service/UpdateTaskBarcode
→ HTTP 405, Allow: POST
```

OPTIONS是无业务写入的方法探测；没有重发GET写请求，也没有猜测POST请求体。
厂家help及两个operation-help地址均404，未取得新的参数说明。没有因为405就认定路由不存在。
已请用户向厂家确认POST参数是JSON请求体还是URL查询，并询问StartExperiment是否也有方法/参数变更。
需要最新接口调用示例或运行版本契约后才能适配；不跳过条码同步、不直接启动已经存在的任务、不将Unknown改为Imported。

## 归档与后续

可转发厂家包：`artifacts/workstation-ml-field-20260921-1150/workstation-barcode-method-evidence-20260921.zip`。
包括原始XLSX、README、manifest、只读回读、逐目标核验及OPTIONS响应；不含数据库、凭据或自动执行脚本。
代码未改变，本轮没有重跑或虚增9月20日的离线测试统计；真实验证覆盖了本轮导入及双来源/小数回读。
确认最新POST契约后再处理条码阶段与后续运行，保留本轮已经导入的任务和Unknown事实，不盲目重复上传。
同库Unknown归档入口、10mL输入上限、完整运行与WPF现场显示仍为后续事项。
不自动推送、合并develop/master或更新其他工作区。

## 13:05补充：用户授权的条码POST格式验证

用户明确要求直接尝试两种格式。仅针对已导入但未运行的测试任务WS8173C2D6208D3E6D25，
使用原冻结来源ID SRC-260921-1150-01、SRC-260921-1150-02，不改其他任务、不启动设备。
发令前只读确认设备Idle/0，任务等待运行。两次请求均得到明确业务应答，无超时或自动重试。

1. POST `/Service/UpdateTaskBarcode`，Content-Type application/json，请求体为三个同名字段TaskNo、SampleBarcode1、SampleBarcode2。
   HTTP200、业务Code201，Data明确返回任务编号不存在，且回显三个参数均为空。该JSON body形式未被服务绑定，没有更新目标任务。
2. POST `/Service/UpdateTaskBarcode?TaskNo=WS8173C2D6208D3E6D25&SampleBarcode1=SRC-260921-1150-01&SampleBarcode2=SRC-260921-1150-02`，空请求体。
   HTTP200、业务Code200，成功后立即停止其他写格式探测：

```text
更新任务信息完成!任务编号:'WS8173C2D6208D3E6D25',1号样品瓶条码:'SRC-260921-1150-01',2号样品瓶条码:'SRC-260921-1150-02')
```

由此确认新版是POST方法，但参数仍放URL查询串，而非JSON请求体。
成功回执由旧“更新成功”变成“更新任务信息完成!”并回显任务及条码，现有严格确认逻辑也需随请求方法一起适配。
不能只改GET为POST而忽略成功应答变化，也不能将所有Code200都判为成功。

只读复核任务仍等待运行，设备仍Idle；任务主表Remark变为“远程接口更新”，MakeTime变为13:06:04（厂家时钟）。
该说明是成功回执及任务更新时间的证据，现有任务详情没有单独返回两条样品条码字段，不声称已独立读回条码存储。
OPTIONS `/Service/StartExperiment` 返回405、Allow: GET，说明启动方法仍支持GET；未发启动请求，尚未实测其完整参数/应答契约。
本机采集UTC约05:05:34/05:05:58，厂家HTTP Date较本机约快6秒，证据均保留原时间。

本次探测直接调用厂家，不经过Adapter出站日志：JSON POST1（业务拒绝）、URL POST1（业务成功）；
加上11:49中控原GET405共3次条码调用尝试，其中仅后一次有成功确认。Init与StartExperiment业务调用仍为0。
没有直接修改中控SQLite，Preparation仍保留原Unknown，不将本次独立探测冒充完整中控链成功。

证据文件均在本轮目录：`barcode-post-json-probe-request/result.json`（实际文件名分别以-request.json、-result.json结尾）、
`barcode-post-query-probe-request.json`、`barcode-post-query-probe-result.json`、`barcode-probe-final-readonly.json`。
后续按明确格式适配条码命令与新回执，回归后再做完整流程验收。10mL上限与异常归档入口仍是原待办。
