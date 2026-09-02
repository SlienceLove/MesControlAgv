# 机械臂与视觉集成开发路线图

> **现场网络修正（2026-08-27）**：RoboshopPro 实测当前 AGV 控制器为
> `192.168.1.2`；早期 `.100/.101/.102` 仅是示意，设备角色需重新确认。
> DO/DI 的直连验证见 [AGV I/O 直连记录](AGV-IO-DIRECT-CONTROL.md)。

## 📋 当前状态

✅ **已完成**：
- Mock 驱动开发完成
- V2 优化版编排服务
- 完整测试套件
- 技术文档齐全
- 代码已推送到 `feat/robot-arm-vision-integration` 分支

## 🎯 开发阶段规划

### 阶段 1：硬件准备与协议获取 (Week 1)

#### 1.1 网络配置
**目标**：连接所有设备到同一网络

**任务清单**：
- [ ] 采购设备
  - [ ] 千兆交换机（TP-Link TL-SG1008D，~150元）
  - [ ] 网线 5m × 4根（~60元）
  - [ ] 可选：无线路由器（如果 AGV 需要移动）
  
- [ ] 网络配置
  - [ ] 中控 PC 配置静态 IP：`192.168.1.10`
  - [x] 现场确认当前 RoboshopPro AGV 控制器 IP：`192.168.1.2`
  - [ ] 机械臂/视觉的 `.100/.101/.102` 角色仍需按现场线缆和设备界面确认
  
- [ ] 连通性测试
  ```bash
  ping 192.168.1.2    # 已确认：AGV 控制器
  ping 192.168.1.102  # 已见于 AUBO 示教器，需在同一链路复核
  ```

**验收标准**：
- 中控 PC 能 ping 通所有设备
- 延迟 < 10ms
- 无丢包

---

#### 1.2 协议文档获取
**目标**：获取厂商通信协议完整文档

**需要从厂商获取**：

**敖博机械臂**：
- [ ] 通信协议文档
  - TCP 端口号
  - 报文格式（JSON/二进制）
  - 命令列表和参数说明
  - 状态码定义
  - 错误码列表
  
- [ ] 示例代码
  - 连接建立示例
  - 移动/抓取/放置示例
  - 状态查询示例
  
- [ ] 坐标系说明
  - 基坐标系定义
  - 工具坐标系定义
  - 单位（mm/m，度/弧度）

**VisionGroup2**：
- [ ] API 文档
  - HTTP/TCP 接口地址
  - 认证方式
  - 接口列表（拍照、识别、定位）
  - 请求/响应格式
  
- [ ] 标定说明
  - Mark 点的作用和位置
  - 坐标转换方法
  - 转换矩阵格式
  
- [ ] 示例数据
  - 实际的识别结果
  - 实际的定位坐标

**获取方式**：
1. 联系厂商技术支持
2. 查看已有文档：`D:\Docment\VSGroup`
3. 现场抓包分析（备用方案）

**验收标准**：
- 拥有完整的接口文档
- 有至少一个完整的命令示例
- 理解坐标系转换关系

**预计时间**：2-3 天

---

### 阶段 2：协议测试工具开发 (Week 1-2)

#### 2.1 创建独立测试工具
**目标**：验证通信协议，无需依赖 MES 系统

**任务清单**：
- [ ] 创建 `DeviceProtocolTester` 项目
  ```csharp
  // 独立的控制台工具
  src/MesControlAgv.DeviceProtocolTester/
  ├── Program.cs
  ├── RobotArmTester.cs
  ├── VisionTester.cs
  └── appsettings.json
  ```

- [ ] 机械臂测试器
  ```csharp
  public class RobotArmTester
  {
      // 测试连接
      public async Task TestConnectionAsync();
      
      // 测试状态查询
      public async Task TestGetStatusAsync();
      
      // 测试移动
      public async Task TestMoveAsync(double x, double y, double z);
      
      // 测试夹爪
      public async Task TestGripperAsync(bool open);
      
      // 记录原始报文
      public void LogRawMessage(string direction, byte[] data);
  }
  ```

