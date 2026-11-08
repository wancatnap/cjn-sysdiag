using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SysDiag.Models;
using SysDiag.Services;
using System.IO;

namespace SysDiag;

public partial class MainWindow : Window
{
    private List<CardGroup> _all = new();
    private readonly LaunchOptions _options;
    private bool _busy;
    private bool? _adminOverride;

    /// <summary>子窗口刚关闭的一小段时间窗；窗口期内主窗口被最小化视为误触，自动还原。</summary>
    private DateTime _restoreUntil = DateTime.MinValue;

    private WindowState _restoreState = WindowState.Normal;

    /// <summary>自检专用：强制显示管理员 / 普通权限状态，不改变真实权限。</summary>
    internal bool? AdminOverride
    {
        get => _adminOverride;
        set
        {
            _adminOverride = value;
            ApplyAdminState();
        }
    }

    private bool IsAdminNow => _adminOverride ?? ElevationService.IsAdmin;

    public int GroupCount => _all.Count;
    public int CardCount => _all.Sum(g => g.Items.Count);

    /// <summary>自检读取：顶部权限标识文案。</summary>
    public string AdminBadgeLabel => AdminBadgeText.Text;

    /// <summary>自检读取：普通权限下是否显示「以管理员身份重启」。</summary>
    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    /// <summary>自检读取：本次是否由「以管理员身份重启」拉起并要求自动打开文件删除记录。</summary>
    public bool AutoUsnPending => _options.OpenUsn;

    /// <summary>自检读取：本次是否要自动打开网卡 MAC 窗口。</summary>
    public bool AutoMacPending => _options.OpenMac;

    /// <summary>自检读取：本次是否要自动打开「内存」窗口。</summary>
    public bool AutoMemoryPending => _options.OpenMemory;

    /// <summary>自检读取：本次是否要自动打开「重复文件」窗口。</summary>
    public bool AutoDuplicatePending => _options.OpenDuplicate;

    public bool AutoGpuPending => _options.OpenGpu;

    public bool AutoTunePending => _options.OpenTune;

    public bool AutoDlssPending => _options.OpenDlss;

    public bool AutoDnsPending => _options.OpenDns;

    public bool AutoNetRepairPending => _options.OpenNetRepair;

    public bool AutoDeepTracePending => _options.OpenDeepTrace;

    public bool AutoExecTracePending => _options.OpenExecTrace;

    public bool AutoDiskCleanPending => _options.OpenDiskClean;

    public bool AutoCheatScanPending => _options.OpenCheatScan;

    public bool AutoSigmaPending => _options.OpenSigma;

    public bool AutoPrefetchPending => _options.OpenPrefetch;

    public bool AutoHandlePending => _options.OpenHandle;

    public bool AutoMonitorPending => _options.OpenMonitor;

    public bool AutoEventsPending => _options.OpenEvents;

    public bool AutoMachinePending => _options.OpenMachine;

    /// <summary>自检读取：界面上显示的版本标识。</summary>
    public string VersionLabel => VersionText.Text;

    public MainWindow() : this(LaunchOptions.None) { }

