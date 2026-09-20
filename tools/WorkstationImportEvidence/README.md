# 工作站导入证据还原（纯离线）

从已保存的 `{ "prepared": ... }` 准备响应还原**当时实际生成的**XLSX，先比对冻结SHA256，匹配才导出；不更换任务号，不修改历史记录，不联网或调用设备。

```powershell
dotnet run --project tools/WorkstationImportEvidence -- artifacts/workstation-field-20260920-0935/prepared.json artifacts/workstation-field-20260920-0935/vendor-evidence
```

输出目录必须不存在。输出为原文件名XLSX、原上传帧 `original-request-body.txt`、含文件hash/任务号/转移/绑定的manifest。已有输出不覆盖；hash不符时不导出。文件输出中途失败可留下部分诊断文件，改用新目录重新离线导出即可，不操作现场。

这是用于对照厂家收到文件与日志的历史证据，**不是新实验任务，也不要直接重新导入现场**。本次历史文件仍保留42位任务号；后续新任务采用的短号由正式准备服务生成，不能通过修改证据文件替代正式准备。

自动回归测试位于 Adapter 测试项目，直接启动构建后的CLI，覆盖原样还原、帧解码、hash/任务号不一致以及拒绝覆盖等行为；全部使用临时文件，不依赖现场。

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj --filter FullyQualifiedName~WorkstationImportEvidenceCliTests
```