- [ ] 视觉测试器
  ```csharp
  public class VisionTester
  {
      // 测试拍照
      public async Task TestCaptureAsync();
      
      // 测试识别
      public async Task TestRecognizeAsync(string imageId);
      
      // 测试定位
      public async Task TestLocalizeAsync(string recognitionId);
      
      // 查看标定参数
      public async Task GetCalibrationAsync();
  }
  ```

**功能要求**：
- 记录所有通信报文（请求和响应）
- 保存日志到文件
- 支持命令行参数
- 输出可读的 JSON 格式

**使用示例**：
```bash
# 测试机械臂连接
dotnet run --project DeviceProtocolTester -- robot-arm test-connection --ip 192.168.1.102

# 测试移动
dotnet run --project DeviceProtocolTester -- robot-arm move --x 300 --y 200 --z 50

# 测试视觉拍照
dotnet run --project DeviceProtocolTester -- vision capture --preset lab_material

# 记录完整会话
dotnet run --project DeviceProtocolTester -- robot-arm record-session --output session.log
```

**验收标准**：
- 能成功连接设备
- 能发送和接收报文
- 完整记录通信日志
- 能执行基本操作

**预计时间**：3-4 天

---

### 阶段 3：真实驱动实现 (Week 2-3)

#### 3.1 实现敖博机械臂驱动
**目标**：替换 Mock 驱动为真实实现

**任务清单**：
- [ ] 创建 `AobotRobotArmClient.cs`
  ```csharp
  namespace MesControlAgv.Adapter.Services;
  
  public sealed class AobotRobotArmClient : IRobotArmDeviceClient
  {
      private readonly TcpClient _tcpClient;
      private readonly AobotArmOptions _options;
      
      // 实现所有接口方法
      public async Task<RobotArmStatusResponse> GetStatusAsync(...);
      public async Task<RobotArmOperationResponse> PickAsync(...);
      public async Task<RobotArmOperationResponse> PlaceAsync(...);
      // ...
  }
  ```

- [ ] 实现协议层
  - 连接管理（重连机制）
  - 报文编码/解码
  - 超时控制
  - 错误处理
  
- [ ] 实现驱动工厂
  ```csharp
  public sealed class AobotRobotArmDriver : IRobotArmDriver
  {
      public const string DriverKind = "aobot-arm";
      // ...
  }
  
  public sealed class AobotRobotArmDriverFactory : IRobotArmDriverFactory
  {
      // ...
  }
  ```

- [ ] 单元测试
  ```csharp
  // tests/MesControlAgv.Adapter.Tests/AobotRobotArmClientTests.cs
  public sealed class AobotRobotArmClientTests
  {
      [Fact]
      public async Task Connect_ShouldSucceed();
      
      [Fact]
      public async Task GetStatus_ShouldReturnValidResponse();
      
      [Fact]
      public async Task Pick_ShouldCompleteSuccessfully();
      // ...
  }
  ```

**关键点**：
- 参考 `TcpAgvClient.cs` 的实现模式
- 复用现有的 TCP 通信基础设施
- 实现完整的异常处理
- 添加详细的日志记录

**验收标准**：
- 能连接真实机械臂
- 能执行所有操作（移动、抓取、放置）
- 能正确处理错误
- 单元测试通过

**预计时间**：3-4 天

---

#### 3.2 实现 VisionGroup2 驱动
**目标**：实现视觉系统真实驱动

**任务清单**：
- [ ] 创建 `VisionGroup2Client.cs`
  ```csharp
  namespace MesControlAgv.Adapter.Services;
  
  public sealed class VisionGroup2Client : IVisionDeviceClient
  {
      private readonly HttpClient _httpClient;
      private readonly VisionGroup2Options _options;
      
      public async Task<VisionCaptureResponse> CaptureAsync(...);
      public async Task<VisionRecognitionResponse> RecognizeAsync(...);
      public async Task<VisionLocalizationResponse> LocalizeAsync(...);
      // ...
  }
  ```

