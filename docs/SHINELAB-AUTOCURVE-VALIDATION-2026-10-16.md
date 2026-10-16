# 新协议 standard / AutoCurve 验证记录

## 来源与范围

- 来源：主目录 `res/下游表协议 - 序列进样(1).docx`，SHA256 `97F3CFFA7117A8A2107EA5FB926B2E0BAE96E0254C58F40F80D48B8CF14355C4`。
- 基线 develop `95c6cf1`；本轮分支 `feature/shinelab-autocurve-validation-20261016`，目录 `.worktrees/shinelab-autocurve-validation`。
- 用户要求按配置预览、AutoCurve 应答、实际方法/曲线验证顺序推进，随后询问是否必须真实进样。第一阶段已完成本机模拟验证，无须仪器进样。厂家 AutoCurve 是否自动运行标样仍待确认。
- 本轮仅扩展协议验证 API、持久化和测试；尚未增加 WPF 标准选择或建曲线按钮。

## 现场只读观察

2026-10-16 10:13 左右，本机旧 MES PID 6776 监听 HTTP 5145、TCP `192.168.10.11:5500`，客户端 `192.168.10.108:59889` Established，设备 `STN61_01` 持续上报 status=0 / Idle，无活动任务标志。10:20 同一 TCP 连接仍在。不能由空闲状态推断 AQ 没有勾选待运行样品，也不能由连接成功推断厂家模块已更新。

其他窗口运行 visitor-full-demo MES 5155、Adapter 5156 和 WPF。本轮未替换/重启这些服务或原5145服务，未向现场下发 Config、AutoCurve 或 Command。

## 已实现

1. Config 请求与任务创建请求支持可选 `Standard`，持久化到 ConfigJson，计入请求指纹；相同 taskUuid 修改标准会冲突。省略/null 不增加旧 JSON 字段，旧指纹和报文保持兼容。
2. DCSeq 报文明确输出 `body.standard`，不从 InstrumentMethod/ProcessingMethod/DetectionMethod 或中控检测标准 GUID 猜测。空/纯空白值暂拒绝，因为厂家尚未定义其含义；Rike 模式拒绝携带该字段。
3. 新增独立 AutoCurve 构造器及设备接口。单通道 body 为 `{}`，多系统为 `{"channel":"A"}`；不会包装成 `Command.strAction`。
4. `ShineLabTcp.EnableStandardSelection` 和 `EnableAutoCurve` 默认 false，关闭时预览可用、实际下发被拒绝。任务 Config 在写入 Configuring/ConfigAttempted 之前检查新能力，避免未发送却锁住任务。
5. AutoCurve 下发前检查新鲜的仪器 Idle 状态，与托盘共用应答等待互斥；仅为请求/应答阶段的互斥，不代表掌握建曲线完成时机。
6. AutoCurve 请求与回复进入既有 command-attempts / command-replies 持久化，保留原始 JSON、strID、连接和接收时间。迟到回复保留，超时记录不被抹除，不自动重试。它没有关联样品任务，不投影为样品启动或完成。

## 验证入口与预览

以下请求仅适用于本轮候选程序，旧现场 MES 没有新入口。示例标准是本机测试占位值，不能直接用于现场。

- `POST /api/shinelab/preview/config`

```json
{
  "taskUuid": "offline-standard-preview",
  "standard": "厂家测试标准-01",
  "sampleData": [
    { "sampleId": "source-1", "sampleName": "offline-only", "type": "0", "position": 1, "mPos": "1", "channel": "" }
  ]
}
```

单通道预览中的 TCP body：

```json
{
  "standard": "厂家测试标准-01",
  "sampleData": [
    { "sampleId": "source-1", "sampleName": "offline-only", "type": 0, "position": 1, "mPos": "1" }
  ]
}
```

正式任务先通过 `/api/shinelab/tasks` 创建，随后使用该任务 config-preview/config，以中控分配的唯一 sampleId 关联。预览占位号不作为实际进样身份。

- `POST /api/shinelab/preview/auto-curve`，请求 `{}`，响应 `strMethod=AutoCurve`、`body={}`。
- `POST /api/shinelab/devices/{equipmentCode}/auto-curve` 是实际下发入口，默认禁止；不在本轮对现场调用。
- `GET /api/shinelab/devices/{equipmentCode}/command-attempts` 与 `command-replies` 查看新操作证据。
- `GET /api/shinelab/server/status` 公布两个能力开关。

## 离线结果

- 新专项16项通过，覆盖省略字段兼容、标准原样传递/持久化/幂等冲突、空值/错误模式/通道拒绝、关闭功能仍可预览且零写入、非空闲拒绝、独立方法名。
- 本机127.0.0.1临时端口运行真实TCP服务与模拟客户端，按认证 → Device Idle → Config(standard) → AutoCurve 顺序验证 Success、Failed、超时后迟到Success。并发AutoCurve和托盘被拦截；原样品任务保持Configured，未伪造Completed。
- MES全量812项、WPF全量770项，均0失败/0跳过。16项专项包含在812项中，不重复计数。
- 解决方案构建0警告/0错误。TRX保留于本目录 `artifacts/protocol-validation/`，不提交构建产物。
- 以上证明中控协议处理与模拟应答兼容，不能证明厂家实现、真实方法切换或建曲线已通过。

## 现场下一步与缺失信息

1. 确认控制电脑已安装此次厂家新版，并取得可用的 `standard` 精确值及其对应配置。协议仅写“切换检测标准”，未明确名称/编号/路径，也未提供标准目录查询。
2. 配置试验前确认AQ序列状态和勾选行。历史Config会自动勾选且可能接续进样；本轮仍要求至少一条样品，不能自行假设空sampleData可以只切换方法。
3. 切换候选程序并启用标准选择后，一次下发、一条唯一任务；记录应答并在AQ/DA核对目标序列、样品落地和实际方法。Success只作为应答证据。
4. 明确AutoCurve是重算已有数据还是运行标样，以及标样配置来源、覆盖原曲线规则、完成/失败通知。在这些行为明确且现场准备好后，再单独启用并下发一次，超时先核对现场而非重试。
5. 正式结果验证再核对Result中的组分、浓度、k/b/r/formula/curvearray；请求接受不等于曲线生成完成。TaskFinish/EndMission语义仍沿用既有待验证边界。

代码已在独立分支准备，未合入develop/master、未推送或现场部署；上一轮e693333的列表修复仍在其独立分支，本轮没有混合合入。
