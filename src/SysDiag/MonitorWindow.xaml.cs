using System.Diagnostics;
using System.Threading;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 挂机监控：被查的人自己开、自己打游戏，程序按间隔采样并记录事件；
/// 结束时可生成会话报告（可导出 .sdreport）。
/// </summary>
public partial class MonitorWindow : Window
{
    private readonly MonitorRunner _runner = new();
    private readonly Action<ReportPayload> _showReport;

    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private MonitorSession _lastSession = new();
    private bool _autoStarted;

    /// <summary>界面初始化完成标志：XAML 里的 IsChecked 会在其它控件创建前触发事件。</summary>
    private bool _uiReady;

    /// <summary>自检读取：已记录事件数。</summary>
    public int EventCount => _lastSession.Events.Count;

    /// <summary>自检读取：是否在监控。</summary>
    public bool IsMonitoring => _runner.IsRunning;

    public MonitorSession Snapshot => _lastSession;

    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    public MonitorWindow() : this(_ => { })
    {
    }

    public MonitorWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        EventGrid.SetRows(Array.Empty<LogRow>(), new LogViewOptions
        {
            Title = "实时事件",
            TitleNote = "开始监控后事件会实时出现在这里",
            ShowTimeline = false,
            DefaultSort = "time-asc"
        });

        Kpi.Set(
            ("0 分 0 秒", "监控时长", ""),
            ("0", "采样次数", ""),
            ("0", "事件", ""),
            ("0", "高危迹象", Palette.OkHex),
            ("0", "跟随重启", ""));

        _runner.EventRaised += OnEvent;
        _runner.Ticked += s => Dispatcher.BeginInvoke(new Action(() => RefreshKpi(s)));

        _uiTimer.Tick += (_, _) => RefreshKpi(_runner.Session);

        var admin = ElevationService.IsAdmin;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        Loaded += OnLoaded;
        Closing += (_, _) => _runner.Stop();

        // 自动锁定默认开：先把目标下拉禁用，再标记就绪
        TargetCombo.IsEnabled = AutoLockBox.IsChecked != true;
        StatusText.Text = "自动锁定已开启：启动游戏后会自动接上";
        _uiReady = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        RefreshProcessList();

