# 单 AGV 中控 MES WPF MVP 收尾设计

**日期：** 2026-07-31
**状态：** 已确认

## 目标

在现有 `.NET 8 + WPF` 实现基础上完成 MVP 收尾，统一本地三层服务的端口契约，验证真实进程之间的调用链，并把正常闭环、失败重试、超时对账、离线恢复和服务重启恢复写入可重复执行的验收流程。

本设计不重新规划已经完成的领域模型、MES 持久化、Adapter 幂等、AGV 模拟器、恢复服务和 WPF 看板。

## 当前基线

- 实施代码位于 `.claude/worktrees/agv-mes-mvp-wpf`，分支为 `worktree-agv-mes-mvp-wpf`。
- 方案包含 `MesControlAgv.Domain`、`MesControlAgv.Mes`、`MesControlAgv.Adapter`、`MesControlAgv.Simulator` 和 `MesControlAgv.Wpf` 五个项目。
- 当前 `dotnet test MesControlAgv.sln` 已通过 30 项测试：Domain 7、Simulator 1、WPF 3、Adapter 5、MES 8、E2E 6。
- 根目录的 Python/FastAPI 实施计划已删除；本收尾工作只使用 C#、ASP.NET Core、EF Core SQLite 和 WPF。
- worktree 中已有的未提交改动属于现有工作，不在收尾设计中被覆盖；实施时先检查并保留它们。

## 端口契约

| 服务 | 本地地址 | 调用方 |
|---|---|---|
| AGV Simulator | `http://localhost:5183` | Adapter、验收脚本 |
| AGV Adapter | `http://localhost:5041` | MES |
| MES | `http://localhost:5045` | WPF、验收脚本 |

所有开发配置、程序默认回退地址、启动脚本输出和 WPF 默认地址都必须遵守这张表。请求超时仍然必须先按设备操作 ID 对账，不能直接重新发送导航命令。

## 收尾工作

1. 修正 MES 到 Adapter、Adapter 到 Simulator、WPF 到 MES 以及启动脚本中的端口不一致；为无开发配置时的默认地址提供相同的回退值。
2. 增加十次连续 `SAMPLE_01 → ST_PREP_01` 搬运的回归覆盖，并提供一个等待健康检查、执行双人工确认闭环和检查审计事件的本地验证脚本。
3. 更新 README 和进度记录，写明真实端口、启动顺序、故障注入、SQLite 位置、验收命令和真实 AGV 替换边界。

## 验收标准

- 三个服务的健康检查分别返回 `mes`、`adapter`、`simulator` 和 `ok`。
- 本地验证脚本可以创建任务，完成两次模拟到站、取货确认和放货确认，并看到 `Completed` 与对应审计事件。
- 自动化测试覆盖至少 10 次连续闭环；失败重试保持同一设备操作 ID，超时恢复不产生第二次派单，服务重启可以从 SQLite 和 Adapter 状态恢复。
- README 中的命令与端口可以在 Windows + .NET 8 SDK 环境下直接复现上述流程。

## 不在范围内

- 多 AGV 调度、避障、路径规划、WMS、库存、批次追溯、真实 PLC、机械臂和真实 AGV 厂商协议。
- WPF 在线编辑地图或站点配置。
- Python/FastAPI、React、Docker Compose 等另一套技术方案。
