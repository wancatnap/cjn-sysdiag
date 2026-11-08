using System.Collections.ObjectModel;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>DNS 优化：测速选最快、应用到网卡、还原。</summary>
public partial class DnsWindow : Window
{
    private readonly ObservableCollection<DnsAdapter> _adapters = new();
    private readonly ObservableCollection<DnsProbeRow> _rows = new();
    private bool _busy;

    /// <summary>自检用：网卡数量。</summary>
    public int AdapterCount => _adapters.Count;

    /// <summary>自检用：方案数量。</summary>
    public int PresetCount => _rows.Count;

    /// <summary>自检用：应用按钮当前文案。</summary>
    public string ApplyText => ApplyBtn.Content as string ?? "";

    public DnsWindow()
    {
        InitializeComponent();

        AdapterList.ItemsSource = _adapters;
        PresetList.ItemsSource = _rows;

        foreach (var preset in DnsService.Presets)
            _rows.Add(new DnsProbeRow { Preset = preset });

        PresetList.SelectedIndex = 0;

        Reload();
        ApplyAdminState();
    }

    public void Reload()
    {
        var previous = _adapters.Where(a => a.Selected).Select(a => a.Id).ToHashSet();

        _adapters.Clear();
        foreach (var a in DnsService.ListAdapters())
        {
            if (previous.Count > 0) a.Selected = previous.Contains(a.Id);
            _adapters.Add(a);
        }

        AdapterHint.Text = _adapters.Count == 0
            ? "没有找到可改 DNS 的网卡"
            : $"勾选要应用 DNS 的网卡（可多选，共 {_adapters.Count} 块）";
    }

    private void ApplyAdminState()
    {
        var admin = DnsService.IsAdmin;

        AdminBanner.Background = (admin ? Palette.OkSoft : Palette.DangerSoft);
        AdminBanner.BorderBrush = (admin ? Palette.Ok : Palette.Danger);
        AdminTitle.Text = admin ? "已具备管理员权限" : "需要管理员权限";
        AdminTitle.Foreground = (admin ? Palette.Ok : Palette.Danger);
        AdminDetail.Text = admin
            ? ""
            : "需要管理员";

        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
    }

    // --------------------------------------------------------------- 测速

    private async void OnProbe(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        _busy = true;
        ApplyBtn.IsEnabled = false;
        ProbeHint.Text = "测速中…";

        try
        {
            var progress = new Progress<string>(s => ProbeHint.Text = s);
            var rows = await DnsService.ProbeAllAsync(progress);

            var selected = (PresetList.SelectedItem as DnsProbeRow)?.Preset.Name;

            _rows.Clear();
            foreach (var r in rows) _rows.Add(r);

            var keep = _rows.FirstOrDefault(r => r.Preset.Name == selected);
            PresetList.SelectedItem = keep ?? _rows.FirstOrDefault();

            var best = _rows.FirstOrDefault();
            ProbeHint.Text = best is { Ok: true }
                ? $"最快：{best.Name} {best.LatencyText}（已标推荐）"
                : "全部超时，可能是网络拦截了 53 端口，直接选常用方案即可";

            StatusText.Text = "测速完成。选中一行方案，再点「应用选中方案」。";
        }
        catch (Exception ex)
        {
            ProbeHint.Text = "测速失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            ApplyBtn.IsEnabled = true;
        }
    }

