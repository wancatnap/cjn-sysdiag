using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 系统优化条目清单（数据驱动，全部可还原）。
///
/// 条目来源有两类，界面上不区分，但这里写清便于复核：
///
/// 1. Windows 自身的策略键与公开文档。注册表路径、取值语义都是操作系统的事实性配置，
///    按微软文档与系统行为整理，不是任何人的创作。
///
/// 2. 参考两个 MIT 许可的开源项目补齐取值：
///    farag2/Sophia-Script-for-Windows 与 Raphire/Win11Debloat（见 THIRD-PARTY-NOTICES.md）。
///    用来确认「官方支持的策略键」是哪一个，以及哪些值在 22H2 之后已经失效
///    （例如 TaskbarMn 只在 21H2~22H2 有意义）。**只借鉴「哪个键、哪个值」，没有复制其脚本代码。**
///
/// 三条刻意没做的：
/// - 不提供把 Windows 更新暂停到 2042 年这种「一次到位」的开关，而是把
///   「安全补丁也一起停」写进副作用，仍然归到「谨慎」，不替用户做决定。
/// - 不把 UAC 的 EnableLUA 写成 0（商店应用会直接坏掉），只降通知级别。
/// - 不动 Defender / 安全中心：省下的那点性能，抵不上把机器暴露在风险里。
/// </summary>
public static partial class TweakCatalog
{
    private const string ExplorerAdvanced =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private const string ExplorerRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer";

    private const string PolicySystem =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private static readonly Lazy<List<TweakItem>> Cache = new(BuildAll);

    public static List<TweakItem> All => Cache.Value;

    public static List<TweakItem> OfGroup(TweakGroup group)
        => All.Where(x => x.Group == group).ToList();

    public static TweakItem ById(string id)
        => All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static IReadOnlyList<(TweakGroup Group, string Title, string Note)> Groups { get; } = new[]
    {
        (TweakGroup.Common, "常用开关", "关掉用不上的系统功能。多数只影响后台行为，随时可还原。"),
        (TweakGroup.Explorer, "资源管理器", "文件与任务栏的显示偏好。绝大多数只写 HKCU，不需要管理员。"),
        (TweakGroup.Privacy, "隐私与更新", "减少向微软上传的内容，以及控制更新节奏。"),
        (TweakGroup.System, "系统功能", "远程、休眠、虚拟化安全这类系统层开关。带「高风险」的会削弱防护，改前请看完说明。"),
        (TweakGroup.Disk, "固态硬盘", "碎片整理、预读、TRIM。机械盘上关预读会变慢，先看清是哪一类盘。"),
        (TweakGroup.Network, "网络传输", "TCP/IP 栈与限流参数。多数要重启网络或重启系统才生效。"),
        (TweakGroup.Speed, "响应速度", "界面动画、菜单延迟、文件系统元数据。改完部分项要重启资源管理器。"),
        (TweakGroup.Startup, "开关机", "缩短关机等待、蓝屏行为、资源管理器容错。有两条会影响未保存数据。"),
        (TweakGroup.Atlas, "Atlas 参考", "取自 AtlasOS playbook 里能在运行时改、能还原的注册表项。每条都写清代价；标注「谨慎」的都带取舍，别一口气全开。"),
        (TweakGroup.Latency, "延迟与调度", "MMCSS、中断优先级、前台锁定。一次只改一项，配合「延迟实测」做对照。")
    };

    // ------------------------------------------------------------------ 汇总

    private static List<TweakItem> BuildAll()
    {
        var list = new List<TweakItem>();

        list.AddRange(Common());
        list.AddRange(Explorer());
        list.AddRange(Privacy());
        list.AddRange(SystemTweaks());

        // 固态硬盘 / 网络 / 响应速度 / 开关机与稳定性
        list.AddRange(ExtraItems());

        return list;
    }

    // ------------------------------------------------------------------ 常用开关

