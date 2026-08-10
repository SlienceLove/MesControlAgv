# WPF 地图视图增强 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 让 WPF 地图视图基于 RoboshopPro `.smap` 文件的真实物理坐标渲染站点与路线，并支持缩放、平移和 AGV 移动动画。

**Architecture:** 在 `MesControlAgv.Domain/Map/` 新增纯 .NET 的 `.smap` 解析器与坐标转换器（无 WPF 依赖，可被 net8.0 测试项目直接测试）；在 `MesControlAgv.Wpf` 侧新增地图数据加载器、交互状态 ViewModel 和动画控制器；`MapViewModel` 增加一条「使用 smap 布局」的路径，保留原有 MES 自动布局作为回退。

**Implementation status (2026-08-10):** Tasks 1-9 are implemented. The runtime
uses `advancedCurveList` for navigable Bezier routes and `advancedLineList` for
wall lines, exposes route-area and full-map framing, places direction arrows
inside curves, and disables runtime overlays when the loaded `.smap` identity
cannot be proven against the MES Profile. Release solution tests pass
`312/312`; the final local visual evidence includes
`artifacts/wpf-map-route-focus-final-20260810.png` and
`artifacts/wpf-map-observability-final-20260810.png`. The follow-on
`2026-08-10-wpf-map-observability.md` plan delivered bounded raster scan
points, layer toggles, and read-only station details. Map export and physical
AGV acceptance remain outside both phases.

**Tech Stack:** C# 12、.NET 8、`System.Text.Json`、WPF（Canvas / RenderTransform / DoubleAnimation）、xUnit。

## Global Constraints

- 目标框架：Domain 为 `net8.0`，WPF 及其测试项目为 `net8.0-windows`；`LangVersion` 为 `12.0`。
- `Directory.Build.props` 设置了 `TreatWarningsAsErrors=true`：任何警告都会导致构建失败，禁止留下未使用变量、可空性警告。
- `Nullable` 与 `ImplicitUsings` 均为 `enable`。
- 解析与坐标计算逻辑必须放在 `MesControlAgv.Domain`（`net8.0`，不引用 WPF），以便 `MesControlAgv.Domain.Tests` 测试。
- `tests/MesControlAgv.Wpf.Tests` 当前**没有** `<UseWPF>true</UseWPF>`，因此现有测试无法引用 `System.Windows.*` 类型。任务 6 会显式添加该属性；在此之前不要在 WPF 测试中使用 WPF 类型。
- WPF 应用**没有** `IConfiguration` / `appsettings.json`；配置只通过环境变量读取（见 `src/MesControlAgv.Wpf/App.xaml.cs` 中的 `ReadBaseUrl`）。本计划使用环境变量 `MAP_SMAP_PATH` 与 `MAP_STATION_MAPPING_PATH`，不引入 `appsettings.json`。
- `.smap` 中省略的坐标键表示 `0.0`（实测：`LM1` 的 `pos` 只有 `{"y":0.8}`，即 x=0.0）。解析器必须容忍缺失键并填 0，不得抛异常。
- 不改变 MES / Adapter / Simulator 的任何 API 或数据库；MES 仅用于对比验证。
- 单位：`.smap` 使用米，Canvas 使用像素；物理坐标系 Y 轴向上，Canvas Y 轴向下，转换时必须翻转 Y。

## `.smap` 格式实测结论（重要，与 spec 有一处修正）

以 `guangzhou606.smap`（878KB，JSON，`version` 为 `1.0.6`）为准：

| JSON 字段 | 内容 | 用途 |
|---|---|---|
| `header` | `mapName`/`version`/`minPos`/`maxPos`/`resolution` | 地图边界与指纹 |
| `normalPosList` | 36559 个 `{x,y}` | 激光扫描点（墙体/障碍物），可选背景 |
| `advancedPointList` | 5 个 `LocationMark`（`LM1`..`LM5`） | **站点位置** |
| `advancedLineList` | 151 个 `FeatureLine`，`instanceName` 全为 `"0"` | **激光特征线（墙体），不是可行驶路径** |
| `advancedCurveList` | 9 个 `DegenerateBezier`，`instanceName` 形如 `"LM1-LM2"` | **可行驶路线（贝塞尔曲线）** |

