using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 服务预设：把 AtlasOS 那类项目「批量禁用服务」的做法，做成可以逐项确认、改前留底、随时还原的形式。
///
/// 为什么不是「一键全禁」：服务之间有依赖链，同一个服务在不同用途机器上价值相反
/// （SysMain 在机械盘上是有用的预读，在 SSD 上才接近纯开销；Spooler 有打印机就不能停）。
/// 一把全禁之后，出问题的人不知道该先恢复哪一个。
///
/// 所以这里的做法是：
/// - 每项写清「禁用后会发生什么」，不只写服务名；
/// - 分 safe / careful 两档，careful 项必须自己看说明再勾；
/// - **只改启动类型，不立即停止服务**——正在跑的东西被硬停会中断用户手上的活；
/// - 改前把原值写进 service-preset-backup.json，一个按钮全部还原；
/// - 保护名单里的服务（Defender、更新、RPC、即插即用等）在代码里硬拦，界面上不出现。
/// </summary>
public static class ServicePresetService
{
    private const string BackupName = "service-preset-backup.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string BackupFile => Path.Combine(AppPaths.OutputDir, BackupName);

    // ---------------------------------------------------------------- 模型

    public sealed class Preset
    {
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        public string Title { get; init; } = "";

        /// <summary>禁用之后用户会感觉到什么。</summary>
        public string Effect { get; init; } = "";

        /// <summary>safe / careful。careful 需要看说明再勾。</summary>
        public string Risk { get; init; } = "safe";

        /// <summary>AtlasOS 是否默认禁用这项（供参考，不代表我们也该禁）。</summary>
        public bool AtlasDisables { get; init; }

        // 运行时状态
        public bool Installed { get; set; }
        public bool Disabled { get; set; }
        public bool Running { get; set; }
        public int DependentCount { get; set; }

        public string RiskText => Risk == "careful" ? "谨慎" : "可停";

        public string StateText => !Installed
            ? "未安装"
            : Disabled ? "已禁用" : Running ? "运行中" : "已停止";
    }

    /// <summary>备份里的一条：服务名 + 原启动类型。</summary>
    private sealed class BackupItem
    {
        public string Name { get; set; } = "";
        public int StartRaw { get; set; }
        public bool Delayed { get; set; }
        public string TakenAt { get; set; } = "";
    }

    // ---------------------------------------------------------------- 清单

