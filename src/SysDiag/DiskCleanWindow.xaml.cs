using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// C 盘清理：打开即只读扫描 → 自动勾选安全项 → 用户确认后才真正删除。
/// 安全项直接删除（立即释放空间），谨慎项一律走回收站（可还原）。
/// </summary>
public partial class DiskCleanWindow : Window
{
    private readonly ObservableCollection<DiskCleanTarget> _items = new();

    private DiskScanResult _scan = new();
    private bool _busy;

    /// <summary>自检读取：扫描出的条目数。</summary>
    public int TargetCount => _items.Count;

    /// <summary>自检读取：本次可清理总量。</summary>
    public long CleanableBytes => _scan.CleanableBytes;

    /// <summary>自检读取：默认自动勾选的安全项数量。</summary>
    public int SafeSelectedCount => _items.Count(i => i.Selected && i.Risk == DiskRisk.Safe);

    /// <summary>自检读取：深度分析条目数。</summary>
    public int DeepCount => RootList.Items.Count;

    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    public DiskScanResult Snapshot => _scan;

    public DiskCleanWindow()
    {
        InitializeComponent();

        TargetList.ItemsSource = _items;

        var admin = ElevationService.IsAdmin;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        AdminHint.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ScanAsync();
    }

    /// <summary>同步扫描，供无头自检调用（不依赖窗口显示）。</summary>
    public void ScanSync()
    {
        _scan = DiskCleanService.Scan(null);
        ApplyTargets();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        CleanBtn.IsEnabled = false;
        DeepBtn.IsEnabled = false;
        ScanHint.Text = "正在扫描 C 盘…";

        var progress = new Progress<string>(s => ScanHint.Text = s);

        try
        {
            _scan = await Task.Run(() => DiskCleanService.Scan(progress));
            ApplyTargets();

            ScanHint.Text = _items.Count == 0
                ? "没有发现可清理的内容，C 盘已经很干净了。"
                : $"扫描完成：{_items.Count} 项 · 可清理 {_scan.CleanableText} · 耗时 {_scan.ElapsedMs / 1000.0:N1}s";
        }
        catch (Exception ex)
        {
            ScanHint.Text = "扫描失败：" + ex.Message;
            EmptyHint.Text = "扫描失败：" + ex.Message;
            EmptyHint.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            DeepBtn.IsEnabled = true;
            UpdateSummary();
        }
    }

