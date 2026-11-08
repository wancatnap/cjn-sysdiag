using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

    public bool AutoTracePending => _options.OpenTrace;

    public bool AutoGpuPending => _options.OpenGpu;

    public bool AutoTunePending => _options.OpenTune;

    public bool AutoDlssPending => _options.OpenDlss;

    public bool AutoDnsPending => _options.OpenDns;

    public bool AutoNetRepairPending => _options.OpenNetRepair;

    public MainWindow() : this(LaunchOptions.None) { }

    public MainWindow(LaunchOptions options)
    {
        _options = options;
        InitializeComponent();
        AppPaths.Ensure();

        _all = CardCatalog.Build();
        Render(_all);

        SubTitle.Text = $"全部使用 Windows 自带能力，无需安装 · 共 {CardCount} 个功能 · 报告在软件内直接查看";
        SearchHint.Visibility = Visibility.Visible;
        UpdateStatus(_all);
        ApplyAdminState();

        // 以管理员身份重启回来后，自动回到「文件删除记录」并把刚才选的条件接上
        if (_options.OpenUsn)
            Loaded += OnLoadedOpenUsn;

        // 网卡 MAC 窗口同理
        if (_options.OpenMac)
            Loaded += OnLoadedOpenMac;

        if (_options.OpenTrace)
            Loaded += OnLoadedOpenTrace;

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
    }

    private async void OnLoadedOpenUsn(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenUsn;

        try
        {
            var w = new UsnWindow(ShowReport) { Owner = this };
            w.Show();
            w.Preset(_options.UsnDrive, _options.UsnHours);
            await w.StartScanAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开文件删除记录失败：\n\n" + ex.Message, "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenMac(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenMac;

        try
        {
            var w = new MacWindow(_options.MacMode) { Owner = this };
            w.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开网卡 MAC 窗口失败：\n\n" + ex.Message, "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnLoadedOpenTrace(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenTrace;
        var w = new TraceCleanupWindow { Owner = this };
        w.Show();
    }

    private void OnLoadedOpenGpu(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenGpu;
        var w = new GpuWindow { Owner = this };
        w.Show();
    }

    private void OnLoadedOpenTune(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenTune;
        new SystemTuneWindow { Owner = this }.Show();
    }

    private void OnLoadedOpenDlss(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDlss;
        new DlssWindow { Owner = this }.Show();
    }

    private void OnLoadedOpenDns(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenDns;
        new DnsWindow { Owner = this }.Show();
    }

    private void OnLoadedOpenNetRepair(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoadedOpenNetRepair;
        new NetRepairWindow { Owner = this }.Show();
    }

    private void ApplyAdminState()
    {
        if (IsAdminNow)
        {
            AdminBadge.Background = Palette.Get("#EBFBEE");
            AdminBadge.BorderBrush = Palette.Get("#B2F2BB");
            AdminBadgeText.Text = "管理员模式";
            AdminBadgeText.Foreground = Palette.Get("#2F9E44");
            ElevateBtn.Visibility = Visibility.Collapsed;
            AdminBadge.ToolTip = "整个程序以管理员身份运行，文件删除记录（USN）可直接读取。";
        }
        else
        {
            AdminBadge.Background = Palette.Get("#FFF9DB");
            AdminBadge.BorderBrush = Palette.Get("#FFE066");
            AdminBadgeText.Text = "普通权限";
            AdminBadgeText.Foreground = Palette.Get("#B08900");
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
                MessageBox.Show(this, "提权失败：" + error, "系统诊断台",
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
        StatusText.Text = $"显示 {shown} / 共 {CardCount} 个功能　·　报告输出：outputs";
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
            var finder = new LogFinderWindow(src => _ = RunAnalysisAsync(src)) { Owner = this };
            finder.Show();
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
            var w = new CleanupWindow { Owner = this };
            w.Show();
            return;
        }

        // 文件删除记录（USN，需管理员）
        if (card.Kind == "diag" && card.Target == "usn")
        {
            var w = new UsnWindow(ShowReport) { Owner = this };
            w.Show();
            return;
        }

        // 网卡 MAC：修改 / 恢复（需管理员）
        if (card.Kind == "diag" && (card.Target == "mac-set" || card.Target == "mac-restore"))
        {
            var w = new MacWindow(card.Target == "mac-restore" ? "restore" : "set") { Owner = this };
            w.Show();
            return;
        }

        // 直播伴侣优化
        if (card.Kind == "diag" && card.Target == "live-optimizer")
        {
            var w = new LiveOptimizerWindow { Owner = this };
            w.Show();
            return;
        }

        // 痕迹清理 / 三角洲缓存 / 显卡
        if (card.Kind == "diag" && card.Target == "trace-cleanup")
        {
            new TraceCleanupWindow { Owner = this }.Show();
            return;
        }

        if (card.Kind == "diag" && card.Target == "delta-cache")
        {
            new DeltaCacheWindow { Owner = this }.Show();
            return;
        }

        if (card.Kind == "diag" && (card.Target == "gpu-info" || card.Target == "gpu-rename"))
        {
            new GpuWindow { Owner = this }.Show();
            return;
        }

        if (card.Kind == "diag" && card.Target == "system-tune")
        {
            new SystemTuneWindow { Owner = this }.Show();
            return;
        }

        if (card.Kind == "diag" && card.Target == "dlss")
        {
            new DlssWindow { Owner = this }.Show();
            return;
        }

        if (card.Kind == "diag" && card.Target == "dns")
        {
            new DnsWindow { Owner = this }.Show();
            return;
        }

        if (card.Kind == "diag" && card.Target == "net-repair")
        {
            new NetRepairWindow { Owner = this }.Show();
            return;
        }

        // 设备比对（基线快照）
        if (card.Kind == "diag" && card.Target == "baseline")
        {
            var w = new BaselineWindow(ShowReport) { Owner = this };
            w.Show();
            return;
        }

        // 日志记录开关
        if (card.Kind == "diag" && card.Target == "logging-switch")
        {
            var w = new LoggingWindow { Owner = this };
            w.Show();
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
            MessageBox.Show(this, $"{card.Name} 执行失败：\n\n{ex.Message}", "系统诊断台",
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
            MessageBox.Show(this, $"分析失败：\n\n{ex.Message}", "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ShowReport(ReportPayload payload)
    {
        var win = new ReportWindow(payload) { Owner = this };
        win.Show();
    }

    private void OnOpenOutput(object sender, RoutedEventArgs e)
    {
        try { LauncherService.RevealInExplorer(AppPaths.OutputDir); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "系统诊断台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