    /// <summary>
    /// 预设清单。刻意做得短——只收「禁用后能说清后果、且不会让系统起不来」的那些。
    /// 大量「Atlas 也禁了」的服务没有收录，因为它们要么是硬件相关（因机器而异），
    /// 要么一旦出错很难排查。
    /// </summary>
    private static readonly Preset[] Catalog =
    {
        // ---------- 遥测与诊断 ----------
        new() { Name = "DiagTrack", Category = "遥测与诊断", Risk = "safe", AtlasDisables = true,
            Title = "连接用户体验和遥测",
            Effect = "停止向微软上传使用数据与诊断信息。不影响系统更新，也不影响功能。" },

        new() { Name = "dmwappushservice", Category = "遥测与诊断", Risk = "safe", AtlasDisables = true,
            Title = "WAP 推送消息路由",
            Effect = "停止接收运营商/微软的 WAP 推送消息。桌面机器上基本用不到。" },

        new() { Name = "WerSvc", Category = "遥测与诊断", Risk = "safe", AtlasDisables = true,
            Title = "Windows 错误报告",
            Effect = "程序崩溃时不再生成并上传错误报告。副作用是崩溃后也没有本地记录可查。" },

        new() { Name = "wisvc", Category = "遥测与诊断", Risk = "safe", AtlasDisables = true,
            Title = "Windows 预览体验计划",
            Effect = "不再参与 Windows Insider 预览。普通用户用不到。" },

        new() { Name = "DPS", Category = "遥测与诊断", Risk = "careful",
            Title = "诊断策略服务",
            Effect = "「设置 → 疑难解答」里的自动诊断会不可用。想用系统自带排错工具就留着。" },

        new() { Name = "WdiServiceHost", Category = "遥测与诊断", Risk = "careful",
            Title = "诊断服务宿主",
            Effect = "同上，属于诊断框架的一部分。禁用后疑难解答可能报错。" },

        new() { Name = "WdiSystemHost", Category = "遥测与诊断", Risk = "careful",
            Title = "诊断系统宿主",
            Effect = "同上。" },

        // ---------- 推广与演示 ----------
        new() { Name = "RetailDemo", Category = "推广与演示", Risk = "safe", AtlasDisables = true,
            Title = "零售演示模式",
            Effect = "关闭商店样机用的演示模式。个人机器上没有任何用途。" },

        new() { Name = "MapsBroker", Category = "推广与演示", Risk = "safe", AtlasDisables = true,
            Title = "已下载地图管理",
            Effect = "「离线地图」功能不可用。用「地图」应用才会碰到。" },

        new() { Name = "WMPNetworkSvc", Category = "推广与演示", Risk = "safe", AtlasDisables = true,
            Title = "WMP 网络共享",
            Effect = "Windows Media Player 的媒体库网络共享不可用。不用 WMP 共享就没影响。" },

        new() { Name = "Fax", Category = "推广与演示", Risk = "safe",
            Title = "传真",
            Effect = "传真功能不可用。极少有人用。" },

        // ---------- Xbox ----------
        new() { Name = "XblAuthManager", Category = "Xbox", Risk = "safe", AtlasDisables = true,
            Title = "Xbox Live 身份验证",
            Effect = "Xbox 应用与部分 Xbox 服务的登录失效。**玩 Game Pass / Xbox 商店游戏需要它**，玩普通 Steam 游戏不需要。" },

        new() { Name = "XblGameSave", Category = "Xbox", Risk = "safe", AtlasDisables = true,
            Title = "Xbox Live 游戏存档同步",
            Effect = "Xbox 游戏的云存档同步失效。只在玩 Xbox 生态游戏时有影响。" },

        new() { Name = "XboxNetApiSvc", Category = "Xbox", Risk = "safe", AtlasDisables = true,
            Title = "Xbox Live 网络服务",
            Effect = "Xbox 网络功能与部分游戏的多人联机不可用。同上，只有 Xbox 生态游戏受影响。" },

        new() { Name = "XboxGipSvc", Category = "Xbox", Risk = "safe",
            Title = "Xbox 配件服务",
            Effect = "Xbox 手柄（尤其是通过无线适配器连接的）可能无法识别。**用 Xbox 手柄就别禁**。" },

        // ---------- 搜索与索引 ----------
        new() { Name = "WSearch", Category = "搜索与索引", Risk = "careful", AtlasDisables = true,
            Title = "Windows Search 索引",
            Effect = "开始菜单与资源管理器的**内容搜索**会变得很慢（只能按文件名搜），邮件与 Outlook 搜索失效。"
                   + "换来的是不再后台扫描磁盘。SSD 机器上收益有限。" },

        new() { Name = "SysMain", Category = "搜索与索引", Risk = "careful", AtlasDisables = true,
            Title = "SysMain（原 Superfetch）预读",
            Effect = "**机械硬盘上不要禁**——它靠预读明显改善开机与启动常用程序的体验。"
                   + "SSD 上收益接近零，禁用可以减少一点后台 I/O。" },

        // ---------- 远程与共享 ----------
        new() { Name = "RemoteRegistry", Category = "远程与共享", Risk = "safe", AtlasDisables = true,
            Title = "远程注册表",
            Effect = "其他机器无法远程读写本机注册表。**这一项禁用是纯收益**——它历史上是横向移动的常用入口，"
                   + "正常个人机器不需要它。" },

        new() { Name = "TermService", Category = "远程与共享", Risk = "careful",
            Title = "远程桌面服务",
            Effect = "**用远程桌面连这台机器就别禁**。禁用后无法被 RDP 连接（不影响你主动去连别人）。" },

        new() { Name = "RemoteAccess", Category = "远程与共享", Risk = "careful",
            Title = "路由和远程访问",
            Effect = "本机不再提供拨号/VPN 服务端功能。不用它做服务端就没影响。" },

        new() { Name = "SharedAccess", Category = "远程与共享", Risk = "careful",
            Title = "Internet 连接共享",
            Effect = "无法把本机网络共享给其他设备（比如把电脑当热点）。" },

        new() { Name = "icssvc", Category = "远程与共享", Risk = "careful",
            Title = "Windows 移动热点",
            Effect = "「移动热点」功能不可用。用电脑给手机开热点就别禁。" },

        // ---------- 外设 ----------
        new() { Name = "Spooler", Category = "外设", Risk = "careful", AtlasDisables = true,
            Title = "打印后台处理",
            Effect = "**有打印机（含虚拟 PDF 打印机）就别禁**。禁用后无法打印，"
                   + "但它是历史上被利用最多的服务之一，没有打印机时禁用是纯收益。" },

        new() { Name = "TabletInputService", Category = "外设", Risk = "careful",
            Title = "触摸键盘与手写面板",
            Effect = "触屏设备上的屏幕键盘、手写输入不可用。台式机可以禁。" },

        new() { Name = "BthAvctpSvc", Category = "外设", Risk = "careful",
            Title = "蓝牙音频控制",
            Effect = "**用蓝牙耳机/音箱就别禁**。禁用后蓝牙音频设备可能无法控制音量与播放。" }
    };

