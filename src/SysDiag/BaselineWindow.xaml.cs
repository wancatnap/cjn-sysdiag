using System.Diagnostics;
using System.IO;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>设备比对：保存硬件基线，或与已保存的基线比对差异。</summary>
public partial class BaselineWindow : Window
{
    private readonly Action<ReportPayload> _showReport;

    public BaselineWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();
        _showReport = showReport;
        Loaded += (_, _) => Reload();
    }

    /// <summary>供无头自检读取。</summary>
    public bool HasBaseline => DeviceBaselineService.Load() != null;

    public void Reload()
    {
        var b = DeviceBaselineService.Load();

        if (b == null)
        {
            StatusTitle.Text = "尚未保存基线";
            StatusDetail.Text = "点「保存为新基线」把当前的机器码与 USB 清单存下来，" +
                                "以后换没换硬件、插过什么设备，一比对就知道。";
            CompareBtn.IsEnabled = false;
            return;
        }

        StatusTitle.Text = $"基线：{b.SavedAt:yyyy-MM-dd HH:mm}　{b.Computer}";
        CompareBtn.IsEnabled = true;

        var key = new[] { "系统标识 / SMBIOS UUID", "主板与固件 / 主板序列号", "磁盘 / 磁盘 0" };
        var lines = b.Fields
            .Where(f => key.Contains($"{f.Group} / {f.Name}") || f.Name.StartsWith("磁盘 0 ·"))
            .Select(f => $"{f.Group} / {f.Name}：{f.Value}")
            .ToList();

        StatusDetail.Text = $"字段 {b.Fields.Count} 项　|　USB {b.Usb.Count} 个　|　{b.Os}\n" +
                            string.Join("\n", lines.Take(3)) + "\n" +
                            $"基线文件：{DeviceBaselineService.FilePath}";
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var existing = DeviceBaselineService.Load();
        if (existing != null)
        {
            var ok = UiDialog.Show(this,
                $"会用当前状态覆盖 {existing.SavedAt:yyyy-MM-dd HH:mm} 保存的基线。\n\n" +
                "覆盖后旧基线无法找回，确定继续？",
                "破烂优化", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
        }

        try
        {
            var snap = DeviceBaselineService.Save();
            Reload();
            UiDialog.Show(this,
                $"已保存基线：{snap.SavedAt:yyyy-MM-dd HH:mm}\n字段 {snap.Fields.Count} 项 / USB {snap.Usb.Count} 个\n\n{DeviceBaselineService.FilePath}",
                "破烂优化", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "保存失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCompare(object sender, RoutedEventArgs e)
    {
        try
        {
            var diff = DeviceBaselineService.Compare();
            if (diff == null)
            {
                UiDialog.Show(this, "还没有基线，请先点「保存为新基线」。", "破烂优化",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _showReport?.Invoke(DeviceBaselineService.CompareReport(diff));
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "比对失败：" + ex.Message, "破烂优化",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = DeviceBaselineService.FilePath;
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, ex.Message, "破烂优化", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
