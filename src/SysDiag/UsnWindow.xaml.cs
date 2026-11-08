using System.IO;
using System.Windows;
using System.Windows.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>文件删除记录：非管理员时点一下，整个程序以管理员身份重启并自动读取。</summary>
public partial class UsnWindow : Window
{
    private readonly Action<ReportPayload> _showReport;
    private bool _busy;
    private bool? _adminOverride;

    public int DriveCount => DriveBox.Items.Count;

    /// <summary>自检专用：强制走管理员 / 普通权限分支，不改变真实权限。</summary>
    internal bool? AdminOverride
    {
        get => _adminOverride;
        set
        {
            _adminOverride = value;
            Reload();
        }
    }

    private bool IsAdminNow => _adminOverride ?? LoggingService.IsAdmin();

    public UsnWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();
        _showReport = showReport;
        Loaded += (_, _) => Reload();
    }

    public void Reload()
    {
        if (DriveBox.Items.Count == 0)
        {
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    DriveBox.Items.Add(d.Name.TrimEnd('\\'));
                }
            }
            catch { }
        }

        if (DriveBox.SelectedIndex < 0 && DriveBox.Items.Count > 0) DriveBox.SelectedIndex = 0;

        var admin = IsAdminNow;
        if (admin)
        {
            AdminBanner.Background = Palette.OkSoft;
            AdminBanner.BorderBrush = Palette.Ok;
            AdminTitle.Text = "已具备管理员权限";
            AdminTitle.Foreground = Palette.Ok;
            AdminDetail.Text = "";
            ScanBtn.Content = "开始读取";
        }
        else
        {
            AdminBanner.Background = Palette.DangerSoft;
            AdminBanner.BorderBrush = Palette.Danger;
            AdminTitle.Text = "需要管理员权限";
            AdminTitle.Foreground = Palette.Danger;
            AdminDetail.Text = "需要管理员";
            ScanBtn.Content = "以管理员身份重启并读取";
        }
    }

    /// <summary>自检读取：按钮文案随权限变化。</summary>
    public string ScanButtonText => ScanBtn.Content as string ?? "";

    /// <summary>自检读取：权限提示条标题。</summary>
    public string AdminBannerTitle => AdminTitle.Text;

    /// <summary>接上提权重启前选择的磁盘与时间范围。</summary>
    public void Preset(string drive, int hours)
    {
        Reload();

        var wanted = ElevationService.NormalizeDrive(drive) + ":";
        for (var i = 0; i < DriveBox.Items.Count; i++)
        {
            if (!string.Equals(DriveBox.Items[i] as string, wanted, StringComparison.OrdinalIgnoreCase)) continue;
            DriveBox.SelectedIndex = i;
            break;
        }

        RangePicker.SetPresetHours(hours);
    }

    /// <summary>管理员实例启动后自动读取；没有权限时只提示，不反复弹 UAC。</summary>
    public async Task StartScanAsync()
    {
        if (_busy) return;

        if (!IsAdminNow)
        {
            StatusText.Text = "还没有管理员权限，点右下角按钮重试。";
            return;
        }

        await RunLocalAsync();
    }

    /// <summary>当前区间（自定义或预设，已做边界处理）。</summary>
    private TimeRange SelectedRange() => RangePicker.Range;

    /// <summary>提权重启只能带小时数：自定义区间换算成等效小时（向上取整）。</summary>
    private int SelectedHours()
    {
        var range = SelectedRange();
        return range.IsAll ? 0 : Math.Max(1, range.Hours);
    }

    private string SelectedDrive()
    {
        var name = DriveBox.SelectedItem as string ?? "C:";
        return name.TrimEnd(':');
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (!IsAdminNow)
        {
            RelaunchWholeApp();
            return;
        }

        await RunLocalAsync();
    }

    private async Task RunLocalAsync()
    {
        _busy = true;
        ScanBtn.IsEnabled = false;
        StatusText.Text = "正在读取 USN 日志…";

        try
        {
            var drive = SelectedDrive();
            var range = SelectedRange();

            if (!range.IsValid)
            {
                StatusText.Text = "区间无效：结束时间必须晚于开始时间";
                return;
            }

            var since = range.IsAll ? DateTime.Now.AddYears(-20) : range.From;
            var until = range.IsAll ? (DateTime?)null : range.To;

            var payload = await Task.Run(() =>
            {
                var result = UsnService.Read(drive, since, 3000, until);
                return UsnService.BuildReport(result);
            });

            StatusText.Text = $"{drive}: · {range.Label} · {payload.Subtitle}";
            _showReport?.Invoke(payload);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "读取失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            StatusText.Text = "读取失败";
        }
        finally
        {
            _busy = false;
            ScanBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// 整个程序以管理员身份重启（不是提权跑一次命令）。新实例会带着 --open-usn 回来，
    /// 自动打开本窗口、接上当前选择并开始读取；当前实例随即退出。
    /// </summary>
    private void RelaunchWholeApp()
    {
        var range = SelectedRange();
        var hours = SelectedHours();
        var args = ElevationService.BuildResumeArgs(SelectedDrive(), hours);

        if (range.IsCustom)
            StatusText.Text = $"自定义区间在提权重启后按最近 {hours} 小时读取";

        if (!ElevationService.RelaunchAsAdmin(args, out var error))
        {
            if (error != ElevationService.CancelledMessage)
                UiDialog.Show(this, "提权失败：" + error, "破烂优化",
                    MessageBoxButton.OK, MessageBoxImage.Warning);

            StatusText.Text = "未提权，可再点一次重试";
            return;
        }

        StatusText.Text = "正在以管理员身份重启…";
        Application.Current.Shutdown();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
