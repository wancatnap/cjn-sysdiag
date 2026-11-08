using System.Windows;
using System.Windows.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>修改 / 恢复网卡 MAC。两种模式共用一个窗口，模式由构造参数决定。</summary>
public partial class MacWindow : Window
{
    private readonly string _mode;
    private List<MacAdapter> _all = new();

    public int AdapterCount => AdapterList.Items.Count;

    public bool IsRestoreMode => _mode == "restore";

    public string ApplyButtonText => ApplyBtn.Content as string ?? "";

    public MacWindow(string mode)
    {
        _mode = string.Equals(mode, "restore", StringComparison.OrdinalIgnoreCase) ? "restore" : "set";
        InitializeComponent();
        ApplyModeText();
        Loaded += (_, _) => Reload();
    }

    private void ApplyModeText()
    {
        if (IsRestoreMode)
        {
            Title = "恢复网卡 MAC";
            TitleText.Text = "恢复网卡 MAC";
            SubText.Text = "删除注册表里的 NetworkAddress 覆盖值，恢复网卡出厂 MAC。注意：删注册表只改配置，网卡驱动要重启网卡（禁用再启用）或重启电脑后才会换回固件地址。";
            ApplyBtn.Content = "恢复硬件 MAC";
            ListHint.Text = "只显示被改过 MAC 的网卡";
            InputPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "修改网卡 MAC";
            TitleText.Text = "修改网卡 MAC";
            SubText.Text = "通过注册表 NetworkAddress 修改网卡 MAC。写入后需要重启网卡或重启电脑才生效。";
            ApplyBtn.Content = "写入 MAC";
            ListHint.Text = "选择一块物理网卡（虚拟网卡已过滤）";
            InputPanel.Visibility = Visibility.Visible;
        }
    }

    public void Reload()
    {
        _all = MacAddressService.List();
        var shown = IsRestoreMode
            ? _all.Where(a => a.HasOverride).ToList()
            : _all;

        AdapterList.ItemsSource = null;
        AdapterList.ItemsSource = shown;

        if (shown.Count == 0)
        {
            StatusText.Text = IsRestoreMode
                ? "没有发现被改过 MAC 的网卡，当前都是硬件地址。"
                : "没有找到物理网卡（虚拟网卡已过滤）。";
        }
        else
        {
            StatusText.Text = IsRestoreMode
                ? $"发现 {shown.Count} 块被改过 MAC 的网卡。"
                : $"共 {_all.Count} 块物理网卡。";
        }

        ApplyAdminState();
        UpdateApplyState();
    }

    private void ApplyAdminState()
    {
        if (MacAddressService.IsAdmin)
        {
            AdminBanner.Background = Palette.Get("#EBFBEE");
            AdminBanner.BorderBrush = Palette.Get("#B2F2BB");
            AdminTitle.Text = "已具备管理员权限";
            AdminTitle.Foreground = Palette.Get("#2F9E44");
            AdminDetail.Text = "可以直接写入或恢复注册表。改动只作用于所选网卡，随时可以用另一个功能还原。";
            ElevateBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminBanner.Background = Palette.Get("#FFF5F5");
            AdminBanner.BorderBrush = Palette.Get("#FFC9C9");
            AdminTitle.Text = "需要管理员权限";
            AdminTitle.Foreground = Palette.Get("#C92A2A");
            AdminDetail.Text = "修改网卡注册表需要管理员权限。点右下角「以管理员身份重启」，重启后会自动回到本窗口。";
            ElevateBtn.Visibility = Visibility.Visible;
        }
    }

    private void UpdateApplyState()
        => ApplyBtn.IsEnabled = MacAddressService.IsAdmin && AdapterList.SelectedItem is MacAdapter;

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterList.SelectedItem is MacAdapter adapter)
        {
            if (!IsRestoreMode && string.IsNullOrWhiteSpace(MacBox.Text))
                MacBox.Text = adapter.OverrideMac;
        }

        UpdateApplyState();
    }

    private void OnRandom(object sender, RoutedEventArgs e)
    {
        MacBox.Text = MacAddressService.Random();
        StatusText.Text = "已生成随机 MAC（首字节 02，本地管理地址）。";
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        if (AdapterList.SelectedItem is not MacAdapter adapter)
        {
            StatusText.Text = "先在上面选一块网卡。";
            return;
        }

        if (!MacAddressService.IsAdmin)
        {
            StatusText.Text = "需要管理员权限：点右下角「以管理员身份重启」。";
            return;
        }

        if (IsRestoreMode)
        {
            var confirm = MessageBox.Show(this,
                $"恢复「{adapter.Name}」的硬件 MAC？\n\n" +
                $"当前自定义：{adapter.OverrideText}\n硬件地址：{adapter.HardwareMac}\n\n" +
                "会删除注册表里的 NetworkAddress，重启网卡后生效。",
                "恢复网卡 MAC", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            var (ok, message) = MacAddressService.Restore(adapter);
            StatusText.Text = message;
            if (ok) Reload();
            return;
        }

        var normalized = MacAddressService.Normalize(MacBox.Text);
        if (normalized == null)
        {
            StatusText.Text = "MAC 格式不对：需要 12 位十六进制，例如 02AABBCCDDEE。";
            return;
        }

        var ask = MessageBox.Show(this,
            $"把「{adapter.Name}」的 MAC 改为 {MacAddressService.Pretty(normalized)}？\n\n" +
            $"原硬件地址：{adapter.HardwareMac}\n\n" +
            "写入后需要重启网卡或重启电脑才生效；改错了可以用「恢复网卡 MAC」还原。",
            "修改网卡 MAC", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ask != MessageBoxResult.OK) return;

        var (okSet, msgSet) = MacAddressService.Set(adapter, normalized);
        StatusText.Text = msgSet;
        if (okSet) Reload();
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (MacAddressService.IsAdmin) return;

        if (!ElevationService.RelaunchAsAdmin("--admin --open-mac " + _mode, out var error))
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
