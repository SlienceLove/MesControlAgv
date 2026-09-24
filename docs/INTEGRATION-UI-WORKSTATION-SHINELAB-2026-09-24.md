# UI、开盖分液及离子色谱集成交接（2026-09-24）

## 范围与来源

用户授权本会话串行接管 `D:/Project/Github/Mes/.worktrees/branch-integration` 的 `develop`，并明确将开盖分液现场修复与模板/批量分支一起合入。

| 来源 | 提交 | 内容 |
| --- | --- | --- |
| feature/wpf-ui-layout-optimization | c377c8a | 导航与物料标题 |
| feature/workstation-template-batch | 3d80c37 | 单任务模板、模板复制、Excel 批量导入 |
| feature/workstation-ml-template-validation | 9b639cb | mL 厂家文件、条码 POST/应答、手动结束后续跑 |
| feature/shinelab-dcseq-protocol | 43fb941 | DCSeq 通讯、任务关联、结果待复核、托盘及操作员页面 |

合并前恢复引用：`archive/develop-before-ui-workstation-shinelab-20260924` → `ad2d958`。
四路合并汇合于 `9e6c5d1`。现场日志、数据库、抓包及发布产物未纳入提交；原分支和工作目录均保留。

## 合并处理

- `ShineLabDeviceStatusView.xaml/.cs` 两处冲突保留双方功能：开盖分液单任务模板/批量导入按钮、事件处理，以及离子色谱 ControlModel、只读刷新和中控样品记录列表。
- 离子色谱仍从“设备状态查询／仪器任务导入”进入；设备页不展示技术日志或虚构 AQ 勾选状态。中控记录不代表 AQ 完整序列。
- 合并后批量导入替身仍使用四参数接口，导致首次专项测试构建失败；已更新为显式文件单位的接口，并修复对应实际业务路径，不删除或放宽测试。
- 批量导入原默认 µL 与现场 mL 配置不兼容。新增 capabilities 的可空 `TemplateVolumeUnit`，按声明单位生成 XLSX、传递给 Adapter 并检查返回单位；Adapter 仍检查其配置和文件单位一致。操作员工作簿及 DTO 始终为整数 µL，50 µL 对应厂家 mL 文件 0.05。
- `WorkstationBatchResult.FileVolumeUnit` 保存本批实际文件单位。旧日志无该字段仍能重放；重放不重新读能力、不重新发送。Unknown/Importing 阻断与逐项空闲检查保留，导入不自动初始化/启动。
- 只读代码审查覆盖上述单位链、回读/重放和页面合并交界，未发现阻断项。

## 验证与证据

离线测试使用替身、临时数据和测试宿主。结果保存在本工作区 `artifacts/integration-20260924/`，不提交到 Git。

| 测试集 | 通过 |
| --- | ---: |
| Adapter | 493 |
| MES | 729 |
| WPF | 688 |
| WorkflowContract | 81 |
| Domain | 48 |
| 合计 | 2,039 |

批量导入专项 13 项已包含在 MES 总数中，不重复计数。覆盖 mL 独立 XLSX 单元格检查、单位缺失/非法值零下发、错误回读单位停止和原单位日志重放。
WPF 验证含小窗口/宽窗口及设备页切换后工作站入口与离子色谱记录同时保留。
补充复测发现既有命令异常测试在 Message 事件后抢先断言 IsBusy，偶发失败；测试改为同时等待预期提示和非忙碌状态（5 秒超时），保留全部原断言，未修改生产状态逻辑。页面切换测试显式注入离线模型，验证真实绑定而非无 DataContext 的空壳布局。

`dotnet build MesControlAgv.sln -p:SkipLocalServiceBuild=true -p:SkipLocalServiceCopy=true --no-restore` 通过，0 警告、0 错误。WPF 测试同样禁用本地服务构建/复制；本轮未运行 E2E 真机验收，也不以离线通过代替现场验收。

## 部署边界与下一步

1. 当前成果仅在本地 develop，未推送或合入 master；未启动、停止、替换现场 MES、Adapter、WPF，未发送设备动作。其他活跃分支未自动同步。
2. 真实批量导入部署需同版 MES 和支持单位声明的 Adapter；旧 Adapter 缺单位时明确拒绝新导入，本地模板编辑不受影响。部署前保留原内容目录、数据库、配置及批次日志，不清理 Unknown。
3. 本检查点仅保留已合入的模板/批量入口和既有联调准入，未完成开盖分液状态区压缩、扩大任务表。随后用户另行授权接管并同步 UI 工作区，该后续优化已实施，见[紧凑设备页交接](WORKSTATION-COMPACT-UI-2026-09-24.md)；不将后续验证追记为本检查点已完成。
4. 部署与真实验收另行确认：先只读检查唯一控制入口和设备空闲，再按批准范围做新任务导入/回读；启动、托盘动作均独立确认。零峰/ND 提示为待复核及可能外部原因，不能仅凭中控结果确诊缺液或仪器故障。
