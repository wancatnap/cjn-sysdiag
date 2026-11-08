using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

public partial class CleanupWindow : Window
{
    private readonly ObservableCollection<LogCandidate> _items = new();
    private bool _busy;

    public CleanupWindow()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        Loaded += async (_, _) => await RefreshAsync();
    }

    /// <summary>供无头自检读取扫描结果。</summary>
    public int CandidateCount => _items.Count;
    public int FileCount => _items.Sum(i => i.Count);
    public long TotalSize => _items.Sum(i => i.Size);

    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        ScanHint.Text = "扫描中…";
        DeleteBtn.IsEnabled = false;

        try
        {
            var list = await Task.Run(LogScanner.Scan);
            _items.Clear();

            foreach (var c in list)
            {
                c.PropertyChanged += OnItemChanged;
                _items.Add(c);
            }

            ScanHint.Text = $"共 {_items.Count} 处 / {_items.Sum(i => i.Count)} 个文件 / " +
                            $"{LogFileItem.FormatSize(_items.Sum(i => i.Size))}";
        }
        catch (Exception ex)
        {
            ScanHint.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateSummary();
        }
    }

    private void OnItemChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LogCandidate.GroupSelected) or nameof(LogCandidate.SelectedText))
            UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = _items.SelectMany(i => i.Files).Where(f => f.Selected).ToList();
        var size = selected.Sum(f => f.Size);

        SummaryText.Text = selected.Count == 0
            ? "未选择任何项"
            : $"已选 {selected.Count} 个文件 / 合计 {LogFileItem.FormatSize(size)}";

        DeleteBtn.IsEnabled = selected.Count > 0 && !_busy;
    }

    /// <summary>组复选框：全选时取消，其余情况全选（含三态中间态）。</summary>
    private void OnGroupCheckPreview(object sender, MouseButtonEventArgs e)
    {
        if (_busy) return;
        if (sender is not CheckBox { DataContext: LogCandidate c } cb) return;
        if (!c.CanDelete) return;

        e.Handled = true;
        c.GroupSelected = c.GroupSelected != true;
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items.Where(x => x.CanDelete))
            foreach (var f in i.Files) f.Selected = true;
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var f in _items.SelectMany(i => i.Files)) f.Selected = false;
    }

    private void OnExpandAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsExpanded = true;
    }

    private void OnCollapseAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.IsExpanded = false;
    }

    private async void OnScanGames(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        DeleteBtn.IsEnabled = false;
        ScanHint.Text = "正在全盘查找游戏日志…";

        var progress = new Progress<string>(s => ScanHint.Text = s);

        try
        {
            var found = await Task.Run(() => GameLogFinder.FindAll(progress));
            var added = 0;

            foreach (var g in found)
            {
                if (_items.Any(i => string.Equals(i.DisplayPath, g.Path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var candidate = LogScanner.ScanCustomFolder(g.Path);
                if (candidate == null) continue;

                candidate.Category = "游戏日志";
                candidate.Name = g.Game + " 日志";
                candidate.PropertyChanged += OnItemChanged;
                _items.Insert(0, candidate);
                added++;
            }

            ScanHint.Text = $"全盘找到 {found.Count} 处，新增 {added} 组 / " +
                            $"共 {_items.Count} 处 / {_items.Sum(i => i.Count)} 个文件";
        }
        catch (Exception ex)
        {
            ScanHint.Text = "扫描失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            UpdateSummary();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要清理的日志目录" };
        if (dlg.ShowDialog(this) != true) return;

        var candidate = LogScanner.ScanCustomFolder(dlg.FolderName);
        if (candidate == null)
        {
            UiDialog.Show(this, "该目录下没有找到日志文件（*.log / *.txt / *.etl / *.dmp / *.old）。",
                "日志清理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        candidate.PropertyChanged += OnItemChanged;
        _items.Insert(0, candidate);
        ScanHint.Text = $"共 {_items.Count} 处 / {_items.Sum(i => i.Count)} 个文件 / " +
                        $"{LogFileItem.FormatSize(_items.Sum(i => i.Size))}";
        UpdateSummary();
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = _items.SelectMany(i => i.Files).Where(f => f.Selected).ToList();
        if (selected.Count == 0) return;

        var targets = selected.Select(f => f.Path).Distinct().ToList();
        var size = LogFileItem.FormatSize(selected.Sum(f => f.Size));

        var preview = string.Join("\n", selected.Take(8).Select(f => "· " + f.Name));
        if (selected.Count > 8) preview += $"\n… 其余 {selected.Count - 8} 个";

        var confirm = UiDialog.Show(this,
            $"即将把以下 {selected.Count} 个文件移入回收站（合计 {size}）：\n\n{preview}\n\n" +
            $"⚠ 移入回收站后可在资源管理器右键「还原」；一旦清空回收站则无法恢复。\n" +
            $"需要管理员权限的项（如 Windows\\Logs）可能失败，失败项会逐条列出。\n\n确认继续？",
            "确认移入回收站", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _busy = true;
        DeleteBtn.IsEnabled = false;
        ScanHint.Text = "正在移入回收站…";

        var errors = new List<string>();
        var moved = await Task.Run(() => RecycleBin.MoveToRecycleBin(targets, errors));

        _busy = false;

        var msg = $"已移入回收站：{moved.Count} 个对象，约 {size}。";
        if (errors.Count > 0)
        {
            msg += $"\n\n有 {errors.Count} 项失败（多为文件被占用或权限不足）：\n" +
                   string.Join("\n", errors.Take(8));
            if (errors.Count > 8) msg += $"\n… 其余 {errors.Count - 8} 项略";
        }

        UiDialog.Show(this, msg, "日志清理",
            MessageBoxButton.OK, errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

        await RefreshAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
