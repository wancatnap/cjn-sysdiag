using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 通用的「软件内工具页」窗口：一整套页头 / KPI / 工具条 / 统一表格 / 选中详情。
///
/// 为什么做成通用窗口：设备管理器、服务、电源计划、可靠性监视器、程序和功能、
/// 任务管理器、网络连接、防火墙、磁盘管理这九处原本都是往外丢 msc / cpl / exe，
/// 精减系统里这些 exe 常被删，弹出去的信息也带不走。它们的数据结构不同，
/// 但「列表 + 搜索 + 对选中项操作 + 出报告」这套骨架是一样的，所以骨架只写一遍，
/// 每个工具只提供 <see cref="ToolPage"/>（怎么采、KPI 显示什么、能做什么）。
/// </summary>
public partial class ToolListWindow : Window
{
    private readonly ToolPage _page;
    private readonly Action<ReportPayload> _showReport;
    private readonly DispatcherTimer _timer;
    private readonly List<Button> _actionButtons = new();

    private LogRow _selected;
    private string _selectedKey = "";
    private bool _busy;
    private bool _silentReload;

    /// <summary>自检读取：当前页描述。</summary>
    public ToolPage Page => _page;

    /// <summary>自检读取：表格里的总行数。</summary>
    public int RowCount => Grid.TotalCount;

    /// <summary>自检读取：KPI 第一格显示的数字。</summary>
    public string KpiFirst => Kpi.NumOf(0);

    /// <summary>自检读取：选中行标题。</summary>
    public string SelectedTitle => _selected?.Title ?? "";

    public ToolListWindow(ToolPage page) : this(page, _ => { })
    {
    }

