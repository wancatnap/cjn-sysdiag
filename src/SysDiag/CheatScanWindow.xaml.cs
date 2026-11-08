using System.Linq;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SysDiag.Controls;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 外挂痕迹体检：一次跑完 8 个只读采集项，给统一结论与一份 HTML 报告。
/// 打开即自动体检，结果固定可复核（一次性快照，不做后台监控）。
/// </summary>
public partial class CheatScanWindow : Window
{
    private readonly Action<ReportPayload> _showReport;

    private CheatScanResult _result = new();
    private CheatChecklist _checklist = new();
    private bool _busy;

    /// <summary>自检读取：发现条目数。</summary>
    public int FindingCount => _result.Findings.Count;

    /// <summary>自检读取：采集项数量。</summary>
    public int StageCount => _result.Stages.Count;

    public int Score => _result.Score;

    public int GroupCount => _result.GroupCount;

    /// <summary>自检读取：检查清单项数 / 未检查项数。</summary>
    public int CheckCount => _checklist.Items.Count;

    public int CheckSkipped => _checklist.SkipCount;

    public CheatChecklist Checklist => _checklist;

    public CheatScanResult Snapshot => _result;

    public bool ElevateButtonVisible => ElevateBtn.Visibility == Visibility.Visible;

    public CheatScanWindow() : this(_ => { })
    {
    }

    public CheatScanWindow(Action<ReportPayload> showReport)
    {
        InitializeComponent();

        _showReport = showReport ?? (_ => { });

        var admin = ElevationService.IsAdmin;
        ElevateBtn.Visibility = admin ? Visibility.Collapsed : Visibility.Visible;

        SwitchView(CheatView.Findings);

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await ScanAsync();
    }

    /// <summary>同步体检，供无头自检调用。</summary>
    public void ScanSync()
    {
        _result = CheatScanService.Scan(null, OperatorBox.Text);
        Apply();
    }

    private async Task ScanAsync()
    {
        if (_busy) return;

        _busy = true;
        RescanBtn.IsEnabled = false;
        ReportBtn.IsEnabled = false;
        PackBtn.IsEnabled = false;
        ScanHint.Text = "体检中";
        VerdictText.Text = "正在体检…";

        var progress = new Progress<string>(s => ScanHint.Text = s);

        try
        {
            var who = OperatorBox.Text;
            _result = await Task.Run(() => CheatScanService.Scan(progress, who));
            Apply();

            ScanHint.Text = $"完成 · {_result.Findings.Count} 项";
        }
        catch (Exception ex)
        {
            ScanHint.Text = "失败：" + ex.Message;
            VerdictText.Text = "体检失败";
            DetailText.Text = ex.Message;
        }
        finally
        {
            _busy = false;
            RescanBtn.IsEnabled = true;
            ReportBtn.IsEnabled = true;
            PackBtn.IsEnabled = true;
        }
    }

    private void Apply()
    {
        FindingsGrid.SetRows(_result.Findings.Select(f => new LogRow
        {
            Title = f.Title,
            Detail = f.Detail,
            Basis = f.Basis,
            Level = f.Level,
            LevelText = f.LevelText,
            Payload = f
        }), new SysDiag.Controls.LogViewOptions { ShowTimeline = false, DefaultSort = "level" });

        ScoreText.Text = _result.Score.ToString();
        ScoreLevelText.Text = $"风险分 · {_result.ScoreLevel}";
        ScoreText.Foreground = Palette.Get(_result.ScoreColor);
        ScoreLevelText.Foreground = Palette.Get(_result.ScoreColor);
        ScoreRing.BorderBrush = Palette.Get(_result.ScoreColor);
        ScoreRing.Background = Palette.Get(_result.ScoreBg);

        VerdictText.Text = _result.Verdict;
        DetailText.Text = _result.VerdictDetail;

        WarnCount.Text = _result.WarnCount.ToString();
        HintCount.Text = _result.HintCount.ToString();
        OkCount.Text = _result.OkCount.ToString();

        StageList.ItemsSource = _result.Stages;
        ElapsedText.Text = _result.ElapsedText;

        _checklist = CheatChecklistBuilder.Build(_result);
        ViewChecksBtn.Content = $"检查清单（{_checklist.Items.Count}）";

        CheckGrid.SetRows(_checklist.Items.Select(c => new LogRow
        {
            Title = $"{c.Layer} · {c.Name}",
            Detail = c.Evidence,
            Basis = c.Why,
            Level = c.Level,
            LevelText = c.ResultText,
            Payload = c
        }), new SysDiag.Controls.LogViewOptions { ShowTimeline = false, DefaultSort = "level" });

        TimelineGrid.SetRows(_result.Timeline.Select(e => new LogRow
        {
            Title = e.Text,
            Tag = e.Source,
            Level = e.Level,
            LevelText = e.Level switch { "warn" => "线索", "hint" => "提示", _ => "" },
            Time = e.Time,
            TimeText = e.TimeText,
            Payload = e
        }), new SysDiag.Controls.LogViewOptions { ShowTimeline = true, DefaultSort = "time-desc" });
        ViewTimelineBtn.Content = $"时间线（{_result.Timeline.Count}）";
    }

