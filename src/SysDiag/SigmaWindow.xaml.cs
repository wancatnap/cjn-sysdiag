using System.IO;
using System.Linq;
using System.Windows;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 日志规则匹配：把事件日志读成结构化字段，按内置 Sigma 子集规则匹配。
/// 打开即自动跑一次，命中列表按规则分组。
/// </summary>
public partial class SigmaWindow : Window
{
    private readonly Action<ReportPayload> _showReport;

    private SigmaScanResult _result = new();
    private bool _busy;

    /// <summary>自检读取：加载的规则数。</summary>
    public int RuleCount => _result.Rules.Count;

    /// <summary>自检读取：命中数。</summary>
    public int HitTotal => _result.Hits.Count;

    /// <summary>自检读取：表格显示行数。</summary>
    public int HitShown => HitGrid.ShownCount;

    /// <summary>自检读取：扫描的事件数。</summary>
    public int EventCount => _result.EventsScanned;

    public SigmaScanResult Snapshot => _result;

    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    public SigmaWindow() : this(_ => { })
    {
    }

    public SigmaWindow(Action<ReportPayload> showReport)
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
        _result = SigmaRuleEngine.Scan(null);
        Apply();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        RescanBtn.IsEnabled = false;
        ReportBtn.IsEnabled = false;
        ScanHint.Text = "开始匹配…";
        VerdictText.Text = "正在读取事件日志…";

        var progress = new Progress<string>(s => ScanHint.Text = s);

        try
        {
            _result = await Task.Run(() => SigmaRuleEngine.Scan(progress));
            Apply();

            ScanHint.Text = $"匹配完成：{_result.Hits.Count} 条命中 · 耗时 {_result.ElapsedText}";
        }
        catch (Exception ex)
        {
            ScanHint.Text = "匹配失败：" + ex.Message;
            VerdictText.Text = "匹配失败";
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
        HitGrid.SetRows(_result.Hits.Select(h => new LogRow
        {
            Title = h.Rule.Title,
            Detail = h.Why,
            Basis = h.Rule.Description,
            Tag = h.Tag,
            Level = h.IsKnownGood ? "known"
                : h.Rule.Level is "critical" or "high" ? "warn"
                : h.Rule.Level == "medium" ? "hint" : "ok",
            LevelText = h.IsKnownGood ? h.Tag : h.Rule.LevelText,
            Time = h.Event?.Time,
            TimeText = h.Event?.TimeText ?? "",
            Payload = h
        }), new LogViewOptions { ShowTimeline = true });

        HitCount.Text = _result.Hits.Count.ToString();
        HighCount.Text = _result.HighCount.ToString();
        MediumCount.Text = _result.MediumCount.ToString();
        ScanText.Text = _result.EventsScanned.ToString("N0");
        RuleBadge.Text = SigmaRules.ExternalCount > 0
            ? $"Sigma 子集 · {_result.Rules.Count} 条（外部 {SigmaRules.ExternalCount}）"
            : $"Sigma 子集 · {_result.Rules.Count} 条规则";
        VerdictText.Text = _result.Verdict;

        ChannelList.ItemsSource = _result.Channels;
        ElapsedText.Text = _result.ElapsedText;
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            var payload = SigmaRuleEngine.BuildReport(_result);
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
        if (ElevationService.RelaunchAsAdmin("--open-sigma", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            ScanHint.Text = error;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
