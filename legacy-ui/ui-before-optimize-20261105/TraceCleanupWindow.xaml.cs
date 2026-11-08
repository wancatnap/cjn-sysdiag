using System.Collections.ObjectModel;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>痕迹清理：USB 插拔记录 / 事件日志 / 运行痕迹，逐项勾选删除。</summary>
public partial class TraceCleanupWindow : Window
{
    private readonly ObservableCollection<TraceItem> _items = new();
    private bool _busy;

    public int ItemCount => _items.Count;

    public int SelectedCount => _items.Count(i => i.Selected);

    public TraceCleanupWindow()
    {
        InitializeComponent();
        TraceList.ItemsSource = _items;
        Loaded += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        DeleteBtn.IsEnabled = false;
        ScanHint.Text = "扫描中…";

        try
        {
            var list = await Task.Run(TraceCleanupService.Scan);

            _items.Clear();
            foreach (var item in list) _items.Add(item);

            ScanHint.Text = $"共 {_items.Count} 项：" +
                            string.Join(" / ", _items.GroupBy(i => i.Group)
                                .Select(g => $"{g.Key} {g.Count()}"));

            ApplyAdminState();
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

    private void ApplyAdminState()
    {
        if (TraceCleanupService.IsAdmin)
        {
            AdminBanner.Background = Palette.Get("#EBFBEE");
            AdminBanner.BorderBrush = Palette.Get("#B2F2BB");
            AdminTitle.Text = "已具备管理员权限";
            AdminTitle.Foreground = Palette.Get("#2F9E44");
            AdminDetail.Text = "USB 记录、系统日志都可以直接删除。删除前会再次确认。";
            ElevateBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminBanner.Background = Palette.Get("#FFF5F5");
            AdminBanner.BorderBrush = Palette.Get("#FFC9C9");
            AdminTitle.Text = "需要管理员权限";
            AdminTitle.Foreground = Palette.Get("#C92A2A");
            AdminDetail.Text = "USB 记录在 HKLM、系统日志清空也需要管理员。点右下角「以管理员身份重启」，重启后自动回到本窗口。";
            ElevateBtn.Visibility = Visibility.Visible;
        }
    }

    private void UpdateSummary()
    {
        var selected = _items.Where(i => i.Selected).ToList();
        DeleteBtn.IsEnabled = selected.Count > 0 && !_busy;
        StatusText.Text = selected.Count == 0
            ? "未选择任何项目"
            : $"已选 {selected.Count} 项";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.Selected = true;
        TraceList.Items.Refresh();
        UpdateSummary();
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items) item.Selected = false;
        TraceList.Items.Refresh();
        UpdateSummary();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.Selected).ToList();
        if (selected.Count == 0)
        {
            StatusText.Text = "先勾选要删除的项目。";
            return;
        }

        var usb = selected.Count(i => i.Kind == TraceKind.UsbKey);
        var logs = selected.Count(i => i.Kind == TraceKind.EventLog);
        var files = selected.Count(i => i.Kind is TraceKind.File or TraceKind.Folder);

        var ask = MessageBox.Show(this,
            $"将删除 {selected.Count} 项：\n\n" +
            $"· USB 插拔记录：{usb} 条\n" +
            $"· 事件日志：{logs} 个通道（会清空该通道全部记录）\n" +
            $"· 文件 / 目录：{files} 项（移入回收站）\n\n" +
            "注册表项和事件日志删除后不可恢复。确定继续？",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.OK) return;

        _busy = true;
        DeleteBtn.IsEnabled = false;

        var ok = 0;
        var failed = new List<string>();

        foreach (var item in selected)
        {
            var (success, message) = TraceCleanupService.Delete(item);
            if (success) ok++;
            else failed.Add(message);
        }

        _busy = false;
        StatusText.Text = failed.Count == 0
            ? $"完成：{ok} 项已删除"
            : $"完成 {ok} 项，失败 {failed.Count} 项：{failed[0]}";

        if (failed.Count > 0)
        {
            MessageBox.Show(this,
                $"有 {failed.Count} 项失败：\n\n" + string.Join("\n\n", failed.Take(6)),
                "删除结果", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await RefreshAsync();
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (TraceCleanupService.IsAdmin) return;

        if (!ElevationService.RelaunchAsAdmin("--admin --open-trace", out var error))
        {
            if (error != ElevationService.CancelledMessage)
                MessageBox.Show(this, "提权失败：" + error, "系统诊断台",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Application.Current.Shutdown();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
