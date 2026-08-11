# WPF 地图视图增强设计

> **设计日期：** 2026-08-10  
> **设计者：** Claude (Opus 5)  
> **状态：** 已实现阶段 1-4（2026-08-10）；地图导出与真实 AGV 现场验收仍为后续 backlog

> **实现校正：** 当前 WPF 使用 `MAP_SMAP_PATH` / `MAP_STATION_MAPPING_PATH` 环境变量，路线来自
> `advancedCurveList`，`advancedLineList` 只作为墙体线层。缺少站点映射时保留 `.smap` 的 LM 标记；
> 只有 `.smap` 未配置或加载失败才回退 MES 自动布局。地图身份（名称、版本、实际文件 MD5、站点集合、
> 有向边集合）无法验证或不一致时，静态几何仍显示，但 AGV/活动路径画布叠加 fail-closed。

## 一、总体目标

将当前简单的 Canvas 地图视图升级为功能完整的地图查看器，支持：

- ✅ 缩放和平移交互
- ✅ AGV 实时位置和移动动画
- ✅ 基于 `.smap` 文件的真实地图数据
- ✅ 站点、路径、障碍物的可视化
- ✅ 为将来的站点映射配置预留扩展点
- ✅ 可选 Gray8 栅格背景、图层开关和站点只读详情

### 1.1 背景

当前 WPF 地图视图使用自动布局算法生成站点位置，缺乏与现场实际布局的对应关系。RoboshopPro 提供了 `.smap` 格式的地图文件，包含真实的站点坐标、路径和障碍物信息。本设计旨在解析并渲染这些数据，提供更直观的地图可视化。

### 1.2 设计原则

1. **渐进式交付** - 分三个阶段实现，每个阶段独立交付价值
2. **向后兼容** - 保留现有功能，.smap 不可用时回退到自动布局
3. **性能优先** - 栅格背景等重型功能设为可选
4. **扩展性** - 为将来的站点映射配置和地图编辑预留架构

## 二、实施策略

### 2.1 三阶段实施计划

采用渐进式增强策略，分三个阶段实施：

**阶段1：核心交互功能**（2-3天）
- 缩放和平移交互
- AGV 移动动画
- 视觉样式增强

**阶段2：.smap 数据解析与渲染**（2-3天）
- .smap JSON 解析器
- 坐标系转换
- 路径箭头和代价标签
- 栅格背景渲染（可选）

**阶段3：站点映射配置**（1-2天）
- 映射配置文件格式
- LocationMark → MES 站点映射
- MES API 对比验证
- 配置加载和回退逻辑

### 2.2 优先级说明

考虑到需要同步推进中控软件调试和机械臂 API 对接，本设计采用灵活的优先级策略：

- **必须实现**：阶段1（交互）+ 阶段2核心（.smap解析、坐标转换、基础渲染）
- **建议实现**：路径箭头、视觉增强
- **可延后**：栅格背景渲染、阶段3站点映射配置
- **后续扩展**：配置 UI 工具、历史轨迹回放


## 三、架构设计

### 3.1 分层架构

```
┌─────────────────────────────────────────┐
│  MainWindow.xaml (地图视图 TabItem)      │
│  - ViewBox (缩放容器)                    │
│  - Canvas (地图画布)                     │
│  - 工具栏（缩放控制、图层开关）           │
└─────────────────────────────────────────┘
              ↓ 绑定
┌─────────────────────────────────────────┐
│  MapViewModel                            │
│  - 地图数据（站点、路径、AGV）            │
│  - 交互命令（缩放、平移）                 │
│  - 动画状态管理                          │
└─────────────────────────────────────────┘
              ↓ 使用
┌─────────────────────────────────────────┐
│  Domain Layer                            │
│  - SmapParser (解析 .smap JSON)         │
│  - MapData (地图数据模型)                │
│  - StationMapper (站点映射，待实现)       │
└─────────────────────────────────────────┘
```

### 3.2 核心组件

