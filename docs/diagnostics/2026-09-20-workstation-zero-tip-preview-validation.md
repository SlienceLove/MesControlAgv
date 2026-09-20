# 工作站默认枪头兼容与 50 µL 本地预览

## 本轮范围

用户确认枪头由工作站按默认逻辑管理，中控不指定使用哪个枪头。仅修改 `SampleWorkstationTemplateFile`：完整 `(0,0)` 或双正整数可原样读写；半零、负数、缺值、非整数仍拒绝。来源/目标孔位和移液量保持正整数，回读比较仍逐字段精确匹配，`(0,0)` 不等于 `(1,1)`。

没有新增枪头选择/分配 UI，没有修改调度、准入、worker 或现场配置。只读抓取一次厂家 `test` 模板作为 fixture，其后测试和预览均离线，不向真机导入、写码、初始化或启动，也未合并日常分支。

## 证据与预览

- 实际响应 fixture：`tests/MesControlAgv.Adapter.Tests/Fixtures/workstation-test-zero-tip-response.json`，捕获时间 `2026-09-20T01:12:44.9591535Z`；其中 XLSX 的 SHA256 为 `44EBCF668DB46F1EEC7D79CFA86DC21D7397C594D5434650BCBB8B48249CE917`。
- 原文件内任务号 `test`，两来源各8条，共16条，每条1000µL，枪头 `QT-001/(0,0)`。
- `TrajectoryParameterDetails?key=test` 的只读结果同样含 `WarehouseX=0, WarehouseY=0`；目标信息位于两组 `TargetData` 内。外层 `TargetX/TargetY/TransferVolume=0` 是聚合结果的空占位，不能拿来替代实际逐目标值或据此放宽目标坐标规则。
- 既有2026-09-09厂家 DLL 展示按可用枪头记录选位的逻辑，详见本轮设计；这是旧实现证据，不宣称当前现场程序已验证。

本地文件（ignored，不自动上传）：`artifacts/workstation-test-50ul-preview/local-preview.xlsx`。

- 预览任务号：`LOCAL-20260920011928-8e7f15e607544f69b`。
- SHA256：`E0D2C2DB5E326F603083758ADBE86C7B31152C95B64EB599817EFC851E221A7B`。
- 1号来源 `CYC-001-1000/(1,1)` → `FYB-001` 的8孔；2号来源 `CYC-002-1000/(1,1)` → `FYB-002` 的8孔。
- 每条50µL，各来源合计400µL，总计800µL；原始1000µL输入未修改，枪头默认值及其余模板字段/顺序保持原样。
- 正式执行仍须在中控绑定已核对来源 ID，创建正式准备记录、导入回读、独立运行准入。此预览不代表创建了厂家任务或通过了现场验收。

## 验证结果

基线模板测试31/31。新增测试先运行：旧实现19例中2例失败（真实零坐标模板读取及零坐标生成），17例通过；兼容后模板相关52/52通过。

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --nologo -v minimal
# 348 passed / 0 failed
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --nologo -v minimal
# 372 passed / 0 failed
dotnet build MesControlAgv.sln -c Release --no-restore --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-workstation-layout-20260920-build -p:SkipLocalServiceCopy=true --nologo -v minimal
# succeeded / 0 warnings / 0 errors
dotnet run --project tools/WorkstationTemplatePreview -- tests/MesControlAgv.Adapter.Tests/Fixtures/workstation-test-zero-tip-response.json artifacts/workstation-test-50ul-preview/local-preview.xlsx
# first run succeeds; 16 rows / 50 per target / total800
```

MES 完整准备→导入→带码启动→Running→Completed→释放租约的替身测试同时覆盖双正整数与默认零坐标（2/2）；没有真实设备调用。Adapter 测试确认零坐标上传/回读一致才成功，回读改成正坐标会报内容不一致且不重发。

离线工具已实际编译执行；再次输出到同一文件时返回1并拒绝覆盖，前后输出 SHA256 一致。另用独立 ZIP/XML 读取检查16行、两个来源对应目标、目标顺序、默认枪头值与体积合计。未安装 openpyxl，未使用该库或宣称其验收结果。

## 建议转告厂家

“中控后续按工作站默认方式取枪头，不指定具体枪头孔位。我们准备以 test 为模板，新建两来源、16孔、每孔50µL的任务，不覆盖原 test。想请帮忙确认：目前界面顶部显示50µL，但导出各目标孔是1000µL，新导入任务的实际移液量是否以每条目标记录的 TransferVolume 为准？另外，1号瓶对应 FYB-001 的8孔、2号瓶对应 FYB-002 的8孔，批量导入后能否完整保留两组来源关系？我们会先回读核对再启动。”
