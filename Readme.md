# AGV MES MVP

面向实验室自动化的 `.NET 8 + WPF` MVP：系统编排单台 AGV 的固定搬运路线 `SAMPLE_01 -> ST_PREP_01`，并提供 Simulator、可审计的 MES 服务、Adapter 和 WPF 中控看板。

MES 是任务状态和审计事件的唯一写入者；Adapter 隔离设备协议、控制权和幂等派单；Simulator 仅用于开发和验收。

## 前置条件

- Windows 10/11
- .NET 8 SDK

## 构建与测试

在仓库根目录执行：

```powershell
dotnet restore MesControlAgv.sln
dotnet build MesControlAgv.sln --no-restore
dotnet test MesControlAgv.sln --no-build
```

当前自动化基线为 35 项通过测试，包括十任务连续搬运和 Adapter 并发幂等场景。实时冒烟脚本是独立的进程级检查，不计入自动化测试数量。

如果默认共享编译器在本机失败，使用以下串行构建和测试命令：

```powershell
dotnet build MesControlAgv.sln --no-restore -p:UseSharedCompilation=false -m:1
dotnet test MesControlAgv.sln --no-build -p:UseSharedCompilation=false -m:1
```

Windows 应用控制策略可能拦截 `bin/Debug` 下未签名的服务 `.exe`。不要双击服务 apphost；使用下面的启动脚本，或用系统签名的 `dotnet.exe` 直接加载 DLL。

## 本地启动

启动脚本会按 Simulator -> Adapter -> MES 的依赖顺序，在无窗口模式下加载三个 Web DLL：

在 PowerShell 窗口 1 中按以下顺序启动服务并执行服务级验证：

```powershell
.\scripts\run-local.ps1
.\scripts\verify-local.ps1
```

在单独的 PowerShell 窗口 2 中设置 MES 地址并以前台方式运行 WPF 客户端；保持此窗口运行期间窗口 1 的服务不要停止：

```powershell
$env:MES_BASE_URL = 'http://localhost:5045/'
dotnet run --project src/MesControlAgv.Wpf
```

完成 WPF 验证后先关闭窗口 2 中的 WPF 客户端。最后回到窗口 1 清理服务：

```powershell
.\scripts\stop-local.ps1
```

服务端点：

| Service | URL |
|---|---|
| Simulator | `http://localhost:5183` |
| Adapter | `http://localhost:5041` |
| MES | `http://localhost:5045` |

Adapter 调用 Simulator，MES 调用 Adapter，WPF 调用 MES。`run-local.ps1` 将服务 PID 写入临时状态文件，`stop-local.ps1` 只停止该文件记录的 `dotnet.exe` 进程，不会关闭 Rider 的其他进程。

如果需要手动启动单个 Web 服务，先构建解决方案，然后从对应项目目录执行 DLL：

```powershell
dotnet .\bin\Debug\net8.0\MesControlAgv.Adapter.dll --urls http://localhost:5041 --environment Development
dotnet .\bin\Debug\net8.0\MesControlAgv.Mes.dll --urls http://localhost:5045 --environment Development
```

## 正常搬运演示

按以下顺序完成一条正常搬运：创建 `SAMPLE_01 -> ST_PREP_01` 任务，模拟到达 `SAMPLE_01`，确认取货，模拟到达 `ST_PREP_01`，确认放货，最后检查任务详情中的审计事件时间线。

WPF 中的创建任务、模拟到站、确认取货和确认放货按钮对应这些操作。任务完成后，可以用 `GET /api/tasks/{taskId}` 查看事件时间线。

## 故障注入与恢复

Simulator 提供开发用控制端点 `POST /controls/{mode}`：

- `fail`：下一次导航变为 `Failed`。

- `timeout`：已接收的导航仍可查询；Adapter 会先对账设备状态，能确定时 MES 直接恢复到对应移动状态，无法确定时才进入 `Unknown`。

- `offline`：AGV 离线时拒绝导航。

- `recover`：Simulator 恢复在线。

- `arrive`：当前 Simulator 任务变为已到站。

失败重试使用现有 MES 任务和现有搬运腿的操作 ID。超时恢复先查询 Adapter 的设备任务状态，绝不盲目发送第二次导航。

示例：

```powershell
Invoke-RestMethod -Method Post http://localhost:5183/controls/fail
```

## 持久化与边界

SQLite 文件位置为：

- MES：`data/mes.db`
- Adapter：`data/adapter.db`

MES 保存任务状态和完整事件审计；Adapter 保存设备操作和幂等映射；Simulator 为内存实现，仅适用于开发和验收。

对接真实 AGV 时，只需在确认厂商协议和控制权规则后替换 Adapter 的设备客户端实现。MES 生命周期、事件审计、WPF 中控和 MES -> Adapter API 契约无需变更。

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
