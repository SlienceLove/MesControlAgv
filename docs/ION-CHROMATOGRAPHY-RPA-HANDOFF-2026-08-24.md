# ShineLab RPA 阶段交接

日期：2026-08-24；最新收口：2026-08-27

## 当前结论

ShineLab CSV 导入 RPA 阶段一已经在真实控制电脑上完成人工验收。管理员 PowerShell 中运行 `2026-08-24.13` 脚本后，RPA 成功触发任务表右键菜单、校验并点击“从CSV导入”、识别原生“打开”窗口并提交 CSV；操作人员确认页面显示 7 条样品任务。2026-08-25，MES 生成的小批次也已真机成功追加 3 条。脚本没有点击“运行”。

自动导入后校验、批次幂等和 MES 回执已经实现。2026-08-26 的真机导出暴露出旧 `Full` 比对错位和 ShineLab 空值继承；2026-08-27 已离线完成确定性 `AppendDelta` 门禁与非消耗预检，但新代理尚未部署到控制电脑，端到端 `Verified` 仍未验收，因此仍不是无人值守生产闭环。

## 环境与文件

> 地址更新（2026-08-27）：现场控制电脑已从 `192.168.250.2` 改为
> `192.168.1.108`。下方旧地址仅保留作历史记录，新的共享路径和代理权限
> 需在控制电脑本机重新确认。

- 工作区：`D:\Project\Github\Mes`
- 当前分支：`docs/experiment-workflow-architecture-plan`
- 当前基线提交：`865d040`
- MES 直连地址（历史）：`192.168.250.1`
- 控制电脑地址（当前）：`192.168.1.108`
- SMB（待确认）：`\\192.168.1.108\MES-RPA-Inbox`
- 控制电脑本地目录：`C:\MES-RPA\inbox`
- 当前脚本：`C:\MES-RPA\inbox\Invoke-ShineLabCsvImport-v2026-08-24.13.ps1`
- 当前 CSV：`C:\MES-RPA\inbox\batch-20260824-001.csv`
- 唯一现场说明：`C:\MES-RPA\inbox\ShineLab-RPA-Validation.txt`
- 本地源脚本：[Invoke-ShineLabCsvImport.ps1](../scripts/Invoke-ShineLabCsvImport.ps1)

脚本 SHA256：`C730DFF9EE8B7D15172020DFF8D284E11ADF28DDAD2D83FC34E54FFBD740FAAE`

CSV SHA256：`F226A13969643B529F480BF1B076F6075A427E7AFB9DE707085CAFE280E818BF`

## 已验证事实

- ShineLab 主窗口标题/进程可识别为 `ShineDataAcquisition`。
- 样品任务表类为 `XTPReport`。
- 已验证任务表坐标为 `900,650`，现场桌面为 `1920x1080`。
- 任务表右键菜单第一项是“导出CSV”，第二项是“从CSV导入”。
- 正式成功路径使用 Win32 菜单文本校验，不依赖固定菜单项坐标。
- ShineLab 原生文件窗口类为 `#32770`、标题为“打开”。
- ShineLab 导入会追加，不会覆盖。
- `ShineDataAcquire.exe` 声明 `requestedExecutionLevel="highestAvailable"`；非管理员 PowerShell 的模拟右键会被 Windows 拦截，管理员 PowerShell 可正常操作。
- 成功日志的右键变化为 `采样点 143，范围 938,658-996,690`。
- UI Automation 无法可靠读取导入后的 `XTPReport` 行数，当前由操作人员人工确认 7 条任务。

## 安全边界

- **禁止再次导入 `batch-20260824-001.csv`**，否则会追加第二组 7 条任务。
- 不得把“已提交 CSV”直接等同于“已验证成功”；只有导出比对或人工确认后才能进入 `Verified`。
- 超时或无法判断的批次必须标记为 `Unknown`，不得自动重试。
- RPA 只能在已登录的交互桌面中运行；普通 Windows 服务会话不能可靠操作 ShineLab UI。
- 控制电脑执行端必须提升权限，但 MES 不应获得远程管理员桌面控制能力。
- 脚本继续禁止点击“运行”，禁止直接写串口、寄存器或仪器控制命令。
- 工作树已有其他用户修改和删除项；不要回滚与本任务无关的文件。

## 证据入口

- [POC 证据记录](../artifacts/ion-chromatography/shinelab-rpa-import-evidence-20260824.md)
- [正式导入控制台截图](../artifacts/ion-chromatography/shinelab-rpa-import-console-20260824-1604.jpg)
- [右键菜单证据截图](../artifacts/ion-chromatography/shinelab-rpa-menu-evidence-20260824-160412.png)
- [RPA POC 说明](ION-CHROMATOGRAPHY-RPA-POC.md)
- [总进度](PROGRESS.md)