    /// <summary>
    /// 保护名单：这些服务**永远不提供禁用入口**。写死在代码里，界面上连勾选框都不出现。
    ///
    /// 选它们的理由只有一条：禁了之后系统会以难以排查的方式坏掉，
    /// 或者直接失去安全更新（Defender / 更新 / 加密服务）。
    /// </summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "SecurityHealthService", "wuauserv", "BITS", "CryptSvc", "TrustedInstaller",
        "RpcSs", "RpcEptMapper", "DcomLaunch", "Power", "PlugPlay", "EventLog", "ProfSvc",
        "gpsvc", "Schedule", "Winmgmt", "LanmanWorkstation", "Nsi", "Dhcp", "Dnscache",
        "mpssvc", "BFE", "LSM", "SamSs", "KeyIso", "Wcmsvc", "NlaSvc", "netprofm"
    };

    // ---------------------------------------------------------------- 扫描

    /// <summary>读当前服务状态，合并到清单上。</summary>
    public static List<Preset> Scan()
    {
        var installed = new Dictionary<string, ServiceEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in ServiceInventoryService.Scan())
            installed[s.Name] = s;

        // 谁依赖谁：把每个服务的 Dependencies 反过来建索引
        var dependents = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in installed.Values)
        {
            foreach (var dep in SplitDeps(s.Dependencies))
            {
                dependents[dep] = dependents.TryGetValue(dep, out var n) ? n + 1 : 1;
            }
        }

        var list = new List<Preset>();

        foreach (var p in Catalog)
        {
            // 保护名单里的服务即使误写进清单也不显示
            if (Protected.Contains(p.Name)) continue;

            var has = installed.TryGetValue(p.Name, out var entry);

            list.Add(new Preset
            {
                Name = p.Name,
                Category = p.Category,
                Title = p.Title,
                Effect = p.Effect,
                Risk = p.Risk,
                AtlasDisables = p.AtlasDisables,
                Installed = has,
                Disabled = has && entry.StartRaw == 4,
                Running = has && entry.Running,
                DependentCount = dependents.TryGetValue(p.Name, out var d) ? d : 0
            });
        }

        return list;
    }

    private static IEnumerable<string> SplitDeps(string raw)
        => (raw ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>自检用：保护名单与清单不能有交集，否则等于自己打自己。</summary>
    public static bool CatalogRespectsProtected()
        => Catalog.All(p => !Protected.Contains(p.Name));

    /// <summary>自检用：清单里不能出现保护名单中的服务名。</summary>
    public static IReadOnlyList<string> ProtectedNames => Protected.ToList();

    public static IReadOnlyList<string> CatalogNames => Catalog.Select(p => p.Name).ToList();

    // ---------------------------------------------------------------- 应用与还原

    /// <summary>当前是否有可还原的备份。</summary>
    public static bool HasBackup() => LoadBackup().Count > 0;

    /// <summary>备份里记录的服务数。</summary>
    public static int BackupCount() => LoadBackup().Count;

    /// <summary>
    /// 把选中的服务设为「禁用」。
    ///
    /// 只改启动类型，不停止正在运行的服务——硬停会中断用户手上的活，
    /// 而且停服务失败通常还要处理依赖，收益不值这个风险。改动在下次重启后完全生效。
    /// </summary>
    public static (bool Ok, string Message) Apply(IEnumerable<string> names)
    {
        if (!ElevationService.IsAdmin)
            return (false, "修改服务启动类型需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        var targets = (names ?? Enumerable.Empty<string>())
            .Where(n => !Protected.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0) return (false, "没有选中任何服务。");

        var backup = LoadBackup();
        var known = backup.Select(b => b.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var current = ServiceInventoryService.Scan()
            .ToDictionary(s => s.Name, s => s, StringComparer.OrdinalIgnoreCase);

        var ok = 0;
        var failed = new List<string>();

        foreach (var name in targets)
        {
            if (!current.TryGetValue(name, out var entry))
            {
                failed.Add(name + "（未安装）");
                continue;
            }

            // 第一次改之前先记原值；已经记过的不覆盖，否则会把「已改后的值」当成原值
            if (!known.Contains(name))
            {
                backup.Add(new BackupItem
                {
                    Name = name,
                    StartRaw = entry.StartRaw,
                    Delayed = entry.DelayedAutoStart,
                    TakenAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                });
                known.Add(name);
            }

            var (success, message) = ServiceInventoryService.SetStartType(name, 4);
            if (success) ok++;
            else failed.Add($"{name}（{message}）");
        }

        SaveBackup(backup);

        var detail = failed.Count == 0
            ? $"已禁用 {ok} 项。原值已留存，可随时还原；重启后完全生效。"
            : $"成功 {ok} 项，失败 {failed.Count} 项：{string.Join("、", failed.Take(5))}";

        return (ok > 0, detail);
    }

    /// <summary>按备份还原全部改动。</summary>
    public static (bool Ok, string Message) Restore()
    {
        if (!ElevationService.IsAdmin)
            return (false, "还原服务启动类型需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        var backup = LoadBackup();
        if (backup.Count == 0) return (false, "没有可还原的记录。");

        var ok = 0;
        var failed = new List<string>();

        foreach (var item in backup)
        {
            var (success, message) = ServiceInventoryService.SetStartType(item.Name, item.StartRaw);
            if (success) ok++;
            else failed.Add($"{item.Name}（{message}）");
        }

        if (failed.Count == 0)
        {
            // 全部还原成功才清空备份，否则留着让用户能再试
            try { File.Delete(BackupFile); } catch { }
        }

        return (ok > 0, failed.Count == 0
            ? $"已还原 {ok} 项到改动前的启动类型。"
            : $"还原 {ok} 项，失败 {failed.Count} 项：{string.Join("、", failed.Take(5))}");
    }

    // ---------------------------------------------------------------- 备份存取

    private static List<BackupItem> LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupFile)) return new List<BackupItem>();
            return JsonSerializer.Deserialize<List<BackupItem>>(File.ReadAllText(BackupFile), Json)
                   ?? new List<BackupItem>();
        }
        catch
        {
            return new List<BackupItem>();
        }
    }

    private static void SaveBackup(List<BackupItem> items)
    {
        try
        {
            AppPaths.Ensure();
            Directory.CreateDirectory(AppPaths.OutputDir);
            File.WriteAllText(BackupFile,
                JsonSerializer.Serialize(items, Json), new UTF8Encoding(false));
        }
        catch { }
    }
}
