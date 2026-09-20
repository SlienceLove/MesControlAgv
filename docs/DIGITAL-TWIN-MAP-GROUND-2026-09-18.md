# 轻量地面与导航参照交付

已按用户确认在独立工作区完成。原 GLB 没有修改，未更改 MES/Adapter 运行服务，无设备动作、无自动标定、无提交／合并。

## 现在可见

- 三维浅灰地面、深色工作区边框和 1 米网格。地面按导航站点覆盖范围加留白提供尺度参照，不把矩形边框声称为真实房间外墙。
- 默认设备区视角保留模型大小；“完整场景”查看整块地面，“设备区”返回近一些的全设备视角。
- 右下导航参照来自原 guangzhou606 地图，包含 6 个 LM 站点、148 条原特征线、工作区内降采样的 2365 个扫描点及 10 条规划路线。路线可开关；蓝线是规划路径，不是实机历史轨迹。
- 未标定时不把地图任意旋转平移到 CAD 上。参照窗明确写“未与 CAD 对齐”。有效确认标定后才在三维地面叠加轮廓与站点；进入标定选点时隐藏这层叠加，避免将参照本身误当现场观测。

用户圈出的 LM1 图片已留存 `artifacts/digital-twin-map-ground/LM1-user-approximate.png`，仅作为大致位置参考，没有转成精确标定或移动蓝色机器人。

## 验证

- WPF 回归 529/529；JS 地图／插值测试 8/8。
- 原生离线 WPF/WebView 冒烟通过地面／路线／参照开关、未标定独立参照、确认后叠加、编辑时隐藏叠加，以及既有位姿冻结、整机移动和卸载停止采样。
- 证据：`artifacts/digital-twin-map-ground/smoke-final/result.json`。
- 截图：同目录 `position-unconfigured-ui.png`、`floor-full-overview.png`；它们是离线测试，不是现场运动证明。
- 独立只读代码审查通过；此前 CalibrationPreview 启动截图与关窗竞争已修复，JSON 证据与截图独立写入，关窗等待在途捕获，错误记录失败不再掩盖原错误。
- 模型 SHA256 仍为 `96ca7f34c189c9c1add62c29441a7a9b16e67619c96659c259dca7ea8412671a`；原模型 1,287,310 面，地面／站点新增几何单独计入渲染统计，不混作模型几何修改。

## 预览与后续

新发布包：`artifacts/digital-twin-map-ground/package`，启动参数 `--live-readonly <evidence-directory>`。只实例化数字孪生控件，现有 MES 提供状态、独立只读 TCP 提供位姿，不启动第二个调度器。

确认旧预览“尚未选点”后，仅正常关闭本任务旧预览（PID 25328，已核对可执行文件路径），再启动本包。真实启动证据在 `artifacts/digital-twin-map-ground/live-session/startup.json`，界面截图在同目录 `live-preview.png`。

下一步让用户借助地面网格指出 LM1 的底盘中心位置，再核对 LM2 和 LM6／其他非共线点、底盘参考点偏移及车头方向。仅有一个大致红框仍不能确认完整地图→CAD 变换，位置同步尚未启用。

## 用户追加参照与地面扩大

用户认可地面效果，但认为范围偏小；地面四周各增加 2 m 显示留白，未标定底板从 16×11 m 扩为 20×15 m。网格仍为 1 m；不放大设备、不修改站点距离或原 SMAP 工作区范围。默认设备区视角不因边界扩大而缩小设备。

追加两张图保存于 `artifacts/digital-twin-map-ground-expanded/references/`：

- `LM1-ground-approximate.png`：有地面网格的三维截图，右侧红框为用户指出的 LM1 大致停车区域。
- `workstation-navigation-reference.png`：导航地图红箭头指中间开盖／分液工作站所在位置。按设备本体／所在台面参照处理，不将其误标为 LM6 或 AGV 对接停车点。

两图是用于后续核对方向和相对位置的人工参照，没有从截图估计出精确坐标，没有写入标定文件，也没有移动三维设备或实机。

扩大地面的原生冒烟已通过（`artifacts/digital-twin-map-ground-expanded/smoke/result.json`），并增加 20×15 m 范围及 1 m 网格断言；独立增量审查通过。新包位于 `artifacts/digital-twin-map-ground-expanded/package`。确认旧预览“尚未选点”后正常关闭该窗口并打开新包，现场 MES/Adapter 的监听 PID 未改变；新启动证据位于同级 `live-session/`。
