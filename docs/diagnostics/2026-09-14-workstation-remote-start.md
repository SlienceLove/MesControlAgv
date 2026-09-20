# 开盖分液工作站远程启动失败：现场 EXE 核对结果

## 结论

对现场复制的 `res/DLHWorkstation_新.exe` 做静态反编译和原始 IL 核对，发现远程启动路径有确定的代码缺口：`DLHWorkstation.FormMain.Cmd_LoadForm(int)` 是空方法；远程轮询调用它后直接返回，没有恢复 `timer1.Enabled`。这可以导致远程任务不启动，且后续远程请求不再被该定时器处理。

这是对所提供文件的确认；未读取现场进程内存，因此不将其当作对当前进程状态的直接测量。厂家后来补充的 DLL、现场 WCF DLL 与数据库参数值尚未完整取得，仍需厂家核对。

## 文件证据

| 文件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| MODELDLL.zip 中的 DLHWorkstation.exe | 1460224 | A9608C90D630B44CF91574FEF04790915559D18AF0B55D53836F428E0B7B1916 |
| 现场复制的 DLHWorkstation_新.exe | 1469440 | 11D58BB6F088B5F7CACDB84487242A61CF7C83325AD36B4EE68CA7A9F30B379E |

两者文件版本均为 `1.0.0.0`，但哈希不同。FormMain 反编译对比显示记录页面从 `FormExperimentalResult` 改为 `FormTaskInformation2`；下面的远程启动方法和定时器逻辑没有改变。此对比不代表整个 EXE 只有该处变化。

新版入口链：`Program.Main -> Login -> FormLogin -> FormMain`，确认检查的是正常登录后使用的主界面类型。

## 关键代码

新版 EXE 中：

```csharp
private void Cmd_LoadForm(int m_Tag)
{
}
```

该方法原始 IL 为 `00-2A`，即 `nop; ret`，没有调用任何外部 DLL、页面加载或任务执行方法。

`FormMain.timer1_Tick` 中的相关逻辑如下（省略初始化和避让分支）：

```csharp
timer1.Enabled = false;
// RemoteControlFlag == 0 时恢复 timer1 并返回。
// 非零时先处理初始化、避让，再读取任务号。
Data_Global_Parameter.StartExperimentTaskNo =
    Get_SystemParameter("StartExperimentTaskNo");
if (Data_Global_Parameter.StartExperimentTaskNo != "")
{
    Display_List("正在启动实验任务[" +
        Data_Global_Parameter.StartExperimentTaskNo + "]运行...");
    Cmd_LoadForm(103);
    return;
}
timer1.Enabled = true;
```

因此进入任务分支后：空方法不执行任务，`return` 又跳过重新启用定时器。界面提示具体如何被后续操作覆盖，需要现场日志或调试确认；不能仅凭上述逻辑断定某一时刻的界面状态。

旧包中 WcfServiceDLH 的 `StartExperiment` 先写 `Tab_SystemParameter.StartExperimentTaskNo`，再将 `StartExperimentFlag` 设为 `false`，轮询约 15 秒等待其变为 `true`。未得到确认时仍返回 `Code=200, Data="启动失败"`。客户端已兼容约 16 秒的实际响应耗时及这一失败语义。

## 已验证现场事实

- HTTP 服务：`http://192.168.200.157:8082/Service/`，读取正常。
- 2026-09-14 09:23:44（北京时间）通过 MES 发送一次 `TEST-001` 启动请求，约 16 秒后收到厂家“启动失败”。本次没有自动重发。
- 随后读取：设备状态 `0`、错误码 `0`、任务“等待运行”，请求/生产/完成时间均为 null。这仅表示未观察到执行确认，不能保证没有残留请求。
- `TEST-001` 的源编码已改为精确的 `CYC-001-1000`，无首尾空格；目标 `FYB-001` 的 X=1/Y=1；移液量为 50 μl。
- 此次分析只处理本地副本，没有修改厂家 EXE、现场配置或数据库，也没有发送新的控制命令。

## 请厂家核对和修复

1. 确认当前运行的主程序是否与上述新版 EXE 哈希一致。
2. 实现或正确接通 `Cmd_LoadForm(103)` 对应的远程任务执行分支，不能只修复 WCF 的启动依赖。
3. 检查远程任务成功、失败及异常路径如何恢复定时器；核对任务号的消费/清除和 `StartExperimentFlag` 回写，避免丢失后续命令或重复执行。
4. 在现场数据库只读检查 `StartExperimentTaskNo`、`StartExperimentFlag`、`InstrumentInitFlag` 和 `MoveX`，确认 WCF 与主程序使用同一数据库及同一参数行。不要以手动将确认标志改成 true 代替真实任务执行。
5. 提供修复后的主程序及匹配 DLL 清单，再用已确认的 `TEST-001` 验证单次启动及任务状态变化。

补充 DLL 的具体内容尚未知，无法仅凭 EXE 推断当时 WCF 启动失败的确切原因。普通 DLL 补齐不会改变上述 EXE 私有空方法的 IL。