**修正：** 设计文档中「路径边从 `advancedLineList` 渲染」不正确。真正的路线图在 `advancedCurveList`：每条曲线的 `startPos`/`endPos` 各带 `instanceName`（引用站点）与 `pos`，另有 `controlPos1`/`controlPos2` 两个贝塞尔控制点，`property` 中含 `direction` 与 `movestyle`。本计划按 `advancedCurveList` 构建路线图，`advancedLineList` 仅作为可选的墙体图层。

---

## 任务分解（TDD，逐任务提交）

### Task 1: `.smap` 数据模型与解析器（Domain，net8.0）

**Files:**
- Create: `src/MesControlAgv.Domain/Map/SmapModels.cs`
- Create: `src/MesControlAgv.Domain/Map/SmapParser.cs`
- Test: `tests/MesControlAgv.Domain.Tests/Map/SmapParserTests.cs`

**Interfaces:**
- Consumes: 无（基础任务）。
- Produces（供 Task 2/3 消费）：
  - `readonly record struct MapPoint(double X, double Y)`
  - `sealed record SmapHeader(string MapType, string MapName, MapPoint MinPos, MapPoint MaxPos, double Resolution, string Version)`
  - `sealed record SmapLocationMark(string InstanceName, MapPoint Pos)`
  - `sealed record SmapFeatureLine(MapPoint Start, MapPoint End)`
  - `sealed record SmapCurve(string InstanceName, string StartMark, MapPoint Start, string EndMark, MapPoint End, MapPoint Control1, MapPoint Control2, int Direction, int MoveStyle)`
  - `sealed record SmapDocument(SmapHeader Header, IReadOnlyList<MapPoint> NormalPosList, IReadOnlyList<SmapLocationMark> LocationMarks, IReadOnlyList<SmapFeatureLine> FeatureLines, IReadOnlyList<SmapCurve> Curves)`
  - `static class SmapParser`：`SmapDocument Parse(Stream json)` 与 `Task<SmapDocument> ParseFileAsync(string path, CancellationToken ct = default)`
  - `sealed class SmapParseException : Exception`

**Step 1 — Write failing test** (`SmapParserTests.cs`)：
- `Parses_header_and_defaults_missing_coordinates_to_zero`：喂入含 `header`/`advancedPointList`（一个 `LM1`，`pos` 只有 `{"y":0.8}`）的最小 JSON，断言 `Header.MapName`、`Header.Version`、`Header.MinPos.X`，断言 `LocationMarks[0].InstanceName == "LM1"`、`Pos.X == 0.0`（缺失键填 0）、`Pos.Y == 0.8`。
- `Parses_curve_endpoints_and_control_points`：喂入含一条 `advancedCurveList` 曲线（`instanceName="LM1-LM2"`，`startPos`/`endPos` 各带 `instanceName`+`pos`，`controlPos1`/`controlPos2`）的 JSON，断言 `Curves[0].StartMark == "LM1"`、`EndMark == "LM2"`、控制点坐标正确。
- `Throws_smap_parse_exception_on_invalid_json`：`Assert.Throws<SmapParseException>(() => SmapParser.Parse(Json("not json")))`。

**Step 2 — Verify fail:** `dotnet test tests/MesControlAgv.Domain.Tests --filter FullyQualifiedName~SmapParserTests`（预期编译失败：类型未定义）。

**Step 3 — Implement:**
- `SmapModels.cs`：按上面的 Produces 定义 records。
- `SmapParser.cs`：用 `System.Text.Json.JsonDocument.Parse`。核心私有助手：
  - `MapPoint Point(JsonElement e)` → `new(Coord(e,"x"), Coord(e,"y"))`
  - `double Coord(JsonElement e, string name)`：仅当 `e` 为 Object 且属性存在且为 Number 才取值，否则 `0.0`（容忍缺失键）。
  - `header` → `ParseHeader`；`advancedPointList` → `ParseMarks`（每项 `instanceName` + `pos`）；`advancedLineList` → `ParseLines`（读 `line.startPos`/`line.endPos`）；`advancedCurveList` → `ParseCurves`（读 `startPos`/`endPos` 的 `instanceName`+`pos`、`controlPos1`、`controlPos2`，以及 `property` 数组里 `key=="direction"`/`"movestyle"` 的 `int32Value`）。
  - `catch (JsonException|KeyNotFoundException|InvalidOperationException)` → 包成 `SmapParseException`。