    public MainWindow(LaunchOptions options)
    {
        _options = options;
        InitializeComponent();
        AppPaths.Ensure();

        // 自绘标题栏：WindowStyle 必须在创建窗口句柄前设置，所以放在构造里
        ApplyThemedChrome(this);

        // 樱花飘落：等窗口有实际尺寸后再生成花瓣；尺寸变化时重建（节流）
        Loaded += (_, _) => StartPetals();
        SizeChanged += OnPetalSizeChanged;

        VersionText.Text = AppInfo.BuildText;

        // 自证：写一份真实启动路径的内存明细，测完自动退出
        if (_options.MemReport)
        {
            Loaded += async (_, _) =>
            {
                await Task.Delay(3000);
                MemoryTuner.WriteMemReport();
                Application.Current?.Shutdown();
            };
        }

        // 启动初始化完成后（6 秒）回收一次工作集：初始化期间加载的程序集页此时已冷，
        // 换出后挂着不占物理内存。只做一次，不做常驻轮询。
        Loaded += async (_, _) =>
        {
            await Task.Delay(6000);
            MemoryTuner.TrimIfIdle();
        };

        // 上次套过 ICC 且勾了「自动重新应用」→ 重启后系统不会保留显卡 LUT，这里补一次。
        // 静默做，失败也不弹窗（打开「显示器配色」就能看到状态）。
        Loaded += (_, _) =>
        {
            try { IccService.ReplaySavedLuts(); } catch { /* 重放失败不影响启动 */ }
        };

        // 远程公告：异步拉取，失败就安静跳过——不阻塞启动、不弹错误。
        Loaded += async (_, _) => await LoadNoticeAsync();

        _all = CardCatalog.Build();
        Render(_all);

        StateChanged += OnWindowStateChanged;

        SearchHint.Visibility = Visibility.Visible;
        UpdateStatus(_all);
        ApplyAdminState();

        // 以管理员身份重启回来后，自动回到「文件删除记录」并把刚才选的条件接上
        if (_options.OpenUsn)
            Loaded += OnLoadedOpenUsn;

        // 网卡 MAC 窗口同理
        if (_options.OpenMac)
            Loaded += OnLoadedOpenMac;

        if (_options.OpenGpu)
            Loaded += OnLoadedOpenGpu;

        if (_options.OpenTune)
            Loaded += OnLoadedOpenTune;

        if (_options.OpenDlss)
            Loaded += OnLoadedOpenDlss;

        if (_options.OpenDns)
            Loaded += OnLoadedOpenDns;

        if (_options.OpenNetRepair)
            Loaded += OnLoadedOpenNetRepair;

        if (_options.OpenDeepTrace)
            Loaded += OnLoadedOpenDeepTrace;

        if (_options.OpenExecTrace)
            Loaded += OnLoadedOpenExecTrace;

        if (_options.OpenDiskClean)
            Loaded += OnLoadedOpenDiskClean;

        if (_options.OpenCheatScan)
            Loaded += OnLoadedOpenCheatScan;

        if (_options.OpenSigma)
            Loaded += OnLoadedOpenSigma;

        if (_options.OpenPrefetch)
            Loaded += OnLoadedOpenPrefetch;

        if (_options.OpenHandle)
            Loaded += OnLoadedOpenHandle;

        if (_options.OpenMonitor)
            Loaded += OnLoadedOpenMonitor;

        if (_options.OpenEvents)
            Loaded += OnLoadedOpenEvents;

        if (_options.OpenMachine)
            Loaded += OnLoadedOpenMachine;

        if (_options.OpenHwid)
            Loaded += OnLoadedOpenHwid;

        if (_options.OpenRegClean)
            Loaded += OnLoadedOpenRegClean;

        if (_options.OpenSysInfo)
            Loaded += OnLoadedOpenSysInfo;

        if (_options.OpenTweak)
            Loaded += OnLoadedOpenTweak;

        if (_options.OpenIcc)
            Loaded += OnLoadedOpenIcc;

        if (_options.OpenColorTune)
            Loaded += OnLoadedOpenColorTune;

        if (_options.OpenBigFile)
            Loaded += OnLoadedOpenBigFile;

        if (_options.OpenLatency)
            Loaded += OnLoadedOpenLatency;

        if (_options.OpenLatencyLab)
            Loaded += OnLoadedOpenLatencyLab;

        if (_options.OpenCoreParking)
            Loaded += OnLoadedOpenCoreParking;

        if (_options.OpenSlim)
            Loaded += OnLoadedOpenSlim;

        if (_options.OpenNotes)
            Loaded += OnLoadedOpenNotes;

        if (_options.OpenLive)
            Loaded += OnLoadedOpenLive;

        if (_options.OpenMemory)
            Loaded += OnLoadedOpenMemory;

        if (_options.OpenDuplicate)
            Loaded += OnLoadedOpenDuplicate;

        if (_options.OpenDefenderReview)
            Loaded += OnLoadedOpenDefenderReview;

        if (_options.OpenServicePreset)
            Loaded += OnLoadedOpenServicePreset;

        if (_options.OpenDelta)
            Loaded += OnLoadedOpenDelta;

        if (_options.OpenAutostart)
            Loaded += OnLoadedOpenAutostart;

        if (_options.OpenTool.Length > 0)
            Loaded += OnLoadedOpenTool;

        if (_options.PreviewTune)
            Loaded += OnLoadedPreviewTune;

        if (_options.PreviewRecovery)
            Loaded += OnLoadedPreviewRecovery;

        if (_options.PreviewDialog)
            Loaded += OnLoadedPreviewDialog;
    }

