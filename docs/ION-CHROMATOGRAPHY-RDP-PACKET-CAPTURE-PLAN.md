  下一步现场执行顺序：

  1. 在控制电脑确认 D160+ 和 SHA-18i 的实际 COM 号；
  2. 关闭 ShineLab，确保串口未被占用；
  3. 使用只读命令验证 D160+：

  dotnet run --project src/MesControlAgv.DeviceProtocolTester -- serial `
    --com COM3 `
    --request-hex "01 04 19 00 00 14 F7 59" `
    --timeout-ms 3000 `
    --output artifacts/ion-chromatography/d160-readonly.json

  4. 连续验证稳定后，再实现本地仪器网关；
  5. 接入中控仪器状态页面和任务状态机；
  6. 最后在空载环境逐项开放加载方法、进样、启动和停止。

  在完整协议、寄存器表和现场只读响应确认前，暂不开放生产写操作。

  2026-08-28 补充：已取得 `res/18i双通道自动进样器通讯协议.xlsx`，并与现场
  COM3 抓包核对。SHA-18i 普通运行的只读块、方法块和自动进样地址已具备离线编解码
  依据；后续抓包只用于确认现场身份/固件、洗针/托盘动作及 ShineLab“终止”语义。
  原文中的 COM3/D160+ 示例仅为早期草稿，现场当前 D160+ 已核实为 COM4，不能照抄。
