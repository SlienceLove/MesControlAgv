# 现场无线只读验证与自动回切说明

## 目的

本工具用于一次现场网络切换窗口：

1. 从当前网络切换到已确认的 `AMR` Wi-Fi；
2. 由现场明确提供的参数设置 WLAN 固定总控地址；
3. 记录接口、SSID、地址、路由和端口结果；
4. 可选地只读验证 AGV 状态端口和 AUBO WebSocket `9012`；
5. 无论中间成功或失败，最后切回 `SHINE` 并恢复 DHCP/DNS，继续当前对话网络。

切网会让远程会话的网络请求短暂超时，因此必须在现场电脑的本地管理员 PowerShell 窗口执行。不要从依赖当前网络的远程终端直接执行切网命令。

单无线网卡时，切到无互联网的 AMR 后无法继续当前在线对话。应在切网前复制命令，切到 AMR 后只在本机完成采集；看到 `Evidence:` 后人工切回 `SHINE`，再把证据路径发回对话。拥有 USB 无线网卡时，可以让一块网卡连接 SHINE，另一块 WLAN 连接 AMR；SHINE 网卡保留默认网关，AMR 网卡使用 `192.168.1.11/24` 且不设置默认网关，并确认到 `192.168.1.0/24` 的路由落在 AMR 网卡。

## 自动模式（仅同一网卡切换时）

仅当同一块无线网卡已保存 AMR 和 SHINE 两个配置文件时使用。先新鲜确认接口别名、AMR 已广播及本次掩码；下面的 `WLAN` 只是单网卡示例，`/24` 也必须以现场确认值为准。当前双网卡拓扑为 `WLAN 3=AMR`、`WLAN=SHINE`，应使用后面的“人工切网后一键采集”，不要让自动脚本把 `WLAN 3` 切到 SHINE。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Invoke-FieldWirelessReadOnlyCycle.ps1 `
  -StaticAddress '192.168.1.11' `
  -PrefixLength 24 `
  -InterfaceAlias 'WLAN' `
  -AmrProfileName 'AMR' `
  -ReturnProfileName 'SHINE' `
  -VerifyDevices `
  -AgvHost '192.168.1.2' `
  -AgvStatusPort 19204 `
  -AuboHost '192.168.1.102' `
  -AuboPort 9012 `
  -RobotName 'rob1'
```

请在本机 PowerShell 中执行。脚本发现当前窗口不是管理员时会请求一次 UAC，并在管理员子进程中继续；如果系统拒绝 UAC，脚本会停止。`-ExecutionPolicy Bypass` 只对这次启动的 PowerShell 进程有效，不会修改计算机或用户的永久执行策略；它用于绕过现场电脑对仓库脚本的执行拦截。

脚本安全边界：

- `-AgvHost` 和 `-AuboHost` 必须显式传入，不从历史默认值猜测；
- 只连接 AGV 状态端口 `19204`，不会连接 `19206`、`19207`、`19210` 或 Push 端口；
- AUBO 只调用已存在的 WebSocket 只读预检，未传变量键，并显式关闭 Modbus 信号读取；
- 不调用 AUBO `load/run/stop/abort`，不发送 DI/DO、变量、运动或控制权请求；
- 不设置默认网关，适合“AMR 无互联网、仅现场局域网”的情况；
- 地址冲突、AMR 连接失败、静态地址未生效、设备读检查失败，均不会自动重试写操作；
- `finally` 中恢复 `SHINE + DHCP + DNS`，证据写入新建的 `artifacts/physical-acceptance/wireless-cycle-*/` 目录；
- 任何恢复失败都以非零退出码结束，并在证据中标记，现场应人工恢复网络。

如果只想验证网络而不访问设备，省略 `-VerifyDevices` 及其四个设备参数。该模式仍会记录 AMR 地址和路由，然后自动回切。

## 人工切网后一键采集

如果希望由现场人员控制 Wi-Fi 切换，使用不改网络的采集脚本。先在系统 Wi-Fi 界面连接 `AMR`，再按现场确认的掩码手动设置 `192.168.1.11`，不设置默认网关。确认 `SSID=AMR` 和地址稳定后，在本机 PowerShell 执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Invoke-FieldWirelessReadOnlyCapture.ps1 `
  -AgvHost '192.168.1.2' `
  -AuboHost '192.168.1.102' `
  -InterfaceAlias 'WLAN 3' `
  -ExpectedWlanProfile 'AMR' `
  -ExpectedLocalAddress '192.168.1.11' `
  -ExpectedPrefixLength 24 `
  -AgvStatusPort 19204 `
  -AuboPort 9012 `
  -RobotName 'rob1'
```

该脚本只执行一次采集，并在新目录中保存 `wireless-readonly-evidence.json`、AGV/AUBO 各一次 ping 结果、AGV 状态只读输出和 AUBO WebSocket 只读 JSON。它不会切换 Wi-Fi、不会设置或删除地址，也不会自动重试；脚本结束后由现场人员手动切回 `SHINE` 并恢复 DHCP/DNS。