    private async void OnLoadedOpenUsn(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenUsn;

        try
        {
            var w = new UsnWindow(ShowReport);
            OpenChild(w);
            w.Preset(_options.UsnDrive, _options.UsnHours);
            await w.StartScanAsync();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开文件删除记录失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenMac(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenMac;

        try
        {
            var w = new MacWindow(_options.MacMode);
            OpenChild(w);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开网卡 MAC 窗口失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenGpu(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenGpu;
        var w = new GpuWindow();
        OpenChild(w);
    }

    private void OnLoadedOpenTune(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenTune;
        OpenChild(new SystemTuneWindow());
    }

    private void OnLoadedOpenDlss(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDlss;
        OpenChild(new DlssWindow());
    }

    private void OnLoadedOpenDns(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDns;
        OpenChild(new DnsWindow());
    }

    private void OnLoadedOpenNetRepair(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenNetRepair;
        OpenChild(new NetRepairWindow());
    }

    private void OnLoadedOpenDeepTrace(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDeepTrace;
        OpenChild(new DeepTraceWindow(ShowReport));
    }

    private void OnLoadedOpenExecTrace(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenExecTrace;
        OpenChild(new ExecTraceWindow(ShowReport));
    }

    private void OnLoadedOpenDiskClean(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDiskClean;

        try
        {
            OpenChild(new DiskCleanWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开 C 盘清理失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenCheatScan(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenCheatScan;

        try
        {
            OpenChild(new CheatScanWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开外挂痕迹体检失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenSigma(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenSigma;

        try
        {
            OpenChild(new SigmaWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开日志规则匹配失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenPrefetch(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenPrefetch;

        try
        {
            OpenChild(new PrefetchWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开本地执行痕迹失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenHandle(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenHandle;

        try
        {
            OpenChild(new HandleWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开进程句柄排查失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenMonitor(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenMonitor;

        try
        {
            OpenChild(new MonitorWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开挂机监控失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenEvents(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenEvents;

        try
        {
            OpenChild(new EvtxViewerWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开事件查看器失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>验收用：直接打开「优先级与亲和性」对话框（拿本进程当样本，不点应用）。</summary>
    private void OnLoadedPreviewTune(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedPreviewTune;

        try
        {
            var self = ProcessInventoryService.Scan()
                .FirstOrDefault(p => p.Pid == Environment.ProcessId);

            if (self == null)
            {
                UiDialog.Show(this, "没有找到本进程，无法预览。", "破烂优化",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            new ProcessTuneWindow(self) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开预览失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>验收用：直接打开「服务失败恢复」对话框（只读取，不写入）。</summary>
    private void OnLoadedPreviewRecovery(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedPreviewRecovery;

        try
        {
            var service = ServiceInventoryService.Scan()
                .FirstOrDefault(s => s.State == "Running" && !s.IsPerUserTemplate);

            if (service == null)
            {
                UiDialog.Show(this, "没有可用的服务做样本。", "破烂优化",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            new ServiceRecoveryWindow(service) { Owner = this }.Show();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开预览失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenTool(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenTool;

        var page = ToolPages.ByKey(_options.OpenTool);

        if (page == null)
        {
            UiDialog.Show(this, $"没有这个工具页：{_options.OpenTool}", "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            page.OpenChild = OpenChild;
            OpenChild(new ToolListWindow(page, ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开工具页失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenLatency(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenLatency;

        try
        {
            OpenChild(new LatencyWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开延迟优化失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenLatencyLab(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenLatencyLab;

        try
        {
            OpenChild(new LatencyLabWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开延迟实测失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenServicePreset(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenServicePreset;

        try { OpenChild(new ServicePresetWindow()); }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开服务预设失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenLive(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenLive;

        try { OpenChild(new LiveOptimizerWindow()); }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开直播伴侣优化失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>提权重启回来后自动回到内存窗口：用户刚点的就是「清理待机列表」。</summary>
    private void OnLoadedOpenMemory(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenMemory;

        try { OpenChild(new MemoryWindow()); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("打开内存窗口失败：" + ex.Message);
        }
    }

    private void OnLoadedOpenDuplicate(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDuplicate;

        try { OpenChild(new DuplicateWindow(_options.OpenDuplicatePath)); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("打开重复文件窗口失败：" + ex.Message);
        }
    }

    private void OnLoadedOpenDefenderReview(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDefenderReview;

        try { OpenChild(new DefenderReviewWindow()); }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开防护状态审查失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenCoreParking(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenCoreParking;

        try { OpenChild(new CoreParkingWindow()); }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开 CPU 停核失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenSlim(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenSlim;

        try { OpenChild(new SlimWindow()); }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开免重装精简失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenNotes(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenNotes;

        try { OpenChild(new OptimizationNotesWindow()); }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开优化推论失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenIcc(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenIcc;

        try
        {
            OpenChild(new IccWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开显示器配色失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>当前显示的公告（用于标记已读）。</summary>
    private RemoteNotice _notice;

    /// <summary>
    /// 拉远程公告。只有验签通过、未过期、没被关过的才显示。
    /// 任何异常都吞掉——公告是锦上添花，不能因为它让启动变慢或弹错误。
    /// </summary>
    private async Task LoadNoticeAsync()
    {
        try
        {
            var note = await RemoteNoticeService.LoadAsync();

            if (!note.Showable || RemoteNoticeService.IsDismissed(note)) return;

            _notice = note;

            NoticeTitle.Text = note.Title.Length > 0 ? note.Title : "公告";
            NoticeText.Text = note.Message;

            if (string.Equals(note.Level, "warn", StringComparison.OrdinalIgnoreCase))
            {
                NoticeBar.Background = Palette.WarnSoft;
                NoticeBar.BorderBrush = Palette.Warn;
                NoticeTitle.Foreground = Palette.Warn;
            }

            NoticeBar.Visibility = Visibility.Visible;

            // 「显示过就算读过」：本次会话里一直看得见，下次启动不再出现。
            // 用一个按钮换来的关闭权，不如直接不需要关闭——公告本来就不是需要反复看的东西。
            RemoteNoticeService.Dismiss(note);
        }
        catch
        {
            // 拉不到就当没有公告
        }
    }

    private void OnLoadedOpenColorTune(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenColorTune;

        try
        {
            OpenChild(new ColorTuneWindow(_options.TuneDemo));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开手动调色失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenBigFile(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenBigFile;

        try
        {
            OpenChild(new BigFileWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开大文件管理失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenTweak(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenTweak;

        try
        {
            OpenChild(new TweakWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开系统优化失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenDelta(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDelta;

        try
        {
            OpenChild(new DeltaCacheWindow(startOnTune: true));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开游戏优化失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenAutostart(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenAutostart;

        try
        {
            OpenChild(new AutostartWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开开机自启动失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenSysInfo(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenSysInfo;

        try
        {
            OpenChild(new SystemInfoWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开系统信息失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenRegClean(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenRegClean;

        try
        {
            OpenChild(new RegistryCleanWindow());
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开注册表清理失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenHwid(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenHwid;

        try
        {
            OpenChild(new HwidIntegrityWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开机器码伪造检测失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenMachine(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenMachine;

        try
        {
            OpenChild(new MachineWindow(ShowReport));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开机器码校验失败：\n\n" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedPreviewDialog(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedPreviewDialog;

        UiDialog.Show(this,
            "网络诊断 执行失败：\n\n示例：这里是错误详情。这条提示由软件内部的对话框显示，" +
            "不再是 Windows 默认的弹窗样式。",
            "内部对话框示例", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// 给窗口套用自绘浅色标题栏（Theme.xaml 的 ThemedWindow 样式）。
    /// WPF 的隐式 Window 样式在 MergedDictionaries 里查不到，只能显式设置。
    /// </summary>
    private static void ApplyThemedChrome(Window window) => WindowChromeBehavior.ApplyTheme(window);

    private void ApplyAdminState()
    {
        if (IsAdminNow)
        {
            AdminBadge.Background = Palette.OkSoft;
            AdminBadge.BorderBrush = Palette.Ok;
            AdminBadgeText.Text = "管理员模式";
            AdminBadgeText.Foreground = Palette.Ok;
            ElevateBtn.Visibility = Visibility.Collapsed;
            AdminBadge.ToolTip = "整个程序以管理员身份运行，文件删除记录（USN）可直接读取。";
        }
        else
        {
            AdminBadge.Background = Palette.WarnSoft;
            AdminBadge.BorderBrush = Palette.Hint;
            AdminBadgeText.Text = "普通权限";
            AdminBadgeText.Foreground = Palette.Hint;
            ElevateBtn.Visibility = Visibility.Visible;
            AdminBadge.ToolTip = "读取文件删除记录（USN）需要管理员权限，可点右上角按钮重启整个程序。";
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.IsAdmin) return;

        if (!ElevationService.RelaunchAsAdmin("--admin", out var error))
        {
            if (error != ElevationService.CancelledMessage)
                UiDialog.Show(this, "提权失败：" + error, "破烂优化",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 管理员实例已经起来，当前实例退出，避免两个窗口同时存在
        Application.Current.Shutdown();
    }

    /// <summary>供无头自检读取卡片快照。</summary>
    public List<CardGroup> Snapshot() => _all;

    private void Render(List<CardGroup> groups)
    {
        GroupList.ItemsSource = null;
        GroupList.ItemsSource = groups;
    }

    private void UpdateStatus(List<CardGroup> groups)
    {
        var shown = groups.Sum(g => g.Items.Count);

        // 状态栏只放摘要，完整路径放在工具提示里，避免长路径把右侧按钮挤出去
        StatusText.Text = shown == CardCount ? $"{CardCount} 项" : $"{shown} / {CardCount} 项";
        StatusText.ToolTip = AppPaths.OutputDir;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        var keyword = SearchBox.Text?.Trim() ?? "";
        SearchHint.Visibility = keyword.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (keyword.Length == 0)
        {
            Render(_all);
            UpdateStatus(_all);
            return;
        }

        var filtered = new List<CardGroup>();
        foreach (var g in _all)
        {
            var hit = g.Items
                .Where(i => i.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || i.Desc.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                            || g.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hit.Count == 0) continue;
            filtered.Add(new CardGroup
            {
                Name = g.Name,
                AccentHex = g.AccentHex,
                TintHex = g.TintHex,
                Items = hit
            });
        }

        Render(filtered);
        UpdateStatus(filtered);
    }

    private async void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (sender is not Button { Tag: CardItem card }) return;

        // 全盘查找游戏日志
        if (card.Kind == "diag" && card.Target == "find-logs")
        {
            var finder = new LogFinderWindow(src => _ = RunAnalysisAsync(src));
            OpenChild(finder);
            return;
        }

        // 日志分析：三种来源分别处理
        if (card.Kind == "diag" && card.Target.StartsWith("analyze-", StringComparison.Ordinal))
        {
            await PickAndAnalyzeAsync(card.Target);
            return;
        }

        // 日志清理
        if (card.Kind == "diag" && card.Target == "cleanup-logs")
        {
            var w = new CleanupWindow();
            OpenChild(w);
            return;
        }

        // C 盘清理
        if (card.Kind == "diag" && card.Target == "disk-clean")
        {
            OpenChild(new DiskCleanWindow());
            return;
        }

        // 文件删除记录（USN，需管理员）
        if (card.Kind == "diag" && card.Target == "usn")
        {
            var w = new UsnWindow(ShowReport);
            OpenChild(w);
            return;
        }

        // 网卡 MAC：修改 / 恢复（需管理员）
        if (card.Kind == "diag" && (card.Target == "mac-set" || card.Target == "mac-restore"))
        {
            var w = new MacWindow(card.Target == "mac-restore" ? "restore" : "set");
            OpenChild(w);
            return;
        }

        // 直播伴侣优化
        if (card.Kind == "diag" && card.Target == "live-optimizer")
        {
            var w = new LiveOptimizerWindow();
            OpenChild(w);
            return;
        }

        // 游戏缓存 / 显卡
        if (card.Kind == "diag" && card.Target == "delta-cache")
        {
            OpenChild(new DeltaCacheWindow());
            return;
        }

        if (card.Kind == "diag" && (card.Target == "gpu-info" || card.Target == "gpu-rename"))
        {
            OpenChild(new GpuWindow());
            return;
        }

        if (card.Kind == "diag" && card.Target == "system-tune")
        {
            OpenChild(new SystemTuneWindow());
            return;
        }

        if (card.Kind == "diag" && card.Target == "dlss")
        {
            OpenChild(new DlssWindow());
            return;
        }

        if (card.Kind == "diag" && card.Target == "dns")
        {
            OpenChild(new DnsWindow());
            return;
        }

        if (card.Kind == "diag" && card.Target == "net-repair")
        {
            OpenChild(new NetRepairWindow());
            return;
        }

        // 深度硬件痕迹：DMA / 采集卡 / 输入模拟
        if (card.Kind == "diag" && card.Target == "deep-trace")
        {
            OpenChild(new DeepTraceWindow(ShowReport));
            return;
        }

        // 程序执行记录：BAM / AmCache / SRUM
        if (card.Kind == "diag" && card.Target == "exec-trace")
        {
            OpenChild(new ExecTraceWindow(ShowReport));
            return;
        }

        // 外挂痕迹体检：八项只读采集 + 统一报告
        if (card.Kind == "diag" && card.Target == "cheat-scan")
        {
            OpenChild(new CheatScanWindow(ShowReport));
            return;
        }

        // 日志规则匹配：Sigma 子集
        if (card.Kind == "diag" && card.Target == "sigma")
        {
            OpenChild(new SigmaWindow(ShowReport));
            return;
        }

        // 本地执行痕迹：Prefetch / ShimCache
        if (card.Kind == "diag" && card.Target == "prefetch")
        {
            OpenChild(new PrefetchWindow(ShowReport));
            return;
        }

        // 机器码与出厂校验
        if (card.Kind == "diag" && card.Target == "machine")
        {
            OpenChild(new MachineWindow(ShowReport));
            return;
        }

        // 延迟优化（系统时钟 / MSI 中断 / 外设节能 / 计时器）
        // 延迟实测：只测不改，给「改一项前后对照」用
        if (card.Kind == "diag" && card.Target == "latency-lab")
        {
            OpenChild(new LatencyLabWindow());
            return;
        }

        if (card.Kind == "diag" && card.Target == "latency")
        {
            OpenChild(new LatencyWindow());
            return;
        }

        // 显示器配色（ICC）：应用/导入/导出，写在 HKCU 关联
        if (card.Kind == "diag" && card.Target == "icc")
        {
            OpenChild(new IccWindow());
            return;
        }

        // 服务预设：按清单批量禁用，但逐项确认、改前留底、可一键还原
        if (card.Kind == "diag" && card.Target == "service-preset")
        {
            OpenChild(new ServicePresetWindow());
            return;
        }

        // 防护状态审查：只读。查「有没有被别人悄悄关掉」，不提供关闭按钮。
        if (card.Kind == "diag" && card.Target == "defender-review")
        {
            OpenChild(new DefenderReviewWindow());
            return;
        }

        // 免重装精简：Atlas 那类操作里运行时可做、可解释可逆性的部分
        if (card.Kind == "diag" && card.Target == "slim")
        {
            OpenChild(new SlimWindow());
            return;
        }

        // 优化推论：只讲结论与依据，不改系统
        if (card.Kind == "diag" && card.Target == "notes")
        {
            OpenChild(new OptimizationNotesWindow());
            return;
        }

        // 重复文件：按内容比对，只删到回收站
        if (card.Kind == "diag" && card.Target == "duplicates")
        {
            OpenChild(new DuplicateWindow());
            return;
        }

        // 内存：读数 + 两个真实动作
        if (card.Kind == "diag" && card.Target == "memory")
        {
            OpenChild(new MemoryWindow());
            return;
        }

        // CPU 停核 / C-State（延迟优化域）
        if (card.Kind == "diag" && card.Target == "core-parking")
        {
            OpenChild(new CoreParkingWindow());
            return;
        }

        // 系统优化（把逆向出来的开关重新实现，全部可还原）
        if (card.Kind == "diag" && card.Target == "system-optimize")
        {
            OpenChild(new TweakWindow());
            return;
        }

        // 开机自启动
        if (card.Kind == "diag" && card.Target == "autostart")
        {
            OpenChild(new AutostartWindow());
            return;
        }

        // 系统信息（软件内自建，不调 msinfo32）
        if (card.Kind == "diag" && card.Target == "system-info")
        {
            OpenChild(new SystemInfoWindow(ShowReport));
            return;
        }

        // 注册表清理（只删孤儿项，删前备份）
        if (card.Kind == "diag" && card.Target == "registry-clean")
        {
            OpenChild(new RegistryCleanWindow());
            return;
        }

        // 大文件管理（只找不改，删除进回收站）
        if (card.Kind == "diag" && card.Target == "big-files")
        {
            OpenChild(new BigFileWindow());
            return;
        }

        // 机器码伪造检测（不需要出厂基线）
        if (card.Kind == "diag" && card.Target == "hwid-integrity")
        {
            OpenChild(new HwidIntegrityWindow(ShowReport));
            return;
        }

        // 事件查看器（内置，替代系统 eventvwr）
        if (card.Kind == "diag" && card.Target == "eventviewer")
        {
            OpenChild(new EvtxViewerWindow(ShowReport));
            return;
        }

        // 挂机监控
        if (card.Kind == "diag" && card.Target == "monitor")
        {
            OpenChild(new MonitorWindow(ShowReport));
            return;
        }

        // 进程句柄排查
        if (card.Kind == "diag" && card.Target == "handle")
        {
            OpenChild(new HandleWindow(ShowReport));
            return;
        }

        // 报告对比：导入 .sdreport
        if (card.Kind == "diag" && card.Target == "report-compare")
        {
            OpenChild(new ReportCompareWindow());
            return;
        }

        // 设备比对（基线快照）
        if (card.Kind == "diag" && card.Target == "baseline")
        {
            var w = new BaselineWindow(ShowReport);
            OpenChild(w);
            return;
        }

        // 日志记录开关
        if (card.Kind == "diag" && card.Target == "logging-switch")
        {
            var w = new LoggingWindow();
            OpenChild(w);
            return;
        }

        // 系统工具（设备 / 服务 / 电源 / 可靠性 / 软件 / 进程 / 网卡 / 防火墙 / 磁盘）
        // 统一交给软件内工具页：这些原本都是往外丢 msc / cpl / exe，
        // 精简镜像里那些工具常被删，弹出去的信息也带不走。
        if (card.Kind == "diag" && ToolPages.ByKey(card.Target) is { } toolPage)
        {
            toolPage.OpenChild = OpenChild;
            OpenChild(new ToolListWindow(toolPage, ShowReport));
            return;
        }

        try
        {
            if (card.Kind == "diag")
            {
                // 诊断涉及 WMI / 事件日志 / ping，同步执行会卡住界面，放到后台线程
                _busy = true;
                BusyTitle.Text = $"正在生成「{card.Name}」报告";
                BusyText.Text = "读取系统数据中…";
                Busy.Visibility = Visibility.Visible;

                var report = await Task.Run(() => LauncherService.Invoke(card));

                Busy.Visibility = Visibility.Collapsed;
                _busy = false;

                if (report != null) ShowReport(report);
                return;
            }

            LauncherService.Invoke(card);
        }
        catch (Exception ex)
        {
            Busy.Visibility = Visibility.Collapsed;
            _busy = false;
            UiDialog.Show(this, $"{card.Name} 执行失败：\n\n{ex.Message}", "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task PickAndAnalyzeAsync(string mode)
    {
        LogSource source;

        if (mode == "analyze-folder")
        {
            var dlg = new OpenFolderDialog { Title = "选择日志目录（下一步自己勾选要分析的文件）" };
            if (dlg.ShowDialog(this) != true) return;

            // 不直接整目录分析：先把里面的文件列出来，由用户自己挑
            var picker = new LogFilePickerWindow(dlg.FolderName) { Owner = this };
            if (picker.ShowDialog() != true) return;
            if (picker.SelectedPaths.Count == 0) return;

            source = LogSource.FromPickedFiles(dlg.FolderName, picker.SelectedPaths);
        }
        else if (mode == "analyze-files")
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择日志文件（可多选，支持 .log / .txt / .evtx 等）",
                Filter = "日志与事件日志|*.log;*.txt;*.json;*.csv;*.xml;*.evtx|所有文件 (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            if (dlg.FileNames.Length == 0) return;
            source = LogSource.FromFiles(dlg.FileNames);
        }
        else
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择日志压缩包",
                Filter = "日志压缩包 (*.zip)|*.zip|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dlg.ShowDialog(this) != true) return;
            source = LogSource.FromZip(dlg.FileName);
        }

        await RunAnalysisAsync(source);
    }

    private async Task RunAnalysisAsync(LogSource source)
    {
        _busy = true;
        BusyTitle.Text = "正在分析日志";
        Busy.Visibility = Visibility.Visible;
        BusyText.Text = "准备中…";

        var progress = new Progress<string>(s => BusyText.Text = s);

        try
        {
            var report = await Task.Run(() => LogAnalyzer.Analyze(source, progress));
            var findings = LogInsight.Analyze(report);
            var html = HtmlReportBuilder.BuildLog(report, findings);
            var path = HtmlReportBuilder.Save(html, "游戏日志分析");

            // 摘要另存一份 txt，方便直接发客服
            var summary = LogInsight.BuildPlainSummary(report, findings);
            HtmlReportBuilder.SaveText(summary, "游戏日志分析-摘要");

            // 记住这次分析的目录，下次清理时可直接勾选（覆盖 D 盘等非标准路径）
            var folder = source.Kind == LogSourceKind.Folder
                ? source.Paths.FirstOrDefault()
                : source.Root;
            if (!string.IsNullOrEmpty(folder)) AppSettings.AddRecentFolder(folder);

            Busy.Visibility = Visibility.Collapsed;
            _busy = false;

            var top = findings.FirstOrDefault();
            ShowReport(new ReportPayload
            {
                Title = string.IsNullOrEmpty(report.DisplayTitle) ? "日志分析" : report.DisplayTitle,
                Subtitle = top == null
                    ? $"崩溃 {report.CrashCount} · 错误 {report.ErrorCount:N0} · 警告 {report.WarnCount:N0} · 扫描 {report.LineTotal:N0} 行"
                    : $"{top.LevelText}：{top.Title}（{top.Count:N0} 次） · 扫描 {report.LineTotal:N0} 行",
                EngineTag = report.Engine,
                HtmlPath = path,
                PlainSummary = summary
            });
        }
        catch (Exception ex)
        {
            Busy.Visibility = Visibility.Collapsed;
            _busy = false;
            UiDialog.Show(this, $"分析失败：\n\n{ex.Message}", "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 统一打开子窗口，带两道防护：
    /// 1) 关闭时先取消、下一帧再真正关闭——避免这次点击的鼠标抬起落到下方主窗口标题栏的按钮上（点关闭却把主界面最小化就来自这里）；
    /// 2) 关闭后 600ms 内主窗口若被最小化，自动还原回原来的状态，只触发一次。
    /// </summary>
    private void OpenChild(Window child)
    {
        var stateBefore = WindowState;

        ApplyThemedChrome(child);

        child.Owner = this;

        var closing = false;
        child.Closing += (_, e) =>
        {
            if (closing) return;

            closing = true;
            e.Cancel = true;
            child.Dispatcher.BeginInvoke(new Action(() => child.Close()),
                DispatcherPriority.ApplicationIdle);
        };

        child.Closed += (_, _) =>
        {
            if (stateBefore == WindowState.Minimized) return;

            _restoreState = stateBefore;
            _restoreUntil = DateTime.UtcNow.AddMilliseconds(600);

            // 所有子窗口都关掉后再回收一次工作集（有节流，不会反复换页）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (OwnedWindows.Count == 0)
                    MemoryTuner.TrimIfIdle();
            }), DispatcherPriority.ApplicationIdle);
        };

        child.Show();
    }

    // ---------------- 樱花飘落 ----------------

    /// <summary>花瓣总数（自检用）。</summary>
    public const int PetalTotal = 18;

    private bool _petalsOn;
    private readonly DispatcherTimer _petalDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };

    /// <summary>花瓣形状（以原点为中心，顶部带缺口），全实例共用一份冻结几何。</summary>
    private static readonly Geometry PetalShape = Geometry.Parse(
        "M0,-6 L3,-9 C4.6,-6 6,-3 6,0 C6,4.4 3.4,8.6 0,8.6 C-3.4,8.6 -6,4.4 -6,0 C-6,-3 -4.6,-6 -3,-9 Z");

    private static readonly Brush PetalPink = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x9C, 0xBB)));
    private static readonly Brush PetalLight = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xBE, 0xD2)));
    private static readonly Brush PetalEdge = Freeze(new SolidColorBrush(Color.FromRgb(0xF2, 0x85, 0xA6)));

    /// <summary>飞鸟剪影（两段弧组成的海鸥形）。</summary>
    private static readonly Geometry BirdShape = Geometry.Parse("M0,0 C5,-5 10,-5 14,0 C18,-5 23,-5 28,0");

    private static readonly Brush BirdStroke = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x5F, 0x67)));

    /// <summary>自检读取：飞鸟数量。</summary>
    public int BirdCount => BirdLayer?.Children.Count ?? 0;

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    /// <summary>自检读取：当前是否开启樱花动画、当前花瓣数。</summary>
    public bool PetalsOn => _petalsOn;

    /// <summary>自检用：直接开启/关闭花瓣（不依赖窗口是否显示）。</summary>
    public void EnablePetals() => StartPetals();

    public void DisablePetals() => StopPetals();

    public int PetalCount => SakuraLayer.Children.Count;

    /// <summary>
    /// 生成樱花飘落动画。用 WPF 的声明式动画（渲染线程驱动），不是每帧回调，
    /// 15 个花瓣的合成开销可以忽略，不影响挂着打游戏。
    /// </summary>
    private void StartPetals()
    {
        if (_petalsOn || SakuraLayer == null) return;

        var height = ActualHeight > 200 ? ActualHeight : 900;
        var width = ActualWidth > 300 ? ActualWidth : 1240;

        SakuraLayer.Children.Clear();

        // 固定种子：每次启动分布一致，便于截图比对
        var rnd = new Random(20261106);

        for (var i = 0; i < PetalTotal; i++)
        {
            var scale = 0.95 + rnd.NextDouble() * 0.95;
            var opacity = 0.5 + rnd.NextDouble() * 0.36;
            // 分层采样：第 i 片落在第 i 段里随机一点，避免全挤在一侧
            var baseX = (i + rnd.NextDouble()) / PetalTotal * width;
            var fall = TimeSpan.FromSeconds(13 + rnd.NextDouble() * 13);
            var startOffset = TimeSpan.FromSeconds(-rnd.NextDouble() * fall.TotalSeconds);
            var swayBy = 30 + rnd.NextDouble() * 55;
            var spinTime = TimeSpan.FromSeconds(5 + rnd.NextDouble() * 8);
            var spinDir = rnd.Next(2) == 0 ? 1 : -1;
            var fill = i % 3 == 0 ? PetalLight : PetalPink;

            // 主体
            AddPetal(scale, opacity, baseX, fall, startOffset, swayBy, spinTime, spinDir, fill, height);

            // 拖尾：更小更淡，相位晚 0.35s（落在主体后面，形成尾迹感）
            AddPetal(scale * 0.6, opacity * 0.3, baseX, fall,
                startOffset + TimeSpan.FromSeconds(0.35), swayBy, spinTime, spinDir, fill, height);
        }

        StartBirds(width);

        _petalsOn = true;
    }

    /// <summary>加一片花瓣（主体或拖尾），三条循环动画。</summary>
    private void AddPetal(double scale, double opacity, double baseX, TimeSpan fall, TimeSpan offset,
        double swayBy, TimeSpan spinTime, int spinDir, Brush fill, double height)
    {
        var petal = new System.Windows.Shapes.Path
        {
            Data = PetalShape,
            Fill = fill,
            Opacity = opacity,
            Stroke = PetalEdge,
            StrokeThickness = 0.6,
            IsHitTestVisible = false
        };

        var rotate = new RotateTransform(0);
        var move = new TranslateTransform { X = baseX, Y = -80 };

        var group = new TransformGroup();
        group.Children.Add(rotate);
        group.Children.Add(new ScaleTransform(scale, scale));
        group.Children.Add(move);
        petal.RenderTransform = group;

        SakuraLayer.Children.Add(petal);

        // 竖向：匀速落下
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = -80,
            To = height + 100,
            Duration = fall,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = offset
        });

        // 横向：左右轻摆
        var sway = new DoubleAnimationUsingKeyFrames
        {
            Duration = fall,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = offset
        };
        sway.KeyFrames.Add(new LinearDoubleKeyFrame(baseX, KeyTime.FromPercent(0)));
        sway.KeyFrames.Add(new EasingDoubleKeyFrame(baseX + swayBy, KeyTime.FromPercent(0.5),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        sway.KeyFrames.Add(new LinearDoubleKeyFrame(baseX, KeyTime.FromPercent(1)));
        move.BeginAnimation(TranslateTransform.XProperty, sway);

        // 自转
        rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360 * spinDir,
            Duration = spinTime,
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = offset
        });
    }

    /// <summary>
    /// 两只飞鸟：每 30~40 秒掠过屏幕一次，扇翅 + 上下起伏。
    /// 大部分时间停在屏幕外（关键帧把后半段钉在屏外），所以开销近乎为零。
    /// </summary>
    private void StartBirds(double width)
    {
        BirdLayer.Children.Clear();

        for (var i = 0; i < 2; i++)
        {
            var bird = new System.Windows.Shapes.Path
            {
                Data = BirdShape,
                Stroke = BirdStroke,
                StrokeThickness = 2.1,
                Fill = Brushes.Transparent,
                Opacity = 0.62,
                IsHitTestVisible = false,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };

            var flap = new ScaleTransform(2.0, 2.0);
            var move = new TranslateTransform { X = -80, Y = 300 + i * 190 };
            var group = new TransformGroup();
            group.Children.Add(flap);
            group.Children.Add(move);
            bird.RenderTransform = group;

            BirdLayer.Children.Add(bird);

            var period = TimeSpan.FromSeconds(34 + i * 11);
            var begin = TimeSpan.FromSeconds(-i * 9);

            // 横向：0→45% 飞过屏幕，其余时间停在屏外
            var flyX = new DoubleAnimationUsingKeyFrames
            {
                Duration = period,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = begin
            };
            flyX.KeyFrames.Add(new LinearDoubleKeyFrame(-80, KeyTime.FromPercent(0)));
            flyX.KeyFrames.Add(new LinearDoubleKeyFrame(width + 80, KeyTime.FromPercent(0.45)));
            flyX.KeyFrames.Add(new LinearDoubleKeyFrame(width + 80, KeyTime.FromPercent(1)));
            move.BeginAnimation(TranslateTransform.XProperty, flyX);

            // 纵向：随风轻微起伏
            var baseY = move.Y;
            var flyY = new DoubleAnimationUsingKeyFrames
            {
                Duration = period,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = begin
            };
            flyY.KeyFrames.Add(new LinearDoubleKeyFrame(baseY, KeyTime.FromPercent(0)));
            flyY.KeyFrames.Add(new EasingDoubleKeyFrame(baseY - 28, KeyTime.FromPercent(0.22),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            flyY.KeyFrames.Add(new EasingDoubleKeyFrame(baseY + 14, KeyTime.FromPercent(0.45),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
            flyY.KeyFrames.Add(new LinearDoubleKeyFrame(baseY, KeyTime.FromPercent(1)));
            move.BeginAnimation(TranslateTransform.YProperty, flyY);

            // 扇翅：纵向压缩来回
            flap.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0.42,
                TimeSpan.FromSeconds(0.34))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = begin
            });
        }
    }

    private void StopPetals()
    {
        SakuraLayer?.Children.Clear();
        BirdLayer?.Children.Clear();
        _petalsOn = false;
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        // 分享给他人使用后的常驻说明：定位、数据来源、免责、误报。
        // 不做常驻页脚（BottomBar 的设计原则），收敛成一次性对话框。
        var text = "优化系统和游戏的本地工具：诊断全程只读，改动能还原。\n\n" +

            "【数据来源】\n" +
            "检测结果引用的数据（驱动黑名单、厂商信息、优化建议等）来自网络公开数据源，" +
            "并附有对应的警告与出处。本软件不联网上传任何本机数据。\n\n" +

            "【免责声明】\n" +
            "所有修改系统设置的项都有备份与还原入口，但任何系统级操作都有风险：" +
            "操作前请看清确认框，重要数据自行备份，因使用本软件造成的后果由使用者承担。\n\n" +

            "【杀毒软件提示】\n" +
            "本程序未做代码签名，可能被杀毒软件标记为风险程序。它不联网上传数据、不加载驱动；" +
            "若被拦截，请在杀软中添加信任。\n\n" +

            "【更新与公告】\n" +
            "启动时会从网络拉取公告（拉取失败不影响任何功能）。";

        UiDialog.Show(text, "关于 破烂优化　" + AppInfo.BuildText);
    }

    private void OnTogglePetals(object sender, RoutedEventArgs e)
    {
        if (_petalsOn) StopPetals();
        else StartPetals();
    }

    /// <summary>窗口尺寸变化后重建（落下终点依赖高度），带节流避免拖拽时反复重建。</summary>
    private void OnPetalSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_petalsOn) return;

        _petalDebounce.Stop();
        _petalDebounce.Tick -= OnPetalDebounceTick;
        _petalDebounce.Tick += OnPetalDebounceTick;
        _petalDebounce.Start();
    }

    private void OnPetalDebounceTick(object? sender, EventArgs e)
    {
        _petalDebounce.Stop();
        if (!_petalsOn) return;

        StopPetals();
        StartPetals();
    }

    private void OnWindowStateChanged(object sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        if (DateTime.UtcNow > _restoreUntil) return;

        _restoreUntil = DateTime.MinValue;

        WindowState = _restoreState == WindowState.Minimized ? WindowState.Normal : _restoreState;
        Activate();
    }

    private void ShowReport(ReportPayload payload)
    {
        OpenChild(new ReportWindow(payload));
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        try { LauncherService.RevealInExplorer(AppPaths.OutputDir); }
        catch (Exception ex)
        {
            UiDialog.Show(this, ex.Message, "破烂优化", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
