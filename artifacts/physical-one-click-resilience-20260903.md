# 料盘标准流程一键执行离线加固记录

日期：2026-09-03（Asia/Shanghai）

## 本轮结果

- WPF 只提交本次运行授权（操作员、监护人、许可前缀、有效期），不会在运行时打开 MES 或 Adapter 的设备写权限。
- MES 仅在启动时已同时启用自动派发、现场导航、自动生成节点许可、AGV worker 和 AUBO worker 时受理物理一键流程；否则立即明确拒绝，不再创建一个长期停在“就绪”的运行。
- 一键运行只允许已发布且结构完全匹配的 9 节点标准模板：`LM1 → LM7/取料盘.pro → LM2/放料盘.pro → LM7/回收料盘.pro → LM1`。
- 同一 AGV 同时只允许一个活动的物理批次；相同请求 ID 保持幂等，不同授权内容不能复用旧请求。
- AGV 下发前对离线、低置信度、阻挡、急停、故障、重定位状态、地图证据、控制权和活动任务进行限时只读复核；条件恢复并稳定后继续同一节点。
- AGV 厂商状态 `paused` 视为仍在执行；短暂状态读取失败保留原任务，不重复派发。最终返回 LM1 后，在流程完成前只尝试一次释放控制权。
- AGV 3066 导航写入后若 1110 回执读取超时，Adapter 返回结构化 `unknown` 结果和未确认原因，不再把二次核对异常冒成 HTTP 500，也不重发导航；任务查询超时返回明确的网关超时/服务不可用状态。
- Adapter 增加统一 transport 异常边界：AGV 状态/预检/派发的超时、控制器错误、连接断开和参数异常分别返回 504/502/503/400/422，避免网络断开时冒出无法操作的 500；MES AGV 只读接口同步转为可识别的网关错误。
- WPF 运行监控将预检超时和 Adapter/AGV 不可达单独显示为网络预检告警，并保持流程暂停；恢复后继续只读复核，不自动重发设备命令。
- AUBO 在加载/启动前等待 Online、ControlEnabled、Running、Normal、Automatic、Stopped 稳定；启动确认后只轮询状态，不重复发送 load/run。临时暂停恢复为 Running 后继续观察。
- AUBO 完成超时调整为 10 分钟，以覆盖现场约 4 分 31 秒的取料程序；状态读取短时异常可在 60 秒窗口内恢复。
- WPF 对 AGV/AUBO 当前节点异常和批次许可过期提供告警条及一次性弹窗；告警未变化不反复弹窗，恢复后自动清除。
- 流程管理顶部命令区支持自动换行；运行监控继续支持单实例全屏窗口。

## 控制权限边界

WPF 的 `WPF_ENABLE_PHYSICAL_BATCH=true` 只显示并允许使用“一键现场执行”入口，它不是设备写权限开关。实际写入还必须在服务启动配置中预先满足：

- MES：`Profile.features.enableAutomaticDispatch` 保持现场验收配置要求的 `false`（本流程使用 FieldNavigationAcceptance，不走通用自动派发）、`enableFieldNavigationAcceptance=true`、`WorkflowFieldNavigationWorker.Enabled=true`、`AutoAuthorizeFromRunRequest=true`、`WorkflowAuboWorker.Enabled=true`。
- Adapter：标准运行模式、AGV 控制权申请已启用、现场验收导航能力已启用、AUBO `Enabled=true` 且 `ControlEnabled=true`。通用自动派发仍保持关闭，避免绕过现场验收单。
- 每次运行：WPF 提交有效的操作员/监护人/许可信息，并通过新鲜的设备只读预检。

仓库中的 PhysicalAcceptance 配置仍保持写权限关闭，避免仅启动新部署包就误发现场命令。恢复网络后应在明确授权下修改部署配置并重启对应服务，而不是由 WPF 普通运行页临时开启。

## 验证与部署

- WPF：364 通过，0 失败。
- MES：147 通过，0 失败。
- Adapter：222 通过，0 失败。
- 工作流契约：71 通过，0 失败。
- 整个解决方案基线：946 通过，5 个既有 E2E 场景跳过，0 失败；新增 500 修复回归后 Adapter 为 222 通过。
- 独立部署目录：`bin/Verify/PhysicalOneClickResilienceDeploy/`（`Mes`、`Adapter`、`Wpf`）。
- 最新含 transport 边界修复的独立部署目录：`bin/Verify/PhysicalOneClickResilienceDeployFinal/`（`Mes`、`Adapter`、`Wpf`）。
- 旧 Adapter 进程锁定默认 Release 文件，发布过程改用隔离输出，未停止或覆盖现场旧进程。
- 离线加固阶段未恢复现场网络；本次恢复网络后的尝试已产生一次现场 AGV 导航请求，随后因 1110 回执超时进入 Unknown，未发送任何重试或机械臂程序写命令。

## 本次现场尝试记录

- 运行 `1e159361-d76f-4370-9a6f-b3d7b86a43d9` 在首个 `LM1 → LM7` Move 节点进入 Unknown；Adapter 日志显示 3066 请求已进入写入边界，1110 状态确认超时，无法证明 AGV 是否已接受任务。
- 随后只读查询验收单返回 `state=unknown`、`deviceTaskId` 为空、`dispatch_not_confirmed_by_1110`；AGV 预检为在线、`LM1`、控制权 `adapter`、无活动任务、置信度 `0.9233`、无急停/阻挡/故障。由于请求曾进入写入边界，仍须现场目视确认后再人工核销，禁止复用原请求 ID 或自动重派。
- 已完成的代码修复只进入隔离部署包，当前运行中的旧 MES/Adapter 未被停止或替换。

## 2026-09-03 服务切换结果

- 按授权核销旧 Unknown 运行并释放旧 Adapter 控制权后，旧 MES/Adapter 已停止。
- 修复版 Adapter 已以 `standard + vendor-tcp + AcquireControl=true` 启动（PID 42656），但 AGV `192.168.1.2` 随后无法通过 ICMP 和 19204 状态端口；Adapter 只读结果为 `online=false`。
- 因 AGV 在线预检未通过，修复版 MES 未启动、标准模板未恢复、没有创建新运行，也没有发送新 AGV/AUBO 指令。待现场恢复控制器链路后再启动 MES 并恢复模板。

## 断网异常边界回归

- Adapter 全量测试：222 通过，0 失败；MES 全量测试：147 通过，0 失败。
- 新增边界保证：未连接 AGV 时只读预检返回明确的 503/504 语义，物理导航写入后回执不明保持 Unknown；任何异常路径都不会自动重发设备命令。

## 仍需现场验证

- 网络恢复后先做新鲜只读预检，再启用经授权的服务配置并重启 MES/Adapter/WPF。
- 用标准模板做一次有人监护的一键全流程，确认三次程序切换、AGV 四段导航、异常弹窗及最终控制权释放。
- Roboshop 自动重定位写接口尚未确认；在抓到按钮对应的真实协议前继续保持未实现。
- 急停、硬故障或写入结果不明必须安全暂停并人工核对，不能为了“不中断”而自动重发可能已生效的设备命令。