## 下一阶段执行顺序

1. 冻结 `.13` 为已通过基线，不修改控制电脑上的成功脚本，也不重跑现有批次。
2. 新增只读验证脚本或独立模式：自动选择“导出CSV”，写入新的证据文件，解析导出结果并与期望 CSV 比较。先针对当前已导入序列做只读测试，不触发导入。
3. 比较时忽略可重新编号的展示字段，只校验任务数量和业务关键字段；输出结构化 JSON 结果及字段差异。
4. 定义批次清单和回执：至少包含 `BatchId`、CSV SHA256、目标序列、期望行数、创建时间、状态、错误、证据路径。状态建议为 `Ready / Importing / Submitted / Verified / Failed / Unknown`。
5. 在任何 ShineLab UI 操作前检查幂等键；`Verified`、`Submitted` 或 `Unknown` 批次默认都拒绝再次导入，必须由人工处置后才能生成新批次。
6. 为 MES 新建离子色谱专用 Excel/CSV 解析与 ShineLab CSV 生成模块。可以参考现有 `BatchTaskImportParser` 的文件读取方式，但不要复用其 AGV 运输任务字段模型。
7. MES 通过 SMB 暂存文件并原子改名为 ready；控制电脑上的提升权限交互式代理消费任务并写回回执。MES 只在收到 `Verified` 后显示导入成功。
8. 使用全新的批次和空测试序列做受监督验收，覆盖成功、重复批次、文件窗口失败、超时 Unknown、导出字段不一致五类场景。

## 2026-08-24 只读导出验证进展（离线部分完成）

只读“导出当前序列 → 与期望 CSV 结构化比对”的确定性内核已实现并通过离线测试。控制电脑现场的一次只读导出验证**因网线已断开、无法直连现场设备而顺延到下次**（用户 2026-08-24 决定）。

已完成（离线，可复跑，无需设备）：

- 新增 [scripts/Compare-ShineLabSequence.ps1](../scripts/Compare-ShineLabSequence.ps1)：纯文件读取的结构化比对内核，输出 JSON。默认忽略可重新编号/自动生成的展示字段（`序号`、`选择`、`数据名称`），只校验任务数量和业务关键字段；`status` 仅 `Match`/`Mismatch`。
- 新增 [scripts/Export-ShineLabSequence.ps1](../scripts/Export-ShineLabSequence.ps1)：只读导出驱动。仅执行“导出CSV”（已验证菜单首项），写入受控的新临时文件（拒绝覆盖），随后调用比对内核并归档证据（导出 CSV、比对 JSON、右键截图）。绝不导入、绝不点击“运行”。使用独立 C# 命名空间 `ShineLabRpa.Export`，与已冻结的 `.13` 导入脚本完全隔离。
- 新增离线测试 [tests/shinelab-rpa/Test-CompareShineLabSequence.ps1](../tests/shinelab-rpa/Test-CompareShineLabSequence.ps1) + fixtures：覆盖“仅忽略字段不同→Match”“业务字段不同→Mismatch”“重复追加多一行→ExtraRow”，11 项断言全部通过。
- 已离线验证 `Export-ShineLabSequence.ps1`：AST 解析通过、内嵌 C# 可编译、运行至“未找到 ShineLab 主窗口”即停（无设备时的预期行为）。过程中修复了一个真实缺陷：类型存在性判断缺少括号（`([PSTypeName]'…').Type`），否则现场首次运行即会报错。
- 注意：所有含中文的 `.ps1` 必须保存为 **UTF-8 with BOM**，否则 Windows PowerShell 5.1 按 GBK 解析导致乱码/语法错误。

下次接手（需要现场设备时）：

1. 恢复网线/直连控制电脑，在**管理员** PowerShell 交互桌面运行只读导出：
   `\.\Export-ShineLabSequence.ps1 -ExpectedCsvPath <期望CSV> -ExpectedRows 7 -GridPointX 900 -GridPointY 650 -UseKeyboardMenuFallback -UseKeyboardFileDialogFallback`
2. 现场需验证的未知项：ShineLab“导出CSV”是否弹出原生“保存/导出”窗口、其标题与文件名输入框行为，以及导出文件的编码/表头是否与 `res/ExportData.csv` 一致。若窗口行为与假设不符，先用只读方式探针，不要臆断成功。
3. 只读导出比对通过后，再进入 `BatchId + CSV SHA256 + 目标序列` 幂等回执与 MES 文件交接（原计划第 4 步起）。

## 2026-08-25 中控导入接口完成（随后已真机导入）

上节“下一阶段执行顺序”的第 4 至 8 步已实现并通过离线测试，第 8 步的受监督验收只完成了离线可覆盖的部分。

MES 侧（`src/MesControlAgv.Wpf`）：

