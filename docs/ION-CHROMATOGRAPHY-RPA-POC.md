# 离子色谱 ShineLab CSV 导入 RPA POC

状态：P1 暂停；阶段一 CSV 导入 POC 与 MES 规范 CSV 真机导入均已人工验收通过。2026-08-26 已取得真机只读导出，但旧 `Full` 口径把历史行与新增批次错位比较；2026-08-27 已离线完成“导入前快照 + 历史前缀不变 + 精确新增 N 行”的确定性门禁和非消耗预检。新代理尚未部署到控制电脑，`Verified` 仍需下一次实际机器操作确认，详见 [阶段交接](ION-CHROMATOGRAPHY-RPA-HANDOFF-2026-08-24.md)。

## 当前已验证结论

- 仪器控制软件为 `ShineDataAcquisition`，任务编辑入口位于“分析控制”。
- 在序列的样品任务表中右键可以看到“导出CSV”和“从CSV导入”。
- `ExportData.csv` 是任务表导出的模板，当前字段为：
  `序号,选择,样品名称,样品类型,样品等级,处理方法,清除校正,循环次数,进样体积,进样单位,空白,数据名称,色谱方法`。
- ShineLab 的 CSV 导入是追加，不是覆盖。重复导入同一文件会重复创建任务。
- `ShineDataAcquire.exe` 使用 `highestAvailable` 权限；RPA 必须在管理员 PowerShell 的交互桌面中运行。
- 当前 POC 不调用厂商接口、不直接操作串口、不向 CIC-D160+ 写入寄存器，也不自动点击“运行”。

## 阶段一完成项

1. 固化了 ShineLab 导出 CSV 的表头、UTF-8 编码和 7 条测试任务。
2. 验证了导入为追加语义，重复导入会产生重复任务。
3. RPA 已完成前台窗口校验、`XTPReport` 右键、Win32 菜单文本校验、原生文件窗口填写和 CSV 提交。
4. 管理员 PowerShell 下完成空序列现场验证，操作人员确认页面显示 7 条样品任务。
5. 保持人工复核和人工“运行”，脚本未触发仪器执行。

## 阶段二计划

1. 先实现只读的“导出当前序列并比对”验证模式，不再次导入现有批次；比较任务数及样品名称、样品类型、方法、循环次数、进样体积等关键字段。
2. 增加 `BatchId + CSV SHA256 + 目标序列` 幂等键和持久化回执；已完成批次必须在任何 UI 操作前拒绝，超时结果记为 `Unknown` 且禁止自动重试。
3. 为 MES 增加独立的离子色谱 Excel/CSV 模板解析与 ShineLab CSV 生成能力。现有 AGV `BatchTaskImportParser` 的界面和文件读取模式可以参考，但其运输任务字段模型不能直接复用。
4. 定义 MES 与控制电脑的受控文件交接：临时文件写入、原子改名为 ready、控制电脑本地提升权限的交互式代理执行、结果回执和证据归档。
5. 完成新批次的受监督端到端验收：重复批次被拦截、导入后导出比对一致、MES 只在 `Verified` 回执后显示成功；仍不自动点击“运行”。

## POC 脚本

脚本为 Windows PowerShell UI Automation 实现：

当前现场联调版本：`2026-08-24.13`。每次运行开始会打印脚本版本，便于确认控制电脑已替换最新版。

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

当前 ShineLab 的任务表是自定义控件，UI Automation 不一定能识别为标准 `DataGrid`。如果出现“未定位到样品任务表”，可以在受控测试环境中指定任务表屏幕坐标：

```powershell
.\scripts\Invoke-ShineLabCsvImport.ps1 `
  -CsvPath C:\MES\batch-20260824-001.csv `
  -ExecuteImport -AllowAppend `
  -GridPointX 900 -GridPointY 650 `
  -UseKeyboardMenuFallback `
  -UseKeyboardFileDialogFallback
