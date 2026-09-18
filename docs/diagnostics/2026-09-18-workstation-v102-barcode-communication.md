# V1.02 大瓶条码通讯接入记录

## 输入与已确认语义

- 厂家文档：主工作区 `res/开盖分液工作站Http接口文档V1.02.pdf`，42页。
- PDF SHA-256：`70D265AF65F883CA24B830ED8115780188B6A602D49A225527EDC9419AA29BCD`。
- 第17页：StartExperiment 增加 SampleBarcode1 / SampleBarcode2。
- 第20–22页：任务模板下载与导入；sheet2 中 SourceBarCode / TargetBarCode 指模块参数编码，另有来源/目标X/Y和TransferVolume。
- 第24–25页：UpdateTaskBarcode 接收 TaskNo 与两个大瓶条码。用户明确确认仅更新，不启动。

## 本次实现

1. 增加双大瓶条码 DTO 与独立 ISampleWorkstationBarcodeCommands；旧命令接口保持兼容。
2. MES/Adapter `POST /api/workstations/{deviceId}/tasks/{taskNo}/barcodes` 仅更新条码。
3. 现有 `POST .../start` 接受可选双条码 JSON；无请求体保持旧任务号启动。有请求体则两个条码不能为空，不会静默丢弃。
4. 厂家请求保持 GET/no-cache/no-store，条码仅 Trim 并正确编码。两个命令各自只发一次，不自动互调或重试。
5. 更新仅识别 `Code=200, Data=更新成功` 为成功。201任务不存在或明确更新失败为已知拒绝；其他不明回复/断线/超时保留 outcomeUnknown。
6. 两层命令路由从原始 RawTarget 解码任务号一次，正确区分斜杠和字面百分号编码；不在已部分解码的绑定值上再次盲目解码。缺少 RawTarget 的宿主遇到歧义编码返回参数错误。

## 测试与审查修复

初始通讯定向测试 Adapter 55/55、MES 42/42。
独立审查指出非200明确失败文案及带斜杠任务号编码两个缺口，新增用例先得到3失败/6通过，修复后原复现集9/9通过。
随后增加 start/update 两种命令的斜杠、字面 `%2F`、中文特殊任务号覆盖；MES 的四个真实路由用例全部通过。

最终回归命令：

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --no-restore --nologo -v minimal
# 294 passed / 0 failed / 0 skipped
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj --no-restore --no-build --nologo -v minimal
# 359 passed / 0 failed / 0 skipped（先前已编译最终 MES 与测试代码）
dotnet build MesControlAgv.sln -c Release --no-restore --artifacts-path C:/Users/33206/AppData/Local/Temp/mes-sample-field-20260918-build -p:SkipLocalServiceCopy=true --nologo -v minimal
# succeeded / 0 warnings / 0 errors
```

测试使用本地测试宿主与 HTTP handler 替身，不访问工作站，不修改现场服务/数据。
独立输出避免覆盖正在运行的旧 Release 服务。
独立复审确认两个问题均已修复，无剩余 Critical/Important/Minor 正确性问题。

## 接续边界

- 本轮不等于真实验收，不更新先前验收包的发布目录，也未合并日常开发分支。
- 正式 worker 仍调用旧任务号启动。完整业务接入须根据实际模板建立1/2号大瓶位置绑定，再冻结来源ID/位置表并交给正式流程，不能猜测核对行顺序。
- 实际模板文件及上传报文样例尚未取得；文档 multipart/form-data 与 Base64 raw 表述混用，尚未实现/宣称厂家任务导入支持。
- GET参数表题为“请求头设置”，本实现沿用已验证旧接口的查询参数传递。V1.02参数位置仍需新版服务/可运行样例验证。
- 厂家新版就绪后继续真实验收，通过后合入 `feature/wpf-ui-layout-optimization`；不操作主工作区未提交的数字孪生改动。
