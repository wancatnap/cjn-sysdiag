using System.Collections.Generic;
using System.Linq;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 跨仓库的优化推论目录。
///
/// 收录标准：**至少两个独立来源重复出现，或 Windows 自身行为可查**。
/// 单一项目的孤例、以及明显带商业推广色彩的「神优化」，一律不收。
///
/// 每个条目的「限制」栏是重点：这些项目里最容易被忽略的就是代价与失效条件，
/// 把它们抄进软件却不写代价，等于把风险转嫁给用户。
/// </summary>
public static class OptimizationNoteCatalog
{
    public static IReadOnlyList<OptimizationNote> All { get; } = Build();

    public static List<OptimizationNote> OfModule(NoteModule module)
        => All.Where(n => n.Module == module).ToList();

    private static List<OptimizationNote> Build() => new()
    {
        // ============================================================ 延迟优化

        new OptimizationNote
        {
            Id = "lat-hpet",
            Module = NoteModule.Latency,
            Title = "HPET 不是「开了就更快」，多数现代平台应当保持平台默认",
            Conclusion = "强制 HPET（useplatformclock=true）会增加计时开销；现代平台走 TSC 延迟更低。除非实测证明强制 HPET 在你的机器上抖动更小，否则不要开。",
            Premise = "结论成立于 Win10 1803 之后的平台，且 CPU 支持 invariant TSC（近十年的 Intel/AMD 基本都支持）。",
            Basis = "denis-g 指南的「关闭 HPET」一节明确建议关闭；Windows 自身在 Win10 起默认使用 TSC + 动态计时；两家优化脚本（Atlas / WinUtil）都不强制开启 HPET。本软件延迟优化窗口把它做成「两种模式」而不是单向开关，并在读不到时如实标注。",
            Scope = "适合已经出现帧时间抖动、且怀疑计时源问题的机器，用来做单变量对照。普通用户保持平台默认即可。",
            Limits = "没有实测对比就不要动。强制 HPET 在某些老 Intel 平台（如 4 代以前）确实能稳住帧时间，但在新平台上通常是负收益；而且改完必须重启，无法即时验证。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "denis-g/windows10-latency-optimization", "Atlas-OS/Atlas", "ChrisTitusTech/winutil" },
            RelatedCard = "latency"
        },

        new OptimizationNote
        {
            Id = "lat-timer-resolution",
            Module = NoteModule.Latency,
            Title = "计时器分辨率读到 0.5ms，不等于你的延迟就是 0.5ms",
            Conclusion = "NtQueryTimerResolution 返回的是系统当前请求的最小分辨率，不代表线程实际唤醒间隔。本机实测：分辨率 0.600 ms，而 Sleep(1) 的平均唤醒间隔是 15.4~15.6 ms。",
            Premise = "任何 Windows 版本都成立：分辨率是「最小粒度」，实际唤醒还受时钟滴答、调度、电源状态影响。",
            Basis = "本软件在自检与本机采样中实测（约 195 个样本，平均 15.4 ms / P99 15.7 ms，计时器分辨率 0.600 ms）。" +
                    "RigPolice 的 timer-resolution-utility 在同一台 Win11 机器上测得 Sleep(1) 默认 15.2 ms、最细分辨率下 1.4 ms——" +
                    "结论方向一致：默认滴答就是 15.6 ms 量级，锁到 0.5 ms 也只能把唤醒降到 1 ms 上下，不会变成 0.5 ms。" +
                    "denis-g 指南也强调「值会不断变化，需要实测确认」，而不是改完就当生效。",
            Scope = "所有声称「把计时器锁到 0.5ms 就降延迟」的场景，都应先看实测唤醒间隔再下结论。",
            Limits = "Windows 10 2004 之后，timeBeginPeriod 的影响范围收窄到调用进程，不再全局生效；要长期锁定必须常驻进程，本软件不常驻，因此只报告不锁定。",
            Confidence = NoteConfidence.Measured,
            EvidenceScope = NoteScope.SingleMachine,
            ScopeNote = "本机约 195 样本 + RigPolice 单台 Win11",
            Sources = new() { "本机实测（LatencyProbeService）", "denis-g/windows10-latency-optimization",
                "RigPolice Timer Resolution Utility（MIT，附 Sleep(1) 基准）" },
            RelatedCard = "latency-lab"
        },

        new OptimizationNote
        {
            Id = "lat-dynamictick",
            Module = NoteModule.Latency,
            Title = "关闭动态滴答（disabledynamictick）能减少定时器合并，但会增加待机功耗",
            Conclusion = "disabledynamictick=yes 让内核不再把多个定时器合并到一次唤醒，理论上响应更稳；代价是空闲时也更频繁唤醒，笔记本续航变短。",
            Premise = "对延迟敏感、且以插电使用为主的台式机才有明显意义。",
            Basis = "denis-g 指南与 Melody 的 bcdedit 清单都包含该项；本软件延迟优化窗口提供开关，并写明「关掉后待机功耗略升」。",
            Scope = "竞技类游戏、音视频实时处理等对唤醒抖动敏感的场景，可做单变量对照。",
            Limits = "必须重启才生效；对绝大多数日常使用感知不到。与 HPET、C-State 同时改会导致无法归因。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "denis-g/windows10-latency-optimization", "ionuttbara/melody_windows" },
            RelatedCard = "latency"
        },

        new OptimizationNote
        {
            Id = "lat-msi",
            Module = NoteModule.Latency,
            Title = "MSI 中断减少共享线中断的排队，是驱动级延迟里少数能直接改的部分",
            Conclusion = "把支持 MSI 的设备从传统线中断切到消息信号中断，可减少中断共享与抢占；对 GPU、网卡这类高频设备意义最大。",
            Premise = "设备本身与驱动必须支持 MSI。键不存在通常表示该设备不支持，不代表「没开」。",
            Basis = "denis-g 指南专门有「启用 MSI mode」一节；PC-Optimization-Hub 在主板条目里引用 MSI 规范；本软件读取 Enum 下 MSISupported 并区分「未启用 / 无 MSI 能力」。\n" +
                    "RigPolice 的实测把收益说清楚了：两个共享同一条线的音频控制器，每个中断要跑 1.9 次中断例程；切到 MSI 后降到 1.0 次。" +
                    "而本来就处于 MSI 的 GPU，强行改回传统线，中断耗时几乎不变（约多 1 微秒）。",
            Scope = "高频中断设备（显卡、网卡、NVMe）出现 DPC 延迟偏高时值得检查。",
            Limits = "极少数老驱动在 MSI 下会蓝屏；改完必须重启验证，出问题需进安全模式改回 0。虚拟机与部分 USB 控制器不支持。\n" +
                     "⚠ 不要让显卡「热重启」生效：RigPolice 实测在设备管理器里禁用/启用显示适配器会直接黑屏冻结、无崩溃转储，必须硬复位。" +
                     "改完用正常重启，并确认鼠标轮询率没掉。",
            Confidence = NoteConfidence.MultiSource,
            EvidenceScope = NoteScope.SingleMachine,
            ScopeNote = "RigPolice 单台机器，中断例程 1.9 → 1.0 次",
            Sources = new() { "denis-g/windows10-latency-optimization", "BoringBoredom/PC-Optimization-Hub",
                "RigPolice MSI Mode Utility（MIT，含中断成本实测）" },
            RelatedCard = "latency"
        },

        new OptimizationNote
        {
            Id = "lat-irq8",
            Module = NoteModule.Latency,
            Title = "提高 IRQ8（系统时钟）优先级：理论上有利，现代平台上多数测不出差异",
            Conclusion = "IRQ8Priority=1 把时钟中断排在普通设备中断之前。理论成立，但 Win10/11 普遍改用 TSC 计时后，实际影响往往低于测量噪声。",
            Premise = "只在「确实还在用 RTC/HPET 作为主计时源」的平台上可能有效。",
            Basis = "denis-g 指南的「中断优先级」一节给出该键；但同一份指南也强调计时源与平台相关。多个优化脚本对是否启用此项并不一致。",
            Scope = "拿去做单变量对照实验可以；不适合作为默认优化项。",
            Limits = "配置不当会让 USB/网卡中断响应变慢。本软件把它归到「谨慎」，并明确写「只适合做单变量实验」。",
            Confidence = NoteConfidence.Community,
            Sources = new() { "denis-g/windows10-latency-optimization" },
            RelatedCard = "system-optimize"
        },

        new OptimizationNote
        {
            Id = "lat-core-parking",
            Module = NoteModule.Latency,
            Title = "关闭核心停放（Core Parking）：降的是唤醒延迟，涨的是功耗与温度",
            Conclusion = "核心停放会让空闲核心进入停放状态，被调度时需要先唤醒。把停放最小/最大核心数都设为 100%，等于所有核心常开，可消除这部分唤醒延迟。",
            Premise = "结论成立于「调度器确实在使用核心停放」的机器上。若 BIOS 已关闭停核、或 CPU 走硬件自主调频（Intel HWP / AMD CPPC），改电源项可能不改变实际行为。",
            Basis = "Windows 处理器电源管理子组（54533251-…）下的 CPMINCORES / CPMAXCORES 是官方设置项，本机 powercfg 实查确认；MeoBoost 与 Atlas 的电源策略都涉及停核；PC-Optimization-Hub 把「核心停放」列入需要按平台验证的项目。\n" +
                    "RigPolice 在 i9-14900F / Win11 上做过对照：原厂平衡方案空闲停放 31/32 线程，突发负载最后一个线程晚醒 15.9 ms；关闭停核后降到 0.26 ms（约快 50 倍），但 CS2 平均帧率 770 → 769，差异在波动范围内。",
            Scope = "台式机、插电、追求帧时间稳定的场景值得试；笔电或长期高负载机器要权衡温度。",
            Limits = "① 空闲功耗与温度上升，笔记本续航明显变短；② 现代 CPU 的硬件调度可能覆盖电源项，改了看不到变化；③ 必须与 C-State、MMCSS 分开测，否则无法归因；④ 只改当前电源方案，切换方案会回到模板值。\n" +
                     "⚠ 双 CCD 的 X3D 处理器（7900X3D / 7950X3D / 9900X3D / 9950X3D）不要关停核：AMD 的 3D V-Cache 优化器正是靠停放非缓存 CCD 把游戏锁在带缓存的 CCD 上，全部唤醒会让游戏跑到错误的 CCD 上，可能更差。单 CCD 的 7800X3D / 9800X3D 不受影响。",
            Confidence = NoteConfidence.MultiSource,
            EvidenceScope = NoteScope.SingleMachine,
            ScopeNote = "RigPolice i9-14900F / Win11，唤醒 15.9 → 0.26 ms",
            Sources = new() { "meohunterr/MeoBoost", "Atlas-OS/Atlas", "BoringBoredom/PC-Optimization-Hub",
                "RigPolice Latency Toolbox（MIT，i9-14900F 实测）" },
            RelatedCard = "core-parking"
        },

        new OptimizationNote
        {
            Id = "lat-cstate",
            Module = NoteModule.Latency,
            Title = "禁用处理器空闲（C-State）：有效但代价最直接的一项",
            Conclusion = "IDLEDISABLE=1 让核心不再进入深度空闲，唤醒几乎无延迟；代价是空闲功耗、发热与风扇噪音全面上升。",
            Premise = "只对「唤醒延迟确实构成瓶颈」的场景有意义；日常办公完全不需要。",
            Basis = "IDLEDISABLE 是处理器电源管理子组下的官方设置（本机 powercfg 实查确认）；denis-g 指南与多份延迟资料都把 C-State 作为唤醒延迟来源之一；PC-Optimization-Hub 提醒这类改动要按平台实测。",
            Scope = "插电的台式机、竞技游戏、实时音频处理，且能接受更高温度时。",
            Limits = "再深的 C-State（C6/C7 以下）由固件控制，软件改不了，所以「完全关掉 C-State」做不到；笔记本上禁用会显著缩短续航并可能触发降频。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "denis-g/windows10-latency-optimization", "meohunterr/MeoBoost", "BoringBoredom/PC-Optimization-Hub" },
            RelatedCard = "core-parking"
        },

        new OptimizationNote
        {
            Id = "lat-mmcss",
            Module = NoteModule.Latency,
            Title = "MMCSS 保留带宽：降到 0 会让前台更顺，但后台可能被饿死",
            Conclusion = "SystemResponsiveness 默认 20，表示给低优先级后台任务保留的 CPU 带宽百分比。设为 0 会把带宽全给多媒体/前台线程。",
            Premise = "机器上确实跑着需要低延迟的多媒体/游戏线程；否则改了也没有可感知收益。",
            Basis = "MMCSS 是 Windows 自带调度服务，Tasks\\Games 与 SystemResponsiveness 是公开配置项；WinUtil、Atlas、Melody 都调整这一类参数；本软件在「延迟与调度」组提供该项。",
            Scope = "游戏、音视频实时处理。配合「延迟实测」做单变量对照最合适。",
            Limits = "后台转码、下载、备份会明显变慢；满载时音频可能爆音。不要与「游戏任务高优先级」同时开，否则无法判断是哪一项起作用。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "ChrisTitusTech/winutil", "Atlas-OS/Atlas", "ionuttbara/melody_windows" },
            RelatedCard = "system-optimize"
        },

        new OptimizationNote
        {
            Id = "lat-priority-separation",
            Module = NoteModule.Latency,
            Title = "前台优先（Win32PrioritySeparation）：改善的是切窗口手感，不是帧率",
            Conclusion = "把时间片策略调向「前台更多、可变、带加成」，可让当前焦点程序响应更跟手；它不改变 GPU 渲染路径，因此不会直接提升帧率。",
            Premise = "多任务同时跑、且经常切换窗口的场景才有感知。",
            Basis = "该键属 Windows 调度器公开参数；denis-g 指南与多个优化脚本给出取值（38 = 0x26 常见）；本软件「响应速度」组提供该项并写明后台会变慢。",
            Scope = "桌面交互频繁、后台有轻负载任务的机器。",
            Limits = "后台任务（编码、渲染、下载）会变慢；服务器或长期跑批的机器不要用。与「CPU 调度档位」是同一族的设置，别重复叠加。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "denis-g/windows10-latency-optimization", "Raphire/Win11Debloat" },
            RelatedCard = "system-optimize"
        },

        new OptimizationNote
        {
            Id = "lat-gamebar",
            Module = NoteModule.Latency,
            Title = "关 Game Bar / GameDVR：省的是后台录制开销，抖动改善通常很小",
            Conclusion = "关闭游戏录制与 Game Bar 覆盖层，可减少后台捕获与叠加层带来的帧时间抖动；但它不是「提帧」手段。",
            Premise = "系统确实在后台录制（GameDVR 开启）时收益最大；本来就没开录制，改了几乎无感。",
            Basis = "denis-g 指南有「关闭 GameBar、GameDVR」一节；Atlas 与 WinUtil 都把其列入默认精简；本软件在系统调优里提供独立开关（不并入一键优化）。",
            Scope = "使用录屏/叠加层的玩家，或怀疑叠加层导致卡顿的机器。",
            Limits = "关掉后 Xbox Game Bar 的录屏、截图、性能面板都不能用；Xbox 手柄的部分功能受影响。不要为了「提帧」去关它。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "denis-g/windows10-latency-optimization", "Atlas-OS/Atlas", "ChrisTitusTech/winutil" },
            RelatedCard = "system-tune"
        },

        new OptimizationNote
        {
            Id = "lat-usb-suspend",
            Module = NoteModule.Latency,
            Title = "USB / HID 选择性暂停：键盘鼠标的「第一下没反应」常来自这里",
            Conclusion = "外设空闲后被系统挂起，再唤醒需要时间。关闭 USB 选择性暂停与 HID 省电选项，可消除这类唤醒延迟。",
            Premise = "症状确实是「空闲后第一次输入有延迟」；如果是一直都延迟，问题在别处。",
            Basis = "denis-g 指南的「设备省电」部分；Windows 电源方案的 USB 设置子组为公开项；本软件读取 PowerReadACValueIndex 与 HID 的 SelectiveSuspendEnabled。",
            Scope = "键鼠、手柄、音频接口等对唤醒敏感的外设。",
            Limits = "待机功耗上升，笔记本更明显；部分无线外设的省电策略在驱动里，改系统项无效。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "denis-g/windows10-latency-optimization", "BoringBoredom/PC-Optimization-Hub",
                "RigPolice MSI Mode Utility（MIT，含中断成本实测）" },
            RelatedCard = "latency"
        },

