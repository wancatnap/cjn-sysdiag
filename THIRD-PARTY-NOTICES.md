# 第三方组件与素材清单

本项目以 **Apache License 2.0** 发布（见 `LICENSE`）。下面列出所有外部依赖、引用过的开源项目，
以及**刻意不包含**的内容。

维护原则：**能用思路就不用代码，能用公开规范就不用别人的数据。**
凡是带 copyleft 的来源，一律只借格式或做法，不复制内容。

---

## 一、随程序分发的依赖

| 组件 | 版本 | 许可证 | 用途 |
|---|---|---|---|
| Microsoft.Web.WebView2 | 1.0.2903.40 | BSD-3-Clause（Microsoft） | HTML 报告渲染 |
| System.Management | 8.0.0 | MIT | WMI 查询（设备、服务、可靠性） |
| System.Diagnostics.EventLog | 8.0.0 | MIT | 事件日志读取 |
| System.CodeDom | 8.0.0（传递依赖） | MIT | System.Management 的依赖 |
| .NET 8 运行时 | 8.0 | MIT | 单文件自包含发布 |

**WebView2 的两点说明**

1. 其 BSD 条款明确禁止用 Microsoft Corporation 或其贡献者的名义为衍生作品背书。
   本项目未获得也未声称获得微软的任何认证或认可。
2. **WebView2 运行时（Runtime）由操作系统侧安装，本程序不打包、不再分发其二进制**。
   如果目标机器没有安装 Runtime，报告渲染功能会降级，程序其余功能不受影响。

**.NET 运行时**的 MIT 许可要求保留版权声明：分发编译产物（`系统诊断台-独立版.exe`）时，
请连同本文件一起提供。

---

## 二、构建期工具（不随产物分发）

| 工具 | 许可证 | 用途 |
|---|---|---|
| Obfuscar | MIT | 发布前对程序集做字符串加密 |
| .NET SDK 8 | MIT | 编译 |

---

## 三、借鉴过思路的开源项目

以下项目**均未复制源代码**，只在设计层面参考。列出是为了致谢，也为了说明边界。

| 项目 | 许可证 | 参考了什么 | 边界 |
|---|---|---|---|
| notRequiem/VMAware | MIT | 机器码伪造检测的评分模型：按「可靠度 + 特异性」给分，再按误报概率打折 | 权重与四档阈值是本项目自行标定的 |
| microsoft/EventLogExpert | MIT | 日志视图的交互组织方式（时间轴直方图、就地查找） | 控件为自行实现 |
| farag2/Sophia-Script-for-Windows | MIT | 核对优化项的策略键与取值语义 | 只确认「用哪个键」，未复制其脚本 |
| Raphire/Win11Debloat | MIT | 同上，特别是 22H2 之后失效的键 | 同上 |
| System Informer（原 Process Hacker） | **GPL-3.0** | 句柄权限位的表结构：标准权限 / 对象专属权限 / 泛用权限分三张表，先展开 `GENERIC_*` 再查表 | **未复制代码**。表中常量取自 Windows SDK（`ntddk.h` / `winnt.h`）的公开接口定义，属接口事实而非创作 |
| al-khaser | **GPL-2.0** | 「枚举内核驱动服务再比对名单」的做法；虚拟机与伪造标识的常见占位值 | **未复制代码**。用到的 `"A M I"`、`"To be filled by O.E.M."`、`"Default string"`、`VMware` / `QEMU` / `innotek GmbH` 等字样，以及 `00:0C:29`、`08:00:27` 这类 MAC 前缀，均属事实性短语与公开技术标识，不受版权保护 |

> 为什么把 GPL 项目也列出来：这些项目在相关领域是事实标准，回避不了「参考」。
> 本项目采取的做法是——**只看它解决什么问题、按什么结构组织，代码与数据全部自己写**。
> 如果你在贡献代码时发现某个文件与上述项目高度雷同，请提 issue，那属于需要立即修正的合规问题。

### 三之二、延迟优化方法论参考（本轮新增）