- 遵循 `JsonProfileConfigurationLoader` 的既有风格：`FileStream` + `FileOptions.Asynchronous`，`ArgumentException.ThrowIfNullOrWhiteSpace`。

**Step 4 — Verify pass:** 同 Step 2 命令，预期 PASS。

**Step 5 — Commit:** `git commit -m "feat: parse smap header, marks and curves"`

---

### Task 2: 物理坐标 → Canvas 坐标转换（Domain，net8.0）

**Files:**
- Create: `src/MesControlAgv.Domain/Map/MapCoordinateSystem.cs`
- Test: `tests/MesControlAgv.Domain.Tests/Map/MapCoordinateSystemTests.cs`

**Interfaces:**
- Consumes: `MapPoint`、`SmapHeader`（Task 1）。
- Produces:
  - `sealed class MapCoordinateSystem`，构造入参 `SmapHeader header, double canvasWidth, double canvasHeight, double padding = 24`。
  - 计算统一缩放比 `Scale`（保持纵横比，取 `min` 以完整容纳 `maxPos-minPos`），并暴露：
    - `double CanvasWidth { get; }` / `double CanvasHeight { get; }` / `double Scale { get; }`
    - `MapPoint ToCanvas(MapPoint physical)`：`cx = padding + (p.X - minX) * scale`；`cy = canvasHeight - padding - (p.Y - minY) * scale`（**翻转 Y**）。
    - `MapPoint ToPhysical(MapPoint canvas)`：逆变换（供将来命中测试）。

**Step 1 — Write failing test:**
- `Origin_min_corner_maps_to_bottom_left`：header `minPos=(0,0)`、`maxPos=(10,10)`，canvas `200x200`、padding `0`，则 `ToCanvas((0,0))` 期望 `(0,200)`（左下），`ToCanvas((10,10))` 期望 `(200,0)`（右上）。
- `Preserves_aspect_ratio`：`maxPos=(20,10)`、canvas `200x200`、padding 0，断言 `Scale == 10`（受宽度约束），`ToCanvas((20,10)).Y == 100`。
- `Round_trip_to_physical_is_stable`：`ToPhysical(ToCanvas(p))` ≈ `p`（`Assert.Equal(expected, actual, 6)`）。

**Step 2 — Verify fail.**

**Step 3 — Implement:** 纯算术，无 WPF 依赖。`Scale = min((W-2·pad)/(maxX-minX), (H-2·pad)/(maxY-minY))`；对 `max==min` 的退化维度用另一维度的比例（或回退 `1.0`）避免除零。

**Step 4 — Verify pass. Step 5 — Commit:** `git commit -m "feat: add physical-to-canvas coordinate mapping"`

---

### Task 3: 从 `.smap` 构建地图模型（站点 + 路线图）（Domain，net8.0）

**Files:**
- Create: `src/MesControlAgv.Domain/Map/MapLayout.cs`（结果模型）
- Create: `src/MesControlAgv.Domain/Map/SmapMapLayoutBuilder.cs`
- Test: `tests/MesControlAgv.Domain.Tests/Map/SmapMapLayoutBuilderTests.cs`

**Interfaces:**
- Consumes: `SmapDocument`（Task 1）。
- Produces:
  - `sealed record MapStationLayout(string Id, MapPoint Physical)`（`Id` = `LocationMark.InstanceName`）。
  - `sealed record MapRouteLayout(string Id, string FromStationId, string ToStationId, MapPoint From, MapPoint To, MapPoint Control1, MapPoint Control2, int Direction)`。
  - `sealed record MapWallLayout(IReadOnlyList<SmapFeatureLine> Lines)`（可选墙体图层）。
  - `sealed record MapLayout(SmapHeader Header, IReadOnlyList<MapStationLayout> Stations, IReadOnlyList<MapRouteLayout> Routes, MapWallLayout Walls, IReadOnlyList<MapPoint> ScanPoints)`。
  - `static class SmapMapLayoutBuilder`：`MapLayout Build(SmapDocument doc)`。

**Step 1 — Write failing test:**
- `Maps_location_marks_to_stations`：断言站点数 == `advancedPointList` 数，Id 与坐标一致。
- `Maps_curves_to_routes_referencing_stations`：一条 `LM1-LM2` 曲线 → `Route.FromStationId=="LM1"`、`ToStationId=="LM2"`、控制点透传。
- `Carries_walls_and_scan_points_through`：`Walls.Lines` 数 == `advancedLineList` 数，`ScanPoints` 数 == `normalPosList` 数。