        new OptimizationNote
        {
            Id = "lat-measurement",
            Module = NoteModule.Latency,
            Title = "单次对照不构成因果：至少重复 3 轮，且一次只改一项",
            Conclusion = "延迟数据的波动通常大于多数优化带来的真实收益。没有重复测量与单变量控制，「改完更低了」很可能只是噪声。",
            Premise = "任何自测都成立——包括本软件自己的「延迟实测」。",
            Basis = "PC-Optimization-Hub 的方法论明确要求「不要一次改一堆设置，要有系统化测试方法并记录每次改动」；Atlas 的 Check Health 会报告「装了什么、之后哪些设置变过」；本软件的对照判定把阈值设为基线 P99 的 15%，样本不足 30 直接判「样本不足」。",
            Scope = "所有打算用数据证明「优化有效」的场合。",
            Limits = "软件只能提示，不能替用户保证实验条件一致（温度、后台负载、电源状态都会变）。看到「复测更低」也不要直接当成结论。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "BoringBoredom/PC-Optimization-Hub", "Atlas-OS/Atlas", "本软件对照判定规则" },
            RelatedCard = "latency-lab"
        },

        new OptimizationNote
        {
            Id = "lat-read-benchmarks",
            Module = NoteModule.Latency,
            Title = "别人的实测数据只能借方向，不能借数值",
            Conclusion = "社区里「关掉 X 让延迟从 15.9ms 降到 0.26ms」这类数字，来自某一台特定机器的特定时刻。" +
                         "方向通常可参考，绝对值换到你的平台上往往差一个数量级。",
            Premise = "只要结论里出现具体数值，就必须同时看：什么 CPU、什么系统版本、测了几次、有没有对照组、后台在跑什么。",
            Basis = "本软件引用的 RigPolice 数据来自 i9-14900F + Win11 单机；Atlas 的收益有相当部分来自删组件与改镜像，" +
                    "在运行时无法复现同等幅度；PC-Optimization-Hub 自己就反复强调「不同平台结论会反转」。" +
                    "本机实测也出现过同类偏差：计时器分辨率读数 0.600 ms，而实际唤醒间隔 15.4~15.6 ms，" +
                    "只看参数会得出完全相反的判断。",
            Scope = "读任何优化工具的宣传数据、社区测试帖、以及本软件的推论条目时都适用。",
            Limits = "本条不是「别信数据」，而是「数据只能在自己的平台上复现后才算数」。" +
                     "所以软件把测量能力做进产品里，而不是把别人的数字抄成结论。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "BoringBoredom/PC-Optimization-Hub", "RigPolice Latency Toolbox（单机数据示例）",
                "本机实测（分辨率与唤醒间隔不一致）" },
            RelatedCard = "latency-lab"
        },

        // ============================================================ 系统优化

        new OptimizationNote
        {
            Id = "sys-sysmain",
            Module = NoteModule.System,
            Title = "SysMain（原 Superfetch）在 SSD 机器上关掉更划算",
            Conclusion = "SysMain 会预读常用程序到内存。机械盘时代它明显提速；SSD 随机读已经很快，它的收益下降，而内存与后台磁盘活动增加。",
            Premise = "系统盘是 SSD / NVMe。机械盘上关掉反而更慢。",
            Basis = "Atlas 与 WinUtil 都把 SysMain 列为可精简服务；微软自身在部分 SKU 上也会按磁盘类型调整其行为。本软件在「常用开关」组提供该项并写明「机械盘请保留」。",
            Scope = "SSD 系统盘、内存不大（8~16G）的机器。",
            Limits = "关掉后常用程序首次启动可能略慢；机械盘机器不要关。这是系统优化，不是延迟优化——它不改变中断/调度时序。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "Atlas-OS/Atlas", "ChrisTitusTech/winutil" },
            RelatedCard = "system-optimize"
        },

        new OptimizationNote
        {
            Id = "sys-telemetry",
            Module = NoteModule.System,
            Title = "减少遥测：省的是后台活动与隐私，不是延迟",
            Conclusion = "关闭诊断数据、CEIP、广告 ID 等，可减少后台上报与磁盘写入；对延迟没有可验证的改善。",
            Premise = "关注隐私或后台资源占用时值得做；指望它降延迟会失望。",
            Basis = "Atlas、WinUtil、Melody、Hellzerg 都提供同类开关；微软公开文档说明各诊断数据级别的内容。",
            Scope = "所有在意隐私的用户；低配机器也能减少一点后台活动。",
            Limits = "关得太狠会影响反馈中心、部分预览体验功能；企业环境可能受组策略覆盖。不要把它当延迟优化。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "Atlas-OS/Atlas", "ChrisTitusTech/winutil", "hellzerg/optimizer" },
            RelatedCard = "system-optimize"
        },

        new OptimizationNote
        {
            Id = "sys-network-throttling",
            Module = NoteModule.System,
            Title = "NetworkThrottlingIndex 管的是多媒体程序的网络包处理，不是「网络延迟」",
            Conclusion = "该键限制多媒体程序每秒处理的网络包数（默认 10）。设为 0xFFFFFFFF 可解除限制，但它不降低 ping、也不改善丢包。",
            Premise = "只在跑音视频流、且确实受该限流影响时有意义。",
            Basis = "该键属 MMCSS 的 SystemProfile，是公开配置；WinUtil 与多个优化脚本使用它。很多教程把它错写成「降低网络延迟」。",
            Scope = "本地音视频流、游戏内语音等场景。",
            Limits = "对网页、下载、游戏联机的延迟没有直接作用；把它当「网络加速」是误用。真正的网络问题应看丢包/路由，用本软件的网络诊断。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "ChrisTitusTech/winutil", "Windows MMCSS 配置项" },
            RelatedCard = "system-optimize"
        },

        new OptimizationNote
        {
            Id = "sys-vbs",
            Module = NoteModule.System,
            Title = "关 VBS / 内存完整性：可能提性能，但这是拆安全防线",
            Conclusion = "基于虚拟化的安全性（VBS）与 HVCI 会带来虚拟化开销，部分游戏能测到帧率提升；但关闭后内核级防护显著减弱。",
            Premise = "只在你清楚风险、且这台机器只用来玩游戏的场合考虑。",
            Basis = "Atlas 把 CPU 缓解与核心隔离列为「可选安全项」并公开后果；微软文档说明 VBS/HVCI 的防护范围。本软件把该项归为「高风险」并要求二次确认。",
            Scope = "专用游戏机、可接受安全折衷的机器。",
            Limits = "关闭后易受内核级恶意软件攻击；某些反作弊反而不允许关闭；改完必须重启。日常用机、办公机不要关。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "Atlas-OS/Atlas", "Microsoft VBS 文档" },
            RelatedCard = "system-optimize"
        },

        new OptimizationNote
        {
            Id = "sys-ultimate-plan",
            Module = NoteModule.System,
            Title = "「卓越性能」不是延迟优化，它只是把处理器状态钉在高位",
            Conclusion = "卓越性能方案把最小处理器状态设为 100%，减少降频；它不改变中断、调度策略，也不保证帧率更高。",
            Premise = "插电、散热足够的台式机；笔记本上基本是负收益。",
            Basis = "该方案由 powercfg 复制隐藏模板生成（本软件 PowerPlanService 实现）；denis-g 指南讨论电源方案时强调要配合实测；PC-Optimization-Hub 提醒功耗与温度是硬代价。",
            Scope = "台式机、需要稳定高频的场景。",
            Limits = "空闲功耗与温度显著上升；笔记本续航大幅缩短；与「关闭停核」叠加时收益无法区分。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "denis-g/windows10-latency-optimization", "BoringBoredom/PC-Optimization-Hub" },
            RelatedCard = "power-plans"
        },

        new OptimizationNote
        {
            Id = "sys-mem-standby",
            Module = NoteModule.System,
            Title = "清空待机列表能立刻抬高「可用内存」，但对游戏帧率基本没有帮助",
            Conclusion = "待机列表是缓存，不是被谁占着不放。清空后可用内存立刻变大，代价是这些页下次要用得重新从磁盘读；" +
                         "游戏要用的资源本来就在缓存里，清掉它反而是负收益。",
            Premise = "Windows 10/11 默认内存管理。GlobalMemoryStatusEx 的「可用」已经包含待机页——" +
                      "也就是说待机缓存本来就算「可用」，并不存在「缓存占满了导致新分配失败」这回事（除非已提交接近上限）。",
            Basis = "Windows Internals 对 standby list 的定义（可立即回收的缓存页）；RAMMap 把 Empty Standby List 明确标注为会影响性能；" +
                    "本机实测（31.6 GB 内存）：待机 12.8 GB、可用 16.0 GB、占用 15.2 GB，四段合计与物理总量误差 0.0%。",
            Scope = "只有在「已提交 / 上限」逼近 90% 以上、或可用物理长期低于 2% 且已经开始换页时才值得试一次，" +
                    "并且要用本软件的延迟实测做前后对照。普通 16 GB 以上机器日常不需要动。",
            Limits = "效果是一次性的：SysMain 会慢慢把缓存建回来。不减少任何提交量，任务管理器里「已提交」那一格不会变。" +
                     "清完立刻打开大型游戏会明显变慢，因为要重新读盘。",
            Confidence = NoteConfidence.Documented,
            EvidenceScope = NoteScope.SingleMachine,
            ScopeNote = "31.6 GB / Win11 单机四段构成实测",
            Sources = new() { "Windows Internals（standby list）", "Sysinternals RAMMap",
                "本机实测（MemoryOptimizerService --mem-probe）" },
            RelatedCard = "memory"
        },

        new OptimizationNote
        {
            Id = "sys-mem-empty-working-set",
            Module = NoteModule.System,
            Title = "「内存优化」让数字变好看的主要手段是 EmptyWorkingSet，它不释放任何内存",
            Conclusion = "EmptyWorkingSet 只是把进程的工作集页推进待机列表，进程下次访问立刻换回来。" +
                         "任务管理器里的数字会变小，物理内存占用几乎不变，有时还会因为反复缺页更卡。",
            Premise = "对任何用户态进程都成立；不需要管理员也能对自己进程调用，对他人进程需要相应权限。",
            Basis = "psapi!EmptyWorkingSet 的官方说明是「removes as many pages as possible from the working set」，" +
                    "并不承诺释放；RAMMap 的 Empty Working Sets 是同一件事；本机实测回收 60 个进程后可用内存变化在 0 附近。",
            Scope = "本软件把它做成可选动作，并在确认框里直接写明「基本只改数字」，不做成一键加速的主按钮。",
            Limits = "对系统关键进程调用会引起大面积缺页，本软件直接禁止勾选（System / dwm / csrss / lsass 等）。" +
                     "不减少提交；Windows 在几秒内就把页换回来，所以效果不可持续。",
            Confidence = NoteConfidence.Documented,
            EvidenceScope = NoteScope.SingleMachine,
            ScopeNote = "本机 60 个进程回收前后可用内存对比",
            Sources = new() { "Microsoft psapi EmptyWorkingSet 文档", "Sysinternals RAMMap" },
            RelatedCard = "memory"
        },

        new OptimizationNote
        {
            Id = "sys-mem-compression",
            Module = NoteModule.System,
            Title = "关掉内存压缩通常更差，尤其是 16 GB 以下的机器",
            Conclusion = "内存压缩把不常用页压进内存而不是写进页面文件，减少磁盘往返。关掉它省的那点 CPU，" +
                         "换来的往往是更多的页面文件读写与更长的加载时间。",
            Premise = "Windows 10 1709 之后默认开启；判定依据是「Memory Compression」进程是否存在，或注册表 EnableMemoryCompression。",
            Basis = "微软在 Win10 起默认启用压缩内存并把它算作内存管理的一部分；Atlas 与 WinUtil 的清单里都不关它；" +
                    "部分社区脚本关它是为了省 CPU，但没有给出对照数据。",
            Scope = "本软件只做只读展示，不提供开关——关掉之后如果变差，用户很难自己关联回来。",
            Limits = "开启时会占一点 CPU（压缩/解压），在极端 CPU 受限的场景理论上可能有影响，但优先级远低于电源与停核设置。" +
                     "精简系统上这个进程可能不存在，此时界面不显示该项，而不是报「已关闭」。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "Microsoft 内存管理文档", "Atlas-OS/Atlas", "ChrisTitusTech/winutil" },
            RelatedCard = "memory"
        },

        // ============================================================ 免重装精简

        new OptimizationNote
        {
            Id = "slim-what-atlas-does",
            Module = NoteModule.Debloat,
            Title = "AtlasOS 的效果有一半来自「改镜像」，运行时只能做到另一半",
            Conclusion = "Atlas 通过 Playbook 在部署阶段改镜像：删组件、预置策略、去预装应用。运行时不重装能做的只有：卸载预装 Appx、禁用计划任务、禁用可选功能、改服务与策略。",
            Premise = "目标机器已经装好 Windows，且不愿重装。",
            Basis = "Atlas 官方 README 明确要求全新安装（needs a fresh installation）；其包体是 PowerShell + 数据定义，在部署阶段运行。本软件把这些操作拆成运行时等价项，并单独列出「做不到」的部分。",
            Scope = "不想重装、但想减少预装与后台活动的用户。",
            Limits = "运行时方案无法删除 WinSxS 组件、无法彻底移除 Edge/Defender、无法改安装介质。宣称「不重装达到完全同等效果」是不诚实的，本软件不这么写。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "Atlas-OS/Atlas", "ChrisTitusTech/winutil", "hellzerg/optimizer" },
            RelatedCard = "slim"
        },

        new OptimizationNote
        {
            Id = "slim-appx",
            Module = NoteModule.Debloat,
            Title = "卸载预装 Appx：可逆性取决于「从哪装回来」",
            Conclusion = "对当前用户卸载 Appx 可立即释放空间、减少后台；需要时多数能从 Microsoft Store 重装，但部分应用已被下架，属于「难恢复」。",
            Premise = "卸载的是常见预装/推广应用，不是系统组件（框架包、资源包不能动）。",
            Basis = "Atlas 与 WinUtil 都提供预装应用清单；微软文档说明 Remove-AppxPackage 与 Remove-AppxProvisionedPackage 的区别（后者影响新用户）。",
            Scope = "新装系统、想清掉推广应用的机器。",
            Limits = "依赖该应用的功能会失效（例如「手机连接」对应的跨设备功能）；Store 下架后无法恢复；不要批量卸载不认识的包。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "Atlas-OS/Atlas", "ChrisTitusTech/winutil" },
            RelatedCard = "slim"
        },

        new OptimizationNote
        {
            Id = "slim-tasks",
            Module = NoteModule.Debloat,
            Title = "禁用遥测计划任务：可逆性最好的一类精简",
            Conclusion = "把 CEIP、兼容性评估、错误上报等计划任务禁用，可减少后台上报；随时能原样启用回来。",
            Premise = "只禁用明确的遥测/上报类任务，不动系统维护任务（磁盘检查、系统还原点、更新编排）。",
            Basis = "WinUtil 与 Atlas 都按任务路径处理这一类；本软件在「免重装精简」里按内置白名单逐条展示现状，禁用前不需要改注册表。",
            Scope = "所有用户；尤其适合低配机器。",
            Limits = "禁用「兼容性评估器」后，Windows 的兼容性提示可能变少；禁用错误报告排队只影响上报，不影响本地崩溃记录。",
            Confidence = NoteConfidence.MultiSource,
            Sources = new() { "ChrisTitusTech/winutil", "Atlas-OS/Atlas" },
            RelatedCard = "slim"
        },

        new OptimizationNote
        {
            Id = "slim-winsxs",
            Module = NoteModule.Debloat,
            Title = "WinSxS 只能「清理可回收的旧版本」，不能像镜像那样删组件",
            Conclusion = "运行时的组件存储清理只能删被新版本取代的副本；任意删组件会导致更新失败与功能报错，且不可回滚。",
            Premise = "任何已安装的 Windows 都成立。",
            Basis = "微软文档明确说明 WinSxS 使用硬链接、不能手动删除；DISM /StartComponentCleanup 只清可回收部分。Atlas 在镜像阶段做的裁剪不等价。",
            Scope = "系统盘空间紧张时，可做可回收清理。",
            Limits = "本软件不提供「强删 WinSxS」按钮；那类操作一旦出错只能重装。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "Microsoft DISM 文档", "Atlas-OS/Atlas" },
            RelatedCard = "slim"
        },

        new OptimizationNote
        {
            Id = "slim-defender-edge",
            Module = NoteModule.Debloat,
            Title = "Defender 与 Edge 在运行时删不掉，也不该假装能删",
            Conclusion = "Defender 受篡改防护保护，关闭会被系统恢复；Edge 与 WebView2、系统帮助深度绑定，运行时删除会破坏依赖它的程序。",
            Premise = "任何正常运行、保持更新的 Windows 10/11。",
            Basis = "微软对 Defender 篡改防护的说明；Atlas 自己也只是在镜像阶段处理，且仍保留 WebView2。本软件不提供这两个删除操作。",
            Scope = "所有用户。",
            Limits = "任何声称「一键彻底删除 Defender / Edge」的工具，要么依赖未公开漏洞，要么会让系统处于不安全或不可更新状态。",
            Confidence = NoteConfidence.Documented,
            Sources = new() { "Microsoft 安全文档", "Atlas-OS/Atlas" },
            RelatedCard = "slim"
        }
    };
}