这批仓库用于确定「该测什么、该怎么设计对照实验」，以及核对少量注册表键的语义。
**全部只看文档与思路，未复制任何脚本、注册表文件或配置数据**；新增代码均为本项目原创。

| 项目 | 许可证 | 参考了什么 | 本项目的做法 |
|---|---|---|---|
| Atlas-OS/Atlas | **GPL-3.0** | Playbook 逐项可开关的组织方式；`Check Atlas Health`「报告装了什么、之后哪些设置被改过」的思路 | 实验记录每轮拍下已生效优化项快照，两次对照时算差异。**未复制其 Playbook 或脚本** |
| ChrisTitusTech/winutil | 许可证以仓库 LICENSE 为准 | 手动逐项勾选、每项执行都写日志的做法 | 借鉴「逐项 + 记录」，未取其 `config/` 数据 |
| BoringBoredom/PC-Optimization-Hub | 资料汇总仓库 | 方法论原文：「不要一次盲改一堆设置，要有系统化测试方法并记录每次改动」；列出 LatencyMon、Timer Resolution、Bitsum LatencyTest 等验证工具 | 据此把重点放在**可验证测量**而非堆开关；明确本程序不是 LatencyMon |
| denis-g/windows10-latency-optimization | 文档/指南 | HPET、`disabledynamictick`、`useplatformtick`、MSI、中断优先级、计时器固定 0.5 ms 的推论；「参数要用不同值测试找最优」 | 已覆盖的键不重复加；固定 0.5 ms 需常驻进程，**明确不做** |
| meohunterr/MeoBoost | **GPL-3.0** | 面向游戏低延迟的优化分类（计时器 / DPC / IRQ / 电源） | 只参考分类，未复制 Python 代码 |
| ionuttbara/melody_windows | 许可证未在页面明确声明 | bcdedit 参数清单；**其 README 自述「执行后无法回滚」**，正是反面教材 | 只参考参数名；本软件所有写入项一律先记原值、可还原，**不做一键全开** |
| hellzerg/optimizer | 已弃用（转 OptimizerNXT） | C# GUI 优化器的功能分类 | 只参考分类，未复制源码 |
| **RigPolice Latency Toolbox**（cpu-parking-disabler / msi-mode-utility / timer-resolution-utility / interrupt-affinity-utility / gamedvr-fso-disabler / remove-hidden-devices） | **MIT** | **实测数据**：停核后突发线程唤醒 15.9 ms → 0.26 ms 而游戏帧率无变化；共享线音频控制器中断例程 1.9→1.0 次；GPU 改回传统线仅多约 1 微秒；计时器 Sleep(1) 默认 15.2 ms、最细 1.4 ms | **未复制其 PowerShell 代码**。只把上述实测结论写进「优化推论」并标注来源。<br>其「双 CCD X3D 关停核会变差」的提醒已落成代码里的硬拦截 |
| MMCSS / `SystemResponsiveness`、`Tasks\Games` | Windows 自身接口事实 | 注册表键与取值语义 | 取值按 Windows 自带配置与公开文档整理 |

**关于 LatencyMon**：它是内核驱动级 DPC/ISR 采集工具，本项目**不包含、不调用、不冒充**。
本软件的「延迟实测」测的是用户态 `Sleep(1)` 唤醒间隔分布，属代理指标，界面与报告均写明这一点。
需要内核级数据时，应使用 LatencyMon 或 Windows Performance Toolkit 做 ETW 跟踪。

### 三之三、优化推论条目与免重装精简（本轮新增）

「优化推论」窗口里的 22 条结论，每条都标注来源与证据等级。这里说明它们各自参考了什么：

