# 实际任务模板与导入实现（2026-09-18）

## 已取得实例

从原有工作站Adapter只读下载 `TEST-001`：

`GET http://127.0.0.1:15041/api/workstations/SAMPLE-WORKSTATION-01/protocol/ExperimentalTaskTemplate?key=TEST-001`

返回200，文件名 `实验任务模板_TEST-001[TEST-001].xlsx`，FileData为Base64。
文件9005字节，SHA256 `1BD82711494D5BAAECB8A846E55F08601C5BA2E0FE3E06670551526B496BCD78`。
固化fixture：`tests/MesControlAgv.Adapter.Tests/Fixtures/workstation-test-001.xlsx`；原始JSON/反编译参考保留在本地ignored目录 `artifacts/workstation-v102-template-20260918`。

实际样表内容：

| 项目 | 值 |
| --- | --- |
| 任务 | TEST-001 / TEST-001 |
| 溶剂参数 | CGRJ-001 |
| 枪头 | QT-001，X=1，Y=1 |
| 来源模块/位置 | CYC-001-1000，X=1，Y=1 |
| 目标模块/位置 | FYB-001，X=1，Y=1 |
| 移液量 | 50 µL |

工作簿为 `实验任务表` + `移液参数表`，后者11列，比PDF列出的模型字段更精简。
模块参数编码不能冒充大瓶样品条码，当前不推断X/Y到A1或1/2号大瓶的转换。

另一次只读请求 `TaskNo=202609150001` 返回的却是 `202654250001` 模板，
在Adapter与直接厂家GET上结果一致。旧DLL确认任务不存在时回退其他记录。
新强类型下载因此核对Excel内任务号，不把HTTP200/文件名当可靠绑定。

## 上传编码证据

本地既有厂家材料：

- `WcfServiceDLH.dll`，2026-08-18，SHA256 `403DDFC6FBD8A359E3D2AED88FA0E79705B379841A2D140F0B4BDDA8823C8FF6`。
- `WCFTestToolsDLH.exe`，SHA256 `2D7D3A264229081ACC336A9FFA8377729B99A9F7BDF1F31CF445F5C2482EE6BD`。

只读反编译证实生产/消费一致：

1. UTF8文件名转Base64，长度为L（必须能装入一个byte）。
2. 单字节L转Base64，固定4个ASCII字符。
3. 拼接 `Base64([L]) + 文件名Base64 + ExcelBase64` 作为原始POST body。
4. 厂家测试工具使用 `Content-Type: application/json`；body不是JSON字符串，也不是multipart文件部件。
5. 服务方法 `ImportExperimentalTask(Stream)`按上述顺序解析；明确成功返回 `Code=200, Data=导入成功`。

这是旧程序的确切格式证据，不是V1.02现场上传成功证明。
旧程序还存在来源行聚合风险，故新导入器必须下载回读，逐项比较源/目标/枪头/溶剂/体积和顺序。

## 已完成代码

- 强类型单任务/分液行契约。
- 标准ZIP/XML双工作表读取及生成，保留文本编号；拒绝公式、多任务、不明列及有歧义空白/行位置。
- 编码厂家实际上传报文，校验单字节文件名长度和Windows文件名。
- Adapter强类型模板GET，以及受control-enabled保护的POST import。
- 导入发送一次，成功确认后GET回读；不匹配或不确定结果不返回成功，不重试，不调用初始化/写条码/启动。
- MES模板读取HTTP和内部导入端口，检查任务内容/hash/目标设备和回读标记。
- MES任务级导入写路由/WPF入口暂未开放，后续必须接入已有样品核对gate、来源绑定与任务审计。

## 验证

真实fixture读取及生成往返、上传报文按厂家算法解码、文件格式/名称拒绝、丢失任务回退检测、上传一次及全部字段回读一致、来源/目标/体积/任务漂移、控制禁用、超时、独立HTTP绑定、MES错误ACK与空字段回归均已覆盖。

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --nologo -v minimal
# 326 passed / 0 failed
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --nologo -v minimal
# 366 passed / 0 failed
dotnet build MesControlAgv.sln -c Release --no-restore --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-sample-field-20260918-build -p:SkipLocalServiceCopy=true --nologo -v minimal
# succeeded / 0 warnings / 0 errors
```

独立审查中的文本空白、Windows文件名和媒体类型问题已修复；复审无剩余Critical/Important/Minor。
本轮真实网络操作只有状态、任务列表和模板GET。没有向厂家POST导入、写码、启动或重启现场程序，也没有合并主工作区。
下一阶段按明确的大瓶位置绑定，把来源ID/分液表快照、导入回执和正式运行串起来，再做现场验收及日常分支整合。
