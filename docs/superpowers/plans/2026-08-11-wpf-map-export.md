# WPF 地图 PNG 导出实施计划

> 状态：已完成（2026-08-11）

## 已交付实现

- `MapExportMode` 提供当前视口和完整地图两种范围；`MapExportPlanner`
  负责有限尺寸、文件名清洗和 `8192` 单边 / `32,000,000` 总像素上限。
- `MapPngExportService` 在 WPF Dispatcher 线程通过 `VisualBrush`、
  `RenderTargetBitmap` 和 `PngBitmapEncoder` 写入 PNG。
- 地图变换移至 `MapTransformHost`，使内部 `MapCanvas` 保持未变换：当前
  视口从 `MapScrollViewer` 导出，完整地图直接从 `MapCanvas` 导出，不会
  修改用户的缩放、平移、图层或选择状态。
- 工具栏提供 `导出当前 PNG` 和 `导出完整 PNG`，生产路径选择使用
  `SaveFileDialog`；取消没有文件副作用，成功和失败都显示明确反馈。
- 输出复用现有视觉树，因此墙线、路线、站点标签、运行叠加和障碍扫描
  的当前开关会一致反映在 PNG 中。身份不是 `Match` 时，既有
  `RuntimeOverlayAllowed` 门禁仍使运行叠加不可见。

## 验证记录

- 实际 Simulator-only `.smap` 验收：当前视口导出 `918 x 506`，完整地图
  导出 `1140 x 780`；PNG 签名均为 `89 50 4E 47 0D 0A 1A 0A`。
- 完整地图导出确认包含 `normalPosList` 的深色障碍扫描层，且运行叠加在
  当前 Profile 身份不一致时保持关闭。
- Release 构建：0 warnings / 0 errors；Release 全量测试：327/327。

## 原始规划说明（已由上述实现取代）

初稿中的 `1x/2x/3x` 用户倍率、`4096` / `16,777,216` 上限和专用请求/结果
模型没有进入最终实现。最终方案使用原生视觉尺寸、统一的 `MapExportPlan`
以及更高但有界的 `8192` / `32,000,000` 上限；以下记录保留为当时的设计
推演，不是当前 API 契约。

## 目标

在现有只读 WPF 地图视图中提供 PNG 导出。操作员可选择导出当前视口，或导出完整地图；导出的内容必须与导出瞬间的图层开关一致。地图身份验证不是 `Match` 时，运行 AGV 与活动路径叠加必须继续 fail-closed，不能出现在任何导出 PNG 中。

导出是本地只读渲染和文件保存能力。它不调用 MES、Adapter、Simulator 或实体 AGV。

## 范围和非目标

本阶段包含：

- 当前视口与完整地图两种 PNG 导出范围。
- 墙线、路线、站点标签、运行叠加、栅格背景五类既有图层的可见性一致性。
- 导出尺寸、缩放倍率和像素上限校验。
- `SaveFileDialog` 的路径选择、取消处理和 PNG 文件写入。
- 单元测试和本地手工验收，包括 PNG 签名与实际像素尺寸。

明确不包含：

- PDF、SVG 或其他导出格式。
- 地图、站点、路线或图层配置编辑。
- 对 `.smap` 的写回、格式转换或覆盖。
- 实体 AGV 的连接、查询、派发、控制、运动或现场验收。

## 现有基础与约束

- 地图视觉根为 `MainWindow.xaml` 中的 `MapViewportHost` / `MapCanvas`；缩放和平移来自 `Readiness.Viewport`。
- `MapLayerStateViewModel` 是图层可见性的唯一状态源；`RuntimeOverlayAllowed=false` 时会强制 `ShowRuntimeOverlays=false`。
- `ReadinessViewModel` 已用 `MapLayoutIdentityVerifier` 计算地图身份状态，并在状态不是 `Match` 时关闭运行叠加。因此导出不得绕过这条绑定或从原始 AGV/路径集合重建可视元素。
- 所有 WPF 视觉创建、布局、`RenderTargetBitmap.Render` 和 `PngBitmapEncoder` 调用必须在 UI Dispatcher 上完成。编码完成后的冻结位图可在后台写文件，但不得在后台读取或修改活跃 WPF 视觉树。
- 保持 `TreatWarningsAsErrors=true`、`Nullable=enable` 和现有只读安全边界。

## 导出契约

新增下列 WPF 侧类型，放在 `MesControlAgv.Wpf/Services`（或与现有地图视图模型同层的明确命名空间），不引入 Domain、MES 或 Adapter 依赖：

```csharp
public enum MapPngExportScope
{
    CurrentViewport,
    FullMap
}

public sealed record MapPngExportRequest(
    MapPngExportScope Scope,
    int ScaleMultiplier);

public sealed record MapPngExportDimensions(
    int PixelWidth,
    int PixelHeight);

public sealed record MapPngExportResult(
    string FilePath,
    MapPngExportScope Scope,
    MapPngExportDimensions Dimensions);
```