    private void OnPickBest(object sender, RoutedEventArgs e)
    {
        var best = _rows.FirstOrDefault(r => r.IsRecommended) ?? _rows.FirstOrDefault(r => r.Ok);
        if (best == null)
        {
            UiDialog.Show(this, "先点「开始测速」，才有推荐结果。", "DNS 优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PresetList.SelectedItem = best;
        StatusText.Text = $"已选中推荐方案：{best.Name}（{best.LatencyText}）";
    }

    // --------------------------------------------------------------- 应用

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!DnsService.IsAdmin)
        {
            UiDialog.Show(this,
                "改 DNS 需要管理员权限。\n\n点右下角「以管理员身份重启」，重启后会自动回到本窗口。",
                "DNS 优化", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targets = _adapters.Where(a => a.Selected).ToList();
        if (targets.Count == 0)
        {
            UiDialog.Show(this, "先勾选要应用 DNS 的网卡。", "DNS 优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PresetList.SelectedItem is not DnsProbeRow row)
        {
            UiDialog.Show(this, "先选中一个 DNS 方案。", "DNS 优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = UiDialog.Show(this,
            $"将把 {targets.Count} 块网卡的 DNS 设为 {row.Name}：\n\n" +
            string.Join("\n", targets.Select(a => $"· {a.Name}（当前 {a.DnsText}）")) +
            $"\n\n新 DNS：{row.IpHint}\n\n原配置会自动备份，随时可以「恢复到优化前」。确定继续？",
            "确认应用", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        _busy = true;
        ApplyBtn.IsEnabled = false;

        // 改之前先备份当前状态，保证能还原
        var (backupOk, backupMsg) = DnsService.CreateBackup(targets);
        StatusText.Text = backupMsg;

        var ok = 0;
        var failed = new List<string>();

        foreach (var adapter in targets)
        {
            var (success, message) = await DnsService.ApplyAsync(adapter, row.Preset);
            if (success) ok++;
            else failed.Add(adapter.Name + "：" + message);
        }

        _busy = false;
        ApplyBtn.IsEnabled = true;

        StatusText.Text = failed.Count == 0
            ? $"已把 {ok} 块网卡设为 {row.Name}"
            : $"成功 {ok} 块，失败 {failed.Count} 块";

        if (failed.Count > 0)
            UiDialog.Show(this, string.Join("\n\n", failed.Take(3)), "应用结果",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        Reload();
    }

    // --------------------------------------------------------------- 还原

    private async void OnRestoreDhcp(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;

        var targets = _adapters.Where(a => a.Selected).ToList();
        if (targets.Count == 0)
        {
            UiDialog.Show(this, "先勾选要还原的网卡。", "DNS 优化",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = UiDialog.Show(this,
            $"把 {targets.Count} 块网卡改回「自动获取 DNS」？\n\n" +
            string.Join("\n", targets.Select(a => "· " + a.Name)),
            "确认还原", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        var ok = 0;
        var failed = new List<string>();

        foreach (var adapter in targets)
        {
            var (success, message) = await DnsService.RestoreDhcpAsync(adapter);
            if (success) ok++;
            else failed.Add(adapter.Name + "：" + message);
        }

        StatusText.Text = failed.Count == 0 ? $"已还原 {ok} 块网卡为自动获取" : $"完成 {ok} 块，失败 {failed.Count} 块";

        if (failed.Count > 0)
            UiDialog.Show(this, string.Join("\n\n", failed.Take(3)), "还原结果",
                MessageBoxButton.OK, MessageBoxImage.Warning);

        Reload();
    }

    private async void OnRestoreBackup(object sender, RoutedEventArgs e)
    {
        if (!RequireAdmin()) return;

        var backup = DnsService.LoadBackup();
        if (backup == null)
        {
            UiDialog.Show(this,
                "没有找到优化前的备份。\n\n备份只在「应用选中方案」时自动生成。想回到自动获取，用「恢复为自动获取」。",
                "DNS 优化", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = UiDialog.Show(this,
            $"按备份（{backup.Time:yyyy-MM-dd HH:mm:ss}）还原 {backup.Entries.Count} 块网卡的 DNS？\n\n" +
            string.Join("\n", backup.Entries.Select(x => $"· {x.Name} → {(x.WasDhcp || x.Dns.Count == 0 ? "自动获取" : string.Join(" / ", x.Dns))}")),
            "确认还原", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ask != MessageBoxResult.OK) return;

        var (ok, message) = await DnsService.RestoreBackupAsync();
        StatusText.Text = message;

        if (!ok)
            UiDialog.Show(this, message, "还原结果", MessageBoxButton.OK, MessageBoxImage.Warning);

        Reload();
    }

    // --------------------------------------------------------------- 其他

    private bool RequireAdmin()
    {
        if (DnsService.IsAdmin) return true;

        UiDialog.Show(this, "这一项需要管理员权限，请先点「以管理员身份重启」。",
            "DNS 优化", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-dns", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error))
            StatusText.Text = error;
    }
}