| 主题 | 参考来源 | 本项目做了什么 |
|---|---|---|
| HPET / 动态滴答 / 中断优先级 | denis-g 指南、Atlas、WinUtil、Melody 的 bcdedit 清单 | 只核对「用哪个键」；结论里的代价与失效条件为本项目自行整理，未复制其正文 |
| MSI 中断 | denis-g 指南、PC-Optimization-Hub 引用的 MSI 规范 | 读取 `MSISupported`，区分「未启用 / 无 MSI 能力」 |
| 核心停放 / C-State | MeoBoost 的优化分类、Atlas 的电源策略、PC-Optimization-Hub | GUID 全部在本机 `powercfg /query` 实查；**未复制任何脚本** |
| MMCSS / SystemResponsiveness | WinUtil、Atlas、Melody | 只核对键与语义；代价说明为本项目自行整理 |
| SysMain / 遥测 / 网络限流 | Atlas、WinUtil、Hellzerg、Melody | 只借用「哪些服务/键属于哪一类」，取值按 Windows 自身行为核对 |
| 测量方法论 | PC-Optimization-Hub 的原文主张、Atlas 的 Check Health 思路 | 转成软件内的判定规则（阈值、样本数、归因提示），非复制实现 |
| 免重装精简的边界 | Atlas README（要求全新安装）、Microsoft DISM / Defender 文档 | 只按公开事实划边界；「做不到」的四项为本项目自行归纳 |
| Atlas 参考组 6 项 | Atlas 的 `src/playbook/Configuration/tweaks/`（GPL-3.0） | 只确认「这个键 Atlas 确实在用」，键名与取值按 Windows 自身语义核对；**代价说明全部为本项目自行整理**，Atlas 不写代价 |
| DMA 伪装判据 | Clubhouse AC《DMA hardware fingerprinting》公开研究、`gmh5225/awesome-game-security` 的 DMA 章节、PCILeech 生态的公开讨论 | 只用「哪几类信号有判别力」与「每档需要什么权限」的结论。**未复制任何检测规则或样本**；本程序只实现用户态可验证的四条，配置空间层明确不做 |
| PCIe 属性编号 | 本机反推（`--dma-keys` 枚举 + 与系统工具交叉验证） | 未采用任何文档或仓库里的编号。文档编号与实测不一致时以实测为准，见 README 里的记录 |
| DMA 探测工具（drvscan / EmuScan） | 公开仓库，需自签驱动、开测试签名模式 | **不集成、不调用**。它们走内核驱动读配置空间，本程序不装驱动；只借「用固件残留特征判断伪装」的思路 |

**免重装精简的实现独立性**：Appx 枚举与卸载、计划任务禁用、可逆性记录、运行时依赖包排除，
全部为本项目自行实现（`Services/SlimService.cs`），没有引用上述项目的脚本或数据文件。
其中「排除 WinAppRuntime / VCLibs / UI.Xaml 等运行时依赖」这条规则来自本机实测，
不是抄自任何项目。

---

## 四、格式与数据来源

| 来源 | 许可证 | 本项目的用法 |
|---|---|---|
| SigmaHQ/sigma | Sigma 规范：**公有领域**；SigmaHQ 规则库：**DRL 1.1** | **只使用 Sigma 规则格式**。`Services/SigmaRules.cs` 里的 37 条规则为本项目原创（中文标题、无 SigmaHQ 的 UUID 与 author 字段），不含其规则内容 |
| MoscaDotTo/Winapp2 | **CC BY-SA 4.0** | **只使用 `winapp2.ini` 的文件格式**。`Services/CleanerRules.cs` 的内置规则为本项目原创（自写说明、自分类），不含其条目 |

### 给贡献者的硬性提醒

这两个来源的**数据**带 copyleft——DRL 1.1 和 CC BY-SA 4.0 都要求衍生内容沿用同一许可。
它们的「格式」不受限，但「条目内容」受限。

**从 SigmaHQ 或 Winapp2 粘贴任何一条规则进来，整个项目的许可义务就会改变。请自己写。**

---

## 五、刻意不包含的东西

