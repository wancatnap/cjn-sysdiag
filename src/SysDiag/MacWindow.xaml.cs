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
            ApplyBtn.Content = "恢复硬件 MAC";
            ListHint.Text = "只显示被改过 MAC 的网卡";
            InputPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            Title = "修改网卡 MAC";
            TitleText.Text = "修改网卡 MAC";
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
            AdminBanner.Background = Palette.OkSoft;
            AdminBanner.BorderBrush = Palette.Ok;
            AdminTitle.Text = "已具备管理员权限";
            AdminTitle.Foreground = Palette.Ok;
            AdminDetail.Text = "";
            ElevateBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            AdminBanner.Background = Palette.DangerSoft;
            AdminBanner.BorderBrush = Palette.Danger;
            AdminTitle.Text = "需要管理员权限";
            AdminTitle.Foreground = Palette.Danger;
            AdminDetail.Text = "需要管理员";
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
            var confirm = UiDialog.Show(this,
                $"恢复「{adapter.Name}」的硬件 MAC？\n\n" +
                $"当前自定义：{adapter.OverrideText}\n硬件地址：{adapter.HardwareMac}\n\n" +
                "会删除注册表里的 NetworkAddress，重启网卡后生效，重启时网络会瞬断几秒。",
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

        var ask = UiDialog.Show(this,
            $"把「{adapter.Name}」的 MAC 改为 {MacAddressService.Pretty(normalized)}？\n\n" +
            $"原硬件地址：{adapter.HardwareMac}（改错随时用「恢复网卡 MAC」还原，不会丢）\n\n" +
            "改动前要知道的事：\n" +
            "· 写入后要重启网卡才生效，重启的瞬间网络会断开，正在进行的下载、语音、游戏都会中断；\n" +
            "· 公司、校园网这类受管网络常用 IP-MAC 绑定和准入认证，改 MAC 后可能直接上不了网，" +
            "网管侧也会看到一台「新设备」——在这类网络里改 MAC 可能违反使用政策；\n" +
            "· 游戏反作弊（EAC / BattlEye / Vanguard 等）不检测 MAC，改它不会让游戏内检测变松；" +
            "但厂商的风控系统会把 MAC 当作硬件指纹的一部分，指纹突变（尤其这台机器有封禁记录时）" +
            "可能被判定为逃避封禁，导致处罚升级或连坐其他账号——本功能面向网络诊断与隐私场景，" +
            "不是绕过游戏封禁的工具，用它绕封禁通常无效且后果自己承担；\n" +
            "· 手动填的 MAC 如果和同一网络里另一台设备相同，两台设备都会通信异常" +
            "（随机生成的已避开厂商地址段，没这个问题）；\n" +
            "· 少数网卡驱动不支持修改，写了也不生效。",
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
                UiDialog.Show(this, "提权失败：" + error, "破烂优化",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Application.Current.Shutdown();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