    private static IEnumerable<TweakItem> Common()
    {
        // 逆向：Form1 TurnTool "01"
        yield return Hklm("common-restore", TweakGroup.Common, "关闭系统还原", TweakRisk.Careful,
            "关掉系统还原点功能。精简/优化脚本最常干的第一件事，能省下几个 GB。",
            "系统出问题时就没法「恢复到上一个还原点」了。装驱动翻车、误删文件都救不回来。",
            @"SOFTWARE\Policies\Microsoft\Windows NT\SystemRestore", "DisableSR",
            "1", "", admin: true, delOff: true);

        // 逆向：Form1 TurnTool "03"
        yield return Hklm("common-widgets", TweakGroup.Common, "关闭小组件（新闻与兴趣）", TweakRisk.Safe,
            "关掉任务栏小组件面板背后的内容推送（天气、新闻、股票）。",
            "小组件面板会变成空白，其余功能不受影响。",
            @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests",
            "0", "", admin: true, delOff: true);

        // 逆向：Form1 TurnTool "02"
        yield return Hklm("common-search-hotspot", TweakGroup.Common, "关闭搜索界面的热点内容", TweakRisk.Safe,
            "开始菜单搜索面板不再显示「热门搜索」「今日推荐」这类内容块。",
            "只影响搜索面板的推荐区，搜索本身照常。",
            @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "EnableDynamicContentInWSB",
            "0", "", admin: true, delOff: true);

        // 逆向：Form1 TurnTool "04"
        yield return Hklm("common-wu-drivers", TweakGroup.Common, "更新时不自动装硬件驱动", TweakRisk.Careful,
            "让 Windows 更新只推系统补丁，不推厂商驱动。避免「更新完声卡/网卡反而坏了」。",
            "新硬件可能要靠自己装驱动；老驱动有安全问题时也不会被自动替换。",
            @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate",
            "1", "", admin: true, delOff: true);

        // 逆向：Form1 TurnTool "05"
        yield return Hklm("common-ceip", TweakGroup.Common, "关闭客户体验改善计划（CEIP）", TweakRisk.Safe,
            "停止向微软发送使用习惯与诊断统计数据。",
            "没有任何功能损失，只是不再参与体验计划。",
            @"SOFTWARE\Policies\Microsoft\SQMClient\Windows", "CEIPEnable",
            "0", "", admin: true, delOff: true);

        // 逆向：Form1 TurnTool "07"（sc config PcaSvc start=disabled）
        yield return Svc("common-pcasvc", TweakGroup.Common, "关闭程序兼容性助手", TweakRisk.Safe,
            "关掉 PcaSvc。它负责弹出「此程序可能无法正常运行」并尝试兼容性修补。",
            "老旧程序的兼容性提示与自动修补不再出现；极少数老软件可能需要手动设兼容模式。",
            "PcaSvc");

        // 逆向：Form1 TurnTool "08"（sc config DPS start=disabled）
        yield return Svc("common-dps", TweakGroup.Common, "关闭诊断策略服务", TweakRisk.Careful,
            "关掉 DPS。它给「设置 → 系统 → 疑难解答」和部分诊断收集提供数据。",
            "Windows 自带疑难解答会失效；部分企业环境用它做诊断收集。",
            "DPS");

        // 逆向：Form5 button3 / 服务页
        yield return Svc("common-wsearch", TweakGroup.Common, "关闭 Windows 搜索索引", TweakRisk.Careful,
            "停用 WSearch 并设为禁用。索引服务会持续扫描磁盘，是老旧机器卡顿的常见来源。",
            "开始菜单和资源管理器的搜索会变慢甚至搜不到内容（建议改用 Everything）。",
            "WSearch");

        // 逆向：Form4 系统功能调节 "3"
        yield return Svc("common-sysmain", TweakGroup.Common, "关闭 SysMain（预读取）", TweakRisk.Careful,
            "停用 SysMain。它在后台预读常用程序，机械盘上收益明显，SSD 上收益有限但常驻占内存。",
            "机械硬盘上程序首次启动会变慢；SSD 上基本无感。",
            "SysMain");

        // 逆向：Form4 系统功能调节 "1"（powercfg /h off）
        yield return new TweakItem
        {
            Id = "common-hibernate",
            Group = TweakGroup.Common,
            Kind = TweakKind.Command,
            Risk = TweakRisk.Careful,
            Title = "关闭休眠（并删除 hiberfil.sys）",
            Desc = "关闭休眠文件，通常能直接腾出与内存等大（8~32 GB）的 C 盘空间。",
            Effect = "休眠选项、以及依赖它的快速启动会一起失效。想恢复随时可以再打开。",
            NeedsAdmin = true,
            OnCommand = "powercfg /h off",
            OffCommand = "powercfg /h on",
            Probe = () => HibernateFileExists() ? "hiberfil.sys 存在（休眠已启用）" : "没有 hiberfil.sys（休眠已关闭）",
            ProbeOn = HibernateFileExists
        };

        // 逆向：Form4 系统功能调节 "2"（HiberbootEnabled）
        yield return Hklm("common-fastboot", TweakGroup.Common, "关闭快速启动", TweakRisk.Careful,
            "关掉「快速启动」。它其实是把内核会话休眠到磁盘，关机并不真的关干净。",
            "开机会慢几秒；好处是关机真的重置内核状态，双系统、换硬件、驱动异常时更省事。",
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled",
            "0", "1", admin: true);
    }

    // ------------------------------------------------------------------ 资源管理器