| 类别 | 说明 |
|---|---|
| **图片素材** | 一张都没有。手动调色的对比底图由用户自己选，只存在 `%LOCALAPPDATA%\SysDiag\color-reference.jpg`，不随源码或安装包分发；没选时用程序化生成的色卡 |
| **字体文件** | 不打包任何 ttf / otf。界面只引用系统字体名（Microsoft YaHei UI、Consolas），由操作系统提供 |
| **图标** | `Services/IconLibrary.cs` 里 41 个图标全部用 WPF 绘图 API 以代码绘制，未使用任何图标库或第三方素材 |
| **厂商 logo** | 无。界面中出现的 Steam / Epic / NVIDIA 等名称仅用于说明功能适配对象，属描述性使用 |
| **第三方可执行文件** | **不捆绑任何第三方二进制**。绝大多数能力改用 WMI / 注册表 / P/Invoke 自行实现（`msinfo32`、`services.msc`、`powercfg`、`devmgmt.msc`、`perfmon` 均不再调用）。**例外**见下方说明 |

### 关于唯一的例外：调用系统自带 PowerShell

「免重装精简」里，预装应用的枚举与卸载使用 `powershell.exe`（Windows 自带，非第三方分发）。
原因：Appx 的安装/卸载没有等价的稳定 Win32 API，绕过 PowerShell 只能依赖未公开的 COM 接口，
稳定性更差。

约束（都在代码里强制）：

- **不捆绑、不下载**任何 PowerShell 模块或脚本，只用系统自带的 `Get-AppxPackage` / `Remove-AppxPackage`；
- 命令通过 `-EncodedCommand` 传入，**参数不拼接用户输入或扫描结果字符串**；
- 脚本内只做「在已枚举对象里按 PackageFullName 精确匹配」，不执行远程脚本、不改执行策略（仅进程级 `-ExecutionPolicy Bypass`）；
- 用户在界面上必须逐项勾选并二次确认，默认全不勾。

其余窗口（延迟、停核、系统优化等）全部走 WMI / 注册表 / `powrprof.dll`，不调用任何 shell。

### 关于「关闭 Defender」：明确不实现，并说明为什么

有其他工具声称能「一键关闭 Windows Defender」。本软件**不提供该能力**，且不是能力不足：

| 它们的实现路径 | 依据 | 本项目的立场 |
|---|---|---|
| 官方策略键 / `Set-MpPreference` | 微软文档：`DisableAntiSpyware` 自 Windows 10 1903 起对消费版不再生效；实时保护与篡改防护只能由用户在安全中心手动关闭 | 官方路径本来就关不掉，声称能做到的必然走了下面两条 |
| 向 Windows 安全中心注册**假杀软**，诱使 Defender 按冲突逻辑自行退让（DefendNot 的做法，需注入 DLL 到微软签名的 `Taskmgr.exe` 以绕过 PPL） | 公开安全研究（Binary Defense、BleepingComputer）与 ATT&CK **T1562.001** | **属攻击技术，不实现**。微软已将该工具按恶意软件查杀 |
| 内核驱动 / 打补丁 | — | 破坏系统完整性，且随更新失效 |

所以「防护状态审查」做的是相反的事：判断防护有没有被别人悄悄关掉
（例如被动模式开着却没有第三方产品注册，那是假杀软注册被撤销后的残留）。
这是取证需求，也符合本项目「只读、可解释」的定位。

### 内核 DPC/ISR 采集的知识来源

ETW 的 `StartTrace` / `EnableTraceEx2` / `ProcessTrace` 使用方式参考微软官方文档
（[StartTrace function](https://learn.microsoft.com/windows/desktop/ETW/starttrace)、
[Example 15: Measuring DPC/ISR Time](https://learn.microsoft.com/windows-hardware/drivers/devtest/example-15--measuring-dpcisr-time)）
与 Geoff Chappell 对 `EVENT_TRACE_PROPERTIES` 布局的公开分析。
**未使用**任何第三方 ETW 库（如 TraceEvent），全部自行 P/Invoke 实现；
代码中所有结构偏移都经 `Marshal.OffsetOf` 校验并写进 `--kernel-diag` 输出。

---

## 六、商标声明

本项目与 Microsoft、腾讯、天美工作室群、《三角洲行动》开发商及发行商、
Valve、Epic Games、NVIDIA、AMD、Intel、Riot Games、Ubisoft、Blizzard Entertainment、
Electronic Arts 均**无任何隶属、合作或背书关系**。

文中出现的产品名称与商标归各自所有者，仅用于说明兼容性或功能适配对象。
