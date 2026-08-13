# 架构决策：直接控制 vs 通过 AGV 中转

## 关键架构问题

你提出了一个**非常重要的架构决策**：

### 方案 A：中控直接控制所有设备（推荐）
```
┌────────────────────────────┐
│    中控 MES                │
│    192.168.1.10            │
└───┬─────┬─────┬────────────┘
    │     │     │
    │     │     └────────────┐
    │     │                  │
    ▼     ▼                  ▼
┌──────┐ ┌──────┐        ┌──────┐
│ AGV  │ │机械臂│        │ 视觉 │
│.100  │ │.101  │        │.102  │
└──────┘ └──────┘        └──────┘
```

**优势**：
- ✅ 完全掌控所有设备
- ✅ 实时监控每个设备状态
- ✅ 独立的超时和异常处理
- ✅ 可以单独测试每个设备
- ✅ 灵活的任务编排

**劣势**：
- ❌ 需要中控 PC 能访问所有设备（需要交换机）
- ❌ 网络拓扑稍复杂

---

### 方案 B：通过 AGV 控制器中转（你提出的）
```
┌────────────────────────────┐
│    中控 MES                │
│    192.168.1.10            │
└────────────┬───────────────┘
             │
             │ 只连接 AGV
             ▼
        ┌────────┐
        │  AGV   │
        │ 控制器 │
        │  .100  │
        └────┬───┘
             │
      AGV 内部网络
             │
      ┌──────┴──────┐
      ▼             ▼
   ┌──────┐     ┌──────┐
   │机械臂│     │ 视觉 │
   │.101  │     │.102  │
   └──────┘     └──────┘
```

**中控 → AGV 控制器**："请在当前位置，用视觉识别物体，然后机械臂抓取"
**AGV 控制器**：内部协调机械臂和视觉，完成后告诉中控结果

**优势**：
- ✅ 中控只需连接 AGV（网络简单）
- ✅ AGV、机械臂、视觉作为一个整体
- ✅ 中控不需要了解机械臂和视觉的细节

**劣势**：
- ❌ **依赖 AGV 控制器的能力**（它能转发指令吗？）
- ❌ **中控无法直接监控机械臂和视觉状态**
- ❌ **无法独立测试机械臂和视觉**
- ❌ **故障排查困难**（不知道是哪个环节出问题）
- ❌ **灵活性差**（AGV 控制器可能不支持复杂编排）

---

## 核心问题：AGV 控制器支持中转吗？

这取决于你使用的 **AGV 控制器的能力**：

### 如果 AGV 控制器支持：
- 提供了"执行抓取任务"的高层接口
- 内部自动协调机械臂和视觉
- 例如：`POST /api/pick_at_current_location`
  - AGV 控制器接收后，自动调用视觉识别
  - 然后控制机械臂抓取
  - 完成后返回结果

**那么方案 B 可行**，你的中控只需：
```csharp
// 导航到源站
await agvClient.NavigateAsync("SAMPLE_01");

// 让 AGV 自己完成"视觉+抓取"
await agvClient.PickWithVisionAsync();

// 导航到目标站
await agvClient.NavigateAsync("ST_PREP_01");

// 让 AGV 自己完成"放置"
await agvClient.PlaceAsync();
```

### 如果 AGV 控制器不支持（大概率）：
- 它只负责导航（移动到指定站点）
- 机械臂和视觉需要**独立控制**
- 你的中控必须自己编排

**那么必须用方案 A**，中控需要直接控制：
```csharp
// 1. 导航到源站
await agvClient.NavigateAsync("SAMPLE_01");

// 2. 中控自己调用视觉
var visionResult = await visionClient.CaptureAndLocalizeAsync();

// 3. 中控自己调用机械臂
await robotArmClient.PickAsync(visionResult.Position);

// 4. 导航到目标站
await agvClient.NavigateAsync("ST_PREP_01");

// 5. 中控自己调用机械臂
await robotArmClient.PlaceAsync(targetPosition);
```

---

## 判断方法

### 查看 AGV 控制器的 API

你现有的 `TcpAgvClient.cs` 已经对接了 AGV 控制器，看看它的 API：

```csharp
// 当前已有的接口
public interface IAgvDeviceClient
{
    Task EnsureControlAsync(CancellationToken cancellationToken);
    Task<AgvSnapshotResponse> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<AgvTaskResponse> NavigateAsync(Guid taskId, string? sourceStationId, string stationId, ...);
    Task<AgvTaskResponse?> PauseAsync(Guid taskId, ...);
    Task<AgvTaskResponse?> ResumeAsync(Guid taskId, ...);
    Task<AgvTaskResponse?> CancelAsync(Guid taskId, ...);
}
```

**如果 AGV 控制器还提供了**：
- `PickAsync()` - 抓取
- `PlaceAsync()` - 放置
- `CaptureImageAsync()` - 拍照
- `RecognizeObjectAsync()` - 识别

