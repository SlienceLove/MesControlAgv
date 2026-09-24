# 606 模型清理交接（2026-09-21）

## 范围与结果

工作分支 `feature/digital-twin-single-agv`，独立目录 `.worktrees/digital-twin-single-agv`，起点 `develop/ad2d958`。未修改 UI 会话或三设备执行草稿，未操作设备、切换现场窗口、提交、合并或推送。

按用户确认，从模型中删除两处冗余几何：

- `environment` 中白色静态 AGV／机械臂／背架整体，188,464 个三角面。空间选择框 min `[15.5,-0.04,-9.15]`、max `[16.45,1.5,-8.05]`，跨边界相交面为 0。
- `decapper-a` 前侧与工作站重叠的多余盒体，288 个三角面；按独立连通分量精确选择，不切割工作站主体。世界边界 min `[17.810136795043945,0.8400686534150736,-6.543854653835297]`、max `[18.173513740301132,0.9198497608304024,-6.287953197956085]`。

保留蓝色联动 AGV＋机械臂＋背架整体、D160、SHA18i、工作站主体和实验台。所有节点、变换、材质不变，AGV／D160／SHA18i 的 Draco 数据逐字节不变。两个编辑网格保留部分的三角面坐标、法线及朝向集合完全一致，并通过重新解码校验和独立只读复核。

## 资源与体积权衡

| 项目 | 原版 | 清理版 |
| --- | --- | --- |
| 三角面 | 1,287,310 | 1,098,558 |
| 字节 | 5,887,020 | 33,842,572 |
| SHA-256 | `96ca7f34c189c9c1add62c29441a7a9b16e67619c96659c259dca7ea8412671a` | `acb34c95e5c53c713d0a4c5246f90bb179c90ae9371626307621522c24df0e5a` |

编辑网格使用无损 sequential Draco。常规 Edgebreaker 编码会丢弃原模型中 463 个退化面，未通过精确保留检查，因此没有采用。当前版本面数下降但文件增大，不属于体积优化；未对剩余设备再次降精度。离线加载与移动连续性通过，尚未进行新旧冷启动耗时对比或本版实机帧率验收。

资源指纹、面数检查及 `prepare_wpf_assets.ps1` 已同步。准备脚本默认采用已清理的仓库模型；显式 `-ValidatedModelPath` 也须匹配新指纹，避免恢复旧物体。AGV 示意坐标映射、平滑动画和状态读取逻辑未修改。

正式标定文件未修改；旧模型指纹绑定的正式标定仍会被严格拒绝，未自动迁移或放宽校验。既有示意对齐参数保持不变。

## 复现与测试

在本工作区执行，首次需要安装工具依赖（运行时查看器不需要 Node.js）：

```powershell
npm.cmd ci --prefix scripts/digital-twin/model-tools
# 从已提交的原始基线恢复，不依赖本机 artifacts 备份或未跟踪 res 文件。
# 此输出目录须不存在；已有目录请换一个新名字，不覆盖旧证据。
$reproductionDir = 'artifacts/model-cleanup-reproduction'
if (Test-Path -LiteralPath $reproductionDir) { throw '请使用新的复现目录' }
New-Item -ItemType Directory -Path $reproductionDir | Out-Null
git archive --format=zip --output="$reproductionDir/parent.zip" ad2d958 src/MesControlAgv.Wpf/DigitalTwin/Web/lab606.glb
if ($LASTEXITCODE -ne 0) { throw '原模型归档失败，请确认 Git 历史包含 ad2d958' }
Expand-Archive -LiteralPath "$reproductionDir/parent.zip" -DestinationPath "$reproductionDir/parent"
node scripts/digital-twin/model-tools/remove-obsolete-models.mjs "$reproductionDir/parent/src/MesControlAgv.Wpf/DigitalTwin/Web/lab606.glb" "$reproductionDir/reproduced.glb"
node --test tests/digital-twin/model-cleanup.test.mjs tests/digital-twin/pose-motion.test.mjs tests/digital-twin/camera-framing.test.mjs tests/digital-twin/map-ground.test.mjs
```