        // 自动锁定：识别到游戏就直接开监控，用户不用再点一次「开始监控」
        if (AutoLockBox.IsChecked == true && GameProcessCatalog.Best() is { } game && !_autoStarted)
        {
            _autoStarted = true;
            StatusText.Text = $"已自动锁定 {game.Reason}：{game.Display}";
            OnStart(this, new RoutedEventArgs());
        }
        else if (AutoLockBox.IsChecked == true)
        {
            StatusText.Text = "未发现运行中的游戏：先启动游戏，或取消自动锁定手动选进程";
        }
    }

    /// <summary>自动锁定开关：开时禁用目标下拉（由程序自己找游戏）。</summary>
    private void OnAutoLockChanged(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;

        var auto = AutoLockBox.IsChecked == true;

        TargetCombo.IsEnabled = !auto && !IsMonitoring;
        StatusText.Text = auto
            ? "自动锁定已开启：启动游戏后会自动接上，重启也会跟随新进程"
            : "手动模式：自己选目标进程";
    }

    /// <summary>刷新目标下拉框：先自动识别正在运行的游戏。</summary>
    public void RefreshProcessList()
    {
        var items = new List<string>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == Environment.ProcessId) continue;
                items.Add($"{p.ProcessName}（{p.Id}）");
            }
            catch { }
            finally { p.Dispose(); }
        }

        items.Sort(StringComparer.OrdinalIgnoreCase);

        var games = GameProcessCatalog.DetectRunningGames().ToList();

        // 自动锁定且没游戏时，用占位项提示，避免显示一个无关进程误导用户
        if (games.Count == 0 && AutoLockBox.IsChecked == true)
            items.Insert(0, WaitPlaceholder);

        TargetCombo.ItemsSource = items;

        string? pick = null;
        foreach (var g in games)
        {
            var exact = $"{g.Name}（{g.Pid}）";
            pick = items.FirstOrDefault(x => string.Equals(x, exact, StringComparison.OrdinalIgnoreCase))
                   ?? items.FirstOrDefault(x => x.StartsWith(g.Name + "（", StringComparison.OrdinalIgnoreCase));
            if (pick != null) break;
        }

        pick ??= items.FirstOrDefault();
        if (pick != null) TargetCombo.SelectedItem = pick;
    }

    /// <summary>无游戏时的占位项。</summary>
    private const string WaitPlaceholder = "（未发现运行中的游戏）";

    private void OnEvent(MonitorEvent e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            EventGrid.AddRow(new LogRow
            {
                Title = e.Text,
                Detail = e.Detail,
                Basis = e.Source,
                Tag = e.Source,
                Level = e.Level,
                LevelText = e.LevelText,
                Time = e.Time,
                TimeText = e.TimeText,
                Payload = e
            });

            RefreshKpi(_runner.Session);
        }));
    }

    private void RefreshKpi(MonitorSession s)
    {
        if (s == null || s.StartedAt == default) return;

        Kpi.Set(
            (s.DurationText, "监控时长", ""),
            (s.Ticks.ToString(), "采样次数", ""),
            (s.Events.Count.ToString(), "事件", ""),
            (s.WarnCount.ToString(), "高危迹象", s.WarnCount > 0 ? Palette.WarnHex : Palette.OkHex),
            (s.TargetSwitches.ToString(), "跟随重启", s.TargetSwitches > 0 ? Palette.HintHex : ""));

        VerdictText.Text = s.TargetPid <= 0 && IsMonitoring
            ? $"等待 {s.TargetName} 启动…"
            : s.Events.Count == 0 ? "监控中，暂无事件" : s.Verdict;
        VerdictText.Foreground = Palette.Get(s.VerdictColor);

        // 底栏同时给出「运行时长」和「采样开销」，方便判断有没有拖累游戏
        Bar.SetElapsed($"已运行 {(s.EndedAt ?? DateTime.Now) - s.StartedAt:hh\\:mm\\:ss}　·　{s.PerfText}");
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (IsMonitoring) return;

        var autoLock = AutoLockBox.IsChecked == true;
        var name = "";
        var pid = 0;

        if (autoLock)
        {
            // 自动锁定：正在跑的游戏优先；没在跑就按当前选择的名字等它启动
            var game = GameProcessCatalog.Best();

            if (game != null)
            {
                name = game.Name;
                pid = game.Pid;
            }
            else if (TargetCombo.SelectedItem is string waiting && waiting != WaitPlaceholder)
            {
                var wi = waiting.LastIndexOf('（');
                name = wi > 0 ? waiting[..wi] : waiting;
                pid = 0;
            }
            else
            {
                StatusText.Text = "先启动游戏，或取消自动锁定后手动选进程";
                return;
            }
        }
        else
        {
            if (TargetCombo.SelectedItem is not string entry)
            {
                StatusText.Text = "先选一个目标进程";
                return;
            }

            var i = entry.LastIndexOf('（');
            if (i <= 0 || !int.TryParse(entry[(i + 1)..^1], out pid))
            {
                StatusText.Text = "进程条目格式异常";
                return;
            }

            name = entry[..i];
        }

        var interval = 5;
        if (IntervalBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            int.TryParse(tag, out interval);

        // 清空上一轮
        EventGrid.SetRows(Array.Empty<LogRow>(), new LogViewOptions
        {
            Title = "实时事件",
            TitleNote = $"{name} · 每 {interval} 秒采样" + (autoLock ? " · 自动锁定" : ""),
            ShowTimeline = false,
            DefaultSort = "time-asc"
        });

        _runner.Start(name, pid, interval, autoLock);
        _uiTimer.Start();

        StartBtn.IsEnabled = false;
        StopBtn.IsEnabled = true;
        ReportBtn.IsEnabled = false;
        TargetCombo.IsEnabled = false;
        IntervalBox.IsEnabled = false;
        AutoLockBox.IsEnabled = false;

        StatusText.Text = pid > 0
            ? $"监控中：{name}（{pid}）· 每 {interval} 秒采样"
            : $"等待 {name} 启动 · 每 {Math.Min(30, Math.Max(5, interval * 3))} 秒探测一次";
        VerdictText.Text = pid > 0 ? "监控中…" : "等待游戏启动…";

        await Task.CompletedTask;
    }

    private void OnStop(object sender, RoutedEventArgs e) => StopMonitoring();

    public void StopMonitoring()
    {
        if (!_runner.IsRunning && _lastSession.StartedAt != default && _lastSession.EndedAt != null) return;

        _lastSession = _runner.Stop();
        _uiTimer.Stop();

        StartBtn.IsEnabled = true;
        StopBtn.IsEnabled = false;
        ReportBtn.IsEnabled = _lastSession.Events.Count > 0;
        TargetCombo.IsEnabled = AutoLockBox.IsChecked != true;
        IntervalBox.IsEnabled = true;
        AutoLockBox.IsEnabled = true;

        RefreshKpi(_lastSession);
        StatusText.Text = $"已停止：{_lastSession.DurationText} · 采样 {_lastSession.Ticks} 次 · 事件 {_lastSession.Events.Count} 条";
    }

    /// <summary>同步跑一小段监控，供无头自检调用。</summary>
    public void RunSync(int targetPid, string targetName, int seconds, int intervalSec = 2)
    {
        _runner.Start(targetName, targetPid, intervalSec, autoFollow: false);
        var deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline) Thread.Sleep(200);
        _lastSession = _runner.Stop();
        Thread.Sleep(300);   // 等后台采样真正退出，避免自检读到半个会话
    }

    /// <summary>
    /// 测一段真实帧率：调 PresentMon 采 20 秒再解析 CSV。
    /// 这是「他在打游戏时到底卡不卡」的直接证据，比进程 CPU 占用直观。
    /// </summary>
    private async void OnFrameTest(object sender, RoutedEventArgs e)
    {
        if (PresentMonService.Locate().Length == 0)
        {
            UiDialog.Show(this, PresentMonService.MissingHint, "需要 PresentMon",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 目标条目形如「GameName.exe（1234）」，这里只取进程名
        if (TargetCombo.SelectedItem is not string entry)
        {
            UiDialog.Show(this, "先在「目标」里选一个游戏进程，再点测帧率。", "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cut = entry.LastIndexOf('（');
        var name = System.IO.Path.GetFileNameWithoutExtension(cut > 0 ? entry[..cut] : entry);
        if (name.Length == 0)
        {
            UiDialog.Show(this, "目标条目格式异常，请重新选择进程。", "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var csv = System.IO.Path.Combine(AppPaths.OutputDir,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-帧率-{name}.csv");

        FrameBtn.IsEnabled = false;
        StatusText.Text = $"正在采集 {name} 的帧数据（约 20 秒）…";

        try
        {
            var ok = await Task.Run(() => PresentMonService.Capture(name, 20, csv, out var msg) ? "" : msg);

            if (ok.Length > 0)
            {
                StatusText.Text = "帧率采集失败";
                UiDialog.Show(this, ok, "采集失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var stats = PresentMonService.Analyze(csv);
            StatusText.Text = $"帧率：{stats.Summary}";

            if (stats.Frames < 30)
            {
                UiDialog.Show(this,
                    "采到了数据，但有效帧太少（" + stats.Frames + " 帧）。\r\n\r\n" +
                    "常见原因：游戏在后台/最小化、用独占全屏且被其它覆盖层挡住、或采集期间没在出画面。\r\n" +
                    "让游戏在前台跑着再试一次。", "帧数不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            UiDialog.Show(this,
                $"{name}\r\n\r\n{stats.Summary}\r\n\r\n{stats.Detail}\r\n\r\nCSV：{csv}",
                "帧率采集结果", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "帧率采集失败：" + ex.Message;
        }
        finally
        {
            FrameBtn.IsEnabled = true;
        }
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var session = _runner.IsRunning ? _runner.Stop() : (_lastSession.StartedAt != default ? _lastSession : _runner.Session);
            _lastSession = session;

            var payload = MonitorReport.Build(session);
            _showReport(payload);
            StatusText.Text = "报告已生成：" + System.IO.Path.GetFileName(payload.HtmlPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "报告生成失败：" + ex.Message;
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-monitor", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            StatusText.Text = error;
    }
}
