# 数字孪生独立提交与合入边界

## 提交范围

本次仅整理已验收的606数字孪生：离线模型/依赖资源、WPF页面与全屏、地图参照和示意标定、平滑位姿显示、WPF→MES→Adapter只读位姿接口、独立设备就绪刷新、对应测试和技术记录。

保留在原工作区、不纳入此提交：三设备执行层整合、已有任务工作站启动worker、流程模板/运行语义变更、现场脚本与记录、数据库、部署包、日志和截图。已运行的现场整合版本包含这些其他未提交改动，不能把本提交等同于整个现场软件版本。

完整源码只读位姿链路本身不依赖三设备执行worker。GLB及Three.js/Draco离线依赖连同许可证纳入源码；构建无需本地res目录或CDN。相关模型来源见 `src/MesControlAgv.Wpf/DigitalTwin/Web/ASSET-SOURCES.md`。

## 本次提交验证结果

暂存源码树 `b887df72373b94217bcf928b4340de6fc2a9adcf` 已导出至本机 `artifacts/digital-twin-source-validation/source/` 验证；后续只补充本段记录，产品代码不变。

- Adapter：334/334；WPF：534/534；MES：480/480；前端：20/20；干净源码全屏原生烟测通过。
- MES首轮479通过、1失败：既有ShineLab TCP测试在等待完成状态时读到Running；该组11项单独复测及全部480项重新运行均通过。未为此修改无关测试或产品代码；保留首次失败与复测TRX。
- 既有WPF演练测试通过`.git`目录定位且依赖未纳入版本的`operator-sample.csv`。本次仅在导出目录建立定位标记，并复制这一个已有CSV输入，确保测试读写留在验证副本，不碰真实演练收件箱。它是既有测试夹具依赖，不是数字孪生代码依赖，也未混入本提交。
- 与现场整合版测试数量不同：未纳入本次提交的三设备动作代码及测试仍留在原工作区。
- 提交范围只读审查确认无Critical/Important/Minor；许可证、GLB外部依赖检查、所有ProjectReference与链接测试源均齐全。

本机证据为`artifacts/digital-twin-source-validation/results/`及`smoke-fullscreen/result.json`，不随代码提交。

## 验证方式

从暂存树导出干净源码，在独立验证目录执行Adapter、MES、WPF测试及前端测试，检查未暂存的三设备代码没有成为隐藏依赖。验证不启动现场服务、不调用设备控制。

常规验证命令：

```powershell
dotnet test tests/MesControlAgv.Adapter.Tests/MesControlAgv.Adapter.Tests.csproj
dotnet test tests/MesControlAgv.Mes.Tests/MesControlAgv.Mes.Tests.csproj
dotnet test tests/MesControlAgv.Wpf.Tests/MesControlAgv.Wpf.Tests.csproj -p:SkipLocalServiceBuild=true -p:SkipLocalServiceCopy=true
node --test tests/digital-twin/*.test.mjs
dotnet run --project scripts/digital-twin/CentralClientSmoke/CentralClientSmoke.csproj -p:SkipLocalServiceBuild=true -p:SkipLocalServiceCopy=true -- --fullscreen artifacts/fullscreen-smoke
```

`--fullscreen-keyboard`额外验证Windows真实键盘焦点，会短暂聚焦其离线测试窗；不要与其他UI自动化并发。`CalibrationPreview --live-readonly`和`PoseReadOnlyProbe`是显式现场只读工具，不属于离线回归命令。

日常客户端入口源为 `scripts/digital-twin/Start-CentralClient.ps1`，需显式提供已发布WPF目录、现场Adapter配置路径、流程库和SMAP路径。先使用`-InspectOnly`检查。现场15445/15441服务是外部依赖，不由此脚本创建或替换；不提交本机生成的便捷包装脚本和配置。

## 当前合入阻碍

根据 `develop` 上的 `docs/BRANCH-CONSOLIDATION-2026-09-20.md`，集成目标为 `develop`，而不是直接发布到master。

整理本提交时，`.worktrees/branch-integration` 正在合入 `archive/workstation-base-20260920`（MERGE_HEAD `bf717ff8f262de5eb97bb298b4027b9b48ce4350`），仍有27个未解决冲突。这是原有的工作站整合，不属于本次数字孪生提交的授权范围。保留该工作区/索引/冲突状态，不中断、不混入提交，不绕过它修改develop引用。

待该合并完成后，再将本数字孪生提交合入develop，并对共享的MES端点、WPF入口、设备就绪模块运行组合回归。未宣称当前已经合入、发布或推送远端。
