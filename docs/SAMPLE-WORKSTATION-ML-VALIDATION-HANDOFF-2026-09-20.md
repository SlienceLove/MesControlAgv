# 开盖分液 Excel mL 兼容与现场验证交接

## 当前结论

代码已提交 `d62b602`，分支 `feature/workstation-ml-template-validation`，起点develop `ad2d958`。
离线开发、回归和独立审查完成；**15:43真机导入未通过，未启动设备**，不能称为完整真机验收通过。
验证编号 `FIELD-ML-20260920-1540`，厂家任务号 `WS0F3EA1F297B7A79DD3`。

厂家随后通过用户提供的回复明确：导入时小数换算有问题。用户已确认修复版服务运行；
16:34只读检查在线且空闲，原test模板可读16孔，旧失败任务仍无轨迹。
修复后的重新导入及运行尚待本轮后续记录，不能把软件已更新算作验收通过。

## 单位约定与实现

- 中控DTO、样品来源/目标孔位快照和HTTP `TransferVolume` 仍为µL，未整体改成mL。
- 厂家Excel文件用mL：内部50写入0.05，导出1读为1000µL；decimal精确换算，拒绝非正数、溢出、亚µL精度，不猜测文件单位。
- 新 `FileVolumeUnit` 贯穿模板响应、冻结快照、文件重建、MES导入请求及Adapter回执。
- 旧记录缺省为µL，默认字段不写入历史JSON；历史hash及Unknown记录不改。
- Adapter在厂家请求前检查文件单位与设备配置一致，避免旧µL文件误送新版mL导入器。
- 命令不自动重试；仅在上传应答成功且下载回读逐项相同后才允许继续原条码同步/准入流程。
- 新任务号仍为20字符；64位字段扩容为厂家反馈，本轮没有测试64位边界。
- 用户补充移液量最大10mL（10000µL）；本轮每孔0.05mL满足该限制。通用文件转换器尚未加入10mL设备上限校验，不能声称任意输入均受此上限约束。

设计和清单分别见 `superpowers/specs/2026-09-20-workstation-ml-template-design.md`、
`superpowers/plans/2026-09-20-workstation-ml-template.md`。

## 本轮验证证据

使用独立构建输出，不覆盖运行程序：

- Adapter相关141/141；最终Adapter全量476/476（含mL证据导出CLI新增2项）。
- MES相关89/89；最终MES全量587/587。
- WPF相关63/63；没有把它表述为WPF全量回归。
- solution隔离Release构建0警告、0错误。
- 独立reviewer审查62012f5..d62b602，无未关闭问题。配置疑问经实际contentRoot配置与进程命令行核验后关闭，不改变旧部署默认µL。

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --artifacts-path artifacts/workstation-ml-validation-build -p:SkipLocalServiceCopy=true --nologo -v minimal
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --artifacts-path artifacts/workstation-ml-validation-build --no-build --no-restore --nologo -v minimal
dotnet build MesControlAgv.sln -c Release --artifacts-path artifacts/workstation-ml-validation-release -p:SkipLocalServiceCopy=true --nologo -v minimal
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj -c Release --artifacts-path artifacts/workstation-ml-validation-release --no-build --no-restore --filter "FullyQualifiedName~Workstation" --nologo -v minimal
```

## 现场身份、部署与结果

工作目录 `D:/Project/Github/Mes-worktrees/sample-workstation-http-readonly`。
证据目录（本机保留，不入库）`artifacts/workstation-ml-field-20260920-1540/`。

| 项目 | 值 |
| --- | --- |
| 厂家地址 | http://192.168.200.157:8082/Service/ |
| EquipmentNo | CYC-001-1000 |
| 专用Adapter | http://127.0.0.1:15046，启动PID40596 |
| 专用MES | http://127.0.0.1:15047，启动PID15004 |
| Workflow | 51cd1912-d609-475e-b2c7-65b00eda8a30 v1 |
| Plan | eebabd77-79db-41dd-85b9-cb859819b40d v1 |
| Job | ca618544-21c1-4984-9f62-d8fded801de5 |
| Preparation | e6706bc9-9965-47f0-9d6a-f4d350439dc8，Unknown |
| 厂家任务 | WS0F3EA1F297B7A79DD3，R_No=28，等待运行 |

Adapter运行于Production，contentRoot为证据目录下adapter文件夹，明确配置
`Devices:SampleWorkstation:TemplateVolumeUnit = Milliliters`，Enabled/ControlEnabled/ProtocolConfirmed均为true。
MES使用同目录下mes独立配置及SQLite，仅启用本轮需要的工作站worker。
后续先核对PID对应路径再操作，不能仅凭历史PID结束进程。
原15041/15042/15045服务及旧数据库未修改，前处理提交a469425仍保留在原分支。

来源绑定：CYC-001-1000/(1,1)→SRC-260920-1540-01→FYB-001的8孔；
CYC-002-1000/(1,1)→SRC-260920-1540-02→FYB-002的8孔。
各组孔位依次(1,1),(1,2),(1,3),(1,4),(2,1),(2,2),(2,3),(2,4)，默认枪头QT-001/(0,0)。
来源ID为用户授权的中控测试标识，不是扫码读取结果。每孔50µL，各来源400µL，总800µL。

15:43:26唯一一次导入获得成功应答，随后下载回读失败：移液参数表只有表头。
15:43:59和15:45:50直接查厂家任务主表存在，而轨迹接口均Code201/无数据，排除仅文件导出遗漏的解释。
新任务没有获得16条轨迹，也没有启动。中控保存Unknown；Job的workflowRunId为空。
Adapter出站日志计数：ImportExperimentalTask=1，UpdateTaskBarcode=0，Init=0，StartExperiment=0。
原test轨迹仍为两来源16孔；设备与错误接口均返回0。

上传XLSX从冻结快照还原，逐孔独立XML核对0.05mL，SHA256：
`A0B36AC28AD62BD5402753477EA672B00135EEC7EE58FBD967B6B697D0CFD1E8`。
可转发厂家包 `artifacts/workstation-ml-field-20260920-1540/workstation-ml-import-evidence-20260920.zip`，包含原文件、原上传帧、manifest、排查说明、回读证据，不含数据库或执行脚本。
ZIP SHA256：`7470C6A39F54218D8ABDACED93EC989C98CA9B99C1E8809D0BBFAB67FDBB3A70`，包内XLSX hash再次核对一致。

## 修复后继续验证的衔接点

厂家确认小数换算故障并更新服务，16:34用户授权继续新任务。
目前MES `EnsureNoUnresolvedPreparationAsync` 会阻止同设备存在Unknown时再准备/导入，代码没有提供专门的准备记录异常归档入口。
不直接修改旧数据库、不删除旧记录、不绕过同库检查。已向用户提供选择：
保留旧库，用新的独立验收库走完整中控链；或先补异常归档功能后在原库继续。执行选择及结果需后续追加记录。

重新验收标准不变：新任务号、只导入一次；Excel完整16孔且每孔0.05mL，HTTP全部16孔50µL、来源/目标相同；
确认后才启动一次，记录Running→Completed及设备回空闲。失败任务不直接重放。
异常归档和10mL输入上限列为后续事项；完整真机验收通过前不宣称可交付现场或自动合并主线。