- [ ] 实现 HTTP/TCP 通信
  - 根据文档选择协议
  - 认证处理
  - 超时控制
  
- [ ] 坐标转换实现
  ```csharp
  public sealed class VisionCoordinateTransformer
  {
      // 如果视觉系统返回相机坐标，需要转换到机械臂坐标
      public RobotArmPose TransformToRobotBase(
          VisionLocalizationResponse visionResult,
          CalibrationMatrix calibration);
  }
  ```

- [ ] 驱动工厂
  ```csharp
  public sealed class VisionGroup2Driver : IVisionDriver
  {
      public const string DriverKind = "visiongroup2";
      // ...
  }
  ```

- [ ] 单元测试
  ```csharp
  public sealed class VisionGroup2ClientTests
  {
      [Fact]
      public async Task Capture_ShouldReturnImage();
      
      [Fact]
      public async Task Recognize_ShouldFindObject();
      
      [Fact]
      public async Task Localize_ShouldReturnCoordinates();
  }
  ```

**关键点**：
- 理解 Mark 点的使用
- 正确实现坐标转换
- 处理识别失败情况
- 验证标定有效性

**验收标准**：
- 能连接视觉系统
- 能拍照和识别
- 能获取准确的 3D 坐标
- 坐标转换正确

**预计时间**：3-4 天

---

### 阶段 4：集成测试 (Week 3-4)

#### 4.1 驱动切换配置
**目标**：支持 Mock 和真实驱动的灵活切换

**任务清单**：
- [ ] 更新配置文件
  ```json
  // appsettings.json
  {
    "Drivers": {
      "RobotArm": {
        "Type": "mock", // 或 "aobot"
        "Host": "192.168.1.102",
        "Port": 8080
      },
      "Vision": {
        "Type": "mock", // 或 "visiongroup2"
        "Host": "192.168.1.101",
        "Port": 8081
      }
    }
  }
  ```

- [ ] 实现驱动选择逻辑
  ```csharp
  // Program.cs
  var robotArmType = configuration["Drivers:RobotArm:Type"];
  
  if (robotArmType == "aobot")
  {
      services.AddSingleton<IRobotArmDriverFactory, AobotRobotArmDriverFactory>();
  }
  else
  {
      services.AddSingleton<IRobotArmDriverFactory, MockRobotArmDriverFactory>();
  }
  ```

**验收标准**：
- 配置切换无需修改代码
- Mock 和真实驱动可以共存
- 切换后功能正常

**预计时间**：1 天

---

#### 4.2 端到端测试
**目标**：验证完整流程

**测试场景**：

**场景 1：基础搬运（无视觉）**
```
1. AGV 导航到 SAMPLE_01
2. 机械臂在固定坐标抓取
3. AGV 导航到 ST_PREP_01
4. 机械臂在固定坐标放置
```

**场景 2：视觉引导抓取**
```
1. AGV 导航到 SAMPLE_01
2. 视觉拍照识别物体
3. 机械臂根据视觉坐标抓取
4. AGV 导航到 ST_PREP_01
5. 机械臂放置物体
```

**场景 3：错误恢复**
```
1. 视觉识别失败 → 自动重试
2. 机械臂抓取失败 → 任务失败报警
3. AGV 导航失败 → 自动回滚
```

**任务清单**：
- [ ] 编写 E2E 测试脚本
  ```csharp
  [Fact]
  public async Task RealHardware_BasicTransport_ShouldComplete()
  {
      // 使用真实驱动的完整测试
  }
  ```

- [ ] 现场测试
  - 空载测试（验证动作正确性）
  - 负载测试（实际物体）
  - 异常测试（模拟故障）
  
- [ ] 性能测试
  - 测量各阶段耗时
  - 优化慢速环节
  - 记录基准数据

**验收标准**：
- 完整流程无错误执行
- 异常能正确处理
- 性能满足要求（单次任务 < 5 分钟）

**预计时间**：5-7 天

---

