# 厂家新版：启动已确认，但执行任务列表为空

历史版本说明：本文针对 `DLHWorkstation_new.exe`。随后 Beta 已补上列表 Add，现场已进入真实执行；最新进度以 [Beta 联调记录](2026-09-14-workstation-beta-field-checkpoint.md) 为准。

## 本次结果

2026-09-14 13:12:41.860（北京时间），通过隔离分支 `29343b8` 的 MES → Adapter → 厂家 WCF，单次启动 `TEST-001`。约 2.072 秒后收到：

```json
{"deviceId":"SAMPLE-WORKSTATION-01","operation":"StartTask","code":200,"data":"启动成功","observedAtUtc":"2026-09-14T05:12:43.9502251+00:00","taskNo":"TEST-001","acknowledged":true}
```

Adapter 日志确认实际发出 `GET http://192.168.200.157:8082/Service/StartExperiment?TaskNo=TEST-001` 仅 1 次。没有重发，没有另外调用初始化、避让、任务创建或删除。

启动前：设备 Idle、错误码 0、任务 Waiting；任务源编码 `CYC-001-1000`，源 X/Y=1/1；枪头位置 `QT-001`，X/Y=1/1；`TargetData` 中目标 `FYB-001`，X/Y=1/1，50 μl，与此前确认的任务一致。

13:12:50、13:13:05、13:13:29 的只读观察均为设备 Idle、错误码 0、任务 Waiting。任务详情的请求/生产/完成时间仍为 null；旧 WCF 的详情实现不填这三个时间，不能将 null 单独当作实际未执行证据。

用户截图显示已进入实验页面，任务名称为 `[TEST-001]TEST-001`，顶部“正在启动实验任务”，没有显示正在运行。至此确认通讯、任务号传递、页面打开及厂家启动确认已打通；实际任务运行/完成未得到验证。

## 文件与静态代码证据

- 文件：主工作区 `res/DLHWorkstation_new.exe`，1472512 字节，文件版本 1.0.0.0。
- SHA-256：`DCCEFEE29A16384D6054118DAE312CD777A2BF53CD3E6A2D2EFAA554001BAC06`。
- 未修改或运行本地厂家 EXE；以下结论来自本地副本反编译及原始 IL 核对，不是对现场进程内存的测量。

此前 `FormMain.Cmd_LoadForm(int)` 的空入口已实现：创建包含任务的列表并传给 `FormQualityControlExperiment`，打开执行页面。

但是执行页面 `FormQualityControlExperiment.timer2_Tick` 的远程任务分支如下：

```csharp
mListRunTaskInformation = new List<Class_TaskInformation>();
Class_TaskInformation val = new Class_TaskInformation();
val.TaskNo = Data_Global_Parameter.StartExperimentTaskNo;
val.TaskName = Get_TaskName(Data_Global_Parameter.StartExperimentTaskNo);
// 缺少将 val 加入 mListRunTaskInformation 的语句。
if (mListRunTaskInformation.Count > 0)
{
    mlabelTaskNo.Text = "[" + mListRunTaskInformation[0].TaskNo + "]"
        + mListRunTaskInformation[0].TaskName;
}
Data_Global_Parameter.StartExperimentTaskNo = "";
Update_SystemParameter("StartExperimentTaskNo", "");
Data_Global_Parameter.StartExperimentFlag = true;
Update_SystemParameter("StartExperimentFlag", "true");
Cmd_Run(1);
```

这里清空了主界面传入的任务列表，构造了任务对象但没有加入列表。随后无参 `Cmd_Run()` 只遍历该列表：

```csharp
for (i1 = 0; i1 < mListRunTaskInformation.Count; i1++)
{
    // 材料准备确认、任务确认及实际执行。
}
```

因此此分支可以回写“启动成功”，但没有任何任务进入循环。这与本次截图及读取结果一致。`Cmd_Run(1)` 在进入无参运行方法之前仍可能进行设备连接、初始化检查及初始化，因此不能据空列表保证设备完全不会动作。

原始 IL 核对：`timer2_Tick` 的 `IL_013d` 新建 List，`IL_0142` 覆盖字段，`IL_0147` 新建任务，`IL_0155`/`IL_0168` 设置任务号/名称，`IL_0174` 直接读取列表 Count；整个方法没有 List.Add 调用。`IL_01ef` 回写确认标志后，`IL_01f7` 调用 `Cmd_Run(int)`。不是反编译漏掉 Add。

## 给厂家的最小修复建议

1. 在该远程分支填入任务信息后加入 `mListRunTaskInformation.Add(val)`，或保留正确的已加载任务列表；同时核对实际运行依赖的任务字段。
2. 将“启动成功”的确认与实际运行接通，避免在任务列表为空或前置检查失败时仍报启动成功。
3. 核对 `Cmd_Run()` 中现有的材料准备及运行确认弹窗；修复后若仍需人工确认，应明确现场步骤，不将弹窗等待误判为任务运行。
4. 用同一测试任务验证实际运行及完成状态，再继续中控自动流程。

本次未补丁修改厂家程序、未写现场数据库，也未修改主工作区代码。原有入口问题参见 [前一轮诊断](2026-09-14-workstation-remote-start.md)。

## 本地测试实例

仅为此次测试启动 MES `127.0.0.1:15045` 与 Adapter `127.0.0.1:15041`，使用独立临时数据库。工作站连接为真实厂家 HTTP；AGV 为模拟驱动，AUBO 及自动物理工作流关闭。

日志和数据库留在 `C:/Users/33206/AppData/Local/Temp/mes-workstation-field-20260914-131221/`。`EquipmentNo=TEST-001` 沿用此前联调占位值；旧 WCF 状态/错误读取实际使用全局参数，并不按此值选择设备，不代表它是真实设备编号。