```

`-AllowAppend` 是故意设置的安全确认开关，因为导入行为已经验证为追加。脚本永远不会点击“运行”。

`-UseKeyboardMenuFallback` 适用于 BCG/MFC 自绘菜单未被 UI Automation 暴露的版本。脚本先尝试通过 Win32 菜单句柄读取并校验“从CSV导入”的真实文本；如果自绘菜单不提供标准句柄，才按两次向下键选择第二项。该兜底依赖当前已验证的菜单顺序：第一项“导出CSV”，第二项“从CSV导入”。如果厂商调整菜单顺序，应停止使用该开关并重新确认菜单。

`-UseKeyboardFileDialogFallback` 适用于 MFC 原生文件对话框未被 UI Automation 暴露的情况。脚本只接受属于 ShineLab 进程且标题为“打开/选择”的窗口，明确排除“导出/另存为”窗口；随后把 CSV 的绝对路径写入“文件名”并确认。如果文件框没有关闭，脚本会报错，不会报告导入成功。

使用坐标模式时，脚本默认会临时最小化 PowerShell 控制台并把 ShineLab 切到前台，避免鼠标坐标落在控制台窗口上；流程结束或报错时会恢复控制台。只有排查窗口切换问题时才使用 `-KeepConsoleVisible`。

## 使用限制

- 需要 Windows PowerShell 5.1 或支持桌面 UI Automation 的 Windows PowerShell 环境。
- ShineLab 必须在同一台 Windows 控制电脑上运行，且任务序列已经创建或打开。
- Excel 编辑后必须保存为 UTF-8 CSV，保留原始表头、列顺序和空字段；`07` 等带前导零的值应按文本处理。
- 当前仅实现导入 POC，没有实现新建序列、清空旧任务、启动运行和结果回传。
- 当前坐标和控件行为仅在本次控制电脑、`1920x1080` 桌面和现有 ShineLab 版本上验证；换分辨率、缩放或软件版本后必须重新探针。
- 生产使用前必须增加批次号、导入记录、任务数量/关键字段校验和人工确认策略。

## 现场排错

如果提示 `PathNotFound`，先确认 CSV 实际存在于命令中的路径：

```powershell
Test-Path -LiteralPath C:\MES-RPA\batch-20260824-001.csv
Get-ChildItem -LiteralPath C:\MES-RPA
```

如果提示 `HasValue` 属性不存在，需要重新复制最新版 `Invoke-ShineLabCsvImport.ps1`。这是 Windows PowerShell 5.1 对空的可选屏幕坐标参数的兼容性修复，修复后不提供 `-GridPointX/-GridPointY` 也可以继续自动查找任务表。

如果提示 `Value` 属性不存在，也需要重新复制最新版脚本。这是 Windows PowerShell 5.1 传入坐标后把参数转换为普通整数导致的兼容性问题，最新版已直接使用整数坐标。

`2026-08-24.11` 的菜单探针截图已确认 XTP 右键菜单实际出现，菜单约位于 `x=728..884, y=554..604`，第二项“从CSV导入”的中心约为 `805,590`。`2026-08-24.12` 增加 `-ImportMenuPointX/-ImportMenuPointY`，在确认目标仍属于 ShineLab 进程后直接点击第二项，不再依赖 XTP 菜单键盘高亮状态。

## 2026-08-24 现场验收结果

- `.13` 增加右键前后截图差异校验和物理坐标记录。非管理员 PowerShell 下结果为 `采样点 0，范围 none`，确认模拟右键没有送达 ShineLab。
- `ShineDataAcquire.exe` 内嵌清单声明 `requestedExecutionLevel="highestAvailable"`。改用管理员 PowerShell 后，右键菜单正常出现，确认根因是 Windows 完整性级别不一致，不是任务表坐标错误。
- 正式导入日志确认：右键变化采样 143、Win32 菜单文本匹配“从CSV导入”、检测到 `#32770 / 打开` 原生文件窗口并提交 CSV。
- 操作人员人工确认 ShineLab 页面显示 7 条样品任务。因此 CSV 导入 RPA POC 判定为**人工验收通过**。
- `XTPReport` 仍未向 UI Automation 暴露可靠行数，当前不能自动验证导入后数量。批次幂等保护和自动字段校验仍是生产化前置项。
- 完整证据见 [ShineLab CSV 导入 RPA POC 证据记录](../artifacts/ion-chromatography/shinelab-rpa-import-evidence-20260824.md)。
- 新会话继续工作前先阅读 [ShineLab RPA 阶段交接](ION-CHROMATOGRAPHY-RPA-HANDOFF-2026-08-24.md)。

