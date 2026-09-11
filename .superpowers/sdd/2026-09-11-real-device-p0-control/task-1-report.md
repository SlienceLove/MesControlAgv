# Task 1 report

状态：DONE_WITH_CONCERNS

## 修改

- 新增 `DeviceOperationContracts`：统一设备操作生命周期、UnknownReason、请求/结果 DTO，并明确 Unknown 不可自动重试。
- 扩展工作流设备操作状态和持久化记录，保存厂家任务号、结果文件引用、Unknown 原因。
- 扩展节点结果记录及快照映射。
- 启动时为旧 SQLite 表补齐新增列，并建立幂等键索引；重复启动安全。

## 验证

`dotnet build src/MesControlAgv.Contracts/MesControlAgv.Contracts.csproj --no-restore --nologo`：成功，0 警告，0 错误。

MES 项目构建受正在运行的 .NET Host 锁定输出 DLL，出现 MSB3021/MSB3027；未发现代码编译错误。

## 未解决

- 未加入离子色谱 Config/Command 或任何猜测协议；厂家模块到位后再接入。
- 未新增专门单元测试，现有运行时测试可继续覆盖旧行为；建议在厂家模块接入时补 UnknownReason 端到端测试。

## 复核修正

- 增加 `DeviceOperationReconciliationRequest`（actor、decision、comment、operation/run/device/correlation），供审计 API 消费；Unknown 收口不得自动重发。
- UnknownReason 改为受控枚举；Unknown 完成缺少原因时运行时拒绝。
- DeviceOperationRequest 增加发送前 `IsValid`，强制操作/运行/节点/设备/幂等键非空。
- 增加 WorkflowDeviceOperationStatus 到统一生命周期的显式兼容映射。
- 旧库唯一幂等索引使用 SQLite partial unique index；历史重复键导致建索引失败时保留旧记录并不中断启动。
- 补齐厂家任务号和长度受限原始响应摘要的完成请求、落库及快照映射。

复核验证：`dotnet build src/MesControlAgv.Contracts/MesControlAgv.Contracts.csproj --no-restore --nologo` 成功，0 警告、0 错误。

## 编译破坏修正

- 删除重复及错误类型赋值；旧 UnknownReason 通过 `Enum.TryParse` 受控映射。
- 增加 RawResponseSummary 快照字段，补齐 VendorTaskId/原始响应落库。
- 写入前检查幂等键冲突并拒绝不同 OperationId 的重复键。

验证：Contracts 构建成功；MES 使用 `--no-restore -p:BuildProjectReferences=false` 仍因运行中 .NET Host (PID 33372) 锁定 Contracts/Application/Domain DLL，MSB3021/MSB3027，非源码编译诊断。
