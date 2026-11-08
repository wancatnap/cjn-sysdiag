using System.Collections.ObjectModel;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>断网急救：链路体检 + 逐项修复。</summary>
public partial class NetRepairWindow : Window
{
    private readonly ObservableCollection<NetCheckItem> _checks = new();
    private readonly ObservableCollection<NetFixStep> _steps = new();
    private bool _busy;

    public int CheckCount => _checks.Count;
    public int StepCount => _steps.Count;

    /// <summary>自检用：异常 / 注意的项数。</summary>
    public int ProblemCount => _checks.Count(c => c.Level != "ok");

    /// <summary>自检用：默认勾选的步骤数。</summary>
    public int DefaultSelectedCount => _steps.Count(s => s.Selected);

    public NetRepairWindow()
    {
        InitializeComponent();

        CheckList.ItemsSource = _checks;
        StepList.ItemsSource = _steps;

        foreach (var step in NetRepairService.BuildSteps())
            _steps.Add(step);

        StepHint.Text = $"共 {_steps.Count} 项，默认勾选 {DefaultSelectedCount} 项";

        UpdateToggleText();
        ApplyAdminState();

        Loaded += async (_, _) => await RecheckAsync();
    }

    private void ApplyAdminState()
    {
        var admin = NetRepairService.IsAdmin;

        AdminBanner.Background = (admin ? Palette.OkSoft : Palette.DangerSoft);
        AdminBanner.BorderBrush = (admin ? Palette.Ok : Palette.Danger);
        AdminTitle.Text = admin ? "已具备管理员权限" : "需要管理员权限";
        AdminTitle.Foreground = (admin ? Palette.Ok : Palette.Danger);
        AdminDetail.Text = admin
            ? ""
            : "需要管理员";

        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
    }

    // --------------------------------------------------------------- 体检

    public async Task RecheckAsync()
    {
        if (_busy) return;
        _busy = true;
        CheckHint.Text = "正在体检（含一次外网探测，约 2 秒）…";
        FixBtn.IsEnabled = false;

        try
        {
            var items = await Task.Run(() => NetRepairService.Check());

            _checks.Clear();
            foreach (var it in items) _checks.Add(it);

            var bad = _checks.Count(c => c.Level == "bad");
            var warn = _checks.Count(c => c.Level == "warn");

            CheckHint.Text = bad > 0
                ? $"{_checks.Count} 项检查，{bad} 项异常、{warn} 项注意"
                : warn > 0 ? $"{_checks.Count} 项检查，{warn} 项注意" : $"{_checks.Count} 项检查，全部正常";

            StatusText.Text = bad > 0 ? "发现异常项，按提示勾选修复步骤。" : "体检完成。";
        }
        catch (Exception ex)
        {
            CheckHint.Text = "体检失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            FixBtn.IsEnabled = true;
        }
    }

    private async void OnRecheck(object sender, RoutedEventArgs e) => await RecheckAsync();

    // --------------------------------------------------------------- 勾选

    /// <summary>全选 / 全不选合成一个按钮：已全选就全部取消，否则全部勾上。</summary>
    private void OnToggleAll(object sender, RoutedEventArgs e)
    {
        var allChecked = _steps.Count > 0 && _steps.All(s => s.Selected);

        foreach (var s in _steps) s.Selected = !allChecked;

        StepList.Items.Refresh();
        UpdateToggleText();
    }

    private void UpdateToggleText()
    {
        if (ToggleBtn == null) return;

        var allChecked = _steps.Count > 0 && _steps.All(s => s.Selected);

        ToggleBtn.Content = allChecked ? "全不选" : "全选";
    }

    // --------------------------------------------------------------- 修复

    private async void OnFix(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!NetRepairService.IsAdmin)
        {
            UiDialog.Show(this,
                "修复需要管理员权限。\n\n点右下角「以管理员身份重启」，重启后会自动回到本窗口。",
                "断网急救", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selected = _steps.Where(s => s.Selected).ToList();
        if (selected.Count == 0)
        {
            UiDialog.Show(this, "先勾选要执行的修复步骤。", "断网急救",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var restart = selected.Where(s => s.NeedsRestart).Select(s => s.Name).ToList();

        var ask = UiDialog.Show(this,
            $"将要执行 {selected.Count} 项：\n\n" +
            string.Join("\n", selected.Select(s => "· " + s.Name)) +
            "\n\n修复前会自动备份 hosts 与代理设置。" +
            (restart.Count > 0 ? $"\n\n其中 {string.Join("、", restart)} 需要重启电脑才完全生效。" : "") +
            "\n\n确定继续？",
            "确认修复", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (ask != MessageBoxResult.OK) return;

        _busy = true;
        FixBtn.IsEnabled = false;
        StatusText.Text = "正在修复…";

        try
        {
            var progress = new Progress<string>(s => StatusText.Text = Shorten(s));
            var results = await NetRepairService.RunAsync(selected, progress);

            var ok = results.Count(r => r.Ok);
            var failed = results.Where(r => !r.Ok).ToList();

            StatusText.Text = failed.Count == 0
                ? $"修复完成：{ok}/{results.Count} 项成功"
                : $"完成 {ok} 项，失败 {failed.Count} 项";

            if (failed.Count > 0)
                UiDialog.Show(this,
                    string.Join("\n\n", failed.Select(f => $"{f.Name}\n{f.Message}")),
                    "修复结果", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                UiDialog.Show(this,
                    $"已执行 {ok} 项修复。" +
                    (restart.Count > 0 ? "\n\n需要重启电脑才会完全生效。" : "") +
                    "\n\n可以点「重新体检」确认状态。",
                    "修复完成", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText.Text = "修复失败：" + ex.Message;
            UiDialog.Show(this, ex.Message, "断网急救", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            FixBtn.IsEnabled = true;
        }

        await RecheckAsync();
    }

    // --------------------------------------------------------------- 其他

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-netrepair", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error))
            StatusText.Text = error;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var t = text.Trim();
        return t.Length > 100 ? t[..100] + "…" : t;
    }
}
