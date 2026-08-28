# 离子色谱现场交接摘要（2026-08-27）

## 当前决策

机械臂/AUBO 工作暂时暂停。当前优先验证 D160+ 与 SHA-18i 进样器的现场通讯。
ShineLab 先作为辅助软件（查图谱、观察状态、必要时人工操作）；如果下游 TCP
协议无法确认，改为在中控/控制电脑部署双串口仪器代理，直接连接 D160+ 和 SHA-18i。

## 2026-08-28 协议资料补充

已取得并核对厂商 `18i双通道自动进样器通讯协议.xlsx`。普通运行协议与现场
COM3 抓包一致，已可用于离线编解码；动态验证仍只保留为确认设备身份、固件兼容性、
终止语义和清洗/托盘互锁，不再需要为普通进样命令做盲目猜测。详见
`artifacts/ion-chromatography/SHA18I-PROTOCOL-CROSSCHECK-20260828.md`。

## 现场网络现状

- 本机（分析/中控笔记本）有线网卡：最后观测为 `192.168.1.106/24`，链路 `Up`。
- 连接两台仪器串口的控制电脑已由旧地址 `192.168.250.2` 改为
  `192.168.1.108`（用户现场修改）。
- 已验证：`ping 192.168.1.108` 成功（约 3 ms）；TCP `192.168.1.108:445`
  可达。
- 旧共享路径 `\\192.168.250.2\\MES-RPA-Inbox` 不再作为当前地址；对
  `\\192.168.1.108\\MES-RPA-Inbox` 的检查暂未找到共享（可能是共享名/权限尚未配置）。
- `net view \\192.168.1.108` 返回系统错误 5（Access denied）。WinRM/CIM 远程进程查询
  未成功，原因是目标未加入 TrustedHosts/未提供远程凭据。因此 ShineLab 进程和
  监听端口必须在 **控制电脑本机** 执行检查，不能在本分析电脑上猜测。

## 设备与协议已知事实

### D160+

- 既有现场证据：`COM4`、`115200 8N1`、Modbus RTU 从站地址 `1`。
- 已完成只读函数 `0x04` 验证：12/12 帧成功，CRC/长度/功能码通过；主要映射见
  [`ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md`](ION-CHROMATOGRAPHY-D160-PROTOCOL-VERIFICATION.md)。
- 已知读取脚本：`scripts/Test-D160ProtocolReads.ps1`，脚本默认锁定 COM4，运行前要求
  ShineLab 关闭，不能在 ShineLab 占用 COM4 时启动。
- 已准备当前值重写验证包，但它仍是受控门禁，不等于已开放泵、温控、进样或分析启动。
  任何写入超时都应标为 Unknown，不自动重试。

### SHA-18i / AS18

- 已取得厂商 `18i双通道自动进样器通讯协议.xlsx`，并与现场 COM3 抓包逐字节核对；
  普通运行状态为 `01 04 07 6C 00 06 ...` 与 `01 04 07 72 00 02 ...`，方法块为
  `0x10@0x0640/27 + 0x065B/3`，自动进样为 `01 06 07 09 00 01 ...`。
- 协议表定义洗针 `0x070A`、推盘 `0x070B`、缺瓶清零 `0x070C`、抑菌清洗 `0x0711`；
  `0x0708` 在运行表中命名为初始化，而静态 `CmdStop` 指向同一地址，不能直接当作
  硬件急停。
- 现场 COM3、串口参数、型号/固件仍需在 S0 只读场次确认；在终止/清洗互锁得到动态
  证据前，不手工发送写帧、不把写入路径接入 MES。
- 动态取证必须在控制电脑上进行：USBPcap+Wireshark（USB 串口）或高阻抗双向串口
  分析仪（原生 RS-232）。禁止第二个程序抢占 ShineLab 正在使用的 COM。

### ShineLab 下游 TCP

