# 现场恢复后的新任务验证（2026-09-21 16:02）

> 16:18更新：用户明确反馈“已开始分液，无弹窗”，因此本轮实际执行已获得现场确认。但157:8082任务/设备/事件接口均拒绝连接（WinError10061）；中控已于16:14:47因完成状态无法读取超过原观察窗口转Unknown，不是物理停止，也不是已证明分液失败。保留占用，等待恢复只读通讯和完成证据。

> 16:07更新：启动后曾正常返回运行态，但随后任务状态读取连续两次被远端关闭连接；独立读取设备状态和事件也同样无响应。Adapter健康、工作站仍配置vendor-http；中控Run目前仍Running，不能以此证明现场继续运行。等待现场核对厂家主程序、监听服务和实际动作，不补发任何控制命令。

用户明确确认“现场恢复了，继续新的测试”。沿用feature/workstation-ml-template-validation独立工作区，HEAD88700ef，生产代码a6af9dc不变，不重新构建/部署/清库。MES15053、Adapter15052及原验收数据库不变。

验证号FIELD-RECOVERED-20260921-1602。本轮为新任务，不重放上一轮WSCEAD74AFFB073CBF8B。旧任务人工终止、Failed和资源释放见[接续交接](../SAMPLE-WORKSTATION-MANUAL-END-HANDOFF-2026-09-21.md)。

## 身份与输入

- 厂家：http://192.168.200.157:8082/Service/，设备SAMPLE-WORKSTATION-01 / CYC-001-1000。
- 新任务：WSC8024D60B67C926F8A；Job：72ed4996-d1fe-4a33-9a2b-affb0a18c806。
- Preparation：17271d3d-8e7d-4ee4-86ae-e0a5ba17e23f，Imported。
- Run：7c176ba1-38a8-4c49-89e5-1fed8b9049f3；DeviceOperation：02e51ce9-3a83-22c3-b226-9d324056e763，attempt1。
- 来源SRC-260921-1602-01/02分别绑定CYC-001-1000/(1,1)、CYC-002-1000/(1,1)，各到FYB-001/002八孔。
- 16孔每孔50µL，Excel0.05mL，总800µL；默认枪头QT-001/(0,0)，无额外枪头管理。
- 同一个已发布计划f68102db-8caf-4457-9cb9-46de113d8cd1 v1。

## 本轮已验证与当前状态

16:02:28只读确认服务正常、旧Job Failed、无工作站活动lease，设备Idle0、历史事件1。
正式链创建新Job、排程、两来源测试ID及核验快照，准备冻结新模板；单次导入及自动POST条码同步成功。下载模板与HTTP轨迹展开后的16行逐字段、顺序、体积完全一致，启动前仍Idle/Waiting/历史1。
冻结XLSX SHA256：80C23062C01F6F0D734709F5443724ABE007C90A2A0848AE2FDBC07566C3B262。
正式admit返回202。16:04:45.997设备操作进入Running；16:05:01中控Run/操作均Running，厂家任务正在运行、设备忙1、事件3。未观察到异常，尚未验证Completed或实际物理分液。

已请用户现场确认是否通过单泵取针并实际分液，或再次出现操作处置弹窗。中控worker继续按原10分钟完成观察窗口监控；不要在本轮运行/结果未明期间创建另一任务、补发Start或手动绕过弹窗。
现有接口Running不能证明无人工处置窗口，上一轮已出现此信息缺口，本轮需现场反馈。未修改超时配置，未发Init或停止指令。

## 证据和下一步

证据目录artifacts/workstation-recovered-field-20260921-1602/（ignored、本机保留）：preflight.json、central-preparation.json、frozen-import/、import-result.json、import-readback.json、readback-comparison.json、admit-result.json、poll-01.json。
下一步仅查询本Run/device-operations及厂家任务状态、设备状态、事件；结合用户现场反馈确认是否实际分液。完成需Completed+Idle0+事件0及中控Completed/资源释放共同成立；若转Unknown则保留占用和原请求，不自动重试，不沿用上一轮人工收尾授权。

后续读回证据poll-02.json、poll-03.json及connection-readback.json记录`Remote end closed connection without response`。无法据此断言厂家进程崩溃、网络断开或任务已停止；需要现场确认。没有通过HTTP写请求重试探测，也未重启任何服务。

## 现场确认实际分液后的只读核对

用户对本轮运行询问回复“已开始分液，无弹窗”。该反馈只证明观察到实际分液，不代表16孔已完成或体积精度实测通过。
16:18:32中控Run=Unknown、设备操作=Unknown、attempt1，更新时间16:14:47.339；错误为`The workstation completion state remained unreadable before timeout: 由于目标计算机积极拒绝，无法连接。 (192.168.200.157:8082)`。随后直接读取任务、设备及事件也均连接被拒绝，不是返回业务异常事件码。
工作站活动lease仍保留；未人工处置本轮Unknown，未发送重试、初始化或停止。证据field-confirmed-dispensing-readback.json、unreadable-resources.json。之前16:07的Running是历史观测，不是当前中控状态。
下一步请现场查看厂家HTTP服务窗口是否仍监听原157:8082地址，并反馈本任务是否已经完成；不要为了恢复通讯重启正在控制设备的主程序或重复开始实验。通讯恢复后先只读核对本任务及终态，再根据实际结果走既有对账流程，不自动把Unknown标成功。
