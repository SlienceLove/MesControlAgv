# 离子色谱 ShineLab CSV 导入 RPA POC

状态：高优先级，进行中。

## 当前已验证结论

- 仪器控制软件为 `ShineDataAcquisition`，任务编辑入口位于“分析控制”。
- 在序列的样品任务表中右键可以看到“导出CSV”和“从CSV导入”。
- `ExportData.csv` 是任务表导出的模板，当前字段为：
  `序号,选择,样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,数据名称,色谱方法`。
- ShineLab 的 CSV 导入是追加，不是覆盖。重复导入同一文件会重复创建任务。
- 当前 POC 不调用厂商接口、不直接操作串口、不向 CIC-D160+ 写入寄存器，也不自动点击“运行”。

## 高优先级工作项

1. 固化 ShineLab 导出文件的字段、编码和允许值。
2. 使用新的测试序列验证 CSV 导入后的追加行为和任务字段映射。
3. 用 RPA 自动完成：定位 ShineLab、进入分析控制、打开任务表右键菜单、选择 CSV、等待刷新、校验行数。
4. 增加批次幂等保护，避免导入超时重试造成重复任务。
5. 人工确认任务和仪器状态后，再单独执行“运行”。

## POC 脚本

脚本为 Windows PowerShell UI Automation 实现：

```powershell
# 只校验 CSV，不打开或操作 ShineLab
.\scripts\Invoke-ShineLabCsvImport.ps1 `
  -CsvPath .\res\ExportData.csv `
  -ExpectedRows 7

# 在已经打开序列的 ShineLab 中执行追加导入
.\scripts\Invoke-ShineLabCsvImport.ps1 `
  -CsvPath C:\MES\batch-20260824-001.csv `
  -ExpectedRows 7 `
  -ExecuteImport `
  -AllowAppend
```

当前 ShineLab 的任务表可能以 Qt/自定义控件暴露，UI Automation 不一定能识别为标准 `DataGrid`。如果出现“未定位到样品任务表”，可以在受控测试环境中指定任务表屏幕坐标：

```powershell
.\scripts\Invoke-ShineLabCsvImport.ps1 `
  -CsvPath C:\MES\batch-20260824-001.csv `
  -ExecuteImport -AllowAppend `
  -GridPointX 900 -GridPointY 650 `
  -UseKeyboardMenuFallback
```

`-AllowAppend` 是故意设置的安全确认开关，因为导入行为已经验证为追加。脚本永远不会点击“运行”。

`-UseKeyboardMenuFallback` 适用于 Qt 菜单未被 UI Automation 暴露的版本。它依赖当前已验证的右键菜单顺序：第一项“导出CSV”，第二项“从CSV导入”。如果厂商调整菜单顺序，应停止使用该开关并重新确认菜单。

使用坐标模式时，脚本默认会临时最小化 PowerShell 控制台并把 ShineLab 切到前台，避免鼠标坐标落在控制台窗口上；流程结束或报错时会恢复控制台。只有排查窗口切换问题时才使用 `-KeepConsoleVisible`。

## 使用限制

- 需要 Windows PowerShell 5.1 或支持桌面 UI Automation 的 Windows PowerShell 环境。
- ShineLab 必须在同一台 Windows 控制电脑上运行，且任务序列已经创建或打开。
- Excel 编辑后必须保存为 UTF-8 CSV，保留原始表头、列顺序和空字段；`07` 等带前导零的值应按文本处理。
- 当前仅实现导入 POC，没有实现新建序列、清空旧任务、启动运行和结果回传。
- 当前开发机未运行实际 ShineLab，UI Automation 的菜单、文件对话框和任务行定位需要在仪器控制电脑上做一次受控联调。
- 生产使用前必须增加批次号、导入记录、任务数量/关键字段校验和人工确认策略。

## 现场排错

如果提示 `PathNotFound`，先确认 CSV 实际存在于命令中的路径：

```powershell
Test-Path -LiteralPath C:\MES-RPA\batch-20260824-001.csv
Get-ChildItem -LiteralPath C:\MES-RPA
```

如果提示 `HasValue` 属性不存在，需要重新复制最新版 `Invoke-ShineLabCsvImport.ps1`。这是 Windows PowerShell 5.1 对空的可选屏幕坐标参数的兼容性修复，修复后不提供 `-GridPointX/-GridPointY` 也可以继续自动查找任务表。

如果提示 `Value` 属性不存在，也需要重新复制最新版脚本。这是 Windows PowerShell 5.1 传入坐标后把参数转换为普通整数导致的兼容性问题，最新版已直接使用整数坐标。