转换工具要求原模型精确 SHA；输出文件必须尚不存在，生成结果 GLB、`.report.json` 和仅供选区检查的 `.highlight.glb`，不覆盖源模型。原版也可从基线 Git 或交付模型父资源恢复。

`CentralClientSmoke --model-review` 同样要求使用新的输出目录，避免覆盖既有截图和诊断资源。

验证结果：

- Node：24/24 通过，包括模型指纹、保留几何、独立对象、位姿、镜头和地面。
- WPF：621/621 通过，筛选 `FullyQualifiedName!~ShineLabHandoffRehearsalTests`。排除的 2 项无关演练测试通过 `.git` 目录向上定位，会在 linked worktree 中越界写入主目录已有演练产物；本次未更改其实现，不宣称完整 623 项通过。
- 原生 WPF/WebView 位姿冒烟通过：刚体跟随、示意对齐、地图参照、工作站无响应隔离、直行及转弯抖动连续性、卸载停止轮询。
- 原生全屏冒烟通过：按钮／快捷键路径、复用场景、隐藏详情、地图选择保留和错误恢复。结果标记 `nativeKeyboard:false`，不是物理键盘输入测试。
- 只读审查独立解码对照通过，无阻断问题。

以上均使用离线合成坐标／假 MES HTTP，不构成设备实机验收。全屏截图中的状态和错误用于测试，不代表现场设备状态。

## 本地证据与交付

`artifacts/single-agv-model/` 下保留原版备份、新版、转换报告、截图和测试结果。证据和构建产物不整体提交 Git。

- `review-before/`、`review-after/`：相同固定镜头下的 `box.png`、`robot.png`、`overview.png`。
- `review-highlight-r4/`：有效的红色删除选区预览；早期诊断尝试不作为验收依据。
- `smoke-pose-after/result.json`、`smoke-fullscreen-after/result.json`：离线原生验证结果。
- `tests/wpf-model-cleanup.trx`：WPF 测试结果。

独立 WPF 包输出到 `artifacts/single-agv-model/wpf`，不含本地服务，需使用现有服务或另行准备；本次不启动它、不替换原窗口。打包命令：

```powershell
dotnet publish src/MesControlAgv.Wpf/MesControlAgv.Wpf.csproj --no-restore -c Release -p:SkipLocalServiceBuild=true -p:SkipLocalServiceCopy=true -o artifacts/single-agv-model/wpf
```

**新模型必须与包含新资源指纹的 WPF 一起交付**，不能只将 GLB 覆盖进旧程序，否则资源验证失败会禁用跟随。需要回退时恢复匹配的旧程序和旧模型；原版与源资源仍可恢复。切换现场窗口、提交和集成另行按用户指令进行。

## 用户确认后的界面启用

用户确认效果并要求更新到软件界面后，已启动上述独立包（本次 PID 38632），切换数字孪生页并确认新模型显示。截图：`artifacts/single-agv-model/live-client-updated.png`。沿用已记录的 606 地图、现场 `adapter-effective-for-wpf.json` 和 `workflows.json`，连接 MES 15445／Adapter 15441；本地服务托管及实机批量执行开关均为 false，未发送设备动作。

原窗口 PID 39884 来自 `workstation-template-selection/wpf`，包含另一会话尚未集成的工作站模板改动，予以保留，没有覆盖其文件或关闭窗口。后台 MES PID 37684、Adapter PID 39856 保持运行。当前新窗口显示 AGV 离线、机械臂／工作站读取失败、位置同步已停止；模型已加载，但不能据此宣称实时设备跟随正常。仍未提交、合并或推送。

## 2026-09-24 补提交接管

用户确认此前遗漏提交，明确授权本会话接管此 worktree，将清理模型及配套指纹、工具、测试和本文一起提交并合入develop。原有构建/截图证据和未跟踪规划草稿保留，不整体提交；不纳入three-device-sequence的执行草稿。接管后Node24项、WPF数字孪生91项复测通过；首次构建缺少本地服务依赖资产，完成解决方案restore后通过，未修改业务源码。合入与新界面包验证记录另附。
