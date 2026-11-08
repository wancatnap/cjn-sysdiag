using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SysDiag.Models;

namespace SysDiag.Controls;

/// <summary>
/// 统一日志表格：搜索（增量过滤 + 匹配计数）、等级筛选、排序、时间分布柱状图、显示计数。
/// 交互模式借鉴 microsoft/EventLogExpert（MIT）的 timeline histogram 与 in-view find、
/// Timeline Explorer 的快速筛选；实现为纯 WPF 自绘，不引第三方库。
/// </summary>
public partial class LogGridView : UserControl
{
    private List<LogRow> _all = new();
    private List<LogRow> _shown = new();
    private LogViewOptions _opt = new();

    private string _search = "";
    private string _levelFilter = "";
    private (DateTime Start, DateTime End)? _timeFilter;
    private string _sort = "time-desc";

    private List<(DateTime Start, DateTime End, int Count, bool IsDayBucket)> _buckets = new();

    /// <summary>单表最多渲染的行数：超出截断并提示，避免几万条事件把内存顶上去。</summary>
    public const int MaxRows = 3000;

    /// <summary>增量追加的总量上限（监控长时间运行用）。</summary>
    private const int MaxLiveRows = 8000;

    private int _truncatedFrom;

    public LogGridView()
    {
        InitializeComponent();
    }

    /// <summary>用户点了某一行（事件查看器等需要展示详情的地方用）。</summary>
    public event Action<LogRow>? RowSelected;

    public int TotalCount => _all.Count;
    public int ShownCount => _shown.Count;

    /// <summary>自检读取：列表是否真的开着 UI 虚拟化（容器回收 + 像素滚动）。</summary>
    public bool IsVirtualized =>
        VirtualizingPanel.GetIsVirtualizing(CardList)
        && VirtualizingPanel.GetVirtualizationMode(CardList) == VirtualizationMode.Recycling
        && VirtualizingPanel.GetIsVirtualizing(CompactList)
        && VirtualizingPanel.GetVirtualizationMode(CompactList) == VirtualizationMode.Recycling;

    /// <summary>
    /// 自检读取：当前真正实例化了容器的行数。
    /// 虚拟化生效时这个数只跟「可见高度」有关（几十行），与数据总量无关。
    /// </summary>
    public int RealizedRows
    {
        get
        {
            var n = 0;
            for (var i = 0; i < _shown.Count; i++)
                if (CardList.ItemContainerGenerator.ContainerFromIndex(i) != null) n++;
            return n;
        }
    }
    public LogRow? FirstShown => _shown.Count > 0 ? _shown[0] : null;

    /// <summary>填充数据。时间柱状图只在实际存在时间数据时显示。</summary>
    public void SetRows(IEnumerable<LogRow> rows, LogViewOptions? options = null)
    {
        var source = rows?.ToList() ?? new List<LogRow>();
        _truncatedFrom = source.Count > MaxRows ? source.Count : 0;
        _all = _truncatedFrom > 0 ? source.Take(MaxRows).ToList() : source;
        _opt = options ?? new LogViewOptions();
        _sort = _opt.DefaultSort;
        _search = "";
        _levelFilter = "";
        _timeFilter = null;

        SearchBox.Text = "";
        SectionHeader.Visibility = _opt.Title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SectionTitle.Text = _opt.Title;
        SectionNote.Text = _opt.TitleNote;
        var hasTime = _all.Any(x => x.Time.HasValue);
        TimelineHost.Visibility = _opt.ShowTimeline && hasTime ? Visibility.Visible : Visibility.Collapsed;
        SearchHost.Visibility = _opt.ShowSearch ? Visibility.Visible : Visibility.Collapsed;

        // 全展开模式：禁掉列表自己的滚动，交给外层页面；同时不再虚拟化（条目少，全渲染更顺）
        var scrollMode = _opt.ExpandAll ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        ScrollViewer.SetVerticalScrollBarVisibility(CardList, scrollMode);
        ScrollViewer.SetVerticalScrollBarVisibility(CompactList, scrollMode);
        if (!hasTime && (_sort == "time-desc" || _sort == "time-asc"))
            _sort = "level";

        BuildChips();
        BuildSortButtons();
        CardList.Visibility = _opt.Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactList.Visibility = _opt.Compact ? Visibility.Visible : Visibility.Collapsed;

        DrawTimeline();
        ApplyFilters();
        ResetScroll();
    }