`MapPngExportDimensions` 由一个不依赖对话框或文件系统的纯函数计算，方便边界测试：

- `CurrentViewport` 的基础尺寸是 `MapViewportHost` 的实际可见客户区尺寸（包括地图画布可见部分，不包含右侧详情栏、工具栏或窗口标题栏）。它保留现有 Scale/Translate/Clip 的效果。
- `FullMap` 的基础尺寸是未变换地图内容 `MapCanvas.Width x MapCanvas.Height`，以 1.0 地图比例完整绘制，不受当前平移、缩放、选择状态或窗口尺寸影响。
- 用户可选 `1x`、`2x`、`3x`；输出像素尺寸为基础尺寸乘以倍率，并向上取整。
- 宽和高均必须位于 `1..4096`，总像素数必须不超过 `16,777,216`（4096 x 4096）。超限、零尺寸、NaN 或 Infinity 时，导出前显示明确错误且不打开写入流、不生成文件。UI 对不能满足上限的倍率禁用或在执行时拒绝，两处均要守住校验。

选择输出路径的接口必须可以在测试中替换。生产实现用 `Microsoft.Win32.SaveFileDialog`，并设置：

- `Filter = "PNG image (*.png)|*.png"`
- `DefaultExt = ".png"`
- `AddExtension = true`
- `OverwritePrompt = true`
- 默认文件名包含 `map-current` 或 `map-full` 与本地时间戳。

取消对话框是无副作用成功取消，不创建空文件，也不把取消显示为导出失败。

## 渲染设计

为避免完整地图导出受当前 `RenderTransform` 影响，重构视觉层次但不改变现有交互语义：

1. 将承载地图子项的未变换 Canvas 明确命名为 `MapContentCanvas`；把 Scale/Translate 保留在外层的地图内容宿主上。`MapViewportHost` 仍负责裁剪、鼠标缩放和平移。
2. 当前视口导出渲染 `MapViewportHost` 的地图客户区，输出与用户当时看到的画布范围一致。不能捕获整个窗口、右侧详情栏或工具栏。
3. 完整地图导出使用 `MapContentCanvas` 的未变换视觉，以 `CanvasWidth` / `CanvasHeight` 作为 source rectangle 创建离屏 `DrawingVisual` 或等价视觉，再渲染到目标像素尺寸。禁止在导出期间临时改写 `Readiness.Viewport`、切换图层、改变选择或触发数据刷新。
4. 两种范围都从同一张已完成布局的地图视觉树渲染，因此自然继承墙线、路线、站点标签和栅格图层的当前开关。
5. 运行叠加额外执行门禁：渲染前读取 `MapLayerStateViewModel.RuntimeOverlayAllowed` 与 `ShowRuntimeOverlays`。只要前者为 `false`，导出视觉中运行路径和 AGV 标记必须被排除，即便调用方错误地尝试把 `ShowRuntimeOverlays` 设为 `true`。不要从 `Agvs`、`VisualAgvs` 或 `AgvPathSegments` 另行绘制运行元素。
6. 使用 `RenderTargetBitmap(width, height, 96 * multiplier, 96 * multiplier, PixelFormats.Pbgra32)` 与 `PngBitmapEncoder`。保存前冻结可冻结对象；写入使用显式 `FileStream`，仅在对话框确认和尺寸验证成功后打开目标文件。

## 任务拆分（TDD）

### Task 1：导出请求、尺寸与图层快照模型

**Files:**

- Create: `src/MesControlAgv.Wpf/Services/MapPngExportModels.cs`
- Create: `tests/MesControlAgv.Wpf.Tests/MapPngExportModelsTests.cs`

**实现：**

- 定义 `MapPngExportScope`、请求、结果和尺寸模型。
- 实现 `MapPngExportDimensionCalculator.Calculate(...)`，集中处理尺度、有限数、向上取整和 `4096` / `16,777,216` 上限。
- 定义不可变 `MapExportLayerSnapshot`，从 `MapLayerStateViewModel` 读取五个图层状态；当 `RuntimeOverlayAllowed=false` 时无条件令其 `RuntimeOverlays=false`。

**测试：**

- 当前视口和完整地图在 `1x/2x/3x` 的正确像素尺寸。
- `4096x4096` 可接受；任一维度超过 4096、总像素超过上限、零尺寸、负尺寸、NaN 与 Infinity 都被拒绝。
- 隐藏的墙线、路线、站点标签、栅格状态原样保留。
- `RuntimeOverlayAllowed=false` 时，无论请求值为何，快照始终关闭运行叠加。

### Task 2：离屏 PNG 渲染器

**Files:**

- Create: `src/MesControlAgv.Wpf/Services/MapPngExporter.cs`
- Modify: `src/MesControlAgv.Wpf/MainWindow.xaml`
- Modify: `src/MesControlAgv.Wpf/MainWindow.xaml.cs`
- Modify: `tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj`（显式启用 `UseWPF`）
- Create: `tests/MesControlAgv.Wpf.Tests/MapPngExporterTests.cs`

