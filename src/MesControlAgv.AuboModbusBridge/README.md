# AuboModbusBridge

本程序在 MES 电脑上提供一个最小 Modbus TCP Server，兼容现场 Lua 模板中
“AUBO 作为 Modbus 主站、总控电脑作为从站”的方向：

- 监听地址：`192.168.1.11:502`
- Unit ID：`1`
- HR0：总控输入/命令（`0` 空闲，`1..5` 对应 Lua 分支）
- HR1：总控输出/机器人结果
- 支持功能码 `03`、`06`、`16`

默认启动时禁止非零 HR0 写入；只有显式加入 `--allow-motion` 才允许命令触发机械臂动作。

注意：这不是 AUBO `res/modbus从站地址表_v1.15.xlsx` 所描述的“中控作为主站、
AUBO 作为从站”客户端。后者需要另行实现 Modbus TCP Master，使用 AUBO 从站表中的
300~555 等地址，不能把它们和本程序的 HR0/HR1 混用。

当前它是独立控制台验证程序，尚未接入 MES/WPF、持久化审计或现场动作状态机。
现场首次闭环只允许不带 `--allow-motion` 启动，并将 HR0/HR1 保持为 0。

```powershell
dotnet run --project src/MesControlAgv.AuboModbusBridge -- --bind 192.168.1.11 --port 502 --allow-motion
```

控制台命令：

```text
get 0
set 0 0
set 0 1
get 1
quit
```

当前 Lua 模板每次运行只检查一次 `总控输入`；要支持连续任务，需要后续把动作分支包进循环或由上层逐次启动程序。