    /// <summary>
    /// 把视图拉回顶部。
    ///
    /// 必须做：列表开着「像素滚动 + 容器回收」，容器高度变化（例如底栏说明从一行变两行、
    /// 选中详情条出现/收起）时 ScrollViewer 会保留旧偏移，首行就被切掉半截——
    /// 数据刚换完时尤其明显。等一次布局再归零最稳。
    /// </summary>
    private void ResetScroll()
    {
        // 两遍：第一遍在布局完成时（Loaded），第二遍在渲染之后（Background）。
        // 只做一遍不够——底栏说明换行、KPI 项实例化都会在渲染前再改一次可用高度，
        // ScrollViewer 会为了"保持锚点"把偏移加回去，首行又被切掉。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ScrollToZero();

            Dispatcher.BeginInvoke(new Action(ScrollToZero),
                System.Windows.Threading.DispatcherPriority.Background);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ScrollToZero()
    {
        foreach (var list in new ListBox[] { CardList, CompactList })
        {
            var viewer = FindScrollViewer(list);
            if (viewer == null) continue;

            viewer.ScrollToVerticalOffset(0);

            // ScrollIntoView 兜底：像素滚动下它对"回到第一项"比设偏移更稳
            if (list.Items.Count > 0) list.ScrollIntoView(list.Items[0]);
        }
    }

    private static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var result = FindScrollViewer(child);
            if (result != null) return result;
        }