    private void OnShowFindings(object sender, RoutedEventArgs e) => SwitchView(CheatView.Findings);

    private void OnShowTimeline(object sender, RoutedEventArgs e) => SwitchView(CheatView.Timeline);

    private void OnShowChecks(object sender, RoutedEventArgs e) => SwitchView(CheatView.Checks);

    /// <summary>发现 / 时间线两个视图互斥显示，用按钮背景区分当前状态。</summary>
    private enum CheatView { Findings, Timeline, Checks }

    private void SwitchView(CheatView view)
    {
        FindingsGrid.Visibility = view == CheatView.Findings ? Visibility.Visible : Visibility.Collapsed;
        TimelineGrid.Visibility = view == CheatView.Timeline ? Visibility.Visible : Visibility.Collapsed;
        CheckGrid.Visibility = view == CheatView.Checks ? Visibility.Visible : Visibility.Collapsed;

        void Paint(Button b, bool active)
        {
            b.Background = active ? Palette.PrimarySoft : Palette.Surface;
            b.Foreground = active ? Palette.Primary : Palette.InkTertiary;
        }

        Paint(ViewFindingsBtn, view == CheatView.Findings);
        Paint(ViewTimelineBtn, view == CheatView.Timeline);
        Paint(ViewChecksBtn, view == CheatView.Checks);
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await ScanAsync();

    private void OnReport(object sender, RoutedEventArgs e)
    {
        try
        {
            // 让用户自己选存哪儿：报告是拿去转发/归档的，位置应该由他决定。
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存体检报告",
                Filter = "网页报告 (*.html)|*.html",
                DefaultExt = ".html",
                AddExtension = true,
                FileName = $"{_result.FinishedAt:yyyyMMdd-HHmmss}-外挂痕迹体检.html",
                InitialDirectory = AppPaths.OutputDir
            };

            if (dialog.ShowDialog(this) != true) return;

            var target = dialog.FileName;
            var payload = CheatScanService.BuildReport(_result, target);

            // 图片跟 HTML 同名同目录：选一次目录，两个文件都落在那里，列表里还挨着。
            var imagePath = System.IO.Path.ChangeExtension(target, ".png");
            var image = ReportImageExporter.Export(_result, imagePath);

            // 只有存到默认 outputs 时才整理。用户选到别处（比如 U 盘、桌面）不能碰他的目录。
            var pruned = (0, 0L);
            if (string.Equals(System.IO.Path.GetDirectoryName(target), AppPaths.OutputDir,
                    StringComparison.OrdinalIgnoreCase))
                pruned = OutputHousekeeping.Prune();

            _showReport(payload);

            ScanHint.Text = image.Ok ? "报告 + 图已保存" : "报告已保存（图失败）";
            if (pruned.Item1 > 0) ScanHint.Text += $"　清掉旧报告 {pruned.Item1} 个";
            ScanHint.ToolTip = image.Ok
                ? $"{System.IO.Path.GetFileName(payload.HtmlPath)}\n{System.IO.Path.GetFileName(image.Path)}"
                : image.Message;
        }
        catch (Exception ex)
        {
            ScanHint.Text = "生成失败：" + ex.Message;
        }
    }

    private void OnPack(object sender, RoutedEventArgs e)
    {
        var confirm = UiDialog.Show(this,
            "将把机器码、驱动记录、USB 清单、PnP 事件、USN 记录等原始数据打包成一个 zip（带 SHA256 校验），\n" +
            "方便发给别人复核。打包过程只读取，不改任何设置。\n\n继续？",
            "导出证据包", MessageBoxButton.OKCancel, MessageBoxImage.Information);

        if (confirm != MessageBoxResult.OK) return;

        try
        {
            ScanHint.Text = "打包中";
            var pack = EvidenceService.BuildPack();

            ScanHint.Text = $"证据包 {LogFileItem.FormatSize(pack.ZipSize)}";

            var open = UiDialog.Show(this,
                $"证据包已生成：\n{pack.ZipPath}\n\n共 {pack.Files.Count} 个文件，{LogFileItem.FormatSize(pack.ZipSize)}。\n\n要打开所在目录吗？",
                "导出证据包", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (open == MessageBoxResult.Yes)
                LauncherService.RevealInExplorer(Path.GetDirectoryName(pack.ZipPath) ?? AppPaths.OutputDir);
        }
        catch (Exception ex)
        {
            ScanHint.Text = "打包失败：" + ex.Message;
        }
    }

    private void OnElevate(object sender, RoutedEventArgs e)
    {
        if (ElevationService.RelaunchAsAdmin("--open-cheatscan", out var error))
        {
            Close();
            return;
        }

        if (!string.IsNullOrEmpty(error) && error != ElevationService.CancelledMessage)
            ScanHint.Text = error;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
