using System.Linq;
using System.IO;
using System.Windows;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 程序执行记录：BAM 最近执行 / AmCache 安装清单 / SRUM 库状态。
/// 三处记录都在受保护位置，普通权限只能看到「需要管理员」，点一下提权重启后自动回到本窗口重读。
/// </summary>
public partial class ExecTraceWindow : Window
{
    private readonly Action<ReportPayload> _showReport;

    private ExecScanResult _data = new();
    private bool _busy;

    /// <summary>自检读取：当前两组的记录总数。</summary>
    public int ItemCount => _data.Bam.Count + _data.Amc.Count;

    public SrumStatus Srum => _data.Srum;

    /// <summary>自检读取：普通权限下是否显示提权入口。</summary>
    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    public ExecTraceWindow() : this(_ => { })
    {
    }

    public ExecTraceWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        var admin = LoggingService.IsAdmin();
        AdminBanner.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        if (admin)
        {
            AdminBanner.Visibility = Visibility.Collapsed;
            AdminTitle.Text = "已具备管理员权限";
        }

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await CollectAsync();
    }

    /// <summary>同步读取并渲染。自检直接调用，不依赖窗口显示。</summary>
    public void Reload()
    {
        _data = ExecTraceService.Scan();
        Apply();
    }

    private async Task CollectAsync()
    {
        if (_busy) return;

        _busy = true;
        RefreshBtn.IsEnabled = false;
        StatusText.Text = "正在读取 BAM / AmCache / SRUM…";

        try
        {
            _data = await Task.Run(ExecTraceService.Scan);
            Apply();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshBtn.IsEnabled = true;
        }
    }

    private void Apply()
    {
        BamGrid.SetRows(_data.Bam.Select(r => new LogRow
        {
            Title = r.Name,
            Detail = r.Path,
            MetricText = r.SourceText,
            Time = r.Time,
            TimeText = r.TimeText,
            Payload = r
        }), new SysDiag.Controls.LogViewOptions
        {
            Title = "最近执行（BAM）",
            TitleNote = _data.BamNote.Length > 0 ? _data.BamNote : "后台活动管理器记录的最后执行时间，精度到秒",
            Compact = true,
            ShowTimeline = true
        });

        AmcGrid.SetRows(_data.Amc.Select(r => new LogRow
        {
            Title = r.Name,
            Detail = r.Path,
            MetricText = r.SourceText,
            Time = r.Time,
            TimeText = r.TimeText,
            Payload = r
        }), new SysDiag.Controls.LogViewOptions
        {
            Title = "历史安装 / 执行（AmCache）",
            TitleNote = _data.AmcNote,
            Compact = true,
            ShowTimeline = false
        });

        SrumBadge.Visibility = Visibility.Visible;
        var srum = _data.Srum;
        SrumBadgeText.Text = srum.LevelText;
        SrumBadgeText.Foreground = Palette.Get(srum.LevelColor);
        SrumBadge.Background = Palette.Get(srum.LevelBg);
        SrumDetail.Text = srum.Summary +
            (string.IsNullOrEmpty(srum.ServiceState) ? "" : $"\n诊断策略服务（DPS）：{srum.ServiceState}");

        SummaryText.Text = $"BAM {_data.Bam.Count} 条　·　AmCache {_data.Amc.Count} 条　·　SRUM {srum.LevelText}";

        StatusText.Text = _data.HasData
            ? $"读取完成（{DateTime.Now:HH:mm:ss}）。记录只说明系统记录到过，不代表一定跑过。"
            : "三处记录都没读到。若按钮「以管理员身份重启」可见，点它重试。";

        ReportBtn.IsEnabled = true;
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await CollectAsync();

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-exectrace", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error))
            StatusText.Text = error;
    }

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var payload = ExecTraceService.BuildReport(
                _data.Bam, _data.Amc, _data.AmcNote, _data.Srum);

            _showReport(payload);
            StatusText.Text = "报告已生成：" + Path.GetFileName(payload.HtmlPath);
        }
        catch (Exception ex)
        {
            StatusText.Text = "报告生成失败：" + ex.Message;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