        return null;
    }

    /// <summary>
    /// 增量追加一行（实时监控用）。不重置搜索/等级/时间筛选，只重绘时间柱状图并重新排序。
    /// </summary>
    public void AddRow(LogRow row)
    {
        if (row == null) return;

        // 长时间监控不让事件无限增长：到上限后丢弃最早的 1,000 条
        if (_all.Count >= MaxLiveRows)
        {
            _all.RemoveRange(0, 1000);
            _truncatedFrom = Math.Max(_truncatedFrom, _all.Count + 1000);
        }

        _all.Add(row);

        if (row.Time.HasValue && _opt.ShowTimeline)
            TimelineHost.Visibility = Visibility.Visible;

        if (_opt.ShowLevelChips)
        {
            var levelExists = ChipPanel.Children.OfType<Button>().Any(b => (string)b.Tag == row.Level);
            if (!levelExists) BuildChips();
        }

        DrawTimeline();
        ApplyFilters();
    }

    // ---------------- 筛选与排序 ----------------

    private void ApplyFilters()
    {
        IEnumerable<LogRow> q = _all;

        if (_levelFilter.Length > 0)
            q = q.Where(x => x.Level == _levelFilter);

        if (_search.Length > 0)
            q = q.Where(x => x.SearchIndex.Contains(_search, StringComparison.OrdinalIgnoreCase));

        if (_timeFilter.HasValue)
        {
            var (s, e) = _timeFilter.Value;
            q = q.Where(x => x.Time.HasValue && x.Time.Value >= s && x.Time.Value < e);
        }

        _shown = _sort switch
        {
            "time-desc" => q.OrderByDescending(x => x.Time ?? DateTime.MinValue).ToList(),
            "time-asc" => q.OrderBy(x => x.Time ?? DateTime.MaxValue).ToList(),
            "level" => q.OrderBy(x => LogRowOrder.Rank(x.Level)).ThenBy(x => x.Time ?? DateTime.MinValue).ToList(),
            _ => q.OrderBy(x => x.Title).ToList()
        };

        var list = _opt.Compact ? CompactList : CardList;
        var other = _opt.Compact ? CardList : CompactList;

        list.ItemsSource = _shown;
        list.Visibility = _shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // 用不到的那一侧断开数据源：否则它也会为可见行建容器（虽不显示，容器照样占内存）
        other.ItemsSource = null;
        other.Visibility = Visibility.Collapsed;

        EmptyText.Visibility = _shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string> { $"显示 {_shown.Count:N0} / 共 {_all.Count:N0}" };

        if (_truncatedFrom > _all.Count)
            parts.Add($"数据共 {_truncatedFrom:N0} 条，仅载入前 {MaxRows:N0} 条（可缩小筛选范围）");
        if (_timeFilter.HasValue)
            parts.Add("时间已筛选");
        if (_levelFilter.Length > 0)
            parts.Add("等级已筛选");
        if (_search.Length > 0)
            parts.Add($"搜索「{_search}」");
        StatusText.Text = string.Join(" · ", parts);

        SearchCount.Text = _search.Length > 0 ? $"{_shown.Count}" : "";

        SortText.Text = _sort switch
        {
            "time-desc" => "时间 新→旧",
            "time-asc" => "时间 旧→新",
            "level" => "按等级",
            _ => "按标题"
        };
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text.Trim();
        ClearFilterBtn.Visibility = _search.Length > 0 || _levelFilter.Length > 0 || _timeFilter.HasValue
            ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilters();
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        _search = "";
        _levelFilter = "";
        _timeFilter = null;
        SearchBox.Text = "";
        foreach (var b in ChipPanel.Children.OfType<Button>()) b.BorderBrush = Brushes.Transparent;
        DrawTimeline();
        ApplyFilters();
    }

    // ---------------- 等级 chips ----------------

    private static readonly (string Level, string Label)[] LevelDefs =
    {
        ("warn", "高危"), ("hint", "提示"), ("ok", "正常"), ("known", "可信"), ("none", "其他")
    };

    private void BuildChips()
    {
        ChipPanel.Children.Clear();

        if (!_opt.ShowLevelChips || _all.Count == 0) return;

        // 一次分组拿到各等级数量，替代 5 次全表 Any + 5 次全表 Count
        var counts = new Dictionary<string, int>();
        foreach (var row in _all)
            counts[row.Level] = counts.TryGetValue(row.Level, out var c) ? c + 1 : 1;

        var present = LevelDefs.Where(d => counts.ContainsKey(d.Level)).ToList();

        AddChip($"全部 {_all.Count}", "", Brushes.Transparent, _levelFilter.Length == 0);

        foreach (var (level, label) in present)
        {
            var n = counts[level];
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                level switch { "warn" => "#C92A2A", "hint" => "#F08C00", "ok" => "#2F9E44", "known" => "#8B5CF6", _ => "#868E96" }));

            var text = _opt.LevelLabels != null && _opt.LevelLabels.TryGetValue(level, out var custom)
                ? custom
                : label;

            AddChip($"{text} {n}", level, brush, _levelFilter == level);
        }
    }

    private void AddChip(string text, string level, Brush accent, bool active)
    {
        var b = new Button
        {
            Content = text,
            FontSize = 10.5,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(9, 0, 9, 0),
            Height = 26,
            Cursor = Cursors.Hand,
            Background = active ? new SolidColorBrush(Color.FromArgb(40, 0x4C, 0x6E, 0xF5)) : Brushes.Transparent,
            BorderBrush = active && accent != Brushes.Transparent ? accent : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            Tag = level
        };
        b.Click += (s, _) =>
        {
            _levelFilter = _levelFilter == level ? "" : level;
            BuildChips();
            ClearFilterBtn.Visibility = Visibility.Visible;
            ApplyFilters();
        };
        ChipPanel.Children.Add(b);
    }

    private void BuildSortButtons()
    {
        // 排序通过点击状态栏右侧文字循环切换
        SortText.Cursor = Cursors.Hand;
        SortText.MouseDown += (s, e) =>
        {
            var hasTime = _all.Any(x => x.Time.HasValue);
            _sort = _sort switch
            {
                "time-desc" => hasTime ? "time-asc" : "level",
                "time-asc" => "level",
                "level" => hasTime ? "time-desc" : "title",
                _ => hasTime ? "time-desc" : "level"
            };
            ApplyFilters();
        };
    }

    // ---------------- 时间分布柱状图 ----------------

    private void DrawTimeline()
    {
        TimelineCanvas.Children.Clear();
        _buckets.Clear();
        TimelineHint.Text = "";

        var timed = _all.Where(x => x.Time.HasValue).Select(x => x.Time!.Value).ToList();
        if (timed.Count == 0 || TimelineHost.Visibility != Visibility.Visible) return;

        var min = timed.Min();
        var max = timed.Max();
        var isDay = (max - min).TotalDays >= 4;

        if (isDay)
        {
            var d = min.Date;
            while (d <= max.Date)
            {
                var e = d.AddDays(1);
                _buckets.Add((d, e, timed.Count(t => t >= d && t < e), true));
                d = e;
            }
        }
        else
        {
            var h = new DateTime(min.Year, min.Month, min.Day, min.Hour, 0, 0);
            while (h <= max)
            {
                var e = h.AddHours(1);
                _buckets.Add((h, e, timed.Count(t => t >= h && t < e), false));
                h = e;
            }
        }

        // 桶太多时按比例合并（最多画 ~60 根柱）
        while (_buckets.Count > 60)
        {
            var merged = new List<(DateTime, DateTime, int, bool)>();
            for (var i = 0; i < _buckets.Count; i += 2)
            {
                var a = _buckets[i];
                var b = i + 1 < _buckets.Count ? _buckets[i + 1] : (a.Start, a.End, 0, a.IsDayBucket);
                merged.Add((a.Start, b.End, a.Item3 + b.Item3, isDay || (b.End - a.Start).TotalDays >= 4));
            }
            _buckets = merged.Select(x => (x.Item1, x.Item2, x.Item3, x.Item2 - x.Item1 >= TimeSpan.FromDays(4))).ToList();
        }

        var peak = _buckets.Max(x => x.Count);
        if (peak == 0) peak = 1;

        var w = TimelineCanvas.ActualWidth > 10 ? TimelineCanvas.ActualWidth : 800;
        var barW = Math.Max(3.0, w / _buckets.Count - 2);
        var gap = Math.Max(2.0, w / _buckets.Count - barW);

        for (var i = 0; i < _buckets.Count; i++)
        {
            var bucket = _buckets[i];
            var h = Math.Max(2.0, 56.0 * bucket.Count / peak);
            var selected = _timeFilter == (bucket.Start, bucket.End);

            var rect = new Rectangle
            {
                Width = barW,
                Height = h,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = selected
                    ? new SolidColorBrush(Color.FromRgb(0x4C, 0x6E, 0xF5))
                    : new SolidColorBrush(Color.FromArgb(150, 0x4C, 0x6E, 0xF5)),
                Cursor = Cursors.Hand,
                ToolTip = $"{bucket.Start:(yyyy-MM-dd HH\\:00)} 起的 {bucket.Count} 条"
            };

            Canvas.SetLeft(rect, i * (barW + gap));
            Canvas.SetBottom(rect, 0);
            rect.MouseLeftButtonDown += (s, _) =>
            {
                _timeFilter = _timeFilter == (bucket.Start, bucket.End) ? null : (bucket.Start, bucket.End);
                DrawTimeline();
                ClearFilterBtn.Visibility = Visibility.Visible;
                ApplyFilters();
            };

            TimelineCanvas.Children.Add(rect);
        }

        var fmt = _buckets[0].IsDayBucket ? "MM-dd" : "MM-dd HH:00";
        TimelineHint.Text =
            $"{min.ToString(fmt)} — {max.ToString(fmt)} · 按柱可筛选，点空白清除";
    }

    private void OnTimelineBackgroundClick(object sender, MouseButtonEventArgs e)
    {
        if (_timeFilter.HasValue)
        {
            _timeFilter = null;
            DrawTimeline();
            ApplyFilters();
        }
    }

    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LogRow row)
            RowSelected?.Invoke(row);
    }

    // ---------------- Ctrl+F 聚焦搜索 ----------------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (!_opt.ShowSearch) return; // 没有搜索框：不拦截，交给外层页面处理
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && _search.Length > 0)
        {
            _search = "";
            SearchBox.Text = "";
            ApplyFilters();
        }

        base.OnPreviewKeyDown(e);
    }
}

