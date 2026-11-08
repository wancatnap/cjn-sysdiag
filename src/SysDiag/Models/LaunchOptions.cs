using SysDiag.Services;

namespace SysDiag.Models;

/// <summary>
/// 启动参数。界面用的参数（--admin / --open-usn）与命令行批处理参数（--selftest 等）
/// 走不同分支：前者打开界面，后者不弹窗。
/// </summary>
public sealed class LaunchOptions
{
    public static LaunchOptions None { get; } = new();

    /// <summary>本次是由「以管理员身份重启」拉起的实例。</summary>
    public bool AdminMode { get; private init; }

    /// <summary>启动后自动打开「文件删除记录」窗口并开始读取。</summary>
    public bool OpenUsn { get; private init; }

    /// <summary>启动后自动打开「网卡 MAC」窗口（set / restore）。</summary>
    public bool OpenMac { get; private init; }

    public string MacMode { get; private init; } = "set";

    /// <summary>启动后自动打开「痕迹清理」窗口。</summary>
    /// <summary>启动后自动打开「显卡」窗口。</summary>
    public bool OpenGpu { get; private init; }

    /// <summary>启动后自动打开「系统调优」窗口。</summary>
    public bool OpenTune { get; private init; }

    /// <summary>启动后自动打开「DLSS 模型」窗口。</summary>
    public bool OpenDlss { get; private init; }

    /// <summary>启动后自动打开「DNS 优化」窗口。</summary>
    public bool OpenDns { get; private init; }

    /// <summary>启动后自动打开「断网急救」窗口。</summary>
    public bool OpenNetRepair { get; private init; }

    /// <summary>启动后自动打开「深度硬件痕迹」窗口。</summary>
    public bool OpenDeepTrace { get; private init; }

    /// <summary>启动后自动打开「程序执行记录」窗口。</summary>
    public bool OpenExecTrace { get; private init; }

    /// <summary>启动后自动打开「C 盘清理」窗口。</summary>
    public bool OpenDiskClean { get; private init; }

    /// <summary>启动后自动打开「外挂痕迹体检」窗口。</summary>
    public bool OpenCheatScan { get; private init; }

    /// <summary>启动后自动打开「日志规则匹配」窗口。</summary>
    public bool OpenSigma { get; private init; }

    /// <summary>启动后自动打开「本地执行痕迹」窗口。</summary>
    public bool OpenPrefetch { get; private init; }

    /// <summary>启动后自动打开「进程句柄排查」窗口。</summary>
    public bool OpenHandle { get; private init; }

    /// <summary>启动后自动打开「挂机监控」窗口。</summary>
    public bool OpenMonitor { get; private init; }

    /// <summary>启动后自动打开「事件查看器」窗口。</summary>
    public bool OpenEvents { get; private init; }

    /// <summary>启动后自动打开「机器码与出厂校验」窗口。</summary>
    public bool OpenMachine { get; private init; }

    /// <summary>启动后自动打开「机器码伪造检测」窗口（不需要出厂基线）。</summary>
    public bool OpenHwid { get; private init; }

    /// <summary>启动后自动打开「注册表清理」窗口。</summary>
    public bool OpenRegClean { get; private init; }

    /// <summary>启动后自动打开「系统信息」窗口（含精简系统判定）。</summary>
    public bool OpenSysInfo { get; private init; }

    /// <summary>启动后自动打开「系统优化」窗口。</summary>
    public bool OpenTweak { get; private init; }

    /// <summary>启动后自动打开「显示器配色（ICC）」窗口。</summary>
    public bool OpenIcc { get; private init; }

    /// <summary>启动后自动打开「手动调色」窗口。</summary>
    public bool OpenColorTune { get; private init; }

    /// <summary>启动后自动打开「大文件管理」窗口。</summary>
    public bool OpenBigFile { get; private init; }

    /// <summary>调色窗口打开时套一个非中性预设（截图核对 / 演示用）。</summary>
    public bool TuneDemo { get; private init; }

    /// <summary>启动后自动打开「延迟优化」窗口。</summary>
    public bool OpenLatency { get; private init; }

    /// <summary>启动后自动打开「延迟实测 / 对照实验」窗口。</summary>
    public bool OpenLatencyLab { get; private init; }

    /// <summary>启动后自动打开「CPU 停核 / C-State」窗口。</summary>
    public bool OpenCoreParking { get; private init; }

