# 20字符任务号兼容离线验证

## 完成范围

用户批准的最小修正已实现，代码提交 `0f31868`。正式准备的新厂家任务号为 `WS` + 18位大写十六进制随机字符，共20字符、72位随机空间。内部GUID、时间、样品来源和孔位绑定未变；历史42字符任务号和幂等重放结果不截断，现场Unknown记录不改。

未修改厂家协议、导入回读标准、准入门槛或界面；不迁移数据库、不更新运行服务、不重新导入/启动真机、不合并日常分支。当前15042/15045仍为本轮现场验收时的旧构建，尚未部署短号修正。

## 验证

- 修改前基线：业务链14/14通过。
- 加入短号断言后旧实现3项失败（固定时钟16次准备、两种枪头的完整链），12项通过。历史长号测试最初试图修改不可变测试快照，被既有保护拒绝；调整为向临时数据库新增历史快照后通过，没有绕过生产保护。
- 修正后业务链15/15：固定时钟16个新号均20字符、ASCII大写、不重复；幂等请求保留原Preparation/任务号/payload hash；默认零枪头/正坐标均能完成替身导入→启动→Running→Completed链。
- 历史42位Unknown快照在临时测试数据库中读取完整，导入/准入继续拒绝，序列化payload/hash/任务号不变。
- MES全量373/373，Adapter全量348/348。
- solution隔离Release构建0警告、0错误，输出到 `C:/Users/33206/AppData/Local/Temp/mes-workstation-short-taskno-20260920-build`，未覆盖运行服务。

```powershell
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --nologo -v minimal
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --nologo -v minimal
dotnet build MesControlAgv.sln -c Release --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-workstation-short-taskno-20260920-build -p:SkipLocalServiceCopy=true --nologo -v minimal
```

## 原上传文件还原与厂家材料

新增纯离线工具 `tools/WorkstationImportEvidence`，从已保存的prepared响应还原XLSX，必须匹配冻结SHA256后才导出。不会生成新号或调用任何网络。文件和原帧保留42位**历史原号**，不拿新号文件替代旧证据。

- 还原文件：`artifacts/workstation-field-20260920-0935/vendor-evidence/MES-20260920014125514-84cac287ab024f2ebd33.xlsx`，3033字节。
- SHA256：`7B51C8ED1CC6549E14AE82133951BDC1F77093502C10F78CE018BFA0888808EA`，与冻结准备记录一致。
- 独立按厂家帧格式解码，文件名及XLSX hash均相同；Excel COM只读打开实际还原文件，核对原任务号、2来源、16条50µL和默认枪头值。关闭未保存，hash仍一致。
- 工具重复输出拒绝覆盖（返回1，旧hash不变）；更改输入hash后返回1且不创建输出目录。
- 可转发ZIP：`artifacts/workstation-field-20260920-0935/workstation-import-repro-20260920.zip`。
- ZIP SHA256：`7D243A0C1D350F07B6301CECA871C4C05F4850E33E9736BBA4A5C810DC99E381`。
- 包含原上传XLSX、重建原帧、manifest、实测只读查询响应、中文排查说明；无自动上传/启动脚本，无厂家源码/凭据或中控数据库。

这只排除中控新号超出旧schema长度的兼容风险。现场当前字段长度、是否存在其他必填字段/双来源导入问题仍待厂家日志与后续真实验收，不声称根因已确认或真机已恢复。

## 独立审查

只读审查范围 `dd963b5..0f31868`，未发现阻断问题，确认新号长度/随机性、幂等顺序、历史兼容和证据导出范围符合设计。审查时工具拒绝覆盖/hash不符路径只有实际命令验证，缺少自动化CLI测试；此项随后按下节补齐。ZIP再次只读核验为5个预期文件，包内XLSX hash与原冻结hash一致。

## 后续自动化CLI收尾（2026-09-20）

测试提交 `6bf6b92`，只修改测试项目及新测试类，不改生产代码。测试通过项目引用构建工具，使用构建输出直接启动真实CLI，不在测试中嵌套build或搜索仓库路径，不连接厂家设备。

- 新增9项：短号/历史42位号原样还原（2）；独立解码上传帧和manifest核对；hash/任务号不符/非法文件名拒绝（3）；已有文件/目录保留（2）；重复输出全部文件原样保留（1）；缺参数提示且不创建文件（1）。
- 各用例只使用独立临时目录；子进程20秒上限，清理前校验临时根目录和专属前缀。没有清理或修改现场材料。
- Debug全量Adapter 357/357通过。
- 仓库外隔离Release首次全量356/357：唯一失败是未改动的 `FieldStandardConfigurationTests.FindRepositoryRoot` 从构建路径父目录寻找sln失败；同一输出中的新增CLI9/9通过。未改动该无关测试。
- 改为仓库内独立、ignored的Release输出后，全量Adapter 357/357通过，说明新测试可用于常规及隔离构建。
- 本轮未重跑未改动的MES；上一轮373/373证据仍保留，不合并计为本轮重新执行总数。厂家复现ZIP的hash未变，现场服务未部署，Unknown记录未改，未发任何厂家请求。
- 独立只读审查 `c8935b4..6bf6b92` 无发现，确认真实子进程调用、独立帧解码、工程引用和临时目录清理符合范围；上一轮自动化CLI测试缺口已关闭。

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --nologo -v minimal
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj -c Release --artifacts-path D:/Project/Github/Mes-worktrees/sample-workstation-http-readonly/artifacts/workstation-field-20260920-0935/cli-release-build --nologo -v minimal
```
