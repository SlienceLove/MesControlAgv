# ShineLab YhLoop 原生 TCP 帧记录

更新时间：2026-09-09

## 已确认

对现场 `res/ShineLab` 二进制做静态分析后，`ShTcpClient::SendData`、
`ShineLab/ShPipe.dll` 的 `FindFrame` 和接收处理共同证明，ShineLab TCP 线
上帧不是换行 JSON，而是：

```text
55 AA [length_hi] [length_lo] [UTF-8 JSON] 00
```

`length` 是 JSON 的 UTF-8 字节数加 1（末尾 NUL），按大端序写入。因为长度
字段是 16 位，JSON 最大为 65534 字节，完整帧最大为 65539 字节。TCP 半包和
粘包由长度字段处理；不能用 `ReadLineAsync()`。

YhLoop 专属 RTTI/符号包含 `Send_CertificationInfo`、`Send_device`、
`Send_result`、`Send_TaskFinish` 等，首个业务方法目前优先判断为
`Certification`。`BindModule` 出现在其他 IRAY/DataCenter 命令区域，不能
直接套用到当前 YhLoop。

## 未确认

- YhLoop 现场首帧的完整 JSON body、设备编码和响应字段仍需真实报文或厂商
  示例确认。
- `ShPipe` 存在通用 keep-alive API，但当前静态证据不足以确定其线上帧值、
  周期和响应规则；在确认前不主动伪造心跳。

## 当前实现边界

MES 只在收到合法 `Certification` 后回同一原生帧格式的 `Success` 响应；不
自动发送 `Config`、`Command`、泵/进样/方法控制报文。所有验证先在本机离线
fixture 和回环测试中完成。