    private static IEnumerable<TweakItem> Explorer()
    {
        // 逆向：Form2 checkBox1
        yield return Hkcu("ex-ext", TweakGroup.Explorer, "显示文件扩展名", TweakRisk.Safe,
            "让 a.exe、b.txt 这类后缀直接显示出来。查可疑文件时最有用的一项。",
            "文件名会变长一点。",
            ExplorerAdvanced, "HideFileExt", "0", "1");

        // 逆向：Form2 checkBox3
        yield return Hkcu("ex-hidden", TweakGroup.Explorer, "显示隐藏文件", TweakRisk.Safe,
            "显示带隐藏属性的文件（ProgramData、AppData 这类目录）。",
            "目录会多出一堆平时看不见的项。",
            ExplorerAdvanced, "Hidden", "1", "2");

        // 逆向：Form2 checkBox4
        yield return Hkcu("ex-superhidden", TweakGroup.Explorer, "显示受保护的系统文件", TweakRisk.Safe,
            "把「隐藏 + 系统」双属性的文件也显示出来（desktop.ini、引导相关文件）。",
            "误删这些文件可能导致功能异常，看的时候注意。",
            ExplorerAdvanced, "ShowSuperHidden", "1", "0");

        // 逆向：Form2 checkBox2
        yield return Hkcu("ex-fullpath", TweakGroup.Explorer, "标题栏显示完整路径", TweakRisk.Safe,
            "资源管理器标题栏显示完整目录，而不是只显示当前文件夹名。",
            "标题会变长。",
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath", "1", "0");

        // 逆向：Form2 comboBox1
        yield return Hkcu("ex-thispc", TweakGroup.Explorer, "打开资源管理器时显示「此电脑」", TweakRisk.Safe,
            "默认打开「快速访问」，改成直接进「此电脑」。",
            "看不到「最近使用」列表了（那个列表下面能单独关）。",
            ExplorerAdvanced, "LaunchTo", "1", "", delOff: true);

        // 逆向：Form2 checkBox7
        yield return Hkcu("ex-recent", TweakGroup.Explorer, "显示最近使用的文件", TweakRisk.Safe,
            "快速访问里显示最近打开过的文件。",
            "关掉后快速访问更干净，也少一处暴露「最近在用什么」的地方。",
            ExplorerRoot, "ShowRecent", "1", "0");

        // 逆向：Form2 checkBox8
        yield return Hkcu("ex-frequent", TweakGroup.Explorer, "显示常用文件夹", TweakRisk.Safe,
            "快速访问里显示常用文件夹。",
            "无。",
            ExplorerRoot, "ShowFrequent", "1", "0");

        // 逆向：Form2 checkBox9
        yield return Hkcu("ex-cloud", TweakGroup.Explorer, "显示云文件（OneDrive）", TweakRisk.Safe,
            "快速访问里显示 OneDrive 等云盘内容。",
            "不用 OneDrive 的话关掉更清爽。",
            ExplorerRoot, "ShowCloudFilesInQuickAccess", "1", "0");

        // 逆向：Form2 checkBox14
        yield return Hkcu("ex-seconds", TweakGroup.Explorer, "任务栏时钟显示秒", TweakRisk.Safe,
            "时钟按 时:分:秒 显示。",
            "每秒刷新一次，理论上多一点点绘制开销。",
            ExplorerAdvanced, "ShowSecondsInSystemClock", "1", "0");

        // 逆向：Form2 comboBox2
        yield return Hkcu("ex-search-icon", TweakGroup.Explorer, "任务栏搜索只显示图标", TweakRisk.Safe,
            "搜索框收缩成一个放大镜图标，省任务栏宽度。",
            "要点一下才出输入框。",
            @"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", "1", "2");

        // 逆向：Form2 checkBox10
        yield return Hkcu("ex-taskview", TweakGroup.Explorer, "隐藏任务视图按钮", TweakRisk.Safe,
            "去掉任务栏上的「任务视图」按钮。",
            "Win + Tab 仍然可用。",
            ExplorerAdvanced, "ShowTaskViewButton", "0", "1");

        // 逆向：Form2 checkBox12
        yield return Hkcu("ex-widgets-btn", TweakGroup.Explorer, "隐藏任务栏小组件按钮", TweakRisk.Safe,
            "去掉任务栏左侧的天气/小组件按钮。",
            "只影响按钮显示；配合「关闭小组件」一起用更彻底。",
            ExplorerAdvanced, "TaskbarDa", "0", "1");

        // 逆向：Form2 comboBox3
        yield return Hkcu("ex-left", TweakGroup.Explorer, "任务栏图标左对齐", TweakRisk.Safe,
            "Win11 默认居中，改成 Win10 那样的左对齐。",
            "纯外观。需要重启资源管理器。",
            ExplorerAdvanced, "TaskbarAl", "0", "1", restart: true);

        // 逆向：Form2 checkBox15（HKLM Shell Icons 29）
        yield return Hklm("ex-arrow", TweakGroup.Explorer, "去掉快捷方式小箭头", TweakRisk.Careful,
            "把快捷方式图标左下角的箭头换成透明图标，图标更完整。",
            "去掉后光看图不再容易分辨「这是快捷方式还是本体」。需要重启资源管理器。",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Shell Icons", "29",
            @"%systemroot%\system32\imageres.dll,197", "", admin: true, delOff: true, restart: true,
            kind: RegistryValueKind.String);

        // 逆向：Form2 checkBox17（Explorer\link 二进制 00000000）
        yield return Hkcu("ex-shortcut-text", TweakGroup.Explorer, "去掉新建快捷方式的「- 快捷方式」后缀", TweakRisk.Safe,
            "新建快捷方式时不再自动加「- 快捷方式」几个字。",
            "需要重启资源管理器才生效。",
            ExplorerRoot, "link", "00 00 00 00", "", delOff: true, restart: true,
            kind: RegistryValueKind.Binary);

        // 逆向：Form2 checkBox18
        yield return Hklm("ex-onedrive", TweakGroup.Explorer, "禁用 OneDrive 同步", TweakRisk.Careful,
            "通过策略禁止 OneDrive 同步客户端运行。",
            "装着的 OneDrive 也登不上；依赖 OneDrive 存储的商店应用会受影响。",
            @"SOFTWARE\Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC",
            "1", "", admin: true, delOff: true);

        // 逆向：Form2 checkBox19
        yield return Hkcu("ex-copilot", TweakGroup.Explorer, "隐藏 Copilot 按钮", TweakRisk.Safe,
            "去掉任务栏上的 Copilot 入口。",
            "只影响按钮显示。新版本 Windows 里该项可能已无对应 UI。",
            ExplorerAdvanced, "ShowCopilotButton", "0", "", delOff: true);

        // 逆向：Form2 radioButton1（CLSID InprocServer32 默认值写成空串）
        yield return Hkcu("ex-classic-menu", TweakGroup.Explorer, "恢复 Win10 经典右键菜单", TweakRisk.Careful,
            "Win11 的右键菜单要先点「显示更多选项」才能看到完整菜单；这一项直接恢复成 Win10 的一级菜单。",
            "Win11 新菜单里的「复制为路径」等少部分项会消失；需要重启资源管理器。",
            @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InProcServer32", "",
            "", "", delOff: true, restart: true, kind: RegistryValueKind.String);

        // 逆向：Form2 checkBox20（两个 CLSID × 默认值 + ThreadingModel）
        yield return Multi("ex-win10-shell", TweakGroup.Explorer, "使用 Win10 风格资源管理器外壳", TweakRisk.Careful,
            "把 Win11 的资源管理器（至少 22H2）换回 Win10 那套外壳，打开更快、内存占用更低。",
            "没有多标签页；部分新界面的细节会缺失。需要重启资源管理器；" +
            "如果重启后资源管理器起不来，把本项关掉即可恢复（写入的是「故意无效的 DLL 路径」，不改系统文件）。",
            false, true,
            new TweakWrite
            {
                Hive = RegistryHive.CurrentUser,
                KeyPath = @"Software\Classes\CLSID\{2aa9162e-c906-4dd9-ad0b-3d24a8eef5a0}",
                ValueName = "", Kind = RegistryValueKind.String,
                OnValue = "CLSID_ItemsViewAdapter", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.CurrentUser,
                KeyPath = @"Software\Classes\CLSID\{2aa9162e-c906-4dd9-ad0b-3d24a8eef5a0}\InProcServer32",
                ValueName = "", Kind = RegistryValueKind.String,
                OnValue = @"C:\Windows\System32\Windows.UI.FileExplorer.dll_", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.CurrentUser,
                KeyPath = @"Software\Classes\CLSID\{2aa9162e-c906-4dd9-ad0b-3d24a8eef5a0}\InProcServer32",
                ValueName = "ThreadingModel", Kind = RegistryValueKind.String,
                OnValue = "Apartment", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.CurrentUser,
                KeyPath = @"Software\Classes\CLSID\{6480100b-5a83-4d1e-9f69-8ae5a88e9a33}",
                ValueName = "", Kind = RegistryValueKind.String,
                OnValue = "File Explorer Xaml Island View Adapter", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.CurrentUser,
                KeyPath = @"Software\Classes\CLSID\{6480100b-5a83-4d1e-9f69-8ae5a88e9a33}\InProcServer32",
                ValueName = "", Kind = RegistryValueKind.String,
                OnValue = @"C:\Windows\System32\Windows.UI.FileExplorer.dll_", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.CurrentUser,
                KeyPath = @"Software\Classes\CLSID\{6480100b-5a83-4d1e-9f69-8ae5a88e9a33}\InProcServer32",
                ValueName = "ThreadingModel", Kind = RegistryValueKind.String,
                OnValue = "Apartment", DeleteWhenOff = true
            });
    }

    // ------------------------------------------------------------------ 隐私与更新

    private static IEnumerable<TweakItem> Privacy()
    {
        // 逆向：Form5 PrivateCheckBox 1
        yield return Hklm("pv-cloud-search", TweakGroup.Privacy, "禁止搜索云内容", TweakRisk.Safe,
            "搜索时不再去 OneDrive / SharePoint / Outlook / 必应拉取云端结果。",
            "搜不到自己云盘里的文件；本地搜索不受影响。",
            @"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCloudSearch",
            "0", "", admin: true, delOff: true);

        // 逆向：Form5 PrivateCheckBox 2
        yield return Hkcu("pv-bing", TweakGroup.Privacy, "禁止搜索界面里的 Web 结果", TweakRisk.Safe,
            "搜索面板不再显示来自必应的网页结果。",
            "仅屏蔽界面展示；输入内容仍会联网。要彻底阻断请用「阻止搜索联网」动作。",
            @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", "0", "", delOff: true);

        // 逆向：Form5 PrivateCheckBox 3
        yield return Hkcu("pv-history", TweakGroup.Privacy, "不记录搜索历史", TweakRisk.Safe,
            "本地不再保存搜索历史。",
            "搜索建议会变得不那么「懂你」。",
            @"Software\Microsoft\Windows\CurrentVersion\SearchSettings",
            "IsDeviceSearchHistoryEnabled", "0", "", delOff: true);

        // 逆向：Form5 PrivateCheckBox 4
        yield return Hkcu("pv-adid", TweakGroup.Privacy, "关闭广告 ID", TweakRisk.Safe,
            "应用拿不到广告 ID，无法做跨应用个性化广告。",
            "无功能损失。",
            @"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "0", "1");

        // 逆向：Form5 PrivateCheckBox 5
        yield return Hkcu("pv-lang", TweakGroup.Privacy, "不向网站上报语言列表", TweakRisk.Safe,
            "浏览器/应用不再通过 Accept-Language 暴露你装了哪些语言的输入法。",
            "少数网站不会自动切到你偏好的语言。",
            @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", "1", "", delOff: true);

        // 逆向：Form5 PrivateCheckBox 6
        yield return Hkcu("pv-track", TweakGroup.Privacy, "不跟踪应用启动", TweakRisk.Safe,
            "关闭「让 Windows 跟踪应用启动以改进搜索结果」。",
            "开始菜单搜索排序会略差。",
            ExplorerAdvanced, "Start_TrackProgs", "0", "1");

        // 逆向：Form5 PrivateCheckBox 7（三个 SubscribedContent）
        yield return Multi("pv-suggest", TweakGroup.Privacy, "关闭「设置」里的建议与推荐", TweakRisk.Safe,
            "设置应用不再推送「为你推荐」「试试这个功能」这类内容块。",
            "只是少几块推荐位。",
            admin: false, restart: false,
            Sub("SubscribedContent-338393Enabled"),
            Sub("SubscribedContent-353694Enabled"),
            Sub("SubscribedContent-353696Enabled"));

        // 逆向：Form5 PrivateCheckBox 8（HKU\S-1-5-20）
        yield return Multi("pv-delivery", TweakGroup.Privacy, "关闭更新传递优化（P2P 上传）", TweakRisk.Careful,
            "不再把已下载的更新分片上传给其他电脑，也不再从其他电脑下载。",
            "更新下载可能变慢一点；不影响更新本身。",
            admin: true, restart: false,
            new TweakWrite
            {
                Hive = RegistryHive.Users,
                KeyPath = @"S-1-5-20\Software\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Settings",
                ValueName = "DownloadMode", Kind = RegistryValueKind.DWord,
                OnValue = "0", OffValue = "1"
            });

        // 逆向：Form5 PrivateCheckBox 9
        yield return Multi("pv-feature-lock", TweakGroup.Privacy, "锁定 Windows 大版本（不自动升 24H2 之类）", TweakRisk.Careful,
            "用 TargetReleaseVersion 把功能更新钉在当前版本，只收质量更新（安全补丁）。",
            "以后想升级大版本要手动关掉本项；长期留在旧版本会错过新功能。",
            admin: true, restart: false,
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine,
                KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
                ValueName = "TargetReleaseVersion", Kind = RegistryValueKind.DWord,
                OnValue = "1", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine,
                KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
                ValueName = "TargetReleaseVersionInfo", Kind = RegistryValueKind.String,
                OnValue = "@CurrentVersion", DeleteWhenOff = true
            });

        // 逆向：Form5 PrivateCheckBox 10
        yield return Hklm("pv-mrt", TweakGroup.Privacy, "更新里不带恶意软件删除工具", TweakRisk.Safe,
            "Windows 更新不再推送 MSRT（每月一次的全盘扫描器）。",
            "少一层「顺手扫一下」的兜底，但正常杀软会覆盖。",
            @"SOFTWARE\Policies\Microsoft\MRT", "DontOfferThroughWUAU",
            "1", "", admin: true, delOff: true);

        // 逆向：Form1 TurnTool "06"
        yield return Multi("pv-pause-update", TweakGroup.Privacy, "暂停 Windows 更新（长期）", TweakRisk.Careful,
            "把更新的暂停截止时间写到 2042 年，等于长期不再自动更新。",
            "安全补丁也一起停。这是原工具里最有争议的一项：它确实能避免「更新完就出问题」，" +
            "但也会让机器长期暴露在已修补的漏洞下。用完记得还原。",
            admin: true, restart: false,
            Pause("PauseFeatureUpdatesStartTime", "2023-05-08T09:59:52Z"),
            Pause("PauseFeatureUpdatesEndTime", "2042-07-07T09:59:52Z"),
            Pause("PauseQualityUpdatesStartTime", "2023-05-08T09:59:52Z"),
            Pause("PauseQualityUpdatesEndTime", "2042-07-07T09:59:52Z"),
            Pause("PauseUpdatesStartTime", "2023-05-08T09:59:52Z"),
            Pause("PauseUpdatesExpiryTime", "2042-07-07T09:59:52Z"),
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine,
                KeyPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
                ValueName = "FlightSettingsMaxPauseDays", Kind = RegistryValueKind.DWord,
                OnValue = "7000", DeleteWhenOff = true
            });
    }

    // ------------------------------------------------------------------ 系统功能

    private static IEnumerable<TweakItem> SystemTweaks()
    {
        // 逆向：Form4 TurnTool "B"
        yield return Hklm("sys-rdp", TweakGroup.System, "关闭远程桌面（拒绝入站连接）", TweakRisk.Safe,
            "把 fDenyTSConnections 设为 1，别人无法远程桌面连进来。",
            "自己也没法远程连这台机器。需要时关掉本项即可。",
            @"SYSTEM\CurrentControlSet\Control\Terminal Server", "fDenyTSConnections",
            "1", "0", admin: true);

        // 逆向：Form4 TurnTool "A"
        yield return Hklm("sys-ra", TweakGroup.System, "关闭远程协助邀请", TweakRisk.Safe,
            "禁止别人通过「Windows 远程协助」发邀请接管桌面。",
            "正规远程协助要改用第三方工具。",
            @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp",
            "0", "1", admin: true);

        // 逆向：Form1 TurnTool "52"
        yield return Hklm("sys-hvci", TweakGroup.System, "关闭内存完整性（HVCI）", TweakRisk.Risky,
            "关掉基于虚拟化的代码完整性保护。部分老驱动与反作弊会和它冲突，关掉能解决「驱动装不上」。",
            "内核级恶意代码更难被发现；这是实打实的防护降级。需要重启。",
            @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled",
            "0", "1", admin: true, restart: true);

        // 逆向：Form1 TurnTool "54"
        yield return Multi("sys-vbs", TweakGroup.System, "关闭基于虚拟化的安全性（VBS）", TweakRisk.Risky,
            "关掉 VBS。它会占用一部分 CPU 与内存，虚拟机、模拟器、部分反作弊对它敏感。",
            "Credential Guard 等依赖 VBS 的防护一起失效。需要重启。",
            admin: true, restart: true,
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine,
                KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard",
                ValueName = "EnableVirtualizationBasedSecurity", Kind = RegistryValueKind.DWord,
                OnValue = "0", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine,
                KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard",
                ValueName = "HVCIMATRequired", Kind = RegistryValueKind.DWord,
                OnValue = "0", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine,
                KeyPath = @"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard",
                ValueName = "LsaCfgFlags", Kind = RegistryValueKind.DWord,
                OnValue = "0", DeleteWhenOff = true
            },
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine,
                KeyPath = @"SYSTEM\CurrentControlSet\Control\DeviceGuard",
                ValueName = "EnableVirtualizationBasedSecurity", Kind = RegistryValueKind.DWord,
                OnValue = "0", DeleteWhenOff = true
            });

        // 逆向：Form1 TurnTool "53"
        yield return Hklm("sys-wdac", TweakGroup.System, "关闭智能应用控制（WDAC 策略）", TweakRisk.Risky,
            "把 VerifiedAndReputablePolicyState 设为 0，关闭「只允许可信应用运行」的强制策略。",
            "未签名或信誉未知的程序不再被拦；防护降级，且系统可能不允许再打开该功能。",
            @"SYSTEM\CurrentControlSet\Control\CI\Policy", "VerifiedAndReputablePolicyState",
            "0", "1", admin: true, restart: true);

        // 逆向：Form1 TurnTool "51"
        yield return Multi("sys-spectre", TweakGroup.System, "关闭 Meltdown / Spectre 缓解", TweakRisk.Risky,
            "关掉针对 CPU 侧信道漏洞的软件缓解。老 CPU 上能明显提升性能（部分场景 5~15%）。",
            "机器重新暴露在这两个已知漏洞下。只在完全清楚风险时打开。需要重启。",
            admin: true, restart: true,
            Mm("FeatureSettingsOverride", "3"),
            Mm("FeatureSettingsOverrideMask", "3"),
            Mm("FeatureSettings", "1"));

        // 逆向：Form1 TurnTool "57"（只降通知级别，不碰 EnableLUA）
        yield return Multi("sys-uac", TweakGroup.System, "UAC 降到「从不通知」", TweakRisk.Risky,
            "把管理员提权提示关成静默批准。装软件、跑脚本时不再弹窗。",
            "任何程序都能借你的管理员权限静默提权——这是最常被恶意软件利用的一环。" +
            "原工具还会顺手把 EnableLUA 写成 0（那样商店应用会直接坏掉），这里刻意不碰 EnableLUA。",
            admin: true, restart: true,
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine, KeyPath = PolicySystem,
                ValueName = "ConsentPromptBehaviorAdmin", Kind = RegistryValueKind.DWord,
                OnValue = "0", OffValue = "5"
            },
            new TweakWrite
            {
                Hive = RegistryHive.LocalMachine, KeyPath = PolicySystem,
                ValueName = "PromptOnSecureDesktop", Kind = RegistryValueKind.DWord,
                OnValue = "0", OffValue = "1"
            });