    /// <summary>启动后自动打开「免重装精简」窗口。</summary>
    public bool OpenSlim { get; private init; }

    /// <summary>启动后自动打开「优化推论」窗口。</summary>
    public bool OpenNotes { get; private init; }

    /// <summary>启动后自动打开「直播伴侣优化」窗口。</summary>
    public bool OpenLive { get; private init; }

    /// <summary>启动后自动打开「内存」窗口（提权重启后回到这里）。</summary>
    public bool OpenMemory { get; private init; }

    /// <summary>启动后自动打开「重复文件」窗口。</summary>
    public bool OpenDuplicate { get; private init; }

    /// <summary>--open-dup 后面跟的目录；为空则用默认下载目录，不自动扫描。</summary>
    public string OpenDuplicatePath { get; private init; } = "";

    /// <summary>启动后自动打开「防护状态审查」窗口。</summary>
    public bool OpenDefenderReview { get; private init; }

    /// <summary>启动后自动打开「服务预设」窗口。</summary>
    public bool OpenServicePreset { get; private init; }

    /// <summary>命令行直接还原所有显示器的 ICC（不打开窗口，用于「画面不对」时的急救）。</summary>
    public bool IccRestore { get; private init; }

    /// <summary>启动后自动打开「游戏优化」窗口。</summary>
    public bool OpenDelta { get; private init; }

    /// <summary>启动后自动打开「开机自启动」窗口。</summary>
    public bool OpenAutostart { get; private init; }

    /// <summary>
    /// 启动后自动打开某个「系统工具页」（设备 / 服务 / 电源 / 可靠性 / 软件 / 进程 / 网卡 / 防火墙 / 磁盘）。
    /// 用的是 --open-tool=&lt;key&gt; 一个参数，避免为九个页面各加一个布尔。
    /// </summary>
    public string OpenTool { get; private init; } = "";

    /// <summary>启动后弹一个内部对话框样例（验收样式用，不写数据）。</summary>
    public bool PreviewDialog { get; private init; }

    /// <summary>启动后弹「进程优先级与亲和性」对话框（验收样式用，只读自己的进程，不应用）。</summary>
    public bool PreviewTune { get; private init; }

    /// <summary>启动后弹「服务失败恢复」对话框（验收样式用，只读取不写入）。</summary>
    public bool PreviewRecovery { get; private init; }

    /// <summary>启动后写一份内存明细报告并退出（自证用，不改任何数据）。</summary>
    public bool MemReport { get; private init; }

    /// <summary>使用旧版界面主题（保留优化前的样式，便于对照与回退）。</summary>
    public bool LegacyTheme { get; private init; }

    public string UsnDrive { get; private init; } = "C";

    public int UsnHours { get; private init; } = 168;

    /// <summary>只含界面参数，不交给命令行自检。</summary>
    public bool HasUiIntent => AdminMode || OpenUsn || OpenMac || OpenGpu || OpenTune || OpenDlss || OpenDns || OpenNetRepair || OpenDeepTrace || OpenExecTrace || OpenDiskClean || OpenCheatScan || OpenSigma || OpenPrefetch || OpenHandle || OpenMonitor || OpenEvents || OpenMachine || OpenHwid || OpenRegClean || OpenSysInfo || OpenTweak || OpenIcc || OpenColorTune || OpenBigFile || OpenLatency || OpenLatencyLab || OpenCoreParking || OpenSlim || OpenNotes || OpenLive || OpenMemory || OpenDuplicate || OpenDefenderReview || OpenServicePreset || IccRestore || OpenDelta || OpenAutostart || OpenTool.Length > 0 || PreviewDialog || PreviewTune || PreviewRecovery || MemReport || LegacyTheme;

    public static LaunchOptions Parse(string[] args)
    {
        var admin = false;
        var openUsn = false;
        var openMac = false;
        var openGpu = false;
        var openTune = false;
        var openDlss = false;
        var openDns = false;
        var openNetRepair = false;
        var openDeepTrace = false;
        var openExecTrace = false;
        var openDiskClean = false;
        var openCheatScan = false;
        var openSigma = false;
        var openPrefetch = false;
        var openHandle = false;
        var openMonitor = false;
        var openEvents = false;
        var openMachine = false;
        var openHwid = false;
        var openRegClean = false;
        var openSysInfo = false;
        var openTool = "";
        var openTweak = false;
        var openIcc = false;
        var openColorTune = false;
        var openBigFile = false;
        var tuneDemo = false;
        var openLatency = false;
        var openLatencyLab = false;
        var openCoreParking = false;
        var openSlim = false;
        var openNotes = false;
        var openLive = false;
        var openMemory = false;
        var openDuplicate = false;
        var dupPath = "";
        var openDefenderReview = false;
        var openServicePreset = false;
        var iccRestore = false;
        var openDelta = false;
        var openAutostart = false;
        var previewDialog = false;
        var previewTune = false;
        var previewRecovery = false;
        var memReport = false;
        var legacyTheme = false;
        var macMode = "set";
        var drive = "C";
        var hours = 168;

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];