**实现：**

- 完成 `MapContentCanvas` / transform 宿主分离，使完整地图渲染不依赖当前视口变换。
- `MapPngExporter` 接收已验证的尺寸、范围、相关视觉引用和图层快照，返回冻结的 PNG 位图或不可变字节数组。它不显示对话框、不写文件，便于单独验证。
- 当前视口严格应用客户区裁剪；完整地图严格使用未变换内容边界。
- 将 export snapshot 应用于离屏视觉；尤其确保身份不一致时运行叠加不会出现在位图中。

**测试：**

- 在 STA 测试中创建固定尺寸测试视觉，验证 `CurrentViewport` 只包含裁剪区域，`FullMap` 包含所有地图内容且不受现有 Scale/Translate 影响。
- 切换每个图层，验证相应的测试色块或几何出现在/不出现在输出像素中。
- 身份状态设为 `Mismatch` 或 `Unverifiable` 时，即使测试输入存在橙色 AGV/路径视觉，输出中也没有该运行叠加像素；其余静态图层仍保留。
- PNG 编码后的首 8 字节必须为 `89 50 4E 47 0D 0A 1A 0A`，用 PNG `IHDR` 的大端宽高或 `BitmapDecoder` 断言实际像素尺寸等于请求尺寸。

### Task 3：文件保存与地图工具栏交互

**Files:**

- Create: `src/MesControlAgv.Wpf/Services/MapPngExportFileService.cs`
- Modify: `src/MesControlAgv.Wpf/MainWindow.xaml`
- Modify: `src/MesControlAgv.Wpf/MainWindow.xaml.cs`
- Create: `tests/MesControlAgv.Wpf.Tests/MapPngExportFileServiceTests.cs`
- Modify: `tests/MesControlAgv.Wpf.Tests/ControlCenterBoundaryTests.cs`（如需要，固定只读边界）

**实现：**

- 抽象 `IMapPngSaveDialog`，生产实现包装 `SaveFileDialog`，测试替身返回确认、取消和指定临时路径。
- 在地图工具栏加入清晰的“导出 PNG”命令。命令先选择范围（当前视口/完整地图）和倍率，再打开保存对话框；范围与倍率必须在开始渲染前已确定。
- 取消立即返回；校验失败显示原因；成功后显示文件名、范围和实际像素尺寸。没有静默降级或超限自动缩小。
- 写入失败保留原始异常上下文的用户可读消息；不更改地图、视口、图层或 AGV 状态。

**测试：**

- 对话框取消时 exporter 和文件系统写入器均不被调用。
- 确认路径和合法图像时，生成非空 `.png`，签名及尺寸正确，结果包含实际路径和范围。
- 非法尺寸在对话框确认后仍不得创建文件。
- 保存服务不引用 `IMesClient`、Adapter、Simulator 或任何 AGV 命令接口。

### Task 4：回归、手工验收与文档状态

**Files:**

- Modify: `docs/PROGRESS.md`
- Modify: `docs/superpowers/plans/2026-08-11-wpf-map-export.md`
- Optional artifact: `artifacts/wpf-map-export-<timestamp>.png`

**验证：**

1. `dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj --no-restore`
2. `dotnet test MesControlAgv.sln --configuration Release --no-restore`
3. `dotnet build MesControlAgv.sln --configuration Release --no-restore`
4. `git diff --check`
5. 在本地 Simulator 场景打开地图，分别导出：
   - 经过缩放和平移的当前视口，确认 PNG 与可见地图客户区一致；
   - 完整地图，确认所有地图边界均在输出中，不受当前视口位置影响；
   - 关闭墙线、路线、站点标签和栅格后，确认 PNG 同步隐藏对应内容；
   - 让地图身份为 `Mismatch` 或 `Unverifiable`，尝试打开运行叠加并导出，确认控件仍禁用、输出没有 AGV 或活动路径。
6. 对每张验收 PNG 运行独立检查：首 8 个字节等于 PNG 签名，`IHDR` 或图片解码器报告的宽高等于 UI 成功消息中的尺寸，且未超过像素上限。

## 完成标准

以下条件全部满足才可将本计划标记完成：

- 两个导出范围都可用，且语义符合“当前视口”与“完整未变换地图”。
- 所有五个图层开关都在导出中得到一致体现。
- 身份不是 `Match` 时，运行叠加在 UI 和两个导出范围内均不可见；该条件有自动化像素级测试。
- 每个导出的宽、高、总像素数受严格上限约束，非法请求没有文件副作用。
- `SaveFileDialog` 仅提供 PNG，取消无副作用，已保存文件有有效 PNG 签名和经解码验证的尺寸。
- Release 构建、全量测试、`git diff --check` 和手工导出验收通过。
- 没有 PDF、编辑、`.smap` 写回、MES/Adapter/Simulator 调用或实体 AGV 行为进入变更。
