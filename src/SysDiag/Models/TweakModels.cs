using System.Collections.Generic;
using Microsoft.Win32;

namespace SysDiag.Models;

/// <summary>调优条目的分类，对应窗口左侧的四个页签。</summary>
public enum TweakGroup
{
    Common,
    Explorer,
    Privacy,
    System,

    /// <summary>固态硬盘与文件系统：碎片整理、预读、TRIM。</summary>
    Disk,

    /// <summary>TCP/IP 栈与网络限流。多数要重启才生效。</summary>
    Network,

    /// <summary>界面与文件系统的响应速度。</summary>
    Speed,

    /// <summary>开关机耗时与系统稳定性。</summary>
    Startup,

    /// <summary>延迟与调度：MMCSS、中断优先级、前台锁定。专为单变量对照实验准备。</summary>
    Latency,

    /// <summary>取自 AtlasOS playbook、运行时可做且可还原的注册表项。</summary>
    Atlas
}

/// <summary>
/// 风险档。不是装饰：它决定了界面颜色、是否二次确认，以及「全选安全项」会不会包含它。
///
/// - Safe    ：只是显示偏好，改错了顶多不好看，随时能改回
/// - Careful ：会影响某个系统功能（搜索、休眠、更新），有明确副作用但不至于修不回来
/// - Risky   ：动了安全防线（UAC / DEP / VBS / 内存完整性）或引导开关，需要重启，且会让机器更脆弱
/// </summary>
public enum TweakRisk
{
    Safe,
    Careful,
    Risky
}

/// <summary>条目类型：写注册表 / 改服务启动类型 / 执行一条命令。</summary>
public enum TweakKind
{
    Registry,
    Service,
    Command
}

/// <summary>一次注册表写入。一个条目可以有多次写入（例如「关闭设置建议」要同时关三个 SubscribedContent）。</summary>
public sealed class TweakWrite
{
    public RegistryHive Hive { get; init; } = RegistryHive.CurrentUser;

    /// <summary>不含根键的子路径，例如 SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced。</summary>
    public string KeyPath { get; init; } = "";

    /// <summary>值名。空串表示键的默认值（CLSID 那种写法）。</summary>
    public string ValueName { get; init; } = "";

    public RegistryValueKind Kind { get; init; } = RegistryValueKind.DWord;

    /// <summary>「开启」时写入的值（统一用字符串存，按 Kind 解析）。</summary>
    public string OnValue { get; init; } = "";

    /// <summary>「关闭」时写入的值。DeleteWhenOff 为真时忽略。</summary>
    public string OffValue { get; init; } = "";

    /// <summary>关闭时直接删掉这个值（恢复成「系统没设置过」），比写回一个猜测的默认值更安全。</summary>
    public bool DeleteWhenOff { get; init; }

    /// <summary>给界面看的短路径，例如 HKCU\...\Advanced\HideFileExt。</summary>
    public string Display => $"{HiveTag(Hive)}\\{KeyPath}" + (ValueName.Length > 0 ? "\\" + ValueName : "（默认值）");

    public static string HiveTag(RegistryHive hive) => hive switch
    {
        RegistryHive.CurrentUser => "HKCU",
        RegistryHive.LocalMachine => "HKLM",
        RegistryHive.Users => "HKU",
        RegistryHive.ClassesRoot => "HKCR",
        _ => hive.ToString()
    };
}

/// <summary>一条可开关的系统设置。</summary>
public sealed class TweakItem
{
    public string Id { get; init; } = "";
    public TweakGroup Group { get; init; }
    public TweakKind Kind { get; init; } = TweakKind.Registry;
    public TweakRisk Risk { get; init; } = TweakRisk.Safe;

    public string Title { get; init; } = "";

    /// <summary>这条设置做什么。</summary>
    public string Desc { get; init; } = "";

    /// <summary>副作用 / 代价。空串表示没有值得提的代价。</summary>
    public string Effect { get; init; } = "";

    public bool NeedsAdmin { get; init; }
    public bool NeedsRestart { get; init; }

    /// <summary>注册表类：要写的值（可多条）。</summary>
    public List<TweakWrite> Writes { get; init; } = new();

    /// <summary>服务类：服务名。</summary>
    public string ServiceName { get; init; } = "";

    /// <summary>服务类：开启时的 Start 值（2 自动 / 3 手动 / 4 禁用）。</summary>
    public int OnStart { get; init; } = 3;

    public int OffStart { get; init; } = 4;

    /// <summary>命令类：开启 / 关闭要执行的命令。</summary>
    public string OnCommand { get; init; } = "";
    public string OffCommand { get; init; } = "";

    /// <summary>命令类：怎么判断当前状态（注册表/文件探针）。</summary>
    public System.Func<string> Probe { get; init; }

    /// <summary>命令类：probe 返回 true 表示「已开启」。</summary>
    public System.Func<bool> ProbeOn { get; init; }

    /// <summary>来源标记：srv=逆向清单，gh=参考开源项目补充。用于界面与文档溯源。</summary>
    public string Origin { get; init; } = "srv";

    public string RiskText => Risk switch
    {
        TweakRisk.Safe => "安全",
        TweakRisk.Careful => "谨慎",
        _ => "高风险"
    };

    public string GroupText => Group switch
    {
        TweakGroup.Common => "常用开关",
        TweakGroup.Explorer => "资源管理器",
        TweakGroup.Privacy => "隐私与更新",
        _ => "系统功能"
    };

    /// <summary>条目涉及的位置摘要（注册表路径 / 服务名 / 命令），给卡片第二行用。</summary>
    public string TargetText
    {
        get
        {
            if (Kind == TweakKind.Service) return "服务 " + ServiceName;
            if (Kind == TweakKind.Command) return OnCommand.Length > 0 ? "命令 " + Shorten(OnCommand) : "";

            return Writes.Count switch
            {
                0 => "",
                1 => Writes[0].Display,
                _ => Writes[0].Display + $"　等 {Writes.Count} 处"
            };
        }
    }

    private static string Shorten(string text)
        => text.Length <= 56 ? text : text[..53] + "…";
}

/// <summary>条目当前状态。Unknown 表示读不到（权限 / 服务不存在），界面要如实说明。</summary>
public enum TweakState
{
    Off,
    On,
    Partial,
    Unknown
}

/// <summary>读取结果：状态 + 一句人话依据。</summary>
public sealed class TweakReading
{
    public TweakItem Item { get; init; } = new();
    public TweakState State { get; init; } = TweakState.Unknown;
    public string Detail { get; init; } = "";

    public bool IsOn => State == TweakState.On;

    /// <summary>
    /// 状态文案统一成「这项优化生效了没有」。
    ///
    /// 以前用「已开启 / 已关闭」：对「关闭 Windows 搜索索引」这种标题来说，
    /// 「已关闭」到底是「索引关了」还是「优化没做」说不清，截图里就吃过这个歧义。
    /// </summary>
    public string StateText => State switch
    {
        TweakState.On => "已生效",
        TweakState.Off => "未生效",
        TweakState.Partial => "部分生效",
        _ => "读不到"
    };
}
