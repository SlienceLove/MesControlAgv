# SHA-18i 实验直连方案（已停止采用）

本文件记录的“WPF → InstrumentGateway → COM3”直连方案已废止，不再作为当前开发或现场部署路径。

当前方案是：

```text
ShineLab（TCP Client，负责串口和设备动作）
        ↓ 推送状态/任务结果
中控 TCP Server / MES
        ↓ 查询
WPF 设备状态查询页
```

最新接口、角色和交付要求以 [ShineLab—中控 TCP 接口需求清单](/D:/Project/Github/Mes/docs/SHINELAB-SHA18I-INTERFACE-REQUIREMENTS-2026-09-01.md) 为准。

`Sha18iProtocolCodec` 及现场抓包资料仅作为协议证据和离线分析材料保留；中控不再打开 SHA-18i 或 D160+ 串口，也不再从 WPF 下发串口控制命令。
