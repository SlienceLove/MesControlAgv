# DCSeq 持续认证：V3.1 Merge 包静态分析

## 结论

该压缩包内的 DCSeq 实现高度疑似遗漏了 `Certification` 命令工厂注册。这比 `strCode`、`result` 或 `success` 字段差异更能解释现场现象。

证据来自PE导出、虚函数表、x86反汇编和同包DCSimple/YhLoop实现对照；没有执行、修改或替换厂家DLL。尚未核验远端AQ进程实际加载文件的哈希，因此结论严格限定于本压缩包，需厂家按源码和实际发布版本复核。

## 输入及复核指纹

- 源文件：主工作目录 `res/V3.1 Merge.zip`，441391622字节，本机文件时间2026-09-21 10:22:51。
- `V3.1 Merge/ShDeviceFamily.dll`：5092864字节，PE x86，FileVersion 1.0.0.1。
  SHA256：`14A29EECDB02522AEEA9ED74647CFB0B6D534ECABF1FC87EBB6EBF6563630E76`
- `V3.1 Merge/ShDevicePlus.dll`：920064字节，FileVersion 1.0.0.1。
  SHA256：`2E3AFF50072DBDEDBC1CB60FC71EBEEA773D80727DE84FD3CAD67BB994D165FB`
- `ShPipe.dll`：200192字节，SHA256：`ADE5331DA6ED089B1ECD4B9DE47950FA7A0ED7C0EF40143A9275E01C5A6247EA`。
- DLL内含多个模块，DCSeq不是独立命名的DLL，而是在ShDeviceFamily.dll内实现。
- 本文地址均为静态首选VA；相应DLL ImageBase均为 `0x10000000`。如按RVA定位，减去该基址；不要直接当作ASLR后的进程地址。

## 关键证据链

### 1. DCSeq 与 DCSimple 的注册入口不同

`ShSubDeviceProtocol_DCSeq` 构造函数 `0x101351C0` 将认证标志 `this+0x250` 初始化为0，并设置虚表 `0x102E4334`。

| 模块 | 虚表 | 注册槽偏移 | 实际函数 |
|---|---|---|---|
| DCSeq | 0x102E4334 | +0x98 | 0x10137F10 |
| DCSimple | 0x102E484C | +0x98 | 0x101358D0 |

初始化函数 `0x10137D70` 依次调用发送认证(+0xA4)、注册(+0x98)、初始化监听(+0xA0)。

通用注册函数 `0x10137F10` 中：

- `Command`、`Config` 关联业务处理函数表 `this+0x1E8`。
- `Init`、`Command`、`Config`、`Device` 注册到反序列化工厂表 `this+0x1E0`。
- 没有 `Certification` 注册。

DCSimple的额外注册函数 `0x101358D0` 则清楚地执行：

```text
0x101358F8  call 0x10137F10          ; 通用注册
0x101358FD  push 0xD
0x101358FF  push 0x102E3DFC         ; "Certification"
...
0x1013592B  call 0x10135960         ; 将认证构造器加入 this+0x1E0
```

`0x10135960` 在 `this+0x1E0` 注册工厂；后续构造器 `0x10135B40` 创建使用虚表 `0x102E4198` 的 `IDCTCP_Certification`，并写入方法名 Certification。

### 2. 接收端先查方法名，再判断是否认证

`ShSubDeviceProtocol_DCTCP::DecodeBuffer`：`0x10138190`。

其通用JSON解析函数 `0x100F4AE0` 提取：

- `strID` → 对象+0x14
- `strMethod` → 对象+0x2C
- `equipmentCode` → 对象+0x44
- `strCode` → 对象+0x74，按字符串读取
- body经虚方法分派；基础body仅读取channel

随后 `0x10138301—0x1013831A` 以已解析的 **strMethod** 调用 `ShDevicePlus.dll!ShSubDeviceProtocol_DataCenter::CreateCmd`。

`CreateCmd` 位于ShDevicePlus.dll的 `0x10012420`，从 `this+0x1E0` 方法表查找；未找到时在 `0x10012474` 返回空shared_ptr，没有通用认证兜底。

接收处理函数 `0x10137DA0`：

```text
0x10137DF9  call DecodeBuffer
...
0x10137E0C  je   0x10137EA3         ; 无命令对象：返回false
0x10137E14  mov  eax,[eax+0x20]     ; isCertification虚函数
0x10137E17  call eax
0x10137E1B  je   0x10137E28
0x10137E1D  mov  byte ptr [ebx+0x250],1
```

只有构造出认证对象并满足 `isCertification()`，才设置认证成功。该检查路径没有比较 `body.result` 或 `body.success`，因此在方法工厂缺项时改变这些值不会修复问题。

### 3. 500ms重发与现场一致

`CmdLoadState@ShSubDeviceProtocol_DCTCP`：`0x10138400`。

```text
0x10138430  cmp  byte ptr [ebx+0x250],0
0x10138437  jne  已认证处理
0x10138439  push 0x1F4              ; 500ms
0x1013843E  call Sleep
0x10138448  call [eax+0xA4]         ; Send_CertificationInfo
```

现场日志同样约每0.5秒一条Certification。TCP稳定、MES逐条回复，但无Device上报。

### 4. 旧YhLoop并非完全相同的接收实现

`IYhlLoopCmd::CreateCmd` 中，`0x101EDD4A—0x101EDDC1` 有显式字符串比较 `Certification`，匹配后创建认证对象。因此“旧模块能认证，新DCSeq不能”可以由命令工厂差异解释，不必先假设网络、心跳或MES踢连接。

