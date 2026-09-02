# AGV—AUBO 实体 I/O 厂家施工单

## 目标

不增加交换机、不修改 AGV 控制器固件。中控继续通过 Roboshop TCP API 控制
AGV；厂家把 AGV 底盘控制器的一个可控 DO（当前软件测试点为 `DO6`）接到
AUBO-CB-AGV-V2 的标准 DI，机械臂端程序据此启动既有流程。

```text
MES/Adapter --6001--> AGV 底盘 DO6
                         │实体线/继电器/电平匹配
                         ▼
                 AUBO-CB 标准 DI（候选 DI17）
                         │
                 StartProgram(3) 或常驻 Lua
                         ▼
                       机械臂运动
```

## 厂家施工与确认项

请厂家工程师完成以下事项并回传端子照片/测试结果：

1. **确认 AGV 侧信号源**：Roboshop 控制器 API `6001` 的 `DO6` 是否实际引出
   到“上装2”或其他上装端子；如果不是 DO6，请给出实际 DO 编号。
2. **确认 AUBO 侧输入端**：AUBO-CB-AGV-V2 的 `16XT7` 为通用 DI 端子，
   明确接入 `DI00…DI17` 中哪一点（当前工程报告的候选是 `DI17/pin15`）。
3. **确认电气规格**：AUBO 通用 DI 为 NPN 输入，需要外部设备共地；给出
   信号 0V/COM、有效电平、是否需要中间继电器/光耦，以及通电后的高/低电压。
   禁止直接把未知 24V 线插入电脑或示教器端口。
4. **配置启动动作**：将实际接收 DI 的输入动作设置为 `StartProgram(3)`，
   并明确“当前工程”是哪一个；或者在指定工程中部署
   `scripts/aubo/MesDiTrigger.lua`，把 `run_flow()` 替换成既有运动流程。
5. **配置回执（推荐）**：AUBO `16XT8` 的一个 DO（工程中已有
   `DO02=小车DI7`、`DO03=小车DI8` 的命名）接回 AGV 的 DI，分别约定
   `busy/done` 与 `fault`，并给出 AGV 侧 DI 编号。

## 厂家完成后的验收顺序

1. 机械臂停止、无负载，确认急停可用；厂家确认 AUBO 输入动作/常驻工程已加载。
2. 中控电脑保持原有 AGV 网线，读取 `192.168.1.2` 的 API `1013`。
3. 用项目脚本对确认的 AGV DO 发送一次 500 ms 脉冲：

   ```powershell
   .\scripts\Invoke-AgvIoApi.ps1 -Operation set `
     -ControllerHost 192.168.1.2 -DoId 6 -Status true `
     -PulseMs 500 -AcquireControl -ReleaseControl
   ```

4. 厂家同时观察 AUBO DI 是否按预期变化、输入动作是否启动指定工程；
   MES 记录 AGV DO 回读和 AUBO `busy/done/fault` 回执。
5. 任何点位、电平或回执不一致先停机，由厂家修正接线/配置，不在软件侧盲改编号。

## 责任边界

- 我方软件：AGV API 6001/1013、MES/Adapter 路由、Lua 模板和验收脚本。
- 厂家/系统集成商：控柜拆装、端子接线、公共端/电平匹配、DI 启动动作、
  AUBO 工程加载与运动安全确认。
- 当前“上装2”没有在现有项目资料中确认针脚，不能仅凭线缆标签施工。
