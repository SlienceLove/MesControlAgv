# 零枪头兼容实施清单

设计已由用户确认，见同日 zero-tip-compatibility-design。
工作区：`D:\Project\Github\Mes-worktrees\sample-workstation-http-readonly`。

1. 只读获取 `test` 模板响应并固化测试输入；不触发其他设备命令。
2. 先增加枪头 `(0,0)` 及负向矩阵测试，再最小调整模板解析/生成。
3. 离线工具使用该响应生成新任务号的16孔50µL预览并读回核对，确认原输入不变、输出不覆盖。
4. 回归 Adapter/MES 相关用例、隔离构建、独立审查；更新交接和厂家核对文字。没有实际导入、写码、初始化、启动或合并。