### 阶段 5：WPF 界面集成 (Week 4-5)

#### 5.1 设备状态监控
**目标**：在 WPF 界面显示机械臂和视觉状态

**任务清单**：
- [ ] 创建状态查询服务
  ```csharp
  public sealed class DeviceStatusService
  {
      public async Task<DeviceHealthStatus> GetAllDevicesStatusAsync();
  }
  
  public sealed record DeviceHealthStatus(
      AgvStatus Agv,
      RobotArmStatus Arm,
      VisionStatus Vision);
  ```

- [ ] 添加 WPF 控件
  ```xml
  <!-- MainWindow.xaml -->
  <GroupBox Header="设备状态">
      <StackPanel>
          <TextBlock Text="AGV: 在线 | 位置: SAMPLE_01" />
          <TextBlock Text="机械臂: 就绪 | 状态: 空闲" />
          <TextBlock Text="视觉: 已标定 | 最后识别: 0.95" />
      </StackPanel>
  </GroupBox>
  ```

- [ ] 实时状态更新
  ```csharp
  // ViewModel
  private async Task UpdateDeviceStatusAsync()
  {
      while (!_cancellationToken.IsCancellationRequested)
      {
          var status = await _deviceStatusService.GetAllDevicesStatusAsync();
          // 更新 UI
          await Task.Delay(2000); // 每 2 秒更新
      }
  }
  ```

**验收标准**：
- 实时显示设备状态
- 状态颜色指示（绿色正常/黄色警告/红色错误）
- 更新频率合理（1-2秒）

**预计时间**：2-3 天

---

#### 5.2 综合任务创建界面
**目标**：支持创建视觉引导任务

**任务清单**：
- [ ] 添加任务配置界面
  ```xml
  <GroupBox Header="综合任务配置">
      <StackPanel>
          <CheckBox Content="启用视觉引导" IsChecked="{Binding RequireVision}" />
          <CheckBox Content="源站抓取" IsChecked="{Binding RequirePick}" />
          <CheckBox Content="目标站放置" IsChecked="{Binding RequirePlace}" />
          
          <GroupBox Header="抓取位姿">
              <Grid>
                  <TextBox Text="{Binding PickPose.X}" />
                  <TextBox Text="{Binding PickPose.Y}" />
                  <TextBox Text="{Binding PickPose.Z}" />
              </Grid>
          </GroupBox>
      </StackPanel>
  </GroupBox>
  ```

- [ ] 实现任务创建逻辑
  ```csharp
  private async Task CreateCompoundTaskAsync()
  {
      var task = new CompoundTransportTask(
          TaskId: Guid.NewGuid(),
          SourceStationId: SelectedSourceStation,
          TargetStationId: SelectedTargetStation,
          Profile: new CompoundTaskProfile(
              RequireVisionGuidance: RequireVision,
              RequirePickAtSource: RequirePick,
              RequirePlaceAtTarget: RequirePlace,
              DefaultPickPose: PickPose,
              DefaultPlacePose: PlacePose));
      
      await _compoundTaskService.ExecuteAsync(task, CancellationToken);
  }
  ```

- [ ] 任务进度显示
  ```xml
  <ProgressBar Value="{Binding TaskProgress}" Maximum="100" />
  <TextBlock Text="{Binding TaskStatus}" />
  ```

**验收标准**：
- 可配置任务参数
- 显示任务进度
- 显示任务状态和错误

**预计时间**：2-3 天

---

### 阶段 6：生产部署准备 (Week 5-6)

#### 6.1 配置管理
**任务清单**：
- [ ] 创建生产环境配置
  ```json
  // appsettings.Production.json
  {
    "Drivers": {
      "RobotArm": {
        "Type": "aobot",
        "Host": "192.168.1.102",
        "Port": 8080,
        "TimeoutMs": 5000,
        "MaxRetries": 3
      },
      "Vision": {
        "Type": "visiongroup2",
        "Host": "192.168.1.101",
        "Port": 8081
      }
    },
    "CompoundTask": {
      "AgvNavigationTimeout": "00:05:00",
      "EnableDevicePreCheck": true,
      "EnableRollbackOnFailure": true
    }
  }
  ```