**Step 2 — Verify fail. Step 3 — Implement:** 纯映射，无坐标转换（转换在渲染层用 Task 2）。**Step 4 — Verify pass. Step 5 — Commit:** `git commit -m "feat: build map layout from smap document"`

---

### Task 4: 站点 → MES 工作站映射配置（Domain，net8.0）

**Files:**
- Create: `src/MesControlAgv.Domain/Map/StationMappingConfig.cs`
- Create: `src/MesControlAgv.Domain/Map/StationMappingLoader.cs`
- Test: `tests/MesControlAgv.Domain.Tests/Map/StationMappingLoaderTests.cs`

**背景:** 用户明确「目前站点是硬编码，之后应根据地图站点+AGV 映射到 MES 工作站」。本任务提供**可选**映射（`LocationMark.InstanceName` → MES `DashboardStation.AgvStationId`/`Name`）。缺省或文件不存在时返回空映射，渲染层按原样显示 `.smap` 站点名。

**Interfaces:**
- Consumes: 无。
- Produces:
  - `sealed record StationMappingEntry(string SmapMark, string? MesAgvStationId, string? DisplayName)`
  - `sealed record StationMappingConfig(IReadOnlyList<StationMappingEntry> Entries)`，带 `StationMappingConfig Empty { get; }` 与 `bool TryResolve(string smapMark, out StationMappingEntry entry)`。
  - `static class StationMappingLoader`：`Task<StationMappingConfig> LoadFileAsync(string? path, CancellationToken ct = default)`（`path` 为空或文件不存在 → 返回 `Empty`，不抛异常）。

**Step 1 — Write failing test:**
- `Returns_empty_when_path_is_null`。
- `Returns_empty_when_file_missing`。
- `Loads_entries_and_resolves_by_mark`：写临时 JSON `{"entries":[{"smapMark":"LM1","mesAgvStationId":"WS-01","displayName":"上料位"}]}`，断言 `TryResolve("LM1", out var e)` 为 true 且字段正确；`TryResolve("LMX", out _)` 为 false。

**Step 2 — Verify fail. Step 3 — Implement:** 复用 Task 1 的 JSON 风格；文件缺失用 `File.Exists` 判定。**Step 4 — Verify pass. Step 5 — Commit:** `git commit -m "feat: add optional smap-to-mes station mapping"`

---

### Task 5: 地图数据加载服务（WPF 侧，env-var + 回退）

**Files:**
- Create: `src/MesControlAgv.Wpf/Services/IMapLayoutSource.cs`
- Create: `src/MesControlAgv.Wpf/Services/SmapMapLayoutSource.cs`
- Test: `tests/MesControlAgv.Wpf.Tests/SmapMapLayoutSourceTests.cs`（仍为 net8.0，**不含** WPF 类型）

**Interfaces:**
- Consumes: `SmapParser`、`SmapMapLayoutBuilder`、`StationMappingLoader`（Domain）。
- Produces:
  - `interface IMapLayoutSource { Task<MapLayoutResult> LoadAsync(CancellationToken ct = default); }`
  - `sealed record MapLayoutResult(MapLayout? Layout, StationMappingConfig Mapping, bool Loaded, string? Error)`（`Loaded=false` 表示未配置 `.smap`，MapViewModel 走 MES 自动布局回退）。
  - `sealed class SmapMapLayoutSource(Func<string?> smapPathProvider, Func<string?> mappingPathProvider) : IMapLayoutSource`——路径由委托注入，便于测试；生产环境用 `() => Environment.GetEnvironmentVariable("MAP_SMAP_PATH")`。

**行为:**
- `smapPath` 为空 → `MapLayoutResult(null, Empty, Loaded:false, null)`。
- 路径存在但解析失败 → `MapLayoutResult(null, Empty, Loaded:false, Error:"<msg>")`（**不抛**，让 UI 回退并提示）。
- 成功 → `MapLayoutResult(layout, mapping, Loaded:true, null)`。