/// <summary>LogGridView 的呈现选项。</summary>
public sealed class LogViewOptions
{
    /// <summary>节标题（如「Prefetch（运行过）」），空串不显示标题行。</summary>
    public string Title { get; set; } = "";

    /// <summary>节标题后的补充说明。</summary>
    public string TitleNote { get; set; } = "";

    /// <summary>紧凑单行模式（Prefetch / ShimCache 这类短记录）。</summary>
    public bool Compact { get; set; }

    /// <summary>尝试显示时间柱状图（实际有无时间数据再定）。</summary>
    public bool ShowTimeline { get; set; } = true;

    /// <summary>显示等级筛选 chips。</summary>
    public bool ShowLevelChips { get; set; } = true;

    /// <summary>
    /// 全部展开：列表不自带滚动，内容有多长就显示多长，由外层页面统一滚动。
    /// 用于一页放多个分组的窗口——三个分组各自内部滚动时每组只能露出一两行，
    /// 看全 16 条要滚三次，这是「太拥挤」的真正来源。
    /// </summary>
    public bool ExpandAll { get; set; }

    /// <summary>隐藏搜索框。分组内条目很少（≤10 条）时搜索没有意义，只会加一行高度。</summary>
    public bool ShowSearch { get; set; } = true;

    /// <summary>默认排序：time-desc / time-asc / level / title。</summary>
    public string DefaultSort { get; set; } = "time-desc";

    /// <summary>
    /// 等级筛选标签的替换文案（可选）。默认是「高危 / 提示 / 正常 / 可信 / 其他」，
    /// 但设备页的 warn 其实是「故障」、服务页的 none 是「已停止」——传进来就能对上语义。
    /// </summary>
    public Dictionary<string, string> LevelLabels { get; set; }
}