## 建议给厂家的核查内容

> 我们对照了这次V3.1 Merge包：DCSeq持续每500ms发Certification，中控已逐条回复。静态分析发现ShSubDeviceProtocol_DCSeq使用的注册入口只注册了Init/Command/Config/Device，没有Certification；同包DCSimple在通用注册后额外注册了Certification，旧YhLoop也有认证分支。请重点检查DCSeq是否遗漏认证命令工厂注册，导致DecodeBuffer按strMethod查找时返回空对象、认证标志始终为false。ShDeviceFamily.dll的SHA256为14A29EECDB02522AEEA9ED74647CFB0B6D534ECABF1FC87EBB6EBF6563630E76，可先确认是否和现场版本一致。

建议厂家在源码修复DCSeq的认证注册，并补充认证成功/失败语义及测试。无需由中控伪装成Init、Config或Command来绕过认证；这些方法有业务含义。

建议验收：

1. 同一TCP连接中，Certification得到认可后停止500ms认证重发。
2. 正常上报真实Device状态，而非仅中控本地Connected投影。
3. 断线重连重新认证后仍可恢复状态上报。
4. Config/Command的验证另行进行，不把认证成功当作进样闭环验收。

## 临时文件处理

仅将根目录相关Sh*.dll/exe及ChromDataAcquire.exe提取到专用临时目录，用pefile/capstone静态读取。未运行厂家可执行程序、未安装软件、未更改压缩包。

临时目录：`artifacts/dcseq-zip-inspect-6fb9ef6c456a429aa1f98e051b8d10ab`。
完成后删除该目录内的提取DLL、临时分析脚本及Python缓存；保留本报告及原始ZIP，未来可从ZIP重新提取复核。

## 厂家补充包复核（2026-09-21 14:22）

用户提供 `res/补充.zip`，10890451字节，包含19个DLL/EXE；主要程序编译时间为当天14:06—14:14。补充包是控制电脑ShineLab的更新文件，不是中控MES更新。

新旧文件指纹：

| 文件 | 原包字节数 | 补充包字节数 | 补充包SHA256 |
|---|---:|---:|---|
| ShDeviceFamily.dll | 5092864 | 5102592 | 9E7638FBFCC3D07EA19DAC46127DDF34654C7D3BD4E44A15F4245A915EE8DB7E |
| ShDevicePlus.dll | 920064 | 920064 | 76C3A265EA849078C9447D1047AFF9C2A2D4B8FF1BE8ADFF91976E7486F440E0 |
| ShPipe.dll | 200192 | 200192 | ADE5331DA6ED089B1ECD4B9DE47950FA7A0ED7C0EF40143A9275E01C5A6247EA（与原包一致） |

已确认补充包新增此前缺失的Certification工厂注册：

- 新DCSeq虚表：`0x102E64FC`，+0x98槽指向`0x1013A960`。
- 注册函数仍包含Init/Command/Config/Device，并新增以下段落：

```text
0x1013ABB8 push 0xD
0x1013ABBA push 0x102E5F64       ; "Certification"
...
0x1013ABE6 call 0x1013C380
```

- `0x1013C380`在`this+0x1E0`工厂表中注册相应构造器。
- 同包DCSimple的注册入口`0x10138770`现在直接跳转`0x1013A960`，两模块共享补全后的通用注册路径。

结论：静态对比确认厂家已补上此前定位的注册缺项。是否彻底恢复通讯仍以控制电脑加载补丁后的实际握手和Device上报为准。

14:22本机检查：MES PID22844仍监听192.168.10.11:5500，回复模式为原版Result（body.result=Success，无strCode）；现有连接仍在持续认证，尚无用户确认客户端已更新的证据。

现场更新建议：关闭控制电脑ShineLab/AQ相关程序，备份将被替换的同名文件，按厂家要求将补充包“新建文件夹”内文件覆盖到实际软件安装目录（不要只新增一个同名子文件夹），避免仅替换单个DLL造成依赖混用。重新打开并确认DCSeq、STN61_01及192.168.10.11:5500后再连接；首先只验证认证和状态，暂不启动任务。

本次比较使用的临时目录`artifacts/dcseq-patch-inspect-da8e98d678bc4b5a83a8396012eb3f4f`在分析结束后删除；原始两个ZIP及本报告保留。未在本机安装或执行厂家软件。

## 补丁后现场复测：认证转状态成功（14:27—14:28）

用户确认厂家更新后重新连接。MES PID22844、认证回复模式Result保持不变。

- 14:27:11.416：192.168.10.108:64583连接，connectionId=`d16e6dbd8dd94547bb10a61acb0d57df`。
- 14:27:11.934/11.936：收到并回复两条Certification；body.result=Success、msg为空，回复不带strCode。
- 14:27:12.002：收到第一条Device；截至14:28:03共2条Certification、91条Device，没有持续认证重发、断线或解析异常。
- Device周期约0.55秒；实际样本为：

```json
{"strID":"7507686877508538368","strMethod":"Device","equipmentCode":"STN61_01","strCode":"","body":{"status":0}}
```

状态接口为online=true、status=0、Idle、hasActiveTask=false，此处Idle来自真实Device上报。

认证请求中strCode和channel仍为空，但普通Success回复后已开始状态上报，说明这两个空字段没有阻止本次认证。结合补丁静态差异，现场结果支持此前定位的认证注册缺项。

本次仅验证认证和基础状态链路。首批Device不含params，尚未看到压力、流量等测点；未下发Init/Config/Command，未验证真实进样及结果闭环。
