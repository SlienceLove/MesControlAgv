# 条码POST适配及正式启动验收交接（2026-09-21）

> 后续14:20厂商更新复测见[启动已接受但现场仅初始化](diagnostics/2026-09-21-workstation-start-accepted-init-only.md)：新任务WS76C00540582D9E1BEA已收到启动成功确认，但未观测到Running，现场仅初始化；30秒观察窗口后Unknown，没有停止或重发指令。本页保留13:19失败历史。

## 结论

条码POST与新回执适配已实现并提交 `f91b4dc`，离线回归和独立代码审查通过。
本轮真实中控链完成：任务准备→单次导入→双来源及体积回读→自动条码同步→Imported→正式准入→单次启动。
**厂家StartExperiment约16.18秒后返回“启动失败”，没有观测到Running/Completed，完整运行验收未通过。**
中控按明确失败记录Job/Workflow/设备操作为Failed，未重试、未冒充执行成功。

用户现场反馈厂家主程序停留主界面，显示“正在启动试验任务”。13:21:56只读复核仍等待运行/空闲/错误码0，详情运行时间为空。
这个界面提示不等于当前任务启动成功；待现场截图及13:19附近厂家日志确认是启动未完成还是提示未清除。
不再发送Start/Init，不建议此时人工重复点击开始。

## 代码范围与验证

- 仅Adapter→厂家的UpdateTaskBarcode改为POST、URL query参数、空body。
- Init/StartExperiment仍GET；MES→Adapter的既有API不改。
- 接受旧精确“更新成功”；新回执必须完整匹配已发送taskNo及两条trim后条码。
- 业务拒绝、无法确认和传输失败保持原分类；没有fallback、自动重试或所有Code200都通过的放宽。
- 旧GET-only厂家版本不由本次新部署兼容；旧部署及其他工作区没有自动升级。
- Adapter全量491/491、MES全量587/587、WPF工作站相关63/63，均本轮运行通过。
- solution隔离Release构建0警告0错误。独立reviewer审查8731aa7..f91b4dc无发现。
- 工作分支feature/workstation-ml-template-validation，工作目录D:/Project/Github/Mes-worktrees/sample-workstation-http-readonly。
- [批准方案](superpowers/specs/2026-09-21-workstation-barcode-post-design.md)、[实施清单](superpowers/plans/2026-09-21-workstation-barcode-post.md)。

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --artifacts-path artifacts/workstation-barcode-post-build -p:SkipLocalServiceCopy=true --nologo -v minimal
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --artifacts-path artifacts/workstation-barcode-post-build -p:SkipLocalServiceCopy=true --nologo -v minimal
dotnet build MesControlAgv.sln -c Release --artifacts-path artifacts/workstation-barcode-post-release -p:SkipLocalServiceCopy=true --nologo -v minimal
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj -c Release --artifacts-path artifacts/workstation-barcode-post-release --no-build --no-restore --filter "FullyQualifiedName~Workstation" --nologo -v minimal
```

## 本轮身份和部署

验证编号：`FIELD-BC-20260921-1315`。
证据目录：`artifacts/workstation-barcode-field-20260921-1315/`，ignored，仅保留本机，不提交数据库或现场日志。
沿用用户批准的独立验收库，不修改任何旧Unknown；新Adapter15052/PID31896，MES15053/PID43604。
两个进程Production，contentRoot分别为上述目录adapter/mes，二进制来自artifacts/workstation-barcode-post-release/bin。
Excel配置Milliliters；中控和HTTP体积仍µL。后续操作前重新核实PID，不按历史PID直接结束进程。

| 项目 | 值 |
| --- | --- |
| 厂家任务 | WS87AD8E31CBC60B1CB1，主表R_No=31 |
| Workflow | 8e400197-11ca-45c5-bf2f-5a71fcd368e1 v1 |
| Plan | f68102db-8caf-4457-9cb9-46de113d8cd1 v1 |
| Job | 4356cca1-6d72-4a46-9a79-986646717c94 |
| Preparation | aff2f61e-e0bb-4eaa-849c-2ee2e578f9a4，Imported |
| Run | 17437d26-1f1c-459c-83d7-18c79f6d1ced，Failed |
| Device operation | 72c23505-0171-3a8c-6fd0-e93c54ecba6f，attempt=1，Failed |

来源ID：SRC-260921-1315-01绑定CYC-001-1000/(1,1)→FYB-001八孔；SRC-260921-1315-02绑定CYC-002-1000/(1,1)→FYB-002八孔。
各组孔位依次(1,1),(1,2),(1,3),(1,4),(2,1),(2,2),(2,3),(2,4)，枪头QT-001/(0,0)。来源ID是用户授权的中控测试标识，不是扫码所得。
每孔50µL，Excel0.05mL，总800µL；实际移液尚未得到设备运行/现场证据确认。
生成文件SHA256：`20578AD52B97AC48714FED2D10BDFB725F7417D5C2EA6F23B95E11880E83616F`。

## 实测链路及停止位置

1. 单次ImportExperimentalTask成功，Adapter正式下载回读一致。
2. 中控自动POST UpdateTaskBarcode，精确新回执通过，准备记录Imported，LastError=null。
3. 启动前独立HTTP轨迹展开验证16孔、来源/目标/坐标/体积/顺序全部一致，状态Idle、错误0。
4. 13:19:03中控正式admit返回202并建立Run，运行绑定使用冻结任务号及两条来源ID，不是原test模板号。
5. 13:19:05.307创建单次设备操作，实际出站：

```text
GET /Service/StartExperiment?TaskNo=WS87AD8E31CBC60B1CB1&SampleBarcode1=SRC-260921-1315-01&SampleBarcode2=SRC-260921-1315-02
```

6. HTTP层返回200，耗时16175.9066ms；驱动解析业务Data为“启动失败”，不是HTTP方法405或网络超时。
7. 13:19:21.718工作流终态Failed，Job状态9/Failed，设备操作状态10/Failed，attempt1。错误全文：
   `Sample workstation did not confirm task 'WS87AD8E31CBC60B1CB1' start: 启动失败`。
8. 13:19:25及13:21:56直接查询任务仍等待运行、设备0/Idle、错误0；任务RequestTime/ProductionTime/CompletionTime均null。
9. 用户现场反馈“正在启动试验任务”，尚没有确认实际动作。没有将提示文字解释为Running，也没有再发启动或初始化。

Job的startedAt/completedAt是中控运行尝试的起止时间（05:19:05..05:19:21 UTC），不代表设备已经执行或完成分液。
新的条码接口没有错误，不应把本次启动失败再归因于条码POST或小数/双来源导入问题。
启动失败原因未定；不能仅凭16秒等待断言主程序未开、未初始化或厂家某行代码故障。

## 后续衔接

已请用户提供主程序完整截图及13:19附近日志。当前停止额外控制请求，避免潜在延迟动作或重复执行。
待现场状态及失败原因明确后再决定如何继续一次运行验收，不直接重放本Run或手动改Failed为成功。
旧库、Unknown记录、现场服务及证据全部保留；本轮没有推送或合并主线。
10mL上限、异常准备归档入口、正常Completed/WPF现场显示仍为后续事项，不计为本次已完成。
