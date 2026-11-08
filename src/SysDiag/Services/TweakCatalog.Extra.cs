using System.Collections.Generic;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 系统优化的第二批条目：固态硬盘 / 网络传输 / 响应速度 / 开关机与稳定性。
///
/// 数据来源与主文件一致：键路径与取值语义按 Windows 自身行为与公开文档整理，
/// 取值核对过 farag2/Sophia-Script-for-Windows 与 Raphire/Win11Debloat（均为 MIT，见
/// THIRD-PARTY-NOTICES.md）。只借鉴「用哪个键」，没有复制脚本代码。
///
/// 通用约定：<c>on</c> 是「这项优化生效时」写入的值，<c>off</c> 是还原值。
/// 凡是 Windows 默认不存在、删掉就等于恢复默认的键，用 <c>delOff</c>。
/// </summary>
public static partial class TweakCatalog
{
    private const string TcpipParams = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";
    private const string FsControl = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string MemMgmt = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string Desktop = @"Control Panel\Desktop";
    private const string ExplorerAdv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static IEnumerable<TweakItem> ExtraItems()
    {
        foreach (var x in DiskTweaks()) yield return x;
        foreach (var x in NetworkTweaks()) yield return x;
        foreach (var x in SpeedTweaks()) yield return x;
        foreach (var x in StartupTweaks()) yield return x;
        foreach (var x in LatencyTweaks()) yield return x;
        foreach (var x in AtlasTweaks()) yield return x;
    }

    private static TweakWrite W(RegistryHive hive, string keyPath, string valueName,
        string on, string off, RegistryValueKind kind = RegistryValueKind.DWord, bool delOff = false) => new()
    {
        Hive = hive,
        KeyPath = keyPath,
        ValueName = valueName,
        Kind = kind,
        OnValue = on,
        OffValue = off,
        DeleteWhenOff = delOff
    };

    // ------------------------------------------------------------------ 固态硬盘

    private static IEnumerable<TweakItem> DiskTweaks()
    {
        yield return Hklm("disk-bootdefrag", TweakGroup.Disk, "禁止开机磁盘整理", TweakRisk.Safe,
            "开机时不再对启动文件做布局优化（BootOptimizeFunction）。固态盘上这一步纯属浪费读取次数。",
            "机械盘上关掉会让开机文件逐渐碎片化，开机反而变慢。老式 HDD 系统盘不建议关。",
            @"SOFTWARE\Microsoft\Dfrg\BootOptimizeFunction", "Enable", "N", "Y",
            kind: RegistryValueKind.String);

        yield return Svc("disk-defragsvc", TweakGroup.Disk, "关闭驱动器优化服务（空闲整理）", TweakRisk.Careful,
            "defragsvc 负责计划任务里的定期碎片整理与「优化驱动器」。固态盘不需要碎片整理。",
            "机械盘关掉后碎片会持续累积，读写变慢——机械盘请保留。另外这个服务也负责手动执行 TRIM 重试。",
            "defragsvc");

        yield return Hklm("disk-prefetch", TweakGroup.Disk, "禁止预读取（Prefetcher）", TweakRisk.Careful,
            "把 EnablePrefetcher 设为 0，系统不再预先把常用程序读进内存。固态盘随机读很快，预读收益接近于零。",
            "机械盘上关掉会明显变慢：预读原本能减少寻道。另外首次启动某个程序会慢一点。",
            MemMgmt + @"\PrefetchParameters", "EnablePrefetcher", "0", "3");

        yield return Hklm("disk-trim", TweakGroup.Disk, "启用 TRIM", TweakRisk.Safe,
            "DisableDeleteNotification = 0，删除文件时把「这块没用了」告诉固态盘主控，维持写入性能与寿命。",
            "还原会把通知关掉。只有在极老的主控或特定 RAID 驱动上才需要关闭 TRIM。",
            FsControl, "DisableDeleteNotification", "0", "1");
    }

    // ------------------------------------------------------------------ 网络传输

