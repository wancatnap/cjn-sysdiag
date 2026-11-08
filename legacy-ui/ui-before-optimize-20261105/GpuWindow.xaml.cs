using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>显卡检测 + 显示名修改（两张卡片共用）。</summary>
public partial class GpuWindow : Window
{
    private readonly ObservableCollection<GpuAdapter> _items = new();

    public int GpuCount => _items.Count;

    public GpuWindow()
    {
        InitializeComponent();
        GpuList.ItemsSource = _items;
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        try
        {
            var list = GpuService.List();
            _items.Clear();
            foreach (var gpu in list) _items.Add(gpu);

            ScanHint.Text = list.Count == 0 ? "没有找到显示适配器" : $"共 {list.Count} 块显卡";
            if (list.Count > 0 && GpuList.SelectedIndex < 0) GpuList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            ScanHint.Text = "读取失败：" + ex.Message;
        }

        ApplyAdminState();
    }

    private void ApplyAdminState()
    {
        if (GpuService.IsAdmin)
        {
            AdminBanner.Background = Palette.Get("#EBFBEE");
            AdminBanner.BorderBrush = Palette.Get("#B2F2BB");
            AdminTitle.Text = "已具备管理员权限";
            AdminTitle.Foreground = Palette.Get("#2F9E44");
            AdminDetail.Text = "可以直接修改显示名。改完刷新设备管理器或重启后生效，随时可恢复原名。";
            ElevateBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminBanner.Background = Palette.Get("#FFF5F5");
            AdminBanner.BorderBrush = Palette.Get("#FFC9C9");
            AdminTitle.Text = "改名需要管理员权限";
            AdminTitle.Foreground = Palette.Get("#C92A2A");
            AdminDetail.Text = "写显卡注册表需要管理员。检测不受影响，可以先看信息再决定。";
            ElevateBtn.Visibility = Visibility.Visible;
        }
    }

    private void OnGpuChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GpuList.SelectedItem is GpuAdapter gpu)
        {
            NameBox.Text = gpu.DisplayName;
            StatusText.Text = gpu.Detail;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (GpuList.SelectedItem is not GpuAdapter gpu)
        {
            StatusText.Text = "先选一块显卡。";
            return;
        }

        if (!GpuService.IsAdmin)
        {
            StatusText.Text = "需要管理员权限：点右下角「以管理员身份重启」。";
            return;
        }

        var newName = NameBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusText.Text = "名称不能为空。";
            return;
        }

        var ask = MessageBox.Show(this,
            $"把显卡显示名改成：\n\n{newName}\n\n" +
            $"当前：{gpu.DisplayName}\n\n" +
            "只改设备管理器里显示的名字，不影响驱动和性能。刷新或重启后生效。",
            "修改显卡名称", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.OK) return;

        var (ok, message) = GpuService.Rename(gpu, newName);
        StatusText.Text = ok ? "已修改，刷新后生效" : message;
        if (!ok) MessageBox.Show(this, message, "修改失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        Reload();
    }

    private void OnRestore(object sender, RoutedEventArgs e)
    {
        if (GpuList.SelectedItem is not GpuAdapter gpu) return;

        if (!GpuService.IsAdmin)
        {
            StatusText.Text = "需要管理员权限：点右下角「以管理员身份重启」。";
            return;
        }

        var ask = MessageBox.Show(this,
            $"恢复显示名为「{gpu.Name}」？", "恢复显卡名称",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        var (ok, message) = GpuService.Restore(gpu);
        StatusText.Text = message;
        if (!ok) MessageBox.Show(this, message, "恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        Reload();
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (GpuService.IsAdmin) return;

        if (!ElevationService.RelaunchAsAdmin("--admin --open-gpu", out var error))
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
