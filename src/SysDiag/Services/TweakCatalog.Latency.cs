using System.Collections.Generic;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 第三批条目：延迟与调度。
///
/// 这批的定位不是「再来一堆开关」，而是给「延迟实测 / 对照实验」提供**单变量**素材：
/// 每一项只动一处配置，改一项 → 测一轮，差异才有机会归因。
///
/// 取值依据：
/// - MMCSS（Multimedia Class Scheduler Service）的 SystemResponsiveness 与 Tasks\Games
///   任务定义，是 Windows 自带的调度参数，WinUtil / AtlasOS / Melody 都用到；
///   SystemResponsiveness 默认 20，含义是「为低优先级后台任务保留的 CPU 带宽百分比」。
/// - ForegroundLockTimeout 对应 SystemParametersInfo 的前台锁定超时设置。
/// - denis-g 指南与 PC-Optimization-Hub 都强调：这类参数必须逐项测试、记录，
///   不能一次堆完就宣称有效。所以这里每一项都写明代价与「需实测」。
///
/// 只借鉴「用哪个键、什么含义」，没有复制任何脚本代码。许可证见 THIRD-PARTY-NOTICES.md。
/// </summary>
public static partial class TweakCatalog
{
    private const string Mmcss = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string MmcssGames = Mmcss + @"\Tasks\Games";

    private static IEnumerable<TweakItem> LatencyTweaks()
    {
        // MMCSS：默认给后台任务保留 20% CPU 带宽。设为 0 会把带宽全给多媒体/前台，
        // 是社区常见的「降延迟」做法；代价是后台编码、下载可能被饿死，音频也可能爆音。
        yield return Hklm("lat-mmcss-response", TweakGroup.Latency, "MMCSS 保留带宽降到 0", TweakRisk.Careful,
            "SystemResponsiveness 从默认 20 改为 0，不再为低优先级后台任务预留 CPU 带宽。",
            "后台任务（转码、下载、备份）可能明显变慢，音频在满载时可能爆音。不要和「CPU 激进调度」叠加测试，否则无法归因。",
            Mmcss, "SystemResponsiveness", "0", "20",
            admin: true, restart: true);

        // MMCSS 的 Games 任务：把游戏类线程标成高调度类别并给足 GPU/IO 优先级。
        // 四个值属于同一处配置，做成一个条目，避免出现「半套」状态。
        yield return Multi("lat-mmcss-games", TweakGroup.Latency, "游戏任务设为高调度优先级", TweakRisk.Careful,
            "MMCSS 的 Games 任务：Scheduling Category=High、Priority=6、GPU Priority=8、SFIO Priority=High。",
            "系统资源会更偏向被识别为游戏的进程；后台任务响应变慢。只有被 MMCSS 正确识别的程序才吃得到，不是万能加速。",
            admin: true, restart: true,
            W(RegistryHive.LocalMachine, MmcssGames, "Scheduling Category", "High", "Medium", RegistryValueKind.String),
            W(RegistryHive.LocalMachine, MmcssGames, "SFIO Priority", "High", "Normal", RegistryValueKind.String),
            W(RegistryHive.LocalMachine, MmcssGames, "GPU Priority", "8", "8"),
            W(RegistryHive.LocalMachine, MmcssGames, "Priority", "6", "6"));

        // 前台锁定超时：默认 200000 ms，程序要等一段时间才能抢占前台。设为 0 让切换更跟手。
        yield return Hkcu("lat-foreground-lock", TweakGroup.Latency, "取消前台锁定超时", TweakRisk.Safe,
            "ForegroundLockTimeout 从默认 200000 改为 0，切窗口时不再等待前台锁释放。",
            "个别后台程序可能更容易抢到前台抢焦点。只是响应偏好，随时可还原。",
            @"Control Panel\Desktop", "ForegroundLockTimeout", "0", "200000");

        // IRQ8 是 RTC/系统时钟中断。提高它的优先级是 denis-g 指南里「中断优先级」一节的做法。
        // 明确标注：这个键在现代 Windows 上的实际效果依赖平台与计时源，本软件没有实测结论。
        yield return Hklm("lat-irq8-priority", TweakGroup.Latency, "提高系统时钟中断优先级", TweakRisk.Careful,
            "IRQ8Priority = 1，把系统时钟中断排在普通设备中断前面（denis-g 指南的「中断优先级」一节）。",
            "效果依赖平台和计时源，Win10/11 上普遍改走 TSC，很多机器测不出差异；不当配置反而可能让 USB/网卡中断响应变慢。这一项只适合拿去做单变量对照，别默认打开。",
            @"SYSTEM\CurrentControlSet\Control\PriorityControl", "IRQ8Priority", "1", "",
            admin: true, delOff: true, restart: true);
    }
}