## 权威 Adapter 完整只读预检

完成上面的接口、路由、AGV `19204` 和 AUBO `9012` 直读后，可在设备仍处于受监督只读阶段时执行：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Invoke-PhysicalReadOnlyPreflight.ps1 `
  -ControllerHost '192.168.1.2' `
  -AgvStatusPort 19204 `
  -AgvControlPort 19207 `
  -EnableAuboReadOnly `
  -AuboHost '192.168.1.102' `
  -AuboPort 9012 `
  -AuboRobotName 'rob1'
```

运行前仍要用新鲜命令确认当前接口确为 `WLAN 3=AMR`、地址为 `192.168.1.11/24`、AMR 接口没有默认路由；参数值不得从旧证据猜测。脚本会新建 RunId 和隔离数据库，以 `read-only-preflight` 启动 Adapter，依次读取 `/health`、设备目录、AGV 完整预检以及可选 AUBO 状态/就绪/程序目录，然后在 `finally` 中按状态文件停止本次 Adapter。证据保存为新目录中的 `physical-readonly-evidence.json`。

AGV 完整预检只使用状态查询和地图读取 `1300/1301/1302/4011`；`19206` 命令端口、Other、Push 不会被探测或发送请求，不申请/释放控制权。AUBO 只调用 GET 对应的 WebSocket 读取，不执行 `load/run/stop/abort`、变量写入或 Modbus。脚本完成仍是只读 NO-GO 证据，不等于授权派发。启动失败、读失败或收尾失败会记录为 `FAILED`/`CLEANUP-FAILED`，不会伪造成设备已通过。

## 人工模式与截图清单

人工模式更适合首次现场验证或需要厂家/负责人同时审阅时。所有命令在本地管理员 PowerShell 执行；每次切换后先等 `SSID` 和地址稳定，不要在 Wi-Fi 正在关联时设置静态地址。

### 切到 AMR 后截图

```powershell
netsh wlan show interfaces
Get-NetIPConfiguration -InterfaceAlias 'WLAN 3'
Get-NetRoute -InterfaceAlias 'WLAN 3' -AddressFamily IPv4
Get-NetIPInterface -InterfaceAlias 'WLAN 3' -AddressFamily IPv4
```

截图建议保存为：

```text
artifacts/physical-acceptance/wireless-debug-20260907/
  01-amr-interface.png
  02-amr-ip-route.png
```

确认 `SSID=AMR` 后，再按现场确认的前缀设置 `192.168.1.11`，不填默认网关。之后只执行：

```powershell
Test-NetConnection 192.168.1.2 -Port 19204 -InformationLevel Detailed
Test-NetConnection 192.168.1.102 -Port 9012 -InformationLevel Detailed

.\scripts\Invoke-AgvIoApi.ps1 -Operation read `
  -ControllerHost '192.168.1.2' -StatusPort 19204

.\scripts\Invoke-AuboWsReadOnlyPreflight.ps1 `
  -ControllerHost '192.168.1.102' -Port 9012 -RobotName 'rob1' `
  -IncludeModbusSignals:$false `
  -OutputPath '.\artifacts\physical-acceptance\wireless-debug-20260907\aubo-ws-readonly.json'
```

截图建议保存为：

```text
03-static-ip.png
04-route-unique.png
05-agv-status-port.png
06-aubo-ws-readonly.png
```

不要截图或保存 Wi-Fi 密码。AUBO 脚本的 `-OutputPath` 必须使用不存在的新文件名，避免覆盖既有证据。

### 单网卡回切 SHINE 继续对话

以下命令只用于现场新鲜确认的同一块切换网卡；不要把示例别名套到双网卡拓扑。双网卡时保留 `WLAN=SHINE` 的默认路由，`WLAN 3=AMR` 不设默认路由，采集后无需中断 SHINE；若要停用现场链路，只断开 `WLAN 3` 即可。

```powershell
netsh wlan connect name='SHINE' interface='WLAN'
Set-NetIPInterface -InterfaceAlias 'WLAN' -AddressFamily IPv4 -Dhcp Enabled
Get-NetIPAddress -InterfaceAlias 'WLAN' -AddressFamily IPv4 |
  Where-Object { $_.IPAddress -eq '192.168.1.11' } |
  Remove-NetIPAddress -Confirm:$false
Set-DnsClientServerAddress -InterfaceAlias 'WLAN' -ResetServerAddresses

netsh wlan show interfaces
Get-NetIPConfiguration -InterfaceAlias 'WLAN'
```

确认 `SSID=SHINE`、WLAN 已取得 DHCP 地址后，再继续对话。AMR 侧的设备超时现象应保留为 NO-GO/待分析证据，不能因为回切成功而判定设备或流程通过。