        // 参考 Sophia Script：BBR2 拥塞控制
        yield return new TweakItem
        {
            Id = "sys-bbr2",
            Group = TweakGroup.System,
            Kind = TweakKind.Command,
            Risk = TweakRisk.Careful,
            Title = "TCP 拥塞控制切到 BBR2",
            Desc = "把 Internet / Datacenter 模板的拥塞控制算法从 CUBIC 换成 BBR2，高延迟、丢包链路上吞吐更稳。",
            Effect = "需要系统支持 BBR2（Win11 22H2 起默认有）；不支持时会失败，状态保持原样。",
            NeedsAdmin = true,
            OnCommand = "netsh int tcp set supplemental Template=Internet CongestionProvider=bbr2 && " +
                        "netsh int tcp set supplemental Template=Datacenter CongestionProvider=bbr2 && " +
                        "netsh int tcp set supplemental Template=InternetCustom CongestionProvider=bbr2 && " +
                        "netsh int tcp set supplemental Template=DatacenterCustom CongestionProvider=bbr2",
            OffCommand = "netsh int tcp set supplemental Template=Internet CongestionProvider=CUBIC && " +
                         "netsh int tcp set supplemental Template=Datacenter CongestionProvider=CUBIC && " +
                         "netsh int tcp set supplemental Template=InternetCustom CongestionProvider=CUBIC && " +
                         "netsh int tcp set supplemental Template=DatacenterCustom CongestionProvider=CUBIC",
            Probe = () =>
            {
                var text = RunHidden("netsh int tcp show supplemental");
                if (text.Length == 0) return "读不到拥塞控制设置（需要管理员）";

                return text.Contains("bbr2", StringComparison.OrdinalIgnoreCase)
                    ? "当前模板里有 bbr2"
                    : "当前模板是 CUBIC / NewReno（未启用 BBR2）";
            },
            ProbeOn = () => RunHidden("netsh int tcp show supplemental")
                .Contains("bbr2", StringComparison.OrdinalIgnoreCase)
        };

