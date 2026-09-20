# 数字孪生日常中控接入：软件就绪，待受控切换

## 2026-09-20 11:31 更新：已获确认并完成切换

用户确认受控切换后，网关15441更新为新包（PID39856），日常主中控已启动（PID29900）。MES15445仍为PID30812，未重启。新Adapter继续沿用旧内容目录的 PhysicalAcceptance 配置、进程环境与原数据库；没有扩大控制权限或发设备动作。

现场只读验收：AGV停在LM1、控制权none；机械臂程序Stopped；工作站Idle。Adapter/MES连续三组位姿身份、地图MD5和坐标一致且接收时间持续更新；主窗口截图显示同一位置约x=0.004、y=0.803、航向−179°。已实际从数字孪生切到KPI再返回，确认状态刷新恢复、示意跟随仍开启。主WPF当前已建立TCP连接仅到本机MES15445，没有直连设备。

证据与回退：`artifacts/digital-twin-central-client/cutover-20260920/`，含 SQLite一致性备份 `adapter-before.db`、`mes-before.db`，原配置与流程库备份、旧进程环境、切换意图、启动日志、位姿和窗口截图、`verification-summary.json`。切换后两库所有原表行均未改变，原配置和workflows.json哈希未改变。

旧主WPF19632保持打开，避免丢失未保存草稿（其配置为尚不存在的 twin-readonly-workflows.json；新版采用已验收workflows.json，未覆盖旧窗口内容）。独立预览11040在确认标定编辑器折叠后正常关闭，减少重复位姿读取。其他工作站服务/用户窗口未处理。

新增用户反馈：主窗口需要全屏三维查看，全屏隐藏截图指向的三张详细状态卡及不必要说明。此为下一项显示改进；当前中控较矮的三维区域尚未优化，不表述为最终视觉验收完成。该改进不涉及实机移动。

## 本阶段结果

在 `.worktrees/three-device-sequence` 实施，未提交、未合并。未替换运行服务/主窗口，未发任何设备动作。

- 正式 WPF 继续使用 `MesDigitalTwinStatusSource`，位姿走 WPF → MES → Adapter；未引入直连设备 TCP 或第二套服务。
- 新增 `WPF_TWIN_SCHEMATIC_FOLLOW=true` 显式启动配置：仅有效 physical 模式启用，默认关闭。主窗口只应用一次，用户关闭后切页/重新 Loaded 不重开；未知/模拟报告不启用。
- 保持原模型、已验收示意变换、整体 AGV+机械臂和平滑动画；地图参照默认关，无俯视入口，D160/SHA18i 未绑定。正式标定文件不被写入。
- 日常启动脚本只启动客户端，MES15445/Adapter15441 外部连接。清除继承的 WPF 配置及四种历史流程绑定变量，不自动绑定旧执行、不托管服务、不自动动作。批次执行 UI 默认为关，需显式 `-EnablePhysicalBatch` 才开放已有入口，后端现场许可规则不变。

## 验证与证据

均位于 `artifacts/digital-twin-central-client/`：

| 验证 | 结果 | 证据 |
| --- | --- | --- |
| Adapter 完整回归 | 334/334 | `tests/adapter-final.trx` |
| MES 完整回归 | 535/535 | `tests/mes-final.trx` |
| WPF 完整回归 | 543/543 | `tests/wpf-post-review.trx` |
| JS 动画/地图/相机 | 20/20 | node --test 三个既有测试文件 |
| 完整 MainWindow + VM + MES HTTP 替身 + WebView | 通过 | `smoke-main-final/result.json` |
| 既有原生位姿/动画/离线工作站/卸载烟测 | 通过 | `smoke-pose/result.json` |
| 注入旧流程/模拟托管环境变量的启动隔离测试 | 通过 | `startup-inspection.json`、`tests/digital-twin/central-client-launcher.test.ps1` |

主窗口烟测确认 404/501/504 失败冻结，地图/身份/过期拒绝，恢复、切页取消、再次进入，最大同时位姿请求为 1，所有 HTTP 为 GET。离线替身无实机连接；其他页面的只读目录刷新被明确置为不可用。

验证边界：主窗口烟测使用正常 MainWindow/VM/轮询/WebView，但绕过 `App.OnStartup`，未调用完整 `MainViewModel.StartAsync`。启动配置/历史绑定解析另用真实 Inspector/Parser 加净化后的子进程环境验证；完整发布客户端与真实服务的启动、静态数据验收仍待切换。不能将离线结果表述为本次新实机移动验收。

只读代码审查发现并修复了 `WORKFLOW_EXECUTION_ID` / `WORKFLOW_REQUEST_ID` 继承导致误绑定历史监控的问题。新增注入回归；最终独立复核已关闭该 Important，无剩余 Critical/Important，可交接待部署。审查者依据源码及成功产物复核，未独立重跑（其 PowerShell 执行策略拦截）；主代理以进程级执行选项完成回归，未改系统策略。

## 新包与入口

- WPF：`artifacts/digital-twin-central-client/wpf/`
- Adapter：`artifacts/digital-twin-central-client/adapter/`
- 日常入口：`artifacts/digital-twin-central-client/Start-606CentralClient.ps1`
- 通用脚本源：`scripts/digital-twin/Start-CentralClient.ps1`（打包目录也保留副本）

当前 606 入口固定使用之前已验收的 `three-device-20260918-095441/adapter-effective-for-wpf.json` 和 `workflows.json`，以及本机 RoboshopPro 中的 guangzhou606.smap，不复制覆盖草稿。现场切换前确认当前主窗口实际使用的流程库与未保存草稿；若需要其他流程库，使用通用脚本显式指定。

只检查，不启动：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File artifacts/digital-twin-central-client/Start-606CentralClient.ps1 -InspectOnly
```

用户确认切换后，去掉 `-InspectOnly` 启动。这里只对该 PowerShell 进程设置脚本执行选项，不修改系统策略。不要直接双击 WPF exe：那样不会带上此外部服务配置。

发布 DLL SHA256：

- WPF：`32E0B7EDC3AB1B93B074AAEBE268FEB759DDD4586E2892DC7AB86ADCB28940AB`
- Adapter：`FEB9F3CDD0A3CDE30FAE8F09184F15D28CFA7FF36DFB8715E724D3405A433CB0`
- GLB 保持：`96CA7F34C189C9C1ADD62C29441A7A9B16E67619C96659C259DCA7EA8412671A`

## 下一步：确认后受控切换

1. 核对设备/任务空闲、现场无动作，确认主窗口草稿已保存及流程库路径。重新核对 PID/命令行，不凭历史 PID 关闭进程。
2. 保留旧 Adapter15441 包、原配置、SQLite 一致性备份（含 WAL 内容），记录原启动参数和环境。**新发布目录中的默认 appsettings 不是现场配置**；切换必须沿用原 PhysicalAcceptance 有效配置、数据库及实际设备地址，不可裸启默认包。
3. 替换 Adapter15441，保留 MES15445 现有独立设备服务（无需重复部署）。先 GET 核对身份、位姿和 map MD5；不获取控制、不导航、不初始化工作站、不启动机械臂。
4. 用日常入口启动新主中控，确认孪生状态、示意坐标、错误恢复与切页。主页面稳定后再关闭本任务独立预览，减少重复轮询；旧包保留回退。
5. 若后续需要移动复验，单独确认现场条件和路线。本轮授权不包含新移动动作。

最后只读进程核对：MES15445 PID30812、Adapter15441 PID33204、独立预览PID11040、旧主WPF PID19632，均仍原命令行。部署时必须再次核对。