    private static IEnumerable<TweakItem> NetworkTweaks()
    {
        yield return Hklm("net-throttling", TweakGroup.Network, "关闭网络限流", TweakRisk.Safe,
            "NetworkThrottlingIndex 设为 0xFFFFFFFF。默认值 10 会限制多媒体程序的网络包处理速率。",
            "几乎无副作用；个别老式网卡驱动在满负荷下可能丢包。改完重启网卡生效。",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile",
            "NetworkThrottlingIndex", "4294967295", "10");

        yield return Hklm("net-tcp1323", TweakGroup.Network, "启用窗口缩放与时间戳", TweakRisk.Safe,
            "Tcp1323Opts = 3（窗口缩放 + 时间戳）。高带宽高延迟链路（跨省、跨境）靠它才能跑满。",
            "少数老防火墙会丢弃带时间戳选项的包，表现是「某些网站连不上」。遇到就还原。",
            TcpipParams, "Tcp1323Opts", "3", "0", restart: true);

        yield return Hklm("net-sack", TweakGroup.Network, "启用选择性确认（SACK）", TweakRisk.Safe,
            "SackOpts = 1。丢包时只重传丢的那一段，不用重发整窗。",
            "对端不支持时会自动退回。没有已知副作用。",
            TcpipParams, "SackOpts", "1", "0");

        yield return Hklm("net-pmtu", TweakGroup.Network, "自动检测路径 MTU 黑洞", TweakRisk.Safe,
            "EnablePMTUBHDetect = 1。大包被中间设备静默丢弃时，自动缩小 MSS 重试，而不是一直卡住。",
            "会多几个探测包，流量开销可忽略。极少数链路下关掉反而更快。",
            TcpipParams, "EnablePMTUBHDetect", "1", "0", restart: true);

        yield return Hklm("net-ttl", TweakGroup.Network, "统一 TTL 为 64", TweakRisk.Safe,
            "DefaultTTL = 64。默认值随版本变化，统一后路径长度的行为更可预期。",
            "TTL 只影响经过的路由跳数上限，对本地网络没有任何影响。",
            TcpipParams, "DefaultTTL", "64", "", delOff: true);

        yield return Hklm("net-timewait", TweakGroup.Network, "缩短 TIME_WAIT 等待", TweakRisk.Careful,
            "TcpTimedWaitDelay = 30 秒。大量短连接（游戏大厅、下载器）时能更快回收端口。",
            "缩短后旧连接的迟到包可能落到新连接上。正常浏览无感，做压力测试或代理服务时不建议改。",
            TcpipParams, "TcpTimedWaitDelay", "30", "240", restart: true);

        yield return Multi("net-dns-negative", TweakGroup.Network, "关闭 DNS 负缓存", TweakRisk.Safe,
            "解析失败的域名不再缓存。域名刚生效时不用等缓存过期。",
            "解析失败会重复查询，DNS 服务器压力略增；断网时每次都要等超时。",
            false, false,
            W(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters",
                "NegativeCacheTime", "0", "300"),
            W(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters",
                "MaxNegativeCacheTtl", "0", "900"));

        yield return Hklm("net-wins", TweakGroup.Network, "关闭 WINS 广播查询", TweakRisk.Safe,
            "NetBT 的 NodeType 设为 2（P 节点）：只查 DNS，不做广播。家用网络里广播查询基本是纯等待。",
            "如果局域网里还有靠 NetBIOS 名称互访的老设备（老打印机、老 NAS），会找不到它们。",
            @"SYSTEM\CurrentControlSet\Services\NetBT\Parameters", "NodeType", "2", "8");
    }

    // ------------------------------------------------------------------ 响应速度