- `ShineLabSequenceParser`：读取操作人员的 `.csv`/`.xlsx` 样品任务文件，全字段按字符串保留（`07` 不会变成 `7`），逐行产出问题清单；2026-08-27 起要求 `样品等级/处理方法/清除校正` 显式填写，避免 ShineLab 沿用上一行。
- `ShineLabCsvWriter`：生成 ShineLab 可直接“从CSV导入”的 CSV，严格对齐现场 `res/ExportData.csv`——13 列、表头与数据行行尾都多一个逗号、UTF-8 无 BOM、CRLF、序号从 1 连续编号、`选择`/`数据名称` 留空。
- `ShineLabBatchHandoff`：先写 `.tmp` 再原子改名，再写 `{batchId}.manifest.ready`，随后按 `BatchId + CSV SHA256 + 目标序列` 轮询回执。清单未确认 `allowAppend` 时 MES 侧直接拒绝，不让批次流到现场。
- 中控新增独立的「离子色谱任务导入」页签，不挤占原有仪器状态栅格；只有零问题且至少一条任务时才允许下发。

控制电脑侧：

- [scripts/Start-ShineLabBatchAgent.ps1](../scripts/Start-ShineLabBatchAgent.ps1) 当前为 `batch-agent-2026-08-27.4`：串行消费交接目录，校验 schemaVersion/CSV/SHA256/行数/表头及显式字段，命中账本则跳过重复导入；实际导入前先建立只读基线，随后调用**未修改**的 `.13` 导入脚本，导入后做 `AppendDelta` 比对；回执先 `.tmp` 再原子改名。
- 硬编码安全边界（清单无法放宽）：不含任何点击“运行”的路径，回执 `runTriggered` 恒为 `false`；`allowRun=true` 直接判 `Failed`；仅导出比对 `Match` 才判 `Verified`，无法判断一律 `Unknown` 且不自动重试；不覆盖不删除现有序列文件。
- 生产 `Verified` 还必须满足：导入前基线存在、历史前缀不变、总行数精确增加 `expectedRows`、新增尾段逐字段一致。`AppendTail` 和空值继承只允许用于旧证据人工复核。
- `-ValidateOnly` 与 `-PreflightOnly` 都是单次非消耗检查，不写账本/回执、不移动 ready 文件；后者只观察右键菜单，不选择任何菜单项。

最新离线验证（2026-08-27）：全量 .NET 测试 772 通过 / 5 跳过（既有 E2E）/ 0 失败，构建 0 警告 0 错误；PowerShell 比对器与代理非消耗校验测试全部通过。

## 新会话首个任务

**下一步已经需要实际 ShineLab 控制电脑。** `.4` 代码已完成离线验证；恢复现场操作前先部署代理，并准备一个从未使用的新批次，所有 `样品等级/处理方法/清除校正` 均显式填写。在**管理员** PowerShell 交互桌面按序执行：

1. 纯文件检查：`\.\Start-ShineLabBatchAgent.ps1 -InboxDir <交接目录> -RunOnce -ValidateOnly`。
2. 非消耗菜单探针：`\.\Start-ShineLabBatchAgent.ps1 -InboxDir <交接目录> -RunOnce -PreflightOnly`。确认菜单出现后被关闭，账本、回执和 ready 文件都未变化。
3. 审阅前两步证据后，对**同一个全新小批次**加 `-RunOnce -ExecuteImport` 实跑一次；必要时加 `-UseKeyboardMenuFallback -UseKeyboardFileDialogFallback`。不要重跑任何既有 onsite 批次。
4. 确认回执 `status=Verified`、`runTriggered=false`，比对 JSON 为 `AppendDelta`、`baselineUnchanged=true`、`appendedRowCount=expectedRows`，再人工核对 ShineLab 页面任务数。

不得点击“运行”；启动分析必须现场人工操作。

## 可直接用于新会话的交接摘要

离子色谱 ShineLab CSV 导入 RPA POC 已于 2026-08-24 在控制电脑通过人工验收：管理员 PowerShell 运行 `.13` 后成功导入 `batch-20260824-001.csv`，页面人工确认 7 条任务；非管理员输入会因 ShineLab `highestAvailable` 权限被拦截。导入是追加语义，禁止重跑既有批次，脚本也不得点击“运行”。

截至 2026-08-27，中控 WPF → 规范 CSV → 文件交接 → 控制电脑代理 → `.13` 导入已真机证明可追加任务；`.4` 又离线加入显式字段、非消耗预检和导入前后确定性增量门禁。全量 .NET 772 通过 / 0 失败、构建 0 警告，PowerShell 离线测试全部通过。**尚未完成的是把 `.4` 部署到实际控制电脑并取得首个确定性 `Verified` 回执。**执行时严格按上节停点操作，既有批次禁止重跑，脚本不得点击“运行”。
