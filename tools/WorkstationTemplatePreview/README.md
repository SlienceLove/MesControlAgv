# 工作站 50 µL 离线预览

只读取已经保存的 Adapter 原始模板响应 JSON，通过正式模板解析/生成器复制 `test` 的两来源、16 个目标，将每条体积改为 50 µL。生成新的 `LOCAL-*` 预览任务号；枪头值原样保留，由工作站管理。不具备任何网络、导入或执行能力，不创建样品核对记录。

```powershell
dotnet run --project tools/WorkstationTemplatePreview -- tests/MesControlAgv.Adapter.Tests/Fixtures/workstation-test-zero-tip-response.json artifacts/workstation-test-50ul-preview/local-preview.xlsx
```

输出必须是不存在的 `.xlsx` 路径；已有文件一律拒绝覆盖。控制台报告输入/输出 SHA256、任务号、来源和体积统计。再次生成时请使用新文件名。

此文件仅用于离线检查。正式实验仍须由中控绑定已核对样品、生成新的准备记录/任务号，完成实际导入回读和独立运行准入；不能把预览当作已导入或已验收的任务。
