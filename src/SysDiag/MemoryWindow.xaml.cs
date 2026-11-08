using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 内存窗口。
///
/// 界面上刻意分成两层：上面是「看清」（实时读数 + 构成 + 进程排行），下面是「动手」（两个动作）。
/// 两个动作各自带一句代价说明，且都做前后对比——不灵就显示「没释放」，不编数字。
/// 进程列表不跟着 2 秒定时器重刷：每次重扫都会重排、勾选漂移，看起来像列表在自己跳。
/// </summary>
public partial class MemoryWindow : Window
{
    /// <summary>列表最多画这么多行。223 个进程全画既慢又没人看，排序后的前 60 行覆盖了绝大多数情况。</summary>
    private const int MaxRows = 60;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly HashSet<int> _picked = new();

    private List<MemProcessRow> _visible = new();
    private int _totalProcesses;
    private int _sortIndex;
    private int _tickCount;

    public MemoryWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        // 构造时就填一次：窗口第一帧不空，自检也能直接读端口径。
        // 定时器只在 Loaded 里启动——未显示的窗口不该继续轮询。
        RefreshMetrics(forceDiagnostics: true);
        ReloadList();

        Loaded += (_, _) => _timer.Start();

        _timer.Tick += (_, _) => RefreshMetrics(forceDiagnostics: false);
        Closed += (_, _) =>
        {
            _timer.Stop();
            // 关窗口后把自己的冷页还回去，别让这个“内存工具”自己成为占内存的那个
            MemoryOptimizerService.TrimSelf();
        };
    }

    // ---------------------------------------------------------------- 自检读取

    /// <summary>自检用：列表画了多少行。</summary>
    public int VisibleRowCount => _visible.Count;

    /// <summary>自检用：当前「已用」那一格的文本。</summary>
    public string UsedText => Kpi.NumOf(0);

    /// <summary>自检用：构成条分了几段。</summary>
    public int SegmentCount => BarInner.Children.Count;

    /// <summary>自检用：底部诊断行是否拿到了内容。</summary>
    public string DiagnosticsText => DiagText.Text;

    /// <summary>自检用：「回收选中」当前是否可用。</summary>
    public bool ReclaimEnabled => ReclaimBtn.IsEnabled;

    /// <summary>自检用：勾选按钮文案（「勾选可见」/「取消勾选」）。</summary>
    public string PickButtonText => PickBtn.Content?.ToString() ?? "";

    /// <summary>自检用：工具条中间那行状态文字。</summary>
    public string SummaryText => SelText.Text;

    /// <summary>
    /// 自检用：等价于点一次「勾选可见」，返回勾上之后的选中数。
    /// 截图点击对 WPF 首个点击会被「激活窗口」吃掉，逻辑断言比截图可靠。
    /// </summary>
    public int SelectVisibleForTest()
    {
        OnTogglePick(this, new RoutedEventArgs());
        return _picked.Count;
    }

    // ---------------------------------------------------------------- 读数

    private void RefreshMetrics(bool forceDiagnostics)
    {
        var snap = MemoryOptimizerService.Snapshot();

        var usedHex = snap.UsedPercent >= 92 ? Palette.DangerHex
            : snap.UsedPercent >= 82 ? Palette.HintHex
            : "";

        var commitPercent = snap.CommitLimit == 0 ? 0 : snap.CommitTotal * 100.0 / snap.CommitLimit;
        var commitHex = commitPercent >= 92 ? Palette.DangerHex
            : commitPercent >= 82 ? Palette.HintHex
            : "";

        Kpi.Set(
            (MemorySnapshot.Bytes(snap.Used), "已用 " + snap.UsedPercentText, usedHex),
            (MemorySnapshot.Bytes(snap.AvailPhys), "可用", ""),
            (MemorySnapshot.Bytes(snap.CommitTotal), "提交 " + snap.CommitPercentText, commitHex),
            (snap.ListInfoAvailable ? MemorySnapshot.Bytes(snap.StandbyBytes) : "--", "待机缓存", ""));

        BuildBar(snap);

        // 诊断要查 WMI（页面文件 / SysMain），没必要 2 秒一次；每 10 秒或首次刷新一次
        if (forceDiagnostics || _tickCount % 5 == 0)
        {
            try { DiagText.Text = MemoryOptimizerService.Diagnostics().Summary; }
            catch { DiagText.Text = ""; }
        }
        _tickCount++;

        Bar2.SetElapsed("更新于 " + DateTime.Now.ToString("HH:mm:ss"));
    }

    /// <summary>
    /// 构成条：四段宽度按字节占比分配。段与段之间留 2px 缝，不做整条圆角裁剪
    /// （WPF 的 Border 不会裁剪子元素，圆角只能靠每段自己带）。
    /// </summary>
    private void BuildBar(MemorySnapshot snap)
    {
        BarInner.Children.Clear();
        BarInner.ColumnDefinitions.Clear();
        LegendHost.Children.Clear();

        var segments = snap.Segments();
        var total = Math.Max(1UL, snap.TotalPhys);

        for (var i = 0; i < segments.Count; i++)
        {
            var (label, bytes, hex) = segments[i];

            BarInner.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.0001, (double)bytes / total), GridUnitType.Star)
            });

            var bar = new Border
            {
                Background = Palette.Get(hex),
                CornerRadius = new CornerRadius(2),
                Margin = new Thickness(i == 0 ? 0 : 2, 0, 0, 0),
                MinWidth = 0
            };
            Grid.SetColumn(bar, i);
            BarInner.Children.Add(bar);

            // 图例：色点 + 「标签 容量」
            var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
            item.Children.Add(new Border
            {
                Width = 7,
                Height = 7,
                CornerRadius = new CornerRadius(4),
                Background = Palette.Get(hex),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
            item.Children.Add(new TextBlock
            {
                Text = label + " " + MemorySnapshot.Bytes(bytes),
                FontSize = 10,
                Foreground = Palette.InkTertiary,
                VerticalAlignment = VerticalAlignment.Center
            });
            LegendHost.Children.Add(item);
        }
    }

    // ---------------------------------------------------------------- 列表

    private void ReloadList()
    {
        var all = MemoryOptimizerService.ScanProcesses();
        _totalProcesses = all.Count;

        _visible = (_sortIndex switch
        {
            1 => all.OrderByDescending(r => r.PrivateMb),
            2 => all.OrderByDescending(r => r.CommitMb),
            _ => all.OrderByDescending(r => r.WorkingSetMb)
        }).Take(MaxRows).ToList();

        // 勾选状态按 PID 保留，但已经退出的进程要从集合里剔掉，否则计数会虚高
        var alive = all.Select(r => r.Pid).ToHashSet();
        _picked.IntersectWith(alive);

        RowHost.Children.Clear();
        foreach (var row in _visible)
            RowHost.Children.Add(BuildRow(row));

        if (_visible.Count == 0)
        {
            RowHost.Children.Add(new TextBlock
            {
                Text = "没有读到进程内存信息。",
                FontSize = 11,
                Foreground = Palette.InkTertiary,
                Margin = new Thickness(2, 10, 0, 0)
            });
        }

        UpdateSummary();
    }

    private Border BuildRow(MemProcessRow row)
    {
        var card = new Border
        {
            Padding = new Thickness(0, 7, 0, 7),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent
        };

        // 悬停只改底色，不缩放、不加边框——列表里任何“会跳”的效果都会让人点错行
        card.MouseEnter += (_, _) => card.Background = Palette.SurfaceRaise;
        card.MouseLeave += (_, _) => card.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });

        var box = new CheckBox
        {
            IsChecked = _picked.Contains(row.Pid),
            IsEnabled = !row.IsCritical,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = row.IsCritical ? "系统进程：回收工作集后容易立刻大面积缺页，这里不允许勾选。" : null
        };
        box.Checked += (_, _) => { _picked.Add(row.Pid); UpdateSummary(); };
        box.Unchecked += (_, _) => { _picked.Remove(row.Pid); UpdateSummary(); };
        Grid.SetColumn(box, 0);
        grid.Children.Add(box);

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock
        {
            Text = row.Name,
            FontSize = 11.5,
            Foreground = Palette.Ink,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        name.Children.Add(new TextBlock
        {
            // SubText 会带上「系统进程」——不能勾选的行必须看得出为什么
            Text = row.SubText,
            FontSize = 9.5,
            Foreground = row.IsCritical ? Palette.InkSecondary : Palette.InkFaint
        });
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);

        AddNumber(grid, 2, row.WorkingSetText, Palette.Ink);
        AddNumber(grid, 3, row.PrivateText, Palette.InkSecondary);
        AddNumber(grid, 4, row.CommitText, Palette.InkTertiary);

        card.Child = grid;
        return card;
    }

    private static void AddNumber(Grid grid, int column, string text, Brush brush)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontFamily = new FontFamily("Consolas"),
            Foreground = brush,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(tb, column);
        grid.Children.Add(tb);
    }

    private void UpdateSummary()
    {
        var pickedMb = _visible.Where(r => _picked.Contains(r.Pid)).Sum(r => r.WorkingSetMb);

        SelText.Text = _picked.Count == 0
            ? $"共 {_totalProcesses} 个进程 · 显示前 {_visible.Count}"
            : $"已选 {_picked.Count} 个 · 工作集合计约 {MemorySnapshot.Bytes((ulong)Math.Max(0, pickedMb) * 1024 * 1024)}";

        ReclaimBtn.IsEnabled = _picked.Count > 0;
        ReclaimBtn.Content = _picked.Count > 0 ? $"回收选中（{_picked.Count}）" : "回收选中";

        var anyEnabled = _visible.Any(r => !r.IsCritical);
        PickBtn.IsEnabled = anyEnabled;
        PickBtn.Content = _visible.Where(r => !r.IsCritical).All(r => _picked.Contains(r.Pid)) && anyEnabled
            ? "取消勾选"
            : "勾选可见";
    }

    // ---------------------------------------------------------------- 动作

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _sortIndex = SortCombo.SelectedIndex;
        ReloadList();
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        RefreshMetrics(forceDiagnostics: true);
        ReloadList();
    }

    private void OnTogglePick(object sender, RoutedEventArgs e)
    {
        var selectable = _visible.Where(r => !r.IsCritical).Select(r => r.Pid).ToList();
        var allPicked = selectable.Count > 0 && selectable.All(_picked.Contains);

        if (allPicked)
            foreach (var pid in selectable) _picked.Remove(pid);
        else
            foreach (var pid in selectable) _picked.Add(pid);

        ReloadList();
    }

    private void OnPurge(object sender, RoutedEventArgs e)
    {
        var snap = MemoryOptimizerService.Snapshot();
        var standby = snap.ListInfoAvailable ? MemorySnapshot.Bytes(snap.StandbyBytes) : "待机";

        if (!ElevationService.IsAdmin)
        {
            var ask = UiDialog.Show(this,
                $"清理待机列表需要管理员权限（要申请 SeProfileSingleProcessPrivilege）。\n\n" +
                "现在以管理员身份重启程序吗？重启后会直接回到这个窗口。",
                "需要管理员权限", MessageBoxButton.OKCancel);

            if (ask == MessageBoxResult.OK)
                RelaunchAsAdmin("--admin --open-memory");

            return;
        }

        var confirm = UiDialog.Show(this,
            $"把 {standby} 待机缓存还给系统。\n\n" +
            "会发生什么：可用内存立刻变大；这些页下次要用得重新从磁盘读，接下来一段时间打开程序会稍慢；" +
            "系统开着 SysMain 的话会慢慢把缓存建回来。\n" +
            "不会做什么：不结束任何进程，不减少已提交的内存。\n\n确定清理？",
            "清理待机列表", MessageBoxButton.OKCancel);

        if (confirm != MessageBoxResult.OK) return;

        var result = MemoryOptimizerService.PurgeStandbyList();
        UiDialog.Show(this, result.Message, result.Ok ? "已完成" : "没有执行");

        RefreshMetrics(forceDiagnostics: true);
        ReloadList();
    }

    private void OnReclaim(object sender, RoutedEventArgs e)
    {
        if (_picked.Count == 0)
        {
            UiDialog.Show(this, "先勾选要回收的进程。", "没有选中");
            return;
        }

        var confirm = UiDialog.Show(this,
            $"回收 {_picked.Count} 个进程的工作集。\n\n" +
            "说实话：这一步基本只改数字。页并不会被销毁，只是被挪进待机列表，进程下次访问就换回来，" +
            "所以任务管理器里的数字会变小，物理内存占用几乎不变。\n\n" +
            "真想让出物理内存，用「清理待机列表」。\n\n仍然执行？",
            "回收工作集", MessageBoxButton.OKCancel);

        if (confirm != MessageBoxResult.OK) return;

        var result = MemoryOptimizerService.EmptyWorkingSets(_picked.ToList());
        UiDialog.Show(this, result.Message, result.Ok ? "已完成" : "没有执行");

        RefreshMetrics(forceDiagnostics: true);
        ReloadList();
    }

    /// <summary>以管理员身份重启并回到本窗口。失败时如实说明，不假装成功。</summary>
    private void RelaunchAsAdmin(string arguments)
    {
        if (ElevationService.RelaunchAsAdmin(arguments, out var error))
        {
            Application.Current?.Shutdown();
            return;
        }

        UiDialog.Show(this, error, "没有重启");
    }
}