            if (a.Equals("--admin", StringComparison.OrdinalIgnoreCase))
            {
                admin = true;
                continue;
            }

            if (a.Equals("--open-gpu", StringComparison.OrdinalIgnoreCase))
            {
                openGpu = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-tune", StringComparison.OrdinalIgnoreCase))
            {
                openTune = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-dlss", StringComparison.OrdinalIgnoreCase))
            {
                openDlss = true;
                continue;
            }

            if (a.Equals("--open-dns", StringComparison.OrdinalIgnoreCase))
            {
                openDns = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-deeptrace", StringComparison.OrdinalIgnoreCase))
            {
                openDeepTrace = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-exectrace", StringComparison.OrdinalIgnoreCase))
            {
                openExecTrace = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-diskclean", StringComparison.OrdinalIgnoreCase))
            {
                openDiskClean = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-cheatscan", StringComparison.OrdinalIgnoreCase))
            {
                openCheatScan = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-sigma", StringComparison.OrdinalIgnoreCase))
            {
                openSigma = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-prefetch", StringComparison.OrdinalIgnoreCase))
            {
                openPrefetch = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-handle", StringComparison.OrdinalIgnoreCase))
            {
                openHandle = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-monitor", StringComparison.OrdinalIgnoreCase))
            {
                openMonitor = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-events", StringComparison.OrdinalIgnoreCase))
            {
                openEvents = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-machine", StringComparison.OrdinalIgnoreCase))
            {
                openMachine = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-hwid", StringComparison.OrdinalIgnoreCase))
            {
                openHwid = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-tweak", StringComparison.OrdinalIgnoreCase))
            {
                openTweak = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-icc", StringComparison.OrdinalIgnoreCase))
            {
                openIcc = true;
                continue;
            }

            if (a.Equals("--open-color-tune", StringComparison.OrdinalIgnoreCase))
            {
                openColorTune = true;
                continue;
            }

            if (a.Equals("--open-big-file", StringComparison.OrdinalIgnoreCase))
            {
                openBigFile = true;
                continue;
            }

            if (a.Equals("--tune-demo", StringComparison.OrdinalIgnoreCase))
            {
                tuneDemo = true;
                continue;
            }

            if (a.Equals("--icc-restore", StringComparison.OrdinalIgnoreCase))
            {
                iccRestore = true;
                continue;
            }

            if (a.Equals("--open-latency", StringComparison.OrdinalIgnoreCase))
            {
                openLatency = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-latency-lab", StringComparison.OrdinalIgnoreCase))
            {
                openLatencyLab = true;
                continue;
            }

            if (a.Equals("--open-core-parking", StringComparison.OrdinalIgnoreCase))
            {
                // 与卡片点击一致：直接以管理员打开，省掉「先只读再点提权」这一步
                openCoreParking = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-slim", StringComparison.OrdinalIgnoreCase))
            {
                // 同理：扫描本身只读，但后续操作都要管理员，直接以管理员打开
                openSlim = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-notes", StringComparison.OrdinalIgnoreCase))
            {
                openNotes = true;
                continue;
            }

            if (a.Equals("--open-live", StringComparison.OrdinalIgnoreCase))
            {
                openLive = true;
                continue;
            }

            if (a.Equals("--open-memory", StringComparison.OrdinalIgnoreCase))
            {
                openMemory = true;
                continue;
            }

            if (a.Equals("--open-dup", StringComparison.OrdinalIgnoreCase))
            {
                openDuplicate = true;

                // 可选的目录参数：给了就在打开后直接扫，省去手动选
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                    dupPath = args[++i];

                continue;
            }

            if (a.Equals("--open-defender-review", StringComparison.OrdinalIgnoreCase))
            {
                openDefenderReview = true;
                continue;
            }

            if (a.Equals("--open-service-preset", StringComparison.OrdinalIgnoreCase))
            {
                // 扫描只读；真正改启动类型时才需要管理员，窗口里会提示
                openServicePreset = true;
                continue;
            }

            if (a.Equals("--open-delta", StringComparison.OrdinalIgnoreCase))
            {
                openDelta = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-autostart", StringComparison.OrdinalIgnoreCase))
            {
                openAutostart = true;
                continue;
            }

            // 自启动时带上来的参数：不改变任何界面，只是让程序知道「这次是自动启动的」
            if (a.Equals("--autostart", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (a.StartsWith("--open-tool=", StringComparison.OrdinalIgnoreCase))
            {
                openTool = a["--open-tool=".Length..].Trim().ToLowerInvariant();
                admin = true;
                continue;
            }

            if (a.Equals("--open-sysinfo", StringComparison.OrdinalIgnoreCase))
            {
                openSysInfo = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-regclean", StringComparison.OrdinalIgnoreCase))
            {
                openRegClean = true;
                admin = true;
                continue;
            }

            if (a.Equals("--preview-tune", StringComparison.OrdinalIgnoreCase))
            {
                previewTune = true;
                continue;
            }

            if (a.Equals("--preview-recovery", StringComparison.OrdinalIgnoreCase))
            {
                previewRecovery = true;
                continue;
            }

            if (a.Equals("--preview-dialog", StringComparison.OrdinalIgnoreCase))
            {
                previewDialog = true;
                continue;
            }

            if (a.Equals("--mem-report", StringComparison.OrdinalIgnoreCase))
            {
                memReport = true;
                continue;
            }

            if (a.Equals("--legacy-theme", StringComparison.OrdinalIgnoreCase))
            {
                legacyTheme = true;
                continue;
            }

            if (a.Equals("--open-netrepair", StringComparison.OrdinalIgnoreCase))
            {
                openNetRepair = true;
                admin = true;
                continue;
            }

            if (a.Equals("--open-mac", StringComparison.OrdinalIgnoreCase))
            {
                openMac = true;
                admin = true;

                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    var mode = args[++i];
                    macMode = mode.Equals("restore", StringComparison.OrdinalIgnoreCase) ? "restore" : "set";
                }

                continue;
            }

            if (!a.Equals("--open-usn", StringComparison.OrdinalIgnoreCase)) continue;

            openUsn = true;
            admin = true;

            if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                drive = args[++i];

            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var h))
            {
                hours = h;
                i++;
            }
        }

        return new LaunchOptions
        {
            AdminMode = admin,
            OpenUsn = openUsn,
            OpenMac = openMac,
            MacMode = macMode,
            OpenGpu = openGpu,
            OpenTune = openTune,
            OpenDlss = openDlss,
            OpenDns = openDns,
            OpenNetRepair = openNetRepair,
            OpenDeepTrace = openDeepTrace,
            OpenExecTrace = openExecTrace,
            OpenDiskClean = openDiskClean,
            OpenCheatScan = openCheatScan,
            OpenSigma = openSigma,
            OpenPrefetch = openPrefetch,
            OpenHandle = openHandle,
            OpenMonitor = openMonitor,
            OpenEvents = openEvents,
            OpenMachine = openMachine,
            OpenHwid = openHwid,
            OpenRegClean = openRegClean,
            OpenSysInfo = openSysInfo,
            OpenTool = openTool,
            OpenTweak = openTweak,
            OpenIcc = openIcc,
            OpenColorTune = openColorTune,
            OpenBigFile = openBigFile,
            TuneDemo = tuneDemo,
            OpenLatency = openLatency,
            OpenLatencyLab = openLatencyLab,
            OpenCoreParking = openCoreParking,
            OpenSlim = openSlim,
            OpenNotes = openNotes,
            OpenLive = openLive,
            OpenMemory = openMemory,
            OpenDuplicate = openDuplicate,
            OpenDuplicatePath = dupPath,
            OpenDefenderReview = openDefenderReview,
            OpenServicePreset = openServicePreset,
            IccRestore = iccRestore,
            OpenDelta = openDelta,
            OpenAutostart = openAutostart,
            PreviewTune = previewTune,
            PreviewRecovery = previewRecovery,
            PreviewDialog = previewDialog,
            MemReport = memReport,
            LegacyTheme = legacyTheme,
            UsnDrive = ElevationService.NormalizeDrive(drive),
            UsnHours = hours < 0 ? 168 : hours
        };
    }
}
