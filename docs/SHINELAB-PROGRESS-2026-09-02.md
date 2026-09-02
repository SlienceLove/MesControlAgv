# ShineLab / 离子色谱接口当前进度

## 当前架构

```text
ShineLab（TCP Client，负责 D160+/SHA-18i 串口和设备动作）
        ↓ TCP + JSON + \n
MES CentralTcpServer（接收状态、发送业务任务）
        ↓ HTTP
WPF（设备状态查询、实验任务下发）
```

中控不再打开 SHA-18i 或 D160+ 串口。历史串口编解码和抓包资料只作协议参考。

## 已完成

- MES 增加 ShineLab TCP Server，监听配置项 `ShineLabTcp`；
- 支持 `Certification`、`UpdateInfo`、`AlarmInfo`、`SampleFinish`、`TaskFinish`、`TaskError`、`Result`；
- ShineLab Client 断线后可重新连接并重新认证；
- 超过 10 秒没有推送时设备显示离线；
- WPF 增加“设备状态查询”，可选择设备查看在线、运行任务、样品、通道、位置、阶段、进度和报警；
- 增加 Config/Command 高层下发接口，不暴露任意原始报文；
- 增加 `ShineLabTasks`、`ShineLabTaskEvents` 持久化；
- `task_uuid` 幂等，重复任务不会重复创建；
- Config/Command 超时或断线进入 `Unknown`，禁止自动重发；
- MES 重启时未闭环任务恢复为 `Unknown`；
- WPF 增加“ShineLab 任务下发（实验）”页面；
- 增加状态推送模拟器和命令响应模拟器。

## 当前接口

状态查询：

```text
GET /api/shinelab/server/status
GET /api/shinelab/devices/status
GET /api/shinelab/devices/{equipmentCode}/status
```

任务接口：

```text
POST /api/shinelab/tasks
GET  /api/shinelab/tasks
GET  /api/shinelab/tasks/{taskUuid}
POST /api/shinelab/tasks/{taskUuid}/config
POST /api/shinelab/tasks/{taskUuid}/command
```

## 已完成验证

本地模拟 Client 已验证：

```text
Certification → UpdateInfo → Config → Command → SampleFinish → Result → TaskFinish
```

任务状态可从 `Created` 进入 `Configured`、`Accepted`、`Running`、`Completed`，事件时间线写入 SQLite。

## 当前阻塞事项

等待 ShineLab 技术团队提供：

1. 正式 TCP Client 测试版本；
2. 实际中控 IP/端口配置方式；
3. SHA18i `equipmentCode`；
4. Config/Command 最终字段和 Action 映射；
5. 状态、错误码、停止/清洗语义；
6. Result 完整格式和结果文件共享方式；
7. 现场联调时间和接口负责人。

## 下一步

### 1～2 个工作日

- 用 ShineLab 正式 Client 替换模拟器；
- 完成 Certification、心跳和状态推送联调；
- 在 WPF 验证设备在线和任务状态显示。

### 1 周

- 完成 Config/Command 单样品联调；
- 核对样品编号、通道、位置、方法和检测方式；
- 验证任务完成、异常和重连恢复。

### 2～4 周

- 接入正式 MES 任务流程和结果归档；
- 完成交换机/WiFi 网络部署；
- 完成稳定性、异常恢复和现场验收。

## 验证命令

```powershell
$env:ShineLabTcp__Enabled = 'true'
$env:ShineLabTcp__ListenAddress = '0.0.0.0'
$env:ShineLabTcp__Port = '5500'
dotnet run --project src\MesControlAgv.Mes --urls http://0.0.0.0:5045

powershell -NoProfile -ExecutionPolicy Bypass `
  -File scripts\Run-ShineLabTcpSimulator.ps1 `
  -ServerAddress 127.0.0.1 -Port 5500 -RunSeconds 120
```