- [ ] 创建部署脚本
  ```powershell
  # deploy.ps1
  dotnet publish src/MesControlAgv.Launcher -c Release -o deploy/
  Copy-Item appsettings.Production.json deploy/appsettings.json
  ```

**预计时间**：1 天

---

#### 6.2 文档更新
**任务清单**：
- [ ] 更新 README
  - 添加机械臂和视觉功能说明
  - 更新配置示例
  
- [ ] 编写操作手册
  ```markdown
  # 综合任务操作指南
  
  ## 1. 设备准备
  - 确认 AGV 在线
  - 确认机械臂连接
  - 确认视觉系统标定
  
  ## 2. 创建任务
  - 选择源站和目标站
  - 配置视觉引导选项
  - 设置抓取和放置位姿
  
  ## 3. 监控执行
  - 观察任务状态
  - 检查设备状态
  - 查看日志
  ```

- [ ] 故障排查指南
  ```markdown
  # 常见问题
  
  Q: 机械臂连接失败
  A: 检查 IP 地址，检查网络连接
  
  Q: 视觉识别失败
  A: 检查光照条件，重新标定
  ```

**预计时间**：1-2 天

---

## 📊 时间线总结

| 阶段 | 任务 | 预计时间 | 关键里程碑 |
|------|------|---------|-----------|
| **Week 1** | 硬件准备 + 协议获取 | 5 天 | 网络连通，协议文档齐全 |
| **Week 1-2** | 协议测试工具 | 3-4 天 | 能与设备通信并记录日志 |
| **Week 2-3** | 真实驱动实现 | 6-8 天 | 机械臂和视觉驱动完成 |
| **Week 3-4** | 集成测试 | 6-8 天 | 完整流程验证通过 |
| **Week 4-5** | WPF 界面集成 | 4-6 天 | 用户界面完成 |
| **Week 5-6** | 生产部署 | 2-3 天 | 文档齐全，可部署 |

**总计**：26-34 天（约 4-5 周）

---

## 🎯 优先级建议

### 高优先级（必须完成）
1. 网络配置 ✅
2. 协议文档获取 ✅
3. 真实驱动实现 ✅
4. 基础集成测试 ✅

### 中优先级（推荐完成）
5. 协议测试工具
6. WPF 界面集成
7. 完整测试场景

### 低优先级（锦上添花）
8. 性能优化
9. 详细文档
10. 高级功能

---

## 📝 检查点

每个阶段结束前，确认：
- [ ] 代码编译通过
- [ ] 相关测试通过
- [ ] 功能验证完成
- [ ] 文档已更新
- [ ] Git 提交推送

---

## 🚨 风险提示

### 技术风险
1. **协议不兼容**：厂商文档可能与实际不符
   - 缓解：先用测试工具验证
   
2. **坐标系转换错误**：视觉和机械臂坐标不准
   - 缓解：多次标定验证
   
3. **网络不稳定**：WiFi 连接可能中断
   - 缓解：使用有线连接，添加重连机制

### 时间风险
1. **协议复杂度超预期**
   - 缓解：预留缓冲时间
   
2. **现场调试耗时**
   - 缓解：充分的离线测试

### 依赖风险
1. **厂商支持响应慢**
   - 缓解：提前沟通，准备备用方案
   
2. **硬件故障**
   - 缓解：提前测试设备状态

---

## ✅ 成功标准

**阶段性目标**：
- Week 1 结束：能与设备通信
- Week 2 结束：真实驱动可用
- Week 3 结束：完整流程跑通
- Week 4 结束：界面集成完成
- Week 5 结束：可以部署使用

**最终目标**：
- 系统稳定运行 24 小时无故障
- 任务成功率 > 95%
- 单次任务时间 < 5 分钟
- 用户满意度良好

---

**准备好开始了吗？建议先从阶段 1 的网络配置和协议获取开始！** 🚀