    private static IEnumerable<TweakItem> SpeedTweaks()
    {
        yield return Hklm("speed-iconcache", TweakGroup.Speed, "增大图标缓存上限", TweakRisk.Safe,
            "MaxCachedIcons = 4096。图标不用反复从 exe 里重新提取，大文件夹滚动更顺。",
            "多占几 MB 内存。还原后系统会重建图标缓存，第一次打开文件夹会慢一下。",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", "MaxCachedIcons", "4096", "",
            delOff: true, kind: RegistryValueKind.String);

        yield return Hklm("speed-foreground", TweakGroup.Speed, "提高前台程序响应", TweakRisk.Safe,
            "Win32PrioritySeparation = 38（0x26）：给前台进程更多时间片，切窗口更跟手。",
            "后台任务（编码、下载、渲染）会稍微慢一点。服务器上不要用。",
            @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "38", "2");

        yield return Hkcu("speed-peek", TweakGroup.Speed, "加快 Aero Peek 预览", TweakRisk.Safe,
            "鼠标悬停任务栏时立刻显示缩略图，不再等系统默认的延迟。",
            "鼠标划过任务栏会频繁弹出预览，可能觉得吵。还原即恢复延迟。",
            ExplorerAdv, "DesktopLivePreviewHoverTime", "0", "", delOff: true);

        yield return Hkcu("speed-thumbpreview", TweakGroup.Speed, "加快任务栏窗口预览", TweakRisk.Safe,
            "把缩略图悬停延迟设为 0，和上一条配合让任务栏切换更利落。",
            "同上：划过去就弹，习惯慢节奏的话会不适应。",
            ExplorerAdv, "ThumbnailLivePreviewHoverTime", "0", "", delOff: true);

        yield return Hkcu("speed-minanimate", TweakGroup.Speed, "关闭最小化/最大化动画", TweakRisk.Safe,
            "MinAnimate = 0。窗口缩放瞬间完成，不再做缩放动画。",
            "观感会变「生硬」，但切换确实更快。笔记本用户可能觉得少了点质感。",
            @"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0", "1",
            kind: RegistryValueKind.String);

        yield return Hkcu("speed-menudelay", TweakGroup.Speed, "菜单弹出零延迟", TweakRisk.Safe,
            "MenuShowDelay = 0，菜单立刻展开，不用等默认的 400ms。",
            "手抖划过菜单时会连续弹出子菜单，反而容易点错。",
            Desktop, "MenuShowDelay", "0", "400", kind: RegistryValueKind.String);

        yield return Hklm("speed-autoplay", TweakGroup.Speed, "禁用驱动器自动播放", TweakRisk.Safe,
            "NoDriveTypeAutoRun = 255，插入 U 盘、光盘不再自动弹窗或执行 autorun。",
            "插入介质后需要手动打开。这条顺带降低 U 盘病毒自动执行的概率。",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoDriveTypeAutoRun", "255", "145");

        yield return Hklm("speed-8dot3", TweakGroup.Speed, "禁用 8.3 短文件名", TweakRisk.Careful,
            "新建文件不再额外生成 DOS 短名，NTFS 元数据更少，大目录遍历更快。",
            "极老的 16 位安装程序、部分老版本 Java 工具依赖短名，可能装不上。只对新文件生效。",
            FsControl, "NtfsDisable8dot3NameCreation", "1", "2");

        yield return Hklm("speed-lastaccess", TweakGroup.Speed, "不再更新文件最后访问时间", TweakRisk.Careful,
            "每次打开文件都写一次时间戳，这项改动能省掉这部分磁盘写入。",
            "依赖「最后访问时间」的工具会失准：备份软件的增量判断、部分杀软的行为分析、取证工具。",
            FsControl, "NtfsDisableLastAccessUpdate", "1", "0");

        yield return Hklm("speed-wer", TweakGroup.Speed, "禁用 Windows 错误报告", TweakRisk.Careful,
            "程序崩溃后不再收集并上传转储，省掉崩溃瞬间的磁盘与 CPU 抖动。",
            "崩溃信息不再记录，排查「某个程序老崩」时少了最直接的线索。",
            @"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", "1", "0");

        yield return Hklm("speed-aedebug", TweakGroup.Speed, "关闭崩溃时的调试器挂接", TweakRisk.Careful,
            "AeDebug 的 Auto 设为 0，程序崩溃时不自动挂调试器，直接结束。",
            "装了 Visual Studio 或 WinDbg 的人会失去自动附加，手工调试要自己挂。",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug", "Auto", "0", "1",
            kind: RegistryValueKind.String);

        yield return Hklm("speed-updatemode", TweakGroup.Speed, "资源管理器改为即时刷新", TweakRisk.Careful,
            "UpdateMode = 0，文件变化立刻反映到窗口，不再等系统合并刷新。",
            "大量文件变动（解压、编译）时窗口刷新会占用更多 CPU，风扇可能转得勤一点。",
            @"SYSTEM\CurrentControlSet\Control\Update", "UpdateMode", "0", "1");
    }

    // ------------------------------------------------------------------ 开关机与稳定性

    private static IEnumerable<TweakItem> StartupTweaks()
    {
        yield return Multi("boot-hungapp", TweakGroup.Startup, "缩短关机等待", TweakRisk.Careful,
            "把「程序无响应等待」压到 1 秒、「结束程序等待」压到 2 秒，关机不用干等。",
            "有未保存文档的程序可能来不及弹确认框就被结束，**会丢数据**。养成随手保存的习惯再用。",
            false, false,
            W(RegistryHive.CurrentUser, Desktop, "HungAppTimeout", "1000", "5000", RegistryValueKind.String),
            W(RegistryHive.CurrentUser, Desktop, "WaitToKillAppTimeout", "2000", "20000", RegistryValueKind.String));

        yield return Hklm("boot-svctimeout", TweakGroup.Startup, "缩短服务关闭超时", TweakRisk.Careful,
            "WaitToKillServiceTimeout 从 5 秒压到 2 秒，关机时不用等服务慢慢收尾。",
            "数据库、虚拟机这类需要时间落盘的服务会被强制结束，可能导致文件损坏。跑着这类服务时别改。",
            @"SYSTEM\CurrentControlSet\Control", "WaitToKillServiceTimeout", "2000", "5000",
            kind: RegistryValueKind.String);

        yield return Hklm("boot-clearpagefile", TweakGroup.Startup, "关机时不清理页面文件", TweakRisk.Careful,
            "ClearPageFileAtShutdown = 0。关机时不再把整个 pagefile 覆写一遍，关机快很多（尤其在机械盘）。",
            "页面文件里可能残留上次会话的内存内容。**多人共用或涉密机器不要关**。",
            MemMgmt, "ClearPageFileAtShutdown", "0", "1");

        yield return Hklm("boot-noreboot", TweakGroup.Startup, "蓝屏后不自动重启", TweakRisk.Safe,
            "AutoReboot = 0。蓝屏信息留在屏幕上，能看清错误码再手动重启。",
            "无人值守的机器蓝屏后会停在那个画面，不会自己恢复。",
            @"SYSTEM\CurrentControlSet\Control\CrashControl", "AutoReboot", "0", "1");

        yield return Svc("boot-remotereg", TweakGroup.Startup, "关闭远程注册表服务", TweakRisk.Careful,
            "RemoteRegistry 是横向移动的常用入口。单机使用完全不需要它。",
            "远程管理工具、部分企业运维脚本会失效。域环境里先问管理员。",
            "RemoteRegistry");

        yield return Hklm("boot-separateproc", TweakGroup.Startup, "资源管理器独立进程运行", TweakRisk.Careful,
            "SeparateProcess = 1，explorer 崩了不会把整个外壳一起带走。",
            "内存占用略增，且要重新登录才生效。部分老式外壳扩展会因此失效。",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "SeparateProcess", "1", "0",
            restart: true);

        yield return Hklm("boot-autorestartshell", TweakGroup.Startup, "资源管理器崩溃后自动重启", TweakRisk.Safe,
            "AutoRestartShell = 1。explorer 挂掉后自动拉起来，桌面和任务栏会自己回来。",
            "崩溃会进入循环重启时不好察觉（表现是桌面反复闪）。排查阶段建议还原。",
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", "AutoRestartShell", "1", "0");

        yield return Hkcu("boot-autoendtasks", TweakGroup.Startup, "自动结束无响应程序", TweakRisk.Careful,
            "AutoEndTasks = 1，卡死的程序在关机/注销时直接结束，不再弹「是否等待」。",
            "**可能丢未保存的数据**。和「缩短关机等待」叠加时风险更高。",
            Desktop, "AutoEndTasks", "1", "0", kind: RegistryValueKind.String);
    }
}