    private void ApplyTargets()
    {
        foreach (var item in _items) item.PropertyChanged -= OnItemChanged;
        _items.Clear();

        foreach (var t in _scan.Targets)
        {
            // 自动勾选「安全」项；「谨慎」项交给人自己判断
            t.Selected = t.Risk == DiskRisk.Safe;
            t.PropertyChanged += OnItemChanged;
            _items.Add(t);
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = "没有发现可清理的内容，C 盘已经很干净了。";

        ApplyCapacity();
        UpdateSummary();
    }

    private void ApplyCapacity()
    {
        var total = _scan.TotalBytes;
        var free = _scan.FreeBytes;
        var used = Math.Max(0, total - free);

        if (total <= 0)
        {
            CapText.Text = "读不到 C 盘容量信息";
            FreeText.Text = "";
            UsageBar.Value = 0;
        }
        else
        {
            var usedPct = used * 100.0 / total;

            CapText.Text = $"C 盘 {LogFileItem.FormatSize(total)}　·　已用 {LogFileItem.FormatSize(used)}　·　可用 {LogFileItem.FormatSize(free)}";
            FreeText.Text = $"已用 {usedPct:N1}%　·　可用 {free * 100.0 / total:N1}%";
            UsageBar.Value = usedPct;
            UsageBar.Foreground = usedPct >= 90 ? Palette.Danger : usedPct >= 75 ? Palette.Hint : Palette.Primary;
        }

        CleanableText.Text = _scan.CleanableBytes > 0
            ? $"可清理 {_scan.CleanableText}"
            : "";
    }

    private void OnItemChanged(object sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DiskCleanTarget.Selected)) UpdateSummary();
    }

    private void UpdateSummary()
    {
        var selected = _items.Where(i => i.Selected && i.CanClean).ToList();
        var size = selected.Sum(i => i.Size);

        if (selected.Count == 0)
        {
            SummaryText.Text = "未选择任何项";
            CleanBtn.Content = "清理选中项";
        }
        else
        {
            var safe = selected.Count(i => i.Risk == DiskRisk.Safe);
            SummaryText.Text = $"已选 {selected.Count} 项 / {LogFileItem.FormatSize(size)}　·　安全项 {safe} 项，谨慎项 {selected.Count - safe} 项";
            CleanBtn.Content = $"清理选中项（{LogFileItem.FormatSize(size)}）";
        }

        CleanBtn.IsEnabled = selected.Count > 0 && !_busy;
    }

    private void OnSelectSafe(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items)
            i.Selected = i.CanClean && i.Risk == DiskRisk.Safe;
    }

    private void OnClearSelection(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items) i.Selected = false;
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private async void OnDeepScan(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        DeepBtn.IsEnabled = false;
        DeepPanel.Visibility = Visibility.Visible;
        DeepHint.Text = "正在统计各目录体积，可能需要一两分钟…";
        UpdateSummary();

        var progress = new Progress<string>(s => DeepHint.Text = s);

        try
        {
            var list = await Task.Run(() => DiskCleanService.ScanRootUsage(progress));
            RootList.ItemsSource = list;

            DeepHint.Text = list.Count == 0
                ? "没有统计到结果（可能需要管理员权限）"
                : $"{list.Count} 个目录　·　合计 {LogFileItem.FormatSize(list.Sum(x => x.Size))}";
        }
        catch (Exception ex)
        {
            DeepHint.Text = "统计失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            DeepBtn.IsEnabled = true;
            UpdateSummary();
        }
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        var selected = _items.Where(i => i.Selected && i.CanClean).ToList();
        if (selected.Count == 0) return;

        var direct = DirectMode.IsChecked == true;
        var total = selected.Sum(i => i.Size);

        // 逐项列出，让用户看清到底要删什么
        var lines = selected.Select(t =>
        {
            var how = direct || t.Risk == DiskRisk.Safe ? "直接删除" : "移入回收站";
            return $"· {t.Name}　{t.SizeText}　→ {how}";
        }).ToList();

        var preview = string.Join("\n", lines.Take(10));
        if (lines.Count > 10) preview += $"\n… 其余 {lines.Count - 10} 项";

        var body =
            $"即将清理以下 {selected.Count} 项，合计 {LogFileItem.FormatSize(total)}：\n\n{preview}\n\n" +
            "⚠ 此操作不可逆：直接删除的文件无法恢复；移入回收站的要清空回收站才真正释放空间。\n" +
            "正在被程序占用的文件会自动跳过，失败的项会逐条列在结果里。\n\n" +
            "确认继续？";

        var confirm = UiDialog.Show(this, body, "确认清理 C 盘",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        _busy = true;
        CleanBtn.IsEnabled = false;
        ScanHint.Text = "正在清理…";

        var freeBefore = _scan.FreeBytes;
        var progress = new Progress<string>(s => ScanHint.Text = s);
        var directFlag = direct;

        DiskCleanOutcome outcome = null;

        try
        {
            outcome = await Task.Run(() => DiskCleanService.Clean(selected, directFlag, progress));
        }
        catch (Exception ex)
        {
            _busy = false;
            UiDialog.Show(this, "清理失败：\n\n" + ex.Message, "C 盘清理",
                MessageBoxButton.OK, MessageBoxImage.Error);
            await ScanAsync();
            return;
        }

        _busy = false;
        await ScanAsync();

        var freeAfter = _scan.FreeBytes;
        var freed = Math.Max(0, freeAfter - freeBefore);

        var msg = $"清理完成，用时 {outcome.ElapsedMs / 1000.0:N1} 秒。\n\n" +
                  $"直接删除：{LogFileItem.FormatSize(outcome.DeletedBytes)}（{outcome.RemovedFiles:N0} 个文件）";

        if (outcome.RecycledBytes > 0)
            msg += $"\n移入回收站：{LogFileItem.FormatSize(outcome.RecycledBytes)}" +
                   "（清空回收站后才真正释放）";

        msg += $"\n\nC 盘可用空间：{LogFileItem.FormatSize(freeBefore)} → {LogFileItem.FormatSize(freeAfter)}" +
               $"（实际增加 {LogFileItem.FormatSize(freed)}）";

        if (outcome.Errors.Count > 0)
        {
            msg += $"\n\n有 {outcome.Errors.Count} 项被跳过或失败（文件被占用、权限不足等，属正常）：\n" +
                   string.Join("\n", outcome.Errors.Take(6));
            if (outcome.Errors.Count > 6) msg += $"\n… 其余 {outcome.Errors.Count - 6} 项略";
        }

        UiDialog.Show(this, msg, "C 盘清理", MessageBoxButton.OK,
            outcome.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-diskclean", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            ScanHint.Text = error;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