- `res/下游表协议.docx` 描述了换行 JSON 长连接、Certification、Config、Command、
  SampleFinish/TaskFinish/Result/TaskError，但没有证明当前 ShineLab 是 Server。
- ShineLab 安装包中的内部 ZMQ 端口（历史上见过 5556/5557/5558）不能直接当作下游
  TCP 端口；必须按进程 PID 找到真实 `LISTENING` 端口。
- 当前 `src/MesControlAgv.ShineLabDispatcher/ShineLabTcpProtocol.cs` 只是客户端编解码
  骨架，不能据此宣称现场已打通。
- CSV 导入 RPA 已在真机接受过，但它不点击“运行”，暂作为辅助/人工复核路径；不应把
  RPA 导入成功等同于仪器直接控制成功。

## 下一会话第一步（控制电脑本机执行）

请在 `192.168.1.108` 控制电脑、管理员 PowerShell、已登录 ShineLab 的交互桌面运行：

```powershell
$names = 'ShineLab|ShineDataAcquisition|ShineDataAcquire|ShineControl'
$shine = @(Get-Process | Where-Object { $_.ProcessName -match $names })
$shine | Select-Object Id,ProcessName,MainWindowTitle | Format-Table -AutoSize
$ids = @($shine | ForEach-Object Id)
Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
  Where-Object { $ids -contains $_.OwningProcess } |
  ForEach-Object {
    $p = Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue
    [pscustomobject]@{
      ProcessName=$p.ProcessName; PID=$_.OwningProcess;
      LocalAddress=$_.LocalAddress; LocalPort=$_.LocalPort; State=$_.State
    }
  } | Sort-Object LocalPort | Format-Table -AutoSize
```

如果没有 `Get-NetTCPConnection`，使用：

```powershell
netstat -ano | findstr LISTENING
```

只回传 `ProcessName/PID/LocalAddress/LocalPort`，不要先发送 Certification、Config 或
Command。若找到 ShineLab 监听端口，下一步才在本机做一次非破坏性连接/换行 JSON 握手；
若没有监听端口，直接转入双串口代理路线。

## 若 ShineLab 没有下游 Server：双串口代理路线

1. 在控制电脑记录两台设备的 COM、USB VID/PID、波特率、校验、数据位、停止位。
2. ShineLab 关闭后，先用 `scripts/Test-D160ProtocolReads.ps1 -Com COM4` 做 D160+
   只读回归（若控制电脑上的 D160+ 仍为 COM4）。
3. 对 SHA-18i 先做被动抓包：连接/自动识别、状态、初始化、样品位、最小方法、单次进样、
   停止；每个动作间隔约 5 秒并记录时间。
4. 将原始 `pcapng`/串口十六进制日志、动作时间表和设备信息复制到本机，离线解析后再
   实现 SHA-18i 驱动。不得先重放静态猜测帧。
5. 代理独占两个 COM，向 MES 暴露业务级状态/方法/进样接口；ShineLab 只在需要查图谱
   时人工打开，不能与代理同时占用同一串口。

## 已完成的软件入口

- AGV 相关变更与本交接无关，暂不继续现场动作。
- D160+ 只读/受控验证脚本：`scripts/Test-D160ProtocolReads.ps1`、
  `scripts/Test-D160NoOpWrites.ps1`。
- ShineLab RPA/批次导入：`scripts/Start-ShineLabBatchAgent.ps1` 及
  `src/MesControlAgv.ShineLabDispatcher`；默认不点击运行。
- 项目新增/修正的网络和机械臂文件可以保留，但本交接阶段不启动 AUBO 写入。

## 本次会话未完成事项

- 尚未在控制电脑本机确认 ShineLab 真实监听端口；
- 尚未确认新的 `.108` 共享目录和代理交接权限；
- 尚未在新的控制电脑上确认 D160+/SHA-18i 的 COM 号；
- 尚未对 SHA-18i 发送任何启动/进样写帧；
- 尚未把任何 D160+/SHA-18i 写入路径注册到 MES 工作流。
