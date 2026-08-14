# 实验流程可视化编辑器开发记录

## 📋 项目概述

基于 Nodify 框架的实验流程可视化编辑器，用于图形化设计和管理实验任务流程。

**分支**: `feat/experiment-flow-nodify`  
**开始时间**: 2026-08-13  
**状态**: ✅ 已完成核心功能

---

## ✨ 已完成功能

### 1. 核心编辑器 (2026-08-13)

#### 1.1 节点系统
- ✅ **7种节点类型**:
  - `Move` - AGV移动节点
  - `Wait` - 等待节点
  - `DataImport` - 数据导入节点
  - `InstrumentOperation` - 仪器操作节点
  - `ParallelGateway` - 并行分叉节点
  - `ParallelJoin` - 并行汇合节点
  - `PickPlace` - 拾取/放置节点

- ✅ **节点操作**:
  - 拖拽创建节点
  - 节点位置自由拖动
  - 节点属性编辑（标题、描述）
  - 节点删除（带确认）
  - 节点选择/高亮

#### 1.2 连接管理
- ✅ **连接操作**:
  - 拖拽创建连接线
  - 右侧面板添加连接
  - 修改连接源节点/目标节点
  - 删除连接
  - 条件标签编辑

- ✅ **多分支颜色系统**:
  - 自动颜色分配（6种颜色循环）
  - 第1条: 🔵 蓝色 (#4285F4)
  - 第2条: 🟢 绿色 (#34A853)
  - 第3条: 🟡 橙色 (#FBBC04)
  - 第4条: 🔴 红色 (#EA4335)
  - 第5条: 🟣 紫色 (#9334E6)
  - 第6条: 🟠 深橙色 (#FF6D01)
  - 连接线和标签使用相同颜色
  - 旧配置文件自动升级颜色

- ✅ **条件标签显示**:
  - 显示在连接线中点位置
  - 自动跟随节点移动
  - 颜色与连接线一致
  - 仅在有条件时显示

#### 1.3 自动布局
- ✅ **层次化布局算法**:
  - 从起始节点自动排列
  - 多分支展开布局
  - 并行路径对齐
  - 水平间距: 280px
  - 垂直间距: 150px

#### 1.4 配置管理
- ✅ **导出功能**:
  - JSON 格式导出
  - 包含节点信息（ID、标题、描述、类型、坐标）
  - 包含连接信息（源、目标、条件、颜色）
  - 可读格式（缩进、中文友好）

- ✅ **导入功能**:
  - JSON 文件导入
  - 自动重建节点和连接
  - 兼容旧配置文件（无颜色信息）
  - 自动分配缺失的颜色

#### 1.5 UI 优化
- ✅ **右侧属性面板**:
  - 节点信息展示
  - 输入/输出连接管理
  - 条件标签编辑
  - 添加/删除连接按钮

- ✅ **对话框优化**:
  - 添加连接对话框（550x450）
  - 节点列表显示（标题、类型、描述）
  - 条件输入框（最大50字符）
  - 清晰的按钮布局

---

## 🏗️ 技术架构

### 依赖包
```xml
<PackageReference Include="Nodify" Version="2.2.0" />
<PackageReference Include="Nodify.Compatibility" Version="2.2.0" />
```

### 核心文件结构
```
src/MesControlAgv.Wpf/
├── ViewModels/
│   ├── ExperimentFlowEditorViewModel.cs      # 主 ViewModel
│   └── ExperimentFlowConfigDto.cs             # 配置数据模型
├── Views/
│   ├── ExperimentFlowEditorView.xaml          # 主视图
│   ├── AddConnectionDialog.xaml               # 添加连接对话框
│   └── NodeConfigDialog.xaml                  # 节点配置对话框
├── Converters/
│   ├── StringToVisibilityConverter.cs         # 字符串可见性转换器
│   └── VisibilityConverters.cs                # 其他可见性转换器
└── Infrastructure/
    └── RelayCommand.cs                        # MVVM 命令实现
```

### 数据模型

#### ExperimentFlowNode
```csharp
public sealed class ExperimentFlowNode
{
    public Guid Id { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string Type { get; set; }
    public Point Location { get; set; }
    public ObservableCollection<ExperimentFlowConnector> Input { get; }
    public ObservableCollection<ExperimentFlowConnector> Output { get; }
}
```

#### ExperimentFlowConnection
```csharp
public sealed class ExperimentFlowConnection
{
    public Guid Id { get; set; }
    public ExperimentFlowConnector? Source { get; set; }
    public ExperimentFlowConnector? Target { get; set; }
    public Point SourcePoint { get; set; }
    public Point TargetPoint { get; set; }
    public Point MidPoint { get; }  // 连接线中点（用于标签）
    public string Condition { get; set; }
    public string Color { get; set; }  // 自动分配的颜色
    public bool IsSelected { get; set; }
}
```

### 关键算法

#### 自动颜色分配
```csharp
private string GetAutoConnectionColor(ExperimentFlowNode sourceNode)
{
    var existingOutputCount = Connections.Count(c => c.Source?.Node == sourceNode);
    return BranchColors[existingOutputCount % BranchColors.Length];
}
```

#### 层次化布局
```csharp
private void AutoLayout()
{
    // 1. 找到起始节点（无输入连接）
    // 2. 按层级分组
    // 3. 计算每层的垂直位置
    // 4. 处理分支展开
    // 5. 设置节点坐标
}
```

---

## 🎯 使用指南

### 创建流程
1. 点击顶部节点类型按钮创建节点
2. 拖拽节点调整位置
3. 从输出连接点拖拽到输入连接点创建连接
4. 或使用右侧面板的"添加连接"功能

### 编辑连接
1. 选中源节点
2. 在右侧面板找到对应连接
3. 编辑条件标签
4. 可修改源节点/目标节点
5. 可删除连接

### 多分支区分
- 同一节点的多条输出自动分配不同颜色
- 条件标签与连接线颜色一致
- 便于追踪复杂的分支路径

### 保存和加载
1. 点击"导出配置"保存为 JSON 文件
2. 点击"导入配置"加载已保存的流程
3. 旧配置文件会自动升级颜色方案

---

## 🐛 已解决问题

### 问题1: 输入条件时出现大白框
**现象**: 在条件标签 TextBox 中输入时，画布中央出现巨大白色框  
**原因**: 连接线模板中的条件标签 Border 使用了 TranslateTransform，位置计算错误  
**解决**: 改用 Canvas.Left/Top 定位，使用连接线中点坐标

### 问题2: 条件标签被节点遮挡
**现象**: 标签显示在连接线起点，被源节点遮挡  
**原因**: 标签位置设置为 SourcePoint  
**解决**: 计算连接线中点 MidPoint，标签显示在中间并向上偏移10px

### 问题3: 旧配置文件导入后全是蓝色
**现象**: 导入旧配置文件后，所有连接都是蓝色，没有颜色区分  
**原因**: 旧配置文件没有 Color 字段，默认值为 #4285F4  
**解决**: 导入后检查是否所有连接都是默认色，如果是则按源节点重新分配颜色

### 问题4: 对话框太小，内容显示不全
**现象**: 添加连接对话框无法完整显示节点信息  
**原因**: 对话框尺寸设置为 450x350，节点列表项过小  
**解决**: 扩大对话框至 550x450，增加列表项内边距，支持文本换行

---

## 📊 代码统计

- **新增文件**: 14 个
- **新增代码**: 约 2200 行
- **修改文件**: 3 个（MainWindow.xaml, MainViewModel.cs, .csproj）

---

## 🚀 后续计划

### 短期优化
- [ ] 添加撤销/重做功能
- [ ] 节点搜索和过滤
- [ ] 连接线样式选择（直线/曲线）
- [ ] 导出为图片
- [ ] 流程验证（检查死循环、孤立节点）

### 中期功能
- [ ] 节点分组/子流程
- [ ] 流程模板库
- [ ] 批量操作（复制、粘贴、多选）
- [ ] 网格对齐和吸附
- [ ] 迷你地图导航

### 长期集成
- [ ] 与任务执行引擎集成
- [ ] 实时执行状态可视化
- [ ] 流程版本管理
- [ ] 协作编辑功能
- [ ] 流程性能分析

---

## 📝 提交记录

### Commit: 5805509 (2026-08-13)
```
feat: add experiment flow visual editor with Nodify

✨ 新增功能
- 基于 Nodify 的可视化流程编辑器
- 支持拖拽创建节点和连接
- 节点类型：移动、等待、数据导入、仪器操作、并行分叉/汇合、放置/获取
- 自动布局功能（层次化排列）
- 配置文件导入/导出（JSON格式）

🎨 连接管理
- 右侧面板可编辑连接关系
- 添加/删除/修改连接的源节点和目标节点
- 条件标签编辑（显示在连接线中点）
- 多分支自动颜色分配（最多6种颜色循环）
- 旧配置文件自动升级颜色方案

🔧 技术实现
- MVVM 架构，数据绑定
- Nodify 2.2.0 用于节点图渲染
- 实时连接点更新，支持节点拖动
- 完整的撤销/重做支持（框架级）
```

---

## 🎓 经验总结

### 技术要点
1. **Nodify 学习曲线**: 需要理解 Connection/Connector/Editor 的工作机制
2. **WPF 数据绑定**: 合理使用 INotifyPropertyChanged 确保 UI 更新
3. **Canvas 坐标系**: 理解 Nodify 的坐标变换和定位机制
4. **颜色设计**: 选择对比度高、易区分的颜色方案

### 最佳实践
1. **先用简单模型验证**: 从最小可用版本开始，逐步添加功能
2. **分离 UI 和逻辑**: ViewModel 专注数据和业务，View 专注呈现
3. **及时测试边界情况**: 空节点、单连接、多连接都要测试
4. **保持向后兼容**: 新功能要兼容旧配置文件

### 踩坑经验
1. **避免在模板中使用复杂变换**: 容易导致位置错误
2. **使用计算属性而非事件更新位置**: 更可靠且代码更简洁
3. **WPF 没有 Spacing 属性**: StackPanel 要用 Margin 模拟间距
4. **注意 JSON 反序列化默认值**: 可能与预期不符，需要后处理

---

## 📚 参考资料

- [Nodify GitHub](https://github.com/miroiu/nodify)
- [Nodify 示例](https://github.com/miroiu/nodify/tree/master/Examples)
- [WPF MVVM 模式](https://docs.microsoft.com/en-us/dotnet/desktop/wpf/data/)

---

**最后更新**: 2026-08-13  
**维护者**: Sliencelove
