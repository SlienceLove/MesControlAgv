# 工作站任务准备编辑提效（A）验证

用户确认方案A，设计/清单提交 `bf717ff`，实现提交 `18e274a`。本轮只修改隔离分支中的WPF准备ViewModel、现有XAML和测试，没有修改MES/Adapter接口或调用现场设备。

## 新增操作

入口仍为“任务排程 → 工作站任务准备”。加载模板后可在“统一每孔体积 (µL)”输入正整数，点击“应用到全部行”；默认输入50，但不会自动改动模板。操作只改当前表格的体积，随后仍需显式保存准备、导入及独立运行准入。

- 计划汇总：来源数、去重目标孔数、分液条目数、总计划用量。
- 各来源汇总：已绑定瓶号、模块/坐标、来源ID/条码、目标孔数、条目数和计划用量。未使用的第二条码槽不计为实际来源。
- 行内状态：尚未保存准备、有未保存修改、当前表格无未保存修改；有未保存内容时醒目标注，不弹窗。
- 来源/目标/枪头参数和顺序保持原样；单行体积/目标或来源绑定变更后重新汇总。
- 体积无效时禁用批量应用；表内非正体积显示“体积待修正”，不报告有效合计。累计使用long，避免多个int体积相加溢出。
- 无表格、忙碌、只读、Importing/Unknown记录不允许新增批量命令；沿用原保存/导入后端约束，不新增恢复或重试入口。
- 控件置于原准备区，操作按钮采用WrapPanel，长内容允许换行与现有纵向滚动，不扩大固定时间线遮挡区域。

## 离线证据

- 原准备ViewModel/排程绑定基线16/16通过。
- 增补后上述集合31/31通过（新增15例，包括两种窗口尺寸）。
- 同一31项用例在隔离Release输出中再次31/31通过。
- 扩大相关WPF回归：130/130，覆盖Experiment、MesClient、SampleWorkstation测试。
- 隔离solution Release构建0警告、0错误；输出 `C:/Users/33206/AppData/Local/Temp/mes-workstation-editing-20260920-build`，不覆盖运行进程。
- 两来源16孔从1000µL统一改为50µL，来源各400µL、总800µL，其他行字段逐项不变；命令不调用保存/导入/启动。
- 非正数、小数、非数字、int溢出输入均不沿用旧有效值；相同体积不产生新的脏状态；长整型合计、重复目标去重、已冻结身份和任务切换清空均已覆盖。
- 修改后导入资格立即失效；显式保存成功后才恢复。保存忙碌时即使直接执行命令也不修改表格。
- 实际STA WPF控件测试覆盖1380×780与1024×768，输入绑定/按钮状态/汇总提示联动正确；滚动到底可完整看到保存按钮，时间线可见且高度不变。测试使用本地替身，非现场运行界面验收。

```powershell
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore --filter 'FullyQualifiedName~Experiment|FullyQualifiedName~MesClient|FullyQualifiedName~SampleWorkstation' -p:SkipLocalServiceCopy=true --nologo -v minimal
dotnet build MesControlAgv.sln -c Release --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-workstation-editing-20260920-build -p:SkipLocalServiceCopy=true --nologo -v minimal
```

## 接续边界

独立只读审查范围 `bf717ff..18e274a`，无发现；确认批量命令的状态核对、异步任务切换隔离、未保存/导入约束、计划汇总和滚动布局符合已确认设计。

未部署/重启现场WPF或MES/Adapter；未发厂家请求、导入、条码更新或启动；原Unknown记录和厂家复现包未改；未合并 `feature/wpf-ui-layout-optimization`。来源用量是当前任务表的**计划**，不是实际传感结果。厂家导入问题继续以现场导入阻塞及短号兼容记录为准，不能因本界面完成而宣称真机已通过。