**Step 1 — Write failing test:**
- `Not_loaded_when_env_path_empty`。
- `Loads_layout_from_valid_smap_file`：写一个最小 `.smap` 临时文件，断言 `Loaded==true` 且 `Layout!.Stations` 非空。
- `Reports_error_and_not_loaded_on_bad_file`：坏 JSON → `Loaded==false` 且 `Error != null`。

**Step 2 — Verify fail. Step 3 — Implement. Step 4 — Verify pass. Step 5 — Commit:** `git commit -m "feat: load map layout from smap with mes fallback"`

---

### Task 6: `MapViewModel` 增加 `.smap` 布局路径（保留 MES 自动布局回退）

**Files:**
- Modify: `src/MesControlAgv.Wpf/ViewModels/MapViewModel.cs`
- Modify: `src/MesControlAgv.Wpf/ViewModels/ReadinessViewModel.cs`（注入 `IMapLayoutSource`，加载后调用 `Map.ApplyLayout`）
- Test: `tests/MesControlAgv.Wpf.Tests/MapViewModelTests.cs`（新增用例）

**Interfaces:**
- Consumes: `MapLayout`、`MapCoordinateSystem`（Task 2/3）、`MapLayoutResult`（Task 5）、既有 `DashboardMapSnapshot`/`AgvFleetDashboardStatus`。
- Produces（`MapViewModel` 新成员）：
  - `void ApplyLayout(MapLayout layout, StationMappingConfig mapping, double canvasWidth, double canvasHeight)`：用 `MapCoordinateSystem` 把 `.smap` 站点/路线转换成既有的 `MapNodeViewModel`/`MapEdgeViewModel`（曲线额外产出 `MapPathSegmentViewModel` 的贝塞尔控制点）。站点名优先取 `mapping.TryResolve`，否则用 `.smap` `Id`。
  - `bool UsingSmapLayout { get; }`：为 true 时 `Update(...)` **不再**用自动布局重排节点，只更新 AGV overlay 与状态；为 false 时维持既有自动布局行为（**回退**）。
  - 既有 `Update(...)` 签名不变（不破坏 MES 自动布局回退行为）。

**Step 1 — Write failing test:**
- `ApplyLayout_places_nodes_at_converted_coordinates`：构造含 2 站点 1 路线的 `MapLayout`，调用 `ApplyLayout`，断言 `Nodes` 数、坐标经 Y 翻转后落在 canvas 内、`Edges` 连接正确。
- `ApplyLayout_prefers_mapping_display_name`：mapping 里 `LM1→"上料位"`，断言对应节点 `Name=="上料位"`。
- `Update_after_ApplyLayout_keeps_smap_positions`：`ApplyLayout` 后调用 `Update(snapshot,...)`，断言节点坐标不被自动布局覆盖，且 AGV overlay 已更新。
- `Update_without_ApplyLayout_uses_autolayout`（回归）：不调 `ApplyLayout` 时行为与现状一致。

**Step 2 — Verify fail. Step 3 — Implement:** 保持 `Update` 向后兼容；`ApplyLayout` 设 `UsingSmapLayout=true`。`ReadinessViewModel` 在构造/首次刷新时 `await _mapSource.LoadAsync()`，`Loaded` 为 true 则 `ApplyLayout`，否则维持现状。**Step 4 — Verify pass（最终解决方案 312/312 全绿）. Step 5 — Commit:** `git commit -m "feat: render map from smap layout with mes fallback"`

---

### Task 7: 缩放/平移交互状态（纯数学，可测；避免 WPF 依赖）

**Files:**
- Create: `src/MesControlAgv.Wpf/ViewModels/MapViewportViewModel.cs`
- Test: `tests/MesControlAgv.Wpf.Tests/MapViewportViewModelTests.cs`

**设计说明:** 交互状态用纯 `double` 属性表达（`Scale`/`OffsetX`/`OffsetY`），XAML 侧只把它们绑定到 `ScaleTransform`+`TranslateTransform`（Task 9 手测），从而无需给测试项目开 `UseWPF`。

**Interfaces:**
- Consumes: 无。
- Produces（`ObservableObject`）：
  - `[ObservableProperty] double Scale`（默认 1，范围 `[MinScale=0.2, MaxScale=8]`）
  - `[ObservableProperty] double OffsetX` / `OffsetY`
  - `void ZoomAt(double anchorX, double anchorY, double delta)`：以鼠标锚点为中心缩放（更新 `Scale` 并调整 `Offset` 使锚点视觉不动），`Scale` 被 clamp。
  - `void Pan(double dx, double dy)`：`OffsetX += dx; OffsetY += dy`。
  - `void Reset()`：回到 `Scale=1, Offset=0`。

