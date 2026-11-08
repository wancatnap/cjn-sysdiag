using System.IO;
using System.Linq;
using System.Windows;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 本地执行痕迹：Prefetch（运行过几次）+ ShimCache（文件出现过）。
/// 两个都读不全时给出明确原因，不拿空白当「没有」。
/// </summary>
public partial class PrefetchWindow : Window
{
    private readonly Action<ReportPayload> _showReport;

    private ExecArtifactResult _result = new();
    private bool _busy;

    /// <summary>自检读取：Prefetch 条数。</summary>
    public int PrefetchTotal => _result.Prefetch.Count;

    /// <summary>自检读取：ShimCache 条数。</summary>
    public int ShimTotal => _result.ShimCache.Count;

    public ExecArtifactResult Snapshot => _result;

    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    public PrefetchWindow() : this(_ => { })
    {
    }

    public PrefetchWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        var admin = ElevationService.IsAdmin;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ScanAsync();
    }

    /// <summary>同步扫描，供无头自检调用。</summary>
    public void ScanSync()
    {
        _result = PrefetchService.Scan();
        Apply();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        RescanBtn.IsEnabled = false;
        ReportBtn.IsEnabled = false;
        ScanHint.Text = "开始读取…";
        VerdictText.Text = "正在读取…";

        var progress = new Progress<string>(s => ScanHint.Text = s);

        try
        {
            _result = await Task.Run(() => PrefetchService.Scan(progress));
            Apply();

            ScanHint.Text = $"完成：Prefetch {_result.Prefetch.Count} 条 / ShimCache {_result.ShimCache.Count} 条 · {_result.ElapsedText}";
        }
        catch (Exception ex)
        {
            ScanHint.Text = "读取失败：" + ex.Message;
            VerdictText.Text = "读取失败";
        }
        finally
        {
            _busy = false;
            RescanBtn.IsEnabled = true;
            ReportBtn.IsEnabled = true;
        }
    }

    private void Apply()
    {
        PrefetchGrid.SetRows(
            _result.Top.Select(x => new LogRow
            {
                Title = x.Name,
                Detail = x.FilePath,
                MetricText = $"{x.RunCountText} 次 · {x.SizeText} · {x.VersionText}",
                Time = x.LastRun,
                TimeText = x.LastRunText,
                Level = x.IsSuspiciousPath ? "hint" : "none",
                LevelText = x.IsSuspiciousPath ? "临时目录" : "",
                Payload = x
            }),
            new LogViewOptions
            {
                Title = "Prefetch（运行过）",
                TitleNote = _result.PrefetchStateText,
                Compact = true,
                ShowTimeline = true
            });

        ShimGrid.SetRows(
            _result.ShimCache.OrderByDescending(s => s.LastModified).Take(800).Select(x => new LogRow
            {
                Title = x.Path,
                MetricText = x.SizeText,
                Time = x.LastModified,
                TimeText = x.LastModifiedText,
                Level = x.IsSuspiciousPath ? "hint" : "none",
                LevelText = x.IsSuspiciousPath ? "临时目录" : "",
                Payload = x
            }),
            new LogViewOptions
            {
                Title = "ShimCache（出现过）",
                TitleNote = _result.ShimCacheStateText,
                Compact = true,
                ShowTimeline = false
            });

        var suspicious = _result.Prefetch.Count(x => x.IsSuspiciousPath);

        PrefetchCount.Text = _result.Prefetch.Count.ToString("N0");
        ShimCount.Text = _result.ShimCache.Count.ToString("N0");
        SuspiciousCount.Text = suspicious.ToString();
        SuspiciousCount.Foreground = (suspicious > 0 ? Palette.Danger : Palette.Ok);

        VerdictText.Text = _result.Prefetch.Count == 0 && _result.ShimCache.Count == 0
            ? "没有读到数据，多半是权限不足"
            : suspicious > 0
                ? $"{suspicious} 个程序从临时目录运行过"
                : "没有发现从临时目录运行的程序";

        VerdictText.Foreground = (suspicious > 0 ? Palette.Hint : Palette.InkSecondary);

        ElapsedText.Text = _result.ElapsedText;
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var payload = PrefetchService.BuildReport(_result);
            _showReport(payload);
            ScanHint.Text = "报告已生成：" + Path.GetFileName(payload.HtmlPath);
        }
        catch (Exception ex)
        {
            ScanHint.Text = "报告生成失败：" + ex.Message;
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-prefetch", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            ScanHint.Text = error;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
