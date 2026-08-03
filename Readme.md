# AGV MES MVP

面向实验室自动化的 .NET 8 MVP：系统编排单台 AGV 的固定搬运路线 `SAMPLE_01 → ST_PREP_01`，并提供模拟设备、可审计的 MES 服务和 WPF 中控看板。

MES 是业务状态与审计事件的唯一写入者；Adapter 隔离设备协议、控制权与幂等派单；Simulator 仅用于开发和验收。

## 前置条件

- Windows 10/11（WPF 中控客户端为 Windows 桌面应用）
- .NET 8 SDK

## 构建与测试

```powershell
dotnet restore MesControlAgv.sln
dotnet build MesControlAgv.sln --no-restore
dotnet test MesControlAgv.sln --no-build
```

## 本地启动

按依赖顺序启动服务，每条命令使用独立 PowerShell 窗口：

```powershell
dotnet run --project src/MesControlAgv.Simulator --launch-profile http
dotnet run --project src/MesControlAgv.Adapter --launch-profile http
dotnet run --project src/MesControlAgv.Mes --launch-profile http
```

也可以一次启动三个服务进程：

```powershell
.\scripts\run-local.ps1
```

再启动 WPF 中控客户端：

```powershell
$env:MES_BASE_URL = 'http://localhost:5045/'
dotnet run --project src/MesControlAgv.Wpf
```

开发环境已配置 Adapter 调用 Simulator (`http://localhost:5183/`)，MES 调用 Adapter (`http://localhost:5041/`)。

## 正常搬运演示

1. 在 WPF 中选择 **创建 SAMPLE_01 → ST_PREP_01 任务**。
2. 任务进入前往 `SAMPLE_01` 的状态。
3. 选中任务，点击 **模拟到站**，再点击 **确认取货**。
4. 任务进入前往 `ST_PREP_01` 的状态。
5. 点击 **模拟到站**，再点击 **确认放货**。任务变为 `Completed`。
6. 用 `GET /api/tasks/{taskId}` 查询 MES，可查看按时间排序的审计事件。

## 故障注入与恢复演示

Simulator 提供开发用控制端点 `POST /controls/{mode}`：

- `fail`：下一次导航返回设备失败；
- `timeout`：下一次导航在命令边界超时；
- `offline` / `recover`：模拟 AGV 离线或恢复在线；
- `arrive`：将当前模拟任务标记为已到站。

示例：

```powershell
Invoke-RestMethod -Method Post http://localhost:5183/controls/fail
```

然后创建任务。任务将进入 `Failed`，可在 WPF 中点击 **失败后重试**；同一搬运腿会使用相同的幂等操作 ID。若注入 `timeout`，创建任务后调用 `POST /api/tasks/{taskId}/recover`：MES 会先向 Adapter 对账，不会直接重复导航。

## 持久化与边界

从各项目目录启动时，SQLite 文件位于：

- MES：`data/mes.db`
- Adapter：`data/adapter.db`

MES 保存任务状态和完整事件审计；Adapter 保存设备操作与幂等映射；Simulator 为内存实现，仅适用于开发和验收。

对接真实 AGV 时，只需替换 Adapter 中的 `ISimulatorClient` 实现。MES 生命周期、事件审计、WPF 中控与 MES→Adapter API 契约无需变更。

## 固定站点

| 编号 | 站点 | 机器站点 ID |
|---:|---|---|
| 0 | 充电桩 | `CHARGE_01` |
| 1 | 耗材位 | `PICK_01` |
| 2 | 样品位 | `SAMPLE_01` |
| 3 | 开盖分液工作站 | `ST_OPEN_01` |
| 4 | 液体前处理工作站 | `ST_PREP_01` |
| 5 | 自动进样器 | `ST_INJECT_01` |
| 6 | 样品回收位 | `DROP_01` |

## 设计资料

- [MVP 设计说明](docs/superpowers/specs/2026-07-29-agv-mes-mvp-design.md)