**Step 1 — Write failing test:**
- `Zoom_clamps_to_bounds`：连续放大后 `Scale <= MaxScale`；连续缩小后 `Scale >= MinScale`。
- `ZoomAt_keeps_anchor_point_stationary`：给定锚点与 delta，验证 `anchor` 的世界坐标在缩放前后映射到同一屏幕点（用变换公式反算，`Assert.Equal(expected, actual, 6)`）。
- `Pan_accumulates_offsets`。
- `Reset_restores_defaults`。

**Step 2 — Verify fail. Step 3 — Implement. Step 4 — Verify pass. Step 5 — Commit:** `git commit -m "feat: add zoom/pan viewport state"`

---

### Task 8: AGV 路径动画插值（纯数学，可测）

**Files:**
- Create: `src/MesControlAgv.Domain/Map/AgvPathAnimator.cs`
- Test: `tests/MesControlAgv.Domain.Tests/Map/AgvPathAnimatorTests.cs`

**设计说明:** 把「AGV 沿路径随时间移动」拆成纯插值：给一条贝塞尔路线与进度 `t∈[0,1]`，算出位置与朝向。WPF 侧（Task 9）用 `Storyboard`/`DispatcherTimer` 递增 `t` 并把结果绑到 overlay，动画计算本身在此可单测。

**Interfaces:**
- Consumes: `MapPoint`、`MapRouteLayout`（Task 3）。
- Produces:
  - `static class AgvPathAnimator`：
    - `MapPoint PointOnCurve(MapPoint p0, MapPoint c1, MapPoint c2, MapPoint p3, double t)`（三次贝塞尔；退化时控制点等于端点即退化为直线）。
    - `double HeadingRadians(MapPoint p0, MapPoint c1, MapPoint c2, MapPoint p3, double t)`（由一阶导数得切线朝向，供路径箭头/AGV 车头方向）。

**Step 1 — Write failing test:**
- `PointOnCurve_at_t0_is_start_and_t1_is_end`。
- `Straight_line_midpoint_when_controls_on_line`：控制点在直线上时 `t=0.5` 落在两端点中点。
- `Heading_of_horizontal_line_is_zero`：水平向右的直线朝向 ≈ 0 弧度。

**Step 2 — Verify fail. Step 3 — Implement:** 标准三次贝塞尔公式与导数。**Step 4 — Verify pass. Step 5 — Commit:** `git commit -m "feat: add bezier path animation math"`

---

### Task 9: XAML 连接——缩放/平移手势、站点名、路径箭头、AGV 动画（手动验证）

**Files:**
- Modify: `src/MesControlAgv.Wpf/Views/MainWindow.xaml` 与 `MainWindow.xaml.cs`（或地图所在的 View/UserControl）
- Modify: `src/MesControlAgv.Wpf/App.xaml.cs`（读取 `MAP_SMAP_PATH`/`MAP_STATION_MAPPING_PATH` env-var，注入 `SmapMapLayoutSource` 的路径委托）

**说明:** 本任务是纯 WPF 视图接线，**无单元测试**（测试项目无 `UseWPF`；强行开启会拖动 build 且与既有约定冲突——见风险节）。验收靠手动运行 + 截图对比 RoboshopPro。

**Interfaces:**
- Consumes: `MapViewModel`（Task 6）、`MapViewportViewModel`（Task 7）、`AgvPathAnimator`（Task 8）、`SmapMapLayoutSource`（Task 5）。
- Produces: 视图行为，无新公共 API。