## 2026-08-25 现场验收结果（MES 下发链路首次真机导入）

- 批次 `batch-20260825-onsite-03`（3 条样品任务，CSV SHA256 `F651E8EA…0822B`）由 MES 侧
  `MesControlAgv.ShineLabDispatcher` 走生产路径下发，控制电脑代理单次执行并带 `-ExecuteImport`，
  ShineLab 空测试序列新增 水样-101/102/103 共 3 条，操作人员人工确认。
- **本次证实：ShineLab 接受 MES 生成的规范 CSV**（13 列、表头与数据行尾各多一个逗号、
  UTF-8 无 BOM、CRLF）。这是此前只能靠现场确认的两个未知项之一。
- 一个批次号只能跑一次代理。代理在发出导入动作**之前**先占幂等账本（导入不可回滚，
  崩溃也不允许重放），因此不带 `-ExecuteImport` 的演练同样会消耗批次号。
  `onsite-01`/`onsite-02` 即因此作废，现场文档已删除全部演练步骤。
- 代理包装层缺陷已修（`batch-agent-2026-08-25.2`）：严格模式下读取从未赋值的
  `$LASTEXITCODE` 会抛异常，导致导入成功却判 `Unknown`、且 `import.log` 未落盘。
  修法为日志先落盘、退出码改用容错 helper 读取。演练路径因走 `exit 0` 恰好掩盖了该缺陷。
- 仍未验证：只读导出比对在真机上尚未成功执行，`Verified` 判定链路未端到端跑通；
  「导出CSV」保存对话框标题与编码行为待确认。导出为只读、不追加任务，可单独补跑。
- 样品等级在界面不显示一事，MES 侧已排除：CSV 第 5 列为 `样品等级`、第 3 行为 `"07"`，
  与本地留档逐字节一致。操作人员反馈手工导入同样不显示，需用 ShineLab 自身导出的 CSV
  才能区分「未存该字段」「存了但不在当前视图列」「前导零被吞」。

## 2026-08-26 至 2026-08-27 导出证据与离线收口

- `batch-20260826-onsite-07` 已在真机完成导入和只读导出。导出共 10 行，其中前 7 行为
  历史任务、末 3 行为本批次；旧代理按 `Full` 比较而判 `Failed`。`AppendTail` 可用于说明
  末 3 行存在，但不能证明历史前缀未变或没有额外插入任务，因此不再用于生产 `Verified`。
- 真机导出还证实：CSV 中为空的 `样品等级/清除校正` 会被 ShineLab 沿用上一行。该行为
  不能默认为业务等价；MES parser、writer 和控制电脑代理现均要求
  `样品等级/处理方法/清除校正` 显式填写，空值在任何 UI 操作前拒绝。
- `batch-agent-2026-08-27.4` 在实际导入前先做只读快照，快照成功前不占账本、不导入；
  导入后使用 `AppendDelta`，只有历史前缀不变、行数精确增加 N、末 N 行逐字段一致才判
  `Verified`。空序列基线也有离线回归覆盖。
- `-RunOnce -ValidateOnly` 为纯离线非消耗检查；`-RunOnce -PreflightOnly` 为现场菜单探针，
  只右键观察后关闭，不选择菜单。两者都不写账本/回执、不移动 ready 文件。
- 离线验证：PowerShell 两套测试全部通过；全方案构建 0 警告/0 错误；.NET 772 通过、
  5 个既有 E2E 跳过、0 失败。下一步需在实际控制电脑部署 `.4`，依次执行非消耗检查和
  一个全新小批次，取得 `Verified`、`runTriggered=false`、`baselineUnchanged=true`。