→ 说明它支持中转，用方案 B

**如果没有这些接口**：
→ 必须用方案 A，中控直接控制

---

## 我的建议：使用方案 A（直接控制）

基于以下原因：

### 1. 通用性
大多数 AGV 控制器只负责导航，不会集成机械臂和视觉的控制逻辑。

### 2. 灵活性
你可以自由编排任务流程：
- 先拍照，再决定是否抓取
- 抓取失败后重试
- 根据视觉结果调整机械臂策略

### 3. 可维护性
- 独立测试每个设备
- 清晰的日志和错误定位
- 便于调试和优化

### 4. 可扩展性
未来可能需要：
- 更换机械臂品牌
- 升级视觉算法
- 增加其他传感器

方案 A 更容易适配。

---

## 网络问题的解决

你担心"需要加入他们的网络"，其实很简单：

### 选项 1：所有设备同一网段（推荐）
```
交换机
├── 中控 PC: 192.168.1.10
├── AGV: 192.168.1.100 (WiFi)
├── 机械臂: 192.168.1.101
└── 视觉: 192.168.1.102
```

**只需要一个交换机（200元），所有设备都在 `192.168.1.x` 网段**

### 选项 2：如果 AGV 内部有交换机
```
中控 PC (192.168.1.10)
    │
    └──> AGV WiFi 接口 (192.168.1.100)
              │
              └──> AGV 内部交换机
                    ├── 机械臂 (192.168.1.101)
                    └── 视觉 (192.168.1.102)
```

**需要确认**：
- AGV 的 WiFi 接口能否转发到内部网络？
- 中控能否通过 AGV 访问机械臂和视觉？

测试方法：
```powershell
# 中控 PC 上执行
ping 192.168.1.100  # 能 ping 通 AGV
ping 192.168.1.101  # 能 ping 通机械臂？
ping 192.168.1.102  # 能 ping 通视觉？
```

如果后两个 ping 不通，说明 AGV 内部网络与外部隔离，必须用交换机。

---

## 最终方案建议

### 短期（本周）：Mock 开发

我先帮你实现 Mock 版本：

1. **创建机械臂 Mock 驱动**
   ```csharp
   public class MockRobotArmClient : IRobotArmDeviceClient
   {
       // 模拟抓取、放置等操作
       // 返回假数据
   }
   ```

2. **创建视觉 Mock 驱动**
   ```csharp
   public class MockVisionClient : IVisionDeviceClient
   {
       // 模拟拍照、识别、定位
       // 返回假坐标
   }
   ```

3. **实现 CompoundTaskService**
   ```csharp
   // 完整的任务编排逻辑
   // 协调 AGV、机械臂、视觉
   ```

4. **跑通整个流程**
   - 单元测试
   - 集成测试
   - WPF 界面集成

**优势**：
- ✅ 不需要真实设备
- ✅ 可以完整验证 MES 逻辑
- ✅ 为真实对接打好基础

### 中期（下周）：网络验证

1. **配置交换机**（如果需要）
2. **验证网络连通性**
   ```bash
   ping 192.168.1.100
   ping 192.168.1.101
   ping 192.168.1.102
   ```
3. **使用协议测试工具**探测实际接口

### 长期（1-2周后）：真实对接

1. **实现真实的机械臂客户端**（基于厂商协议）
2. **实现真实的视觉客户端**（基于厂商协议）
3. **替换 Mock 驱动**
4. **现场联调测试**

---

## 我现在应该做什么？

### 立即开始：创建 Mock 驱动和编排服务

我可以帮你创建：

1. **接口定义**
   - `IRobotArmDriver`
   - `IVisionDriver`
   - 相关的 Command/Response 类型

2. **Mock 实现**
   - `MockRobotArmClient`
   - `MockVisionClient`

3. **编排服务**
   - `CompoundTaskService`
   - 任务状态机
   - 异常处理

4. **测试用例**
   - 完整流程测试
   - 异常场景测试

5. **WPF 集成**
   - 添加机械臂和视觉状态显示
   - 添加综合任务创建界面

**这些都不需要真实设备，可以立即开始！**

---

## 你的决定

请告诉我：

**问题 1: 架构方案**
- [ ] 方案 A：中控直接控制所有设备（推荐）
- [ ] 方案 B：通过 AGV 中转（需要确认 AGV 控制器支持）
- [ ] 不确定，先看看 AGV 控制器的能力

**问题 2: 开发策略**
- [ ] 立即开始 Mock 开发（推荐）
- [ ] 等网络配置好再开发
- [ ] 先联系厂商确认协议

**问题 3: 时间安排**
- 这个功能什么时候需要完成？
- 是紧急项目还是可以慢慢做？

**我的推荐**：
1. **架构**：用方案 A（直接控制）
2. **策略**：立即开始 Mock 开发
3. **并行**：同时采购交换机（200元）

这样可以**最快速度推进**，不浪费任何等待时间！

要不要我现在就开始创建代码？