**接线要点:**
- 地图 `Canvas` 外套 `Border`（`ClipToBounds=True`），`Canvas.RenderTransform` = `TransformGroup{ ScaleTransform ScaleX/Y=Scale, TranslateTransform X=OffsetX/Y=OffsetY }` 绑定到 `MapViewportViewModel`。
- `MouseWheel` → `ZoomAt(e.GetPosition, e.Delta)`；`MouseLeftButtonDown`+`MouseMove`+`MouseLeftButtonUp` 拖拽 → `Pan(dx,dy)`；双击或按钮 → `Reset`。
- 站点名：节点模板加 `TextBlock`（绑定 `MapNodeViewModel.Name`）。
- 路径箭头：`Path`/`PathGeometry` 用曲线控制点画贝塞尔；中点放一个旋转的箭头 `Polygon`（角度由 `AgvPathAnimator.HeadingRadians` 提供），可选按 `Direction` 决定箭头指向。
- AGV 动画：`DispatcherTimer` 或 `Storyboard` 递增 `t`，用 `AgvPathAnimator.PointOnCurve` 更新 overlay 的 `Canvas.Left/Top`（经 `MapCoordinateSystem` 转换）。
- 障碍/墙体（可选、最后做）：`normalPosList`/`advancedLineList` 画成半透明点/线图层，可用开关隐藏；若渲染 3.6 万点性能不佳，退化为不渲染扫描点、只画 `advancedLineList` 墙线（用户已同意栅格背景可后置）。

**手动验证清单:**
1. 设 `MAP_SMAP_PATH` 指向 `guangzhou606.smap`，启动 WPF，地图显示 5 个站点（LM1..LM5）与 9 条路线，站点名可见。
2. 滚轮缩放围绕鼠标锚点；拖拽平移；双击复位。
3. 身份校验通过时，活动 overlay 沿路线平滑移动且车头朝向正确；当前 Profile 与本地 `.smap` 不一致，运行叠加按设计关闭，动画协调器由定向测试覆盖。
4. 不设 `MAP_SMAP_PATH` 时回退到 MES 自动布局，行为同现状。
5. `dotnet build` 全绿（`TreatWarningsAsErrors=true`），解决方案测试 `312/312` 通过。

**Commit:** `git commit -m "feat: wire smap map view with zoom/pan and agv animation"`

---

## 阶段映射（对应设计文档 3 阶段）

- **Phase 1（基础视图，能看全图信息）：** Task 1–6。产出：解析 `.smap`、坐标转换、地图模型、可选站点映射、数据加载与回退、`MapViewModel` 用 `.smap` 布局渲染站点+路线+站点名。
- **Phase 2（交互：缩放/平移）：** Task 7 + Task 9 的手势接线。
- **Phase 3（动画与美化）：** Task 8 + Task 9 的 AGV 动画/路径箭头/障碍图层。栅格背景与扫描点渲染为可选，性能不佳可后置（用户已同意）。

## 风险与决策

- **测试项目无 `UseWPF`：** 既有 `MesControlAgv.Wpf.Tests` 为 net8.0，不能测 `System.Windows` 类型。因此所有可测逻辑（解析、坐标、插值、缩放/平移状态）都放在 Domain 或纯 `double`/record 的 ViewModel 中；真正的 WPF 视图接线（Task 9）靠手动验证，不改测试项目的 `UseWPF`（避免拖慢 build 且与既有约定冲突）。
- **`advancedCurveList` vs `advancedLineList`：** 已在上文「格式实测结论」修正——路线来自 `advancedCurveList`，`advancedLineList` 是墙体。Task 3 据此实现。
- **缺失坐标键：** 解析器对缺失 `x`/`y` 填 `0.0`，不抛异常（Task 1 已含测试）。
- **性能（3.6 万扫描点）：** 扫描点渲染为可选图层，默认可关闭；优先保证站点/路线/交互流畅。
- **向后兼容：** `MapViewModel.Update(...)` 签名与语义在未 `ApplyLayout` 时保持不变，保护 MES 自动布局回退和既有调用方。
- **中控软件并行调试：** 用户同时推进 AGV 中控调试（当前仅移动验证，机械臂抓取 API 未对接）。本计划纯属 WPF 只读可视化，不触碰 Adapter/Simulator 的控制链路，两条线互不阻塞。

## 全局验证

- 每个 Domain/WPF 测试任务：`dotnet test <对应测试项目> --filter ...` 先红后绿。
- 计划完成后：`dotnet build`（`TreatWarningsAsErrors=true` 必须零警告）+ `dotnet test`（全解决方案 `312/312`）。
- Task 9 手动验证清单见该任务。

## 未来工作（超出本计划范围）

- 站点/AGV → MES 工作站的映射从「可选配置文件」升级为运行期动态绑定（用户提到站点当前硬编码，将来据地图映射）。
- 地图导出（PNG/PDF）和基于历史任务的站点详情扩展。
