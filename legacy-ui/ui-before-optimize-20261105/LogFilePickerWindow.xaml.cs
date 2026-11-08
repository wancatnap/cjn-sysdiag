using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 列出某个目录下的日志文件，由用户自己勾选要分析哪几份。
/// 只读枚举，不改动任何文件。
/// </summary>
public partial class LogFilePickerWindow : Window
{
    private readonly string _root;
    private readonly ObservableCollection<LogPickItem> _items = new();
    private List<LogPickItem> _all = new();
    private bool _loading;

    public List<string> SelectedPaths { get; private set; } = new();
    public int TotalFound => _all.Count;
    public int SelectedCount => _all.Count(i => i.Selected);
    public int VisibleCount => _items.Count;

    public LogFilePickerWindow(string root)
    {
        InitializeComponent();
        _root = root ?? "";
        RootText.Text = _root;
        RootText.ToolTip = _root;
        List.ItemsSource = _items;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await ScanAsync();
    }

    /// <summary>供无头自检：同步扫描一遍，返回找到的文件数。</summary>
    public int ScanNow()
    {
        _all = LogFileDiscovery.Scan(_root, SubdirCheck.IsChecked == true);
        foreach (var i in _all) i.PropertyChanged += OnItemChanged;
        ApplyFilter();
        UpdateSummary();
        return _all.Count;
    }

    private async Task ScanAsync()
    {
        if (_loading) return;
        _loading = true;
        AcceptBtn.IsEnabled = false;
        _items.Clear();
        EmptyHint.Visibility = Visibility.Visible;
        EmptyHint.Text = "正在读取目录…";
        StatusText.Text = "正在读取目录…";

        var progress = new Progress<string>(s => StatusText.Text = s);
        var recurse = SubdirCheck.IsChecked == true;

        try
        {
            var list = await Task.Run(() => LogFileDiscovery.Scan(_root, recurse, progress));

            foreach (var i in _all) i.PropertyChanged -= OnItemChanged;
            _all = list;
            foreach (var i in _all) i.PropertyChanged += OnItemChanged;

            ApplyFilter();
        }
        catch (Exception ex)
        {
            _all = new List<LogPickItem>();
            _items.Clear();
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "读取失败：" + ex.Message;
            StatusText.Text = "读取失败";
        }
        finally
        {
            _loading = false;
            UpdateSummary();
        }
    }

    private void ApplyFilter()
    {
        var onlyLogs = OnlyLogCheck.IsChecked == true;
        var visible = _all.Where(i => !onlyLogs || i.IsAnalyzable).ToList();
        var visibleSet = new HashSet<LogPickItem>(visible);

        // 被过滤掉的行不再保持勾选，避免“看不见却被算进分析”
        foreach (var hidden in _all.Where(i => !visibleSet.Contains(i))) hidden.Selected = false;

        _items.Clear();
        foreach (var v in visible) _items.Add(v);

        if (visible.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
            EmptyHint.Text = _all.Count == 0
                ? "这个目录里没有文件"
                : "没有符合条件的文件（可以取消「只看日志类文件」）";
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    private void OnItemChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogPickItem.Selected)) UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_loading) return;

        var sel = SelectedCount;
        var bytes = _all.Where(i => i.Selected).Sum(i => i.Size);

        StatusText.Text = _all.Count == 0
            ? "没有可分析的文件"
            : $"显示 {_items.Count} / 共 {_all.Count} 个文件　·　已选 {sel} 个（{LogFileItem.FormatSize(bytes)}）";

        AcceptBtn.Content = sel == 0 ? "开始分析" : $"开始分析（{sel}）";
        AcceptBtn.IsEnabled = sel > 0;
    }

    // ---------------- 选择操作 ----------------

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.Selected = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var i in _all) i.Selected = false;
    }

    private void OnSelectNewest(object sender, RoutedEventArgs e) => SelectNewest(1);

    private void OnSelectRecent(object sender, RoutedEventArgs e) => SelectNewest(3);

    private void SelectNewest(int count)
    {
        foreach (var i in _all) i.Selected = false;

        var picks = _items
            .Where(i => i.IsAnalyzable)
            .OrderByDescending(i => i.Modified)
            .Take(count)
            .ToList();

        foreach (var p in picks) p.Selected = true;
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (List.SelectedItem is LogPickItem item) item.Selected = !item.Selected;
    }

    // ---------------- 其它 ----------------

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyFilter();
        UpdateSummary();
    }

    private async void OnScanOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        await ScanAsync();
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"\"{_root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "系统诊断台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        var picked = _all.Where(i => i.Selected).ToList();
        if (picked.Count == 0)
        {
            MessageBox.Show(this, "还没有勾选任何文件。", "系统诊断台",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var analyzable = picked.Where(i => i.IsAnalyzable).Select(i => i.Path).ToList();
        if (analyzable.Count == 0)
        {
            MessageBox.Show(this,
                $"勾选的 {picked.Count} 个文件都不是文本日志，无法分析。\n\n可以勾选 .log / .txt / .evtx 这类文件。",
                "系统诊断台", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (analyzable.Count < picked.Count)
        {
            var skip = picked.Count - analyzable.Count;
            var ok = MessageBox.Show(this,
                $"其中 {skip} 个不是文本日志，会被跳过，只分析其余 {analyzable.Count} 个？",
                "系统诊断台", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (ok != MessageBoxResult.OK) return;
        }

        SelectedPaths = analyzable;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
