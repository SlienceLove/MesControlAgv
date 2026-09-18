# 工作站 V1.02 大瓶条码通讯接入

用户已确认：来源 ID 由中控管理，可填入厂家大瓶条码字段；UpdateTaskBarcode 只更新条码，不会启动任务。中控按任务模板保存来源和目标位置关系，正式运行保持独立准入。

## 本次可独立交付的通讯层

- 新增明确的双大瓶条码请求，字段 SampleBarcode1 / SampleBarcode2。
- MES → Adapter 的现有 POST tasks/{taskNo}/start 支持可选 JSON 请求体；无请求体保留已有 taskNo-only 行为。有请求体时完整携带两个条码，不能丢弃并回退为旧启动。
- 新增 POST tasks/{taskNo}/barcodes，仅执行厂家 UpdateTaskBarcode。写条码和启动不互相调用。
- 厂家侧按现有 GET 查询参数编码方式传 TaskNo / SampleBarcode1 / SampleBarcode2。V1.02 的表标题写“请求头设置”，缺少准确请求示例；查询参数传输须在新版服务验收时确认，未确认前不宣称真机通过。
- UpdateTaskBarcode 只认 Code=200 且 Data=更新成功；201（任务不存在）及明确更新失败作为拒绝，其余不明回复/断线仍保留结果不确定。无自动重试。
- 保留现有 control-enabled 检查、参数编码和错误透明传递。条码仅 Trim，保留大小写及内部字符。

## 与后续模板/业务链的边界

本次仅接入通讯契约、HTTP 路由与客户端；现有正式 worker 的 taskNo-only 调用不自动变成“写码再启动”，也不把任意样品行顺序猜作1/2号大瓶。
下一步取得实际模板后实现明确来源位置绑定，冻结任务/双大瓶条码/分液表，再让正式 worker 消费该冻结数据。当前不宣称已完成自动模板导入或完整位置溯源。

V1.02 第20–25页给出了模板下载、导入、新建任务、移液轨迹及更新条码；模板 SourceBarCode / TargetBarCode 是模块参数编码，不是样品条码。上传文档同时提到 multipart/form-data 与 Base64 raw，需要真实模板/请求样例核对。

## 实施与验证

1. 扩展 Contracts、Application 的条码命令接口；保留旧命令接口。
2. Adapter 实现更新与带码启动、HTTP绑定、明确成功/失败处理。
3. MES 客户端、DI 与路由透传；仅命令端点提供新能力。
4. 定向测试覆盖更新不启动、启动不更新、两个条码编码/透传、空体兼容、参数拒绝、200失败文案、201、超时不重试与禁用控制零出站。
5. 回归 Adapter/MES 工作站相关用例，更新集成说明，提交代码。现场更新和真实验收前不调用设备。

实施仍在现有隔离工作区；真实验收通过后合并到 feature/wpf-ui-layout-optimization 的授权保持有效。