    public ToolListWindow(ToolPage page, Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _page = page ?? new ToolPage { Title = "工具" };
        _showReport = showReport ?? (_ => { });

        Title = _page.Title;
        Header.Title = _page.Title;
        Header.Badge = _page.Badge;

        KpiBox.Visibility = _page.Kpis != null ? Visibility.Visible : Visibility.Collapsed;

        if (_page.NativeFallbackLabel.Length > 0 && _page.NativeFallback != null)
        {
            NativeBtn.Content = _page.NativeFallbackLabel;
            NativeBtn.Visibility = Visibility.Visible;
        }

        BuildContextMenu();

        if (!ElevationService.IsAdmin && (_page.NeedsAdmin || _page.Actions.Any(a => a.NeedsAdmin)))
            ElevateBtn.Visibility = Visibility.Visible;

        if (_page.AutoRefreshSeconds > 0)
        {
            AutoBox.Visibility = Visibility.Visible;
            AutoBox.Content = $"自动刷新（{_page.AutoRefreshSeconds} 秒）";

            // 任务管理器这类页面本来就该是"活的"，默认开着；用户随时可以关掉
            AutoBox.IsChecked = true;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_page.AutoRefreshSeconds) };
            _timer.Start();
            _timer.Tick += (_, _) =>
            {
                if (_busy || !IsVisible) return;
                _silentReload = true;
                Reload();
                _silentReload = false;
            };
        }

        Grid.RowSelected += OnRowSelected;

        Loaded += (_, _) => Reload();

        if (_timer != null)
            Closed += (_, _) => _timer.Stop();
    }

    // ---------------- 采集与渲染 ----------------

    /// <summary>重新采集（按钮、自动刷新、自检共用）。</summary>
    public void Reload()
    {
        if (_busy) return;
        _busy = true;

        try
        {
            RefreshBtn.IsEnabled = false;
            if (!_silentReload) Bar.SetElapsed("正在读取…");

            var sw = Stopwatch.StartNew();
            _page.Rows = _page.Load() ?? new List<LogRow>();
            sw.Stop();

            Grid.SetRows(_page.Rows, _page.Options);

            RestoreSelection();

            ApplyKpis();

            Hint.Text = _page.Rows.Count == 0
                ? _page.EmptyText
                : $"{_page.Rows.Count} 条　·　{(ElevationService.IsAdmin ? "管理员模式" : "普通权限")}";

            Bar.SetElapsed(_silentReload
                ? $"{_page.Rows.Count} 条　·　自动刷新中　·　{DateTime.Now:HH:mm:ss}"
                : $"{_page.Rows.Count} 条　·　耗时 {sw.ElapsedMilliseconds} ms　·　{DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Bar.SetElapsed("读取失败：" + ex.Message);

            if (!_silentReload)
                UiDialog.Show(this, "读取失败：\n\n" + ex.Message, _page.Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
            _busy = false;
        }
    }

    private void ApplyKpis()
    {
        if (_page.Kpis == null) return;

        var items = _page.Kpis();

        Kpi.Set(items
            .Take(4)
            .Select(i => (i.Value, i.Label, i.Color))
            .ToArray());
    }

    private void OnRowSelected(LogRow row)
    {
        _selected = row;
        _selectedKey = KeyOf(row);
        DetailBox.Visibility = Visibility.Visible;

        DetailTitle.Text = row.Title;
        DetailText.Text = string.IsNullOrWhiteSpace(row.Detail) ? row.Basis : row.Detail;

        DetailStripe.Background = row.Level == "none"
            ? Palette.Primary
            : Palette.Get(row.LevelBrush);

        // 点同一行不应重复叠按钮
        DetailActions.Children.Clear();
        _actionButtons.Clear();

        foreach (var action in _page.RowActions.Concat(_page.Actions))
            DetailActions.Children.Add(BuildButton(action));
    }

    /// <summary>
    /// 给表格挂右键菜单。
    ///
    /// 工具条上的按钮多了以后（服务页有 8 个动作）反而不容易发现「改优先级」「改启动类型」在哪，
    /// 右键菜单是这类工具的标准入口：选一行 → 右键 → 直接看到能对它做什么。
    /// </summary>
    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var header = new MenuItem { Header = "先选中一行", IsEnabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new Separator());

        foreach (var action in _page.RowActions.Concat(_page.Actions))
        {
            var needsAdmin = action.NeedsAdmin && !ElevationService.IsAdmin;

            var item = new MenuItem
            {
                Header = needsAdmin ? action.Label + "（需管理员）" : action.Label,
                ToolTip = action.Tip
            };

            item.Click += (_, _) => RunAction(action);
            menu.Items.Add(item);
        }

        menu.Opened += (_, _) =>
        {
            header.Header = _selected == null
                ? "先选中一行（下面这些操作对选中行生效）"
                : "选中：" + _selected.Title;
        };

        Grid.ContextMenu = menu;
    }

    private Button BuildButton(ToolAction action)
    {
        var button = new Button
        {
            Content = action.Label,
            Style = (Style)FindResource(action.Primary ? "PrimaryButton" : "ToolButton"),
            Margin = new Thickness(0, 0, 6, 0),
            FontSize = 10.5
        };

        if (action.Tip.Length > 0)
            button.ToolTip = action.Tip;

        var blockedByAdmin = action.NeedsAdmin && !ElevationService.IsAdmin;
        var blockedBySelection = action.NeedsSelection && _selected == null;
        button.IsEnabled = !blockedByAdmin && !blockedBySelection;

        if (blockedByAdmin)
            button.ToolTip = "需要管理员权限，点右上角「以管理员身份重启」后再试。";

        button.Click += (_, _) => RunAction(action);
        _actionButtons.Add(button);

        return button;
    }

    private void RunAction(ToolAction action)
    {
        var context = new ToolActionContext
        {
            Owner = this,
            Selection = _selected == null ? Array.Empty<LogRow>() : new[] { _selected }
        };

        try
        {
            var result = action.Run(context);

            if (result == null || result.Message.Length == 0)
            {
                if (result?.Reload == true) Reload();
                return;
            }

            if (!result.Ok)
            {
                UiDialog.Show(this, result.Message, _page.Title,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 单条操作成功的提示放在底栏，不打断操作节奏
            Bar.SetElapsed(result.Message);

            if (result.Reload) Reload();
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, action.Label + " 失败：\n\n" + ex.Message, _page.Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>供页面动作打开其它窗口（事件查看器等），沿用主窗口的子窗口行为。</summary>
    public void OpenChildWindow(Window child)
    {
        if (_page.OpenChild != null)
        {
            _page.OpenChild(child);
            return;
        }

        child.Owner = this;
        child.Show();
    }

    private string KeyOf(LogRow row)
        => _page.SelectionKey != null ? _page.SelectionKey(row) : row.Title + "|" + row.Basis;

    /// <summary>刷新后把选中行找回来（进程页 2 秒一刷，否则详情条会自己消失）。</summary>
    private void RestoreSelection()
    {
        if (_selected == null || _selectedKey.Length == 0)
        {
            ClearSelection();
            return;
        }

        var again = _page.Rows.FirstOrDefault(r => KeyOf(r) == _selectedKey);

        if (again == null)
        {
            ClearSelection();
            return;
        }

        OnRowSelected(again);
    }

    private void ClearSelection()
    {
        _selected = null;
        _selectedKey = "";
        DetailBox.Visibility = Visibility.Collapsed;
        DetailActions.Children.Clear();
        _actionButtons.Clear();
    }

    // ---------------- 按钮 ----------------

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnAutoChanged(object sender, RoutedEventArgs e)
    {
        if (_timer == null) return;

        if (AutoBox.IsChecked == true)
        {
            _timer.Start();
            Bar.SetElapsed($"自动刷新已开启（每 {_page.AutoRefreshSeconds} 秒）");
        }
        else
        {
            _timer.Stop();
            Bar.SetElapsed("自动刷新已关闭");
        }
    }

    private void OnNative(object sender, RoutedEventArgs e)
    {
        try
        {
            _page.NativeFallback?.Invoke(this);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "打开系统工具失败：\n\n" + ex.Message, _page.Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin(_page.LaunchArg, out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            Bar.SetElapsed(error);
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var payload = _page.Report?.Invoke(_page) ?? ToolPageReport.BuildGeneric(_page);
            _showReport(payload);
            Bar.SetElapsed("报告已生成");
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "生成报告失败：\n\n" + ex.Message, _page.Title,
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
