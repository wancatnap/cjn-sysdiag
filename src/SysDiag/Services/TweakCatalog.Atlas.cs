using System.Collections.Generic;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 从 AtlasOS 的 playbook 里挑出来的、**运行时就能做**的注册表项。
///
/// 挑选标准有三条，缺一不可：
/// <list type="number">
/// <item>Atlas 的 <c>src/playbook/Configuration/tweaks/</c> 里确实有这一项（不是我编的）；</item>
/// <item>能用「一个键 + 一个值」静态表达，不需要跑脚本、不需要改镜像；</item>
/// <item>写入前能记下原值、随时能还原。</item>
/// </list>
///
/// 因此**没有收录** Atlas 里靠重装/改镜像才能生效的部分（删组件、裁 Appx 预置、改安装介质），
/// 那部分在「免重装精简」里有说明。也没有收录依赖具体硬件的项（蓝牙、触摸板节能）。
///
/// 每一项都写清代价。Atlas 的 README 把这些统一写成「优化」，但从它自己的 PowerShell 源码看，
/// 有几项是明确带取舍的（比如 AutoEndTasks 会直接结束未保存的程序），这里如实标出来。
/// </summary>
public static partial class TweakCatalog
{
    private static IEnumerable<TweakItem> AtlasTweaks()
    {
        // ---------------- 内核常驻内存 ----------------

        yield return Hklm("atlas-no-paging-exec", TweakGroup.Atlas,
            "内核代码常驻内存（DisablePagingExecutive）", TweakRisk.Safe,
            "让内核与驱动的代码页留在物理内存里，不被换到页面文件。Atlas 的 performance tweaks 里有这一项。",
            "少掉一点可用内存（通常几十到一百多 MB），换来内核路径上不出现「缺页→读盘」的停顿。"
            + "内存小于 8GB 时收益不明显，反而更容易触发换页。改完需要重启。",
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "DisablePagingExecutive", "1", "0",
            admin: true, restart: true);

        // ---------------- SMB 带宽限制 ----------------

        yield return Multi("atlas-smb-unthrottle", TweakGroup.Atlas,
            "解除 SMB 带宽限制", TweakRisk.Safe,
            "Windows 默认给 SMB 传输留了带宽节流阀。Atlas 在 LanmanServer 与 LanmanWorkstation 两侧都关掉它。",
            "局域网大文件拷贝能跑满链路。代价是 SMB 流量不再给其他程序让路——"
            + "在同时打游戏又走网络盘的情况下，可能抢占带宽。只有单机使用、不碰局域网共享的人可以不开。",
            admin: true, restart: true,
            W(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters",
                "DisableBandwidthThrottling", "1", "0"),
            W(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters",
                "DisableBandwidthThrottling", "1", "0"));

        // ---------------- LLMNR ----------------

        yield return Hklm("atlas-llmnr-off", TweakGroup.Atlas,
            "关闭 LLMNR 多播解析", TweakRisk.Careful,
            "LLMNR 是局域网里的「喊一嗓子看看有没有人叫这个名字」协议。现代网络基本用 DNS，"
            + "Atlas 与多数安全基线都建议关掉。",
            "减少局域网广播流量，同时堵住 LLMNR 投毒（攻击者冒充目标主机名）这条路。"
            + "代价：同一网段内靠主机名互访的设备（老 NAS、共享打印机）可能突然找不到，得改用 IP。",
            @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient",
            "EnableMulticast", "0", "1",
            admin: true);

        // ---------------- 关机行为 ----------------

        yield return Hkcu("atlas-auto-end-tasks", TweakGroup.Atlas,
            "关机时直接结束程序（AutoEndTasks）", TweakRisk.Careful,
            "Atlas 的 Qol 脚本把这项设为 1：关机/注销时不再弹「程序未响应，是否等待」。",
            "⚠ 会丢数据：正在编辑的文档、没保存的工程，会被直接结束，没有确认机会。"
            + "只在「关机慢、经常卡在等待」的机器上开，且开之前养成随手保存的习惯。"
            + "这也是本项标「谨慎」而不标「安全」的原因。",
            @"Control Panel\Desktop", "AutoEndTasks", "1", "0",
            kind: RegistryValueKind.String);

        // ---------------- 系统缓存 ----------------

        yield return Hklm("atlas-large-system-cache", TweakGroup.Atlas,
            "加大系统文件缓存（LargeSystemCache）", TweakRisk.Careful,
            "把文件系统缓存做大。Atlas 与不少「优化脚本」都收这一项。",
            "对文件服务器、编译机这类「反复读同一批文件」的负载有帮助。"
            + "对游戏与日常桌面**收益不明确**：缓存挤占的是应用可用内存，8GB 机器上可能反而更容易换页。"
            + "建议当单变量实验做——改前改后各测一轮，没差别就还原。",
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
            "LargeSystemCache", "1", "0",
            admin: true, restart: true);

        // ---------------- 资源管理器网络扫描 ----------------

        yield return Hkcu("atlas-no-net-crawling", TweakGroup.Atlas,
            "资源管理器不扫网络位置", TweakRisk.Safe,
            "打开「此电脑」时不去枚举局域网里的其他机器与共享。Atlas 的 explorer tweaks 里有这一项。",
            "打开此电脑不再有那一下「转圈」等待，尤其在网络里有已离线设备时明显。"
            + "代价：网络位置不再自动列出来，要访问共享得手动输地址。",
            @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
            "NoNetCrawling", "1", "0",
            restart: true);
    }
}