**MesControlAgv.Domain/Map/**
- `SmapData.cs` - .smap 文件的数据模型
- `SmapParser.cs` - JSON 解析器
- `MapCoordinateSystem.cs` - 坐标转换（物理坐标 ↔ Canvas坐标）
- `StationMappingConfig.cs` - 站点映射配置（阶段3）

**MesControlAgv.Wpf/ViewModels/**
- `MapViewModel.cs` - 增强现有实现
- `MapInteractionViewModel.cs` - 缩放/平移状态
- `AgvAnimationController.cs` - AGV 动画管理

**MesControlAgv.Wpf/Controls/**
- `ZoomableCanvas.xaml` - 可缩放画布自定义控件

**MesControlAgv.Wpf/Services/**
- `MapDataLoader.cs` - 地图数据加载和策略选择


## 四、技术实现细节

### 4.1 阶段1：核心交互功能

#### 4.1.1 缩放和平移实现

使用 WPF 的 `ScaleTransform` 和 `TranslateTransform`：

```xaml
<ScrollViewer HorizontalScrollBarVisibility="Auto" VerticalScrollBarVisibility="Auto">
    <Grid>
        <Canvas RenderTransformOrigin="0.5,0.5">
            <Canvas.RenderTransform>
                <TransformGroup>
                    <ScaleTransform ScaleX="{Binding ZoomLevel}" ScaleY="{Binding ZoomLevel}"/>
                    <TranslateTransform X="{Binding PanX}" Y="{Binding PanY}"/>
                </TransformGroup>
            </Canvas.RenderTransform>
            <!-- 地图内容 -->
        </Canvas>
    </Grid>
</ScrollViewer>
```

**交互处理：**
- 鼠标滚轮事件 → 更新 `Scale`（当前范围 `0.2-8.0`，以鼠标锚点保持内容位置）
- 左键拖拽 → 更新 `OffsetX/OffsetY`；双击或重置按钮恢复默认视图
- 工具栏提供放大、缩小、路线区域、完整地图和重置视图

#### 4.1.2 AGV 移动动画

使用 `DoubleAnimation` 实现平滑移动：

```csharp
public class AgvAnimationController
{
    public void AnimateMove(MapAgvOverlayViewModel agv, Point from, Point to, TimeSpan duration)
    {
        var xAnim = new DoubleAnimation(from.X, to.X, duration);
        var yAnim = new DoubleAnimation(from.Y, to.Y, duration);
        
        // 应用缓动函数，更自然
        xAnim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        yAnim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        
        // 绑定到 ViewModel 属性
        agv.BeginAnimation(MapAgvOverlayViewModel.XProperty, xAnim);
        agv.BeginAnimation(MapAgvOverlayViewModel.YProperty, yAnim);
    }
}
```

**轮询更新策略：**
- 当前每2秒轮询一次 AGV 位置
- 检测到位置变化时触发动画（动画时长 = 轮询间隔 * 0.8，避免跳跃）
- AGV 状态变化（Idle → Moving）时改变视觉样式


### 4.2 阶段2：.smap 数据解析与渲染

#### 4.2.1 .smap 文件格式分析

基于 `guangzhou606.smap` 文件分析：

**文件结构：**
- **header** - 地图元数据（名称、版本、边界、分辨率）
- **normalPosList** - 栅格点列表（36559个点，用于障碍物/背景）
- **advancedPointList** - 高级点列表（LocationMark 站点标记，5个）
- **advancedLineList** - 特征线列表（墙体线，151条）
- **advancedCurveList** - 曲线列表（可行驶路线，9条）

**关键对象类型：**
- `LocationMark` - 站点位置标记（如 LM1, LM2）
- `FeatureLine` - 特征线，包含起点、终点、方向
- `DegenerateBezier` - 贝塞尔曲线

#### 4.2.2 数据模型

```csharp
// MesControlAgv.Domain/Map/SmapData.cs
public record SmapData
{
    public SmapHeader Header { get; init; }
    public List<Vector2> NormalPosList { get; init; }
    public List<SmapPoint> AdvancedPointList { get; init; }
    public List<SmapLine> AdvancedLineList { get; init; }
    public List<SmapCurve> AdvancedCurveList { get; init; }
}

public record SmapHeader
{
    public string MapType { get; init; }
    public string MapName { get; init; }
    public Vector2 MinPos { get; init; }
    public Vector2 MaxPos { get; init; }
    public double Resolution { get; init; }
    public string Version { get; init; }
}

public record SmapPoint
{
    public string ClassName { get; init; }
    public string InstanceName { get; init; }
    public Vector2 Pos { get; init; }
    public bool IgnoreDir { get; init; }
    public List<SmapProperty> Properties { get; init; }
}

public record SmapLine
{
    public string ClassName { get; init; }
    public string InstanceName { get; init; }
    public Vector2 StartPos { get; init; }
    public Vector2 EndPos { get; init; }
    public double Direction { get; init; }
    public Vector2 DirectionPos { get; init; }
}
```


#### 4.2.3 坐标转换

.smap 使用物理坐标（米），需要转换到 Canvas 坐标（像素）：

```csharp
public class MapCoordinateSystem
{
    private readonly SmapHeader _header;
    private readonly double _scale; // 像素/米
    
    public MapCoordinateSystem(SmapHeader header, double canvasWidth, double canvasHeight)
    {
        _header = header;
        
        // 计算缩放比例，保持宽高比
        var mapWidth = header.MaxPos.X - header.MinPos.X;
        var mapHeight = header.MaxPos.Y - header.MinPos.Y;
        _scale = Math.Min(canvasWidth / mapWidth, canvasHeight / mapHeight) * 0.9;
    }
    
    public Point ToCanvas(Vector2 physicalPos)
    {
        // 物理坐标系：原点在左下，Y轴向上
        // Canvas坐标系：原点在左上，Y轴向下
        var x = (physicalPos.X - _header.MinPos.X) * _scale;
        var y = (_header.MaxPos.Y - physicalPos.Y) * _scale; // Y轴翻转
        return new Point(x, y);
    }
    
    public Vector2 ToPhysical(Point canvasPos)
    {
        var x = canvasPos.X / _scale + _header.MinPos.X;
        var y = _header.MaxPos.Y - canvasPos.Y / _scale;
        return new Vector2(x, y);
    }
}
```

#### 4.2.4 栅格背景渲染优化（可选功能）

36k 个点直接绘制会卡顿，使用 `WriteableBitmap` 预渲染：

```csharp
public class GridMapRenderer
{
    public WriteableBitmap RenderToImage(
        List<Vector2> normalPosList, 
        MapCoordinateSystem coordSys, 
        int width, 
        int height)
    {
        var bitmap = new WriteableBitmap(width, height, 96, 96, 
            PixelFormats.Bgra32, null);
        bitmap.Lock();
        
        unsafe
        {
            var pBackBuffer = (int*)bitmap.BackBuffer;
            var stride = bitmap.BackBufferStride / 4;
            
            foreach (var pos in normalPosList)
            {
                var canvasPos = coordSys.ToCanvas(pos);
                var x = (int)canvasPos.X;
                var y = (int)canvasPos.Y;
                
                if (x >= 0 && x < width && y >= 0 && y < height)
                {
                    pBackBuffer[y * stride + x] = 0xFF333333; // 灰色障碍物点
                }
            }
        }
        
        bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        bitmap.Unlock();
        return bitmap;
    }
}
```

**注意：** 此功能为可选，优先实现基础视图。如果性能有问题可延后优化。


#### 4.2.5 路径箭头渲染

在 `advancedCurveList` 的每条贝塞尔路线中段绘制方向箭头：

```xaml
<ItemsControl ItemsSource="{Binding Map.PathArrows}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Path Canvas.Left="{Binding X}" Canvas.Top="{Binding Y}" 
                  Data="M 0,0 L 10,-5 L 10,5 Z" 
                  Fill="#D97706"
                  RenderTransform="{Binding RotationTransform}"/>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

箭头位置和旋转角度由路线的贝塞尔点计算；`direction` 不被猜测为双向，只有存在反向曲线时才显示反向路线。

### 4.3 阶段3：站点映射配置

#### 4.3.1 映射配置模型

```csharp
// MesControlAgv.Domain/Map/StationMappingConfig.cs
public record StationMappingConfig
{
    public string MapFile { get; init; }
    public string MapMd5 { get; init; }
    public List<StationMapping> Mappings { get; init; }
}

public record StationMapping
{
    public string LocationMarkId { get; init; } // "LM1" from .smap
    public string MesStationId { get; init; } // "SAMPLE_01" from MES
    public int StationCode { get; init; } // 2
    public string StationName { get; init; } // "样品位"
    public bool Enabled { get; init; }
}
```

#### 4.3.2 配置文件示例

```json
{
  "mapFile": "guangzhou606.smap",
  "mapMd5": "abc123...",
  "mappings": [
    {
      "locationMarkId": "LM1",
      "mesStationId": "SAMPLE_01",
      "stationCode": 2,
      "stationName": "样品位",
      "enabled": true
    },
    {
      "locationMarkId": "LM2",
      "mesStationId": "ST_PREP_01",
      "stationCode": 4,
      "stationName": "液体前处理工作站",
      "enabled": true
    }
  ]
}
```


#### 4.3.3 配置优先级策略

```
应用启动
    ↓
尝试加载站点映射配置
    ↓
┌───────────────┐
│ 配置存在？     │
└───────────────┘
  ↓YES        ↓NO
  │           └→ 从 MES API 获取 + 自动布局
  ↓
解析 .smap 文件
  ↓
应用映射，构建站点列表
  ↓
MES API 对比验证
  ↓
显示地图
```

#### 4.3.4 数据加载流程

```csharp
public class MapDataLoader
{
    private readonly IConfiguration _config;
    private readonly IMesClient _mesClient;
    
    public async Task<MapSnapshot> LoadMapAsync()
    {
        // 1. 尝试加载映射配置
        var mappingConfig = await TryLoadMappingConfigAsync();
        
        if (mappingConfig != null)
        {
            // 2. 解析 .smap 文件
            var smapData = await SmapParser.ParseAsync(mappingConfig.MapFile);
            
            // 3. 应用映射，构建站点列表
            var stations = BuildStationsFromMapping(smapData, mappingConfig);
            var edges = BuildEdgesFromSmap(smapData);
            
            // 4. MES API 验证
            var mesStations = await _mesClient.GetStationsAsync();
            var syncStatus = CompareMappingWithMes(stations, mesStations);
            
            return new MapSnapshot
            {
                Source = MapSource.SmapFile,
                Stations = stations,
                Edges = edges,
                Background = smapData.NormalPosList,
                MapFingerprint = mappingConfig.MapMd5,
                SyncStatus = syncStatus
            };
        }
        else
        {
            // 5. 回退到 MES API 数据 + 自动布局
            return await LoadFromMesApiAsync();
        }
    }
}
```


## 五、视觉设计增强

### 5.1 站点节点样式

**当前：** 简单矩形框  
**增强：** 
- 圆角矩形 + 渐变背景
- 根据状态变色：
  - 空闲 = 蓝色 (#E8F1FF)
  - 有任务 = 橙色 (#FFF7ED)
  - 禁用 = 灰色 (#F3F4F6)
- 添加图标（充电桩、工作站、样品位使用不同图标）
- 显示站点名称和 ID

### 5.2 路径边样式

**当前：** 单色直线  
**增强：**
- 虚线表示双向，实线表示单向
- 路径上显示代价值标签（小字，灰色）
- 活动路径（AGV正在使用）高亮加粗
- 方向箭头指示路径方向

### 5.3 AGV 标记样式

**当前：** 简单边框 + 文本  
**增强：**
- AGV 图标（小车形状或圆形标记）
- 方向指示（朝向箭头）
- 状态徽章：
  - 移动中 = 橙色边框 + 动画
  - 等待 = 蓝色边框
  - 离线 = 灰色 + 半透明
- 悬停显示完整信息（任务ID、路径、速度等）

### 5.4 工具栏

添加地图控制工具栏：

```
[🔍-] [🔍+] [⤢ 适应窗口] [↻ 重置] | [👁 图层] [⚙ 设置]
```

**图层开关：**
- ☑ 站点标签
- ☑ 路径箭头  
- ☑ 路径代价
- ☐ 栅格背景（默认关闭）
- ☑ AGV 轨迹

### 5.5 配色方案

- 背景：#FBFCFE（浅灰蓝）
- 路径边：#9AA7B8（灰蓝）
- 活动路径：#D97706（橙色）
- 站点正常：#4285C5（蓝色）
- 站点有任务：#D97706（橙色）
- 站点禁用：#6B7280（灰色）
- AGV 在线：#D97706（橙色）
- AGV 离线：#9CA3AF（浅灰）


## 六、数据流和状态管理

### 6.1 地图数据更新流程

```
应用启动
    ↓
尝试加载 .smap + 映射配置
    ↓
┌───────────────┐
│ 成功？        │
└───────────────┘
  ↓YES        ↓NO
  │           └→ 从 MES API 获取 + 自动布局
  ↓
解析 .smap，构建地图数据
  ↓
MES API 对比验证（站点数、MD5）
  ↓
初始化 MapViewModel
  ↓
启动轮询（每2秒）
  ↓
┌─────────────────────────┐
│ 更新 AGV 位置和状态     │
│ - 检测位置变化 → 触发动画│
│ - 更新活动路径高亮      │
└─────────────────────────┘
```

### 6.2 性能考虑

**优化策略：**

1. **延迟加载栅格背景**
   - 首次加载不渲染 normalPosList
   - 用户点击"显示栅格背景"时才渲染
   - 使用后台线程 + WriteableBitmap 避免 UI 冻结

2. **视口裁剪**
   - 只渲染当前可见区域的元素
   - 缩小时简化站点显示（只显示图标，隐藏文字）

3. **动画节流**
   - 同时移动多个 AGV 时，限制最大同时动画数量
   - 超出视口的 AGV 跳过动画，直接更新位置

4. **数据缓存**
   - .smap 解析结果缓存到内存
   - 映射配置更改时才重新解析

### 6.3 错误处理

**场景1：.smap 文件不存在或损坏**
- 回退到 MES API + 自动布局
- 在地图视图顶部显示警告横幅

**场景2：映射配置与 MES 不一致**
- 显示不一致的站点列表
- 允许用户选择：使用映射配置 / 使用 MES 数据
- 记录审计日志

**场景3：坐标超出地图边界**
- 自动调整 Canvas 大小包含所有元素
- 在控制台警告日志

**场景4：.smap 版本不兼容**
- 检测 version 字段（当前支持 "1.0.6"）
- 不支持的版本显示友好错误消息


## 七、配置和部署

### 7.1 配置文件结构

**运行环境变量：**

```json
MAP_SMAP_PATH=C:\path\to\map.smap
MAP_STATION_MAPPING_PATH=C:\path\to\station-mapping.json
WPF_SOFTWARE_RENDERING=true  # 可选：远程桌面/截图环境
```

未配置 `MAP_SMAP_PATH` 或加载失败时使用 MES 自动布局；配置成功但身份校验失败时不回退几何，
而是保留静态 `.smap` 并关闭运行叠加。

### 7.2 文件组织

```
MesControlAgv.sln
├── src/
│   ├── MesControlAgv.Domain/
│   │   └── Map/
│   │       ├── SmapData.cs
│   │       ├── SmapParser.cs
│   │       ├── MapCoordinateSystem.cs
│   │       └── StationMappingConfig.cs
│   └── MesControlAgv.Wpf/
│       ├── ViewModels/
│       │   ├── MapViewModel.cs (增强)
│       │   ├── MapInteractionViewModel.cs (新增)
│       │   └── AgvAnimationController.cs (新增)
│       ├── Controls/
│       │   └── ZoomableMapCanvas.xaml (新增)
│       ├── Converters/
│       │   ├── ZoomLevelToPercentageConverter.cs
│       │   └── AgvStateToColorConverter.cs
│       └── Services/
│           └── MapDataLoader.cs (新增)
├── config/
│   └── station-mapping.json (可选配置)
└── docs/
    └── superpowers/
        └── specs/
            └── 2026-08-10-wpf-map-enhancement-design.md
```


## 八、验收标准

### 阶段1：核心交互

- [x] 鼠标滚轮缩放地图（0.2x - 8.0x）
- [x] 左键拖拽平移地图
- [x] 工具栏缩放控制按钮可用
- [x] 路线区域/完整地图取景按钮可用
- [x] AGV 位置变化时平滑动画移动（默认 1.6 秒）
- [x] 动画过程中 AGV 标记不闪烁
- [x] 多个 AGV 同时移动互不干扰

### 阶段2：.smap 渲染

- [x] 成功解析 guangzhou606.smap 文件
- [x] 站点坐标从 .smap LocationMark 读取
- [x] 路线从 .smap `advancedCurveList` 贝塞尔曲线渲染
- [x] 路径箭头正确显示方向
- [ ] 路径代价标签显示（可选）
- [x] 地图边界自动适配 .smap 的 minPos/maxPos
- [x] 栅格背景可选启用（单一有界 Gray8 位图，默认关闭）

### 阶段3：站点映射

- [x] 读取可选站点映射 JSON 配置
- [x] LocationMark 可映射到 MES 站点并参与身份校验
- [x] MES API 验证显示同步状态
- [x] 映射缺失时保留 `.smap` LM 名称；`.smap` 缺失/加载失败时回退自动布局
- [x] 配置文件格式错误时保留明确加载错误并回退

### 视觉增强

- [ ] 站点节点显示图标和渐变背景
- [ ] 站点根据状态变色
- [x] AGV 标记显示方向箭头
- [x] 活动路径高亮显示（身份校验通过时）
- [x] 图层开关正常工作（墙线、路线、站点标签、运行叠加、栅格）
- [x] 点击站点显示只读详情（映射、坐标、状态、关联路线）

### 性能要求

- [ ] 初始加载时间 < 2秒
- [ ] 缩放/平移操作流畅（60fps）
- [ ] AGV 动画流畅无卡顿
- [ ] 内存占用 < 200MB（含栅格背景）


## 九、技术风险和缓解策略

| 风险 | 可能性 | 影响 | 缓解策略 |
|------|--------|------|----------|
| .smap 格式变化 | 中 | 高 | 版本检测，不兼容时友好提示；优先支持 1.0.6 |
| 36k 栅格点性能问题 | 高 | 中 | 可选功能，使用 WriteableBitmap 预渲染 |
| 坐标系转换错误 | 中 | 高 | 单元测试覆盖，提供可视化调试工具 |
| 站点映射配置复杂 | 低 | 中 | 阶段3延后，先手动配置 JSON |
| 动画与轮询冲突 | 中 | 低 | 动画时长 < 轮询间隔，检测冲突时取消旧动画 |
| 多 AGV 动画卡顿 | 低 | 低 | 限制同时动画数量，超出视口跳过动画 |

## 十、后续扩展方向

### 短期（1-2周内）

- 站点详情面板扩展为显示任务历史
- 路径规划可视化（显示 Dijkstra 算法结果）
- PNG 图片导出已完成；PDF 仍为后续候选

### 中期（1-2月内）

- 站点映射配置 UI 工具
- 支持多个 .smap 文件切换
- 历史轨迹回放功能

### 长期（3月+）

- 实时地图编辑（拖拽调整站点位置）
- 3D 地图视图（使用 Helix Toolkit）
- AR 增强现实标注（配合现场摄像头）

## 十一、实施建议

### 优先级调整

考虑到需要同步推进中控调试和机械臂对接：

1. **立即开始**：阶段1（交互）+ 阶段2核心（.smap解析、坐标转换）
2. **快速迭代**：先实现基础功能，视觉美化逐步优化
3. **延后实施**：栅格背景渲染、站点映射配置可在中控稳定后补充

### 开发节奏

- **Week 1**：阶段1 + 阶段2基础（不含栅格背景）
- **Week 2**：视觉增强 + 路径箭头 + 测试优化
- **Week 3+**：阶段3站点映射（根据中控调试进度决定）

### 测试策略

- 单元测试：坐标转换、.smap 解析
- 集成测试：数据加载、配置回退
- 手动测试：交互体验、动画流畅度
- 性能测试：大地图加载、多 AGV 动画

---

**设计完成日期：** 2026-08-10  
**当前状态：** 阶段 1-5 已实现并通过 Release 全量测试 `327/327`；
路线/完整地图取景、细路线与方向箭头、地图身份 fail-closed、深色障碍扫描、特征勾勒线、图层开关、站点只读详情和 PNG 导出均已完成。
完整地图导出使用未变换画布，当前视口导出保留当前变换；两者均服从图层开关和运行叠加 fail-closed 门禁。
**后续：** PDF 导出和真实 AGV 现场验收按独立任务推进；真实 AGV 继续 `NO-GO`。
