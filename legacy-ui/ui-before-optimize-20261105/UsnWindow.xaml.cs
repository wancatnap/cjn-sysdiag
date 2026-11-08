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
            AdminBanner.Background = Palette.Get("#EBFBEE");
            AdminBanner.BorderBrush = Palette.Get("#B2F2BB");
            AdminTitle.Text = "已具备管理员权限";
            AdminTitle.Foreground = Palette.Get("#2F9E44");
            AdminDetail.Text = "整个程序已以管理员身份运行，无需再次提权，选好条件直接读取即可。全程只读，不会恢复或修改任何文件。";
            ScanBtn.Content = "开始读取";
        }
        else
        {
            AdminBanner.Background = Palette.Get("#FFF5F5");
            AdminBanner.BorderBrush = Palette.Get("#FFC9C9");
            AdminTitle.Text = "需要管理员权限";
            AdminTitle.Foreground = Palette.Get("#C92A2A");
            AdminDetail.Text = "读取 USN 日志要打开磁盘卷句柄，普通权限拿不到。点下面的按钮会弹一次 UAC，以管理员身份重启整个程序；重启后自动回到这里并按当前选择开始读取，其他功能照常可用。";
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

        foreach (var item in RangeBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var h) && h == hours)
            {
                RangeBox.SelectedItem = item;
                return;
            }
        }
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

    private int SelectedHours()
    {
        if (RangeBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var h)) return h;
        return 168;
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
            var hours = SelectedHours();
            var since = hours <= 0 ? DateTime.Now.AddYears(-20) : DateTime.Now.AddHours(-hours);

            var payload = await Task.Run(() =>
            {
                var result = UsnService.Read(drive, since);
                return UsnService.BuildReport(result);
            });

            StatusText.Text = payload.Subtitle;
            _showReport?.Invoke(payload);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "读取失败：" + ex.Message, "系统诊断台",
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
        var args = ElevationService.BuildResumeArgs(SelectedDrive(), SelectedHours());

        if (!ElevationService.RelaunchAsAdmin(args, out var error))
        {
            if (error != ElevationService.CancelledMessage)
                MessageBox.Show(this, "提权失败：" + error, "系统诊断台",
                    MessageBoxButton.OK, MessageBoxImage.Warning);

            StatusText.Text = "未提权，可再点一次重试";
            return;
        }

        StatusText.Text = "正在以管理员身份重启…";
        Application.Current.Shutdown();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