        // 参考 Win11Debloat：保留存储在部分机型上会长期占 7 GB
        yield return new TweakItem
        {
            Id = "sys-reserved",
            Group = TweakGroup.System,
            Kind = TweakKind.Command,
            Risk = TweakRisk.Careful,
            Title = "关闭系统保留存储",
            Desc = "关掉 Windows 预留的更新空间（通常 7 GB 左右），C 盘立刻多出这部分可用空间。",
            Effect = "更新时如果空间不够可能失败；空间紧张的机器建议保留。",
            NeedsAdmin = true,
            OnCommand = "DISM.exe /English /Online /Set-ReservedStorageState /State:Disabled",
            OffCommand = "DISM.exe /English /Online /Set-ReservedStorageState /State:Enabled",
            Probe = () =>
            {
                var text = RunHidden("DISM.exe /English /Online /Get-ReservedStorageState");
                if (text.Length == 0) return "读不到保留存储状态（需要管理员）";

                if (text.Contains("Disabled", StringComparison.OrdinalIgnoreCase)) return "保留存储：已关闭";
                if (text.Contains("Enabled", StringComparison.OrdinalIgnoreCase)) return "保留存储：已开启";
                return "保留存储：状态未知";
            },
            ProbeOn = () => RunHidden("DISM.exe /English /Online /Get-ReservedStorageState")
                .Contains("Disabled", StringComparison.OrdinalIgnoreCase)
        };
    }

    // ------------------------------------------------------------------ 辅助构造

    /// <summary>HKLM 单值条目。on/off 用字符串，按 kind 解析。</summary>
    private static TweakItem Hklm(string id, TweakGroup group, string title, TweakRisk risk,
        string desc, string effect, string keyPath, string valueName, string on, string off,
        bool admin = false, bool delOff = false, bool restart = false,
        RegistryValueKind kind = RegistryValueKind.DWord)
        => One(RegistryHive.LocalMachine, id, group, title, risk, desc, effect, keyPath, valueName, on, off,
            admin, delOff, restart, kind);

    /// <summary>HKCU 单值条目（不需要管理员）。</summary>
    private static TweakItem Hkcu(string id, TweakGroup group, string title, TweakRisk risk,
        string desc, string effect, string keyPath, string valueName, string on, string off,
        bool delOff = false, bool restart = false,
        RegistryValueKind kind = RegistryValueKind.DWord)
        => One(RegistryHive.CurrentUser, id, group, title, risk, desc, effect, keyPath, valueName, on, off,
            false, delOff, restart, kind);

    private static TweakItem One(RegistryHive hive, string id, TweakGroup group, string title, TweakRisk risk,
        string desc, string effect, string keyPath, string valueName, string on, string off,
        bool admin, bool delOff, bool restart, RegistryValueKind kind)
    {
        return new TweakItem
        {
            Id = id,
            Group = group,
            Kind = TweakKind.Registry,
            Risk = risk,
            Title = title,
            Desc = desc,
            Effect = effect,
            NeedsAdmin = admin,
            NeedsRestart = restart,
            Writes = new List<TweakWrite>
            {
                new()
                {
                    Hive = hive,
                    KeyPath = keyPath,
                    ValueName = valueName,
                    Kind = kind,
                    OnValue = on,
                    OffValue = off,
                    DeleteWhenOff = delOff
                }
            }
        };
    }

    private static TweakItem Multi(string id, TweakGroup group, string title, TweakRisk risk,
        string desc, string effect, bool admin, bool restart, params TweakWrite[] writes)
    {
        return new TweakItem
        {
            Id = id,
            Group = group,
            Kind = TweakKind.Registry,
            Risk = risk,
            Title = title,
            Desc = desc,
            Effect = effect,
            NeedsAdmin = admin,
            NeedsRestart = restart,
            Writes = writes.ToList()
        };
    }

    /// <summary>
    /// 服务类条目。注意 onStart 是「这项优化生效时」的启动类型：
    /// 这几条全是「关掉某个服务」，所以开启优化 = 设为禁用（4），取消 = 恢复手动（3）。
    /// （原来写反了：点「开启它」反而把服务恢复成自启，是截图里发现并修掉的。）
    /// </summary>
    private static TweakItem Svc(string id, TweakGroup group, string title, TweakRisk risk,
        string desc, string effect, string serviceName, int onStart = 4, int offStart = 3)
    {
        return new TweakItem
        {
            Id = id,
            Group = group,
            Kind = TweakKind.Service,
            Risk = risk,
            Title = title,
            Desc = desc,
            Effect = effect,
            NeedsAdmin = true,
            ServiceName = serviceName,
            OnStart = onStart,
            OffStart = offStart
        };
    }

    private static TweakWrite Sub(string valueName) => new()
    {
        Hive = RegistryHive.CurrentUser,
        KeyPath = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
        ValueName = valueName,
        Kind = RegistryValueKind.DWord,
        OnValue = "0",
        OffValue = "1"
    };

    private static TweakWrite Pause(string valueName, string time) => new()
    {
        Hive = RegistryHive.LocalMachine,
        KeyPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings",
        ValueName = valueName,
        Kind = RegistryValueKind.String,
        OnValue = time,
        DeleteWhenOff = true
    };

    private static TweakWrite Mm(string valueName, string value) => new()
    {
        Hive = RegistryHive.LocalMachine,
        KeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management",
        ValueName = valueName,
        Kind = RegistryValueKind.DWord,
        OnValue = value,
        DeleteWhenOff = true
    };

    private static bool HibernateFileExists()
    {
        var drive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        try { return File.Exists(Path.Combine(drive, "hiberfil.sys")); }
        catch { return false; }
    }

    /// <summary>当前版本号（DisplayVersion，如 23H2）。取不到返回空串，调用方就不要写这个值。</summary>
    public static string CurrentReleaseVersion()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            var text = key?.GetValue("DisplayVersion") as string;
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        catch { /* 读不到返回空 */ }

        return "";
    }

    private static string RunHidden(string command)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(info);
            if (process == null) return "";

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(6000);

            return output;
        }
        catch
        {
            return "";
        }
    }
}
