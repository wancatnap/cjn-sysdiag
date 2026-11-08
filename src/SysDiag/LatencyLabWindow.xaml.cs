using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

/// <summary>
/// 延迟实测 / 对照实验窗口。
///
/// 只做测量、记录、对照，不改任何系统设置。这样「改一项 → 测一轮」的因果链才干净：
/// 测量工具本身不会成为变量。
/// </summary>
public partial class LatencyLabWindow : Window
{
    /// <summary>历史行。必须是带真实属性的类，别用元组（DisplayMemberBinding 反射不到元组字段名）。</summary>
    private sealed class HistoryRow
    {
        public LatencySample Sample { get; init; } = new();
        public string TimeText => Sample.TakenAt.ToString("MM-dd HH:mm:ss");
        public string Label => Sample.Label;
        public string RunText => Sample.RunText;
        public string P99Text => Sample.P99Ms.ToString("0.00");
        public string MaxText => Sample.MaxMs.ToString("0.00");
        public string TimerText => Sample.TimerText;
    }

    private sealed class DurationOption
    {
        public int Seconds { get; init; }
        public string Text { get; init; } = "";
    }

    private sealed class RunOption
    {
        public int Count { get; init; }
        public string Text { get; init; } = "";
    }

    private static readonly DurationOption[] Durations =
    {
        new() { Seconds = 5,  Text = "5 秒" },
        new() { Seconds = 10, Text = "10 秒" },
        new() { Seconds = 20, Text = "20 秒" },
        new() { Seconds = 30, Text = "30 秒" }
    };

    /// <summary>
    /// 轮数选项。默认 1 轮（最快），但要下结论至少 3 轮——
    /// 只有连跑多轮才能看出来同条件下的噪声有多大，单轮连噪声都不知道。
    /// </summary>
    private static readonly RunOption[] RunOptions =
    {
        new() { Count = 1, Text = "1 轮" },
        new() { Count = 3, Text = "3 轮" },
        new() { Count = 5, Text = "5 轮" }
    };

    private readonly List<LatencySample> _samples;
    private CancellationTokenSource _cts;
    private bool _measuring;
    private LatencySample _current;
    private LatencySample _baseline;

    public LatencyLabWindow()
    {
        InitializeComponent();

        MaxHeight = SystemParameters.WorkArea.Height - 24;

        DurationCombo.DisplayMemberPath = "Text";
        DurationCombo.SelectedValuePath = "Seconds";
        DurationCombo.ItemsSource = Durations;
        DurationCombo.SelectedIndex = 1;               // 默认 10 秒

        RunsCombo.DisplayMemberPath = "Text";
        RunsCombo.SelectedValuePath = "Count";
        RunsCombo.ItemsSource = RunOptions;
        RunsCombo.SelectedIndex = 1;                   // 默认 3 轮

        _samples = LatencyProbeService.Load();
        RefreshHistory();
    }

    /// <summary>自检用：历史记录条数。</summary>
    public int HistoryCount => _samples.Count;

    /// <summary>自检用：时长下拉的项数，用来验证数据源确实可绑定。</summary>
    public int DurationCount => DurationCombo.Items.Count;

    /// <summary>自检用：轮数下拉的项数。</summary>
    public int RunCount => RunsCombo.Items.Count;

    // ---------------------------------------------------------------- 采样

    private async void OnMeasure(object sender, RoutedEventArgs e)
    {
        if (_measuring) return;

        var seconds = DurationCombo.SelectedValue is int s ? s : 10;
        var runs = RunsCombo.SelectedValue is int n ? n : 1;
        var label = LabelBox.Text.Trim();
        if (label.Length == 0)
            label = "未命名实验";

        var changed = ParseChangedItems(label);
        var batchId = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        _cts = new CancellationTokenSource();
        SetMeasuring(true);

        var progress = new Progress<string>(text => StatusText.Text = text);

        try
        {
            var made = new List<LatencySample>();

            for (var i = 1; i <= runs; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                // 轮间留一段空档：让上一轮的调度余波散掉，否则第二轮的起始样本会被污染。
                if (i > 1)
                {
                    StatusText.Text = $"第 {i}/{runs} 轮将在 2 秒后开始…";
                    await System.Threading.Tasks.Task.Delay(2000, _cts.Token);
                }

                var index = i;
                var runProgress = new Progress<string>(text =>
                    StatusText.Text = runs > 1 ? $"第 {index}/{runs} 轮 · {text}" : text);

                var sample = await System.Threading.Tasks.Task.Run(
                    () => LatencyProbeService.Measure(seconds, label, changed, runProgress,
                        _cts.Token, index, runs, batchId),
                    _cts.Token);

                made.Add(sample);
                _samples.Add(sample);
                LatencyProbeService.Save(_samples);

                // 每轮结束就刷新一次，多轮时用户能看到进度而不是干等。
                _current = sample;
                ShowSample(sample);
                RefreshHistory();
            }

            // 第一轮自动设为基线；这是多轮默认行为，不需要用户再点一次。
            _baseline = made[0];
            BaselineText.Text = runs > 1
                ? $"基线：第 1/{runs} 轮（自动）"
                : $"基线：{_baseline.Label}（{_baseline.TakenAt:MM-dd HH:mm}）";
            CompareBtn.IsEnabled = true;

            var summary = LatencyProbeService.SummarizeBatch(made);

            var sb = new StringBuilder();
            sb.AppendLine($"结论：{summary.Text.Split('\n')[0]}");
            sb.AppendLine();
            sb.AppendLine(summary.Text);
            sb.AppendLine();
            sb.AppendLine("逐轮 P99（相对第 1 轮）：");

            foreach (var (run, delta) in LatencyProbeService.BatchDeltas(summary))
            {
                sb.AppendLine($"  {run.RunText.PadRight(12)} P99 {run.P99Ms:0.00} ms　" +
                              (run.RunIndex == 1 ? "（基线）" : $"{delta:+0.00;-0.00;0.00} ms"));
            }

            sb.AppendLine();
            sb.AppendLine("多轮之间没改任何设置，所以轮间差异只反映测量噪声。");
            sb.AppendLine("要判断某项优化有没有效：改一项后按同样轮数再跑一批，比较两批的 P99 中位数。");

            CompareText.Text = sb.ToString().TrimEnd();

            StatusText.Text = $"完成 {made.Count} 轮，已保存。" +
                              (changed.Count > 1 ? "　⚠ 声明改了多项，不是单变量实验。" : "");

            // 本机实测出现过：计时器分辨率读到 0.6 ms，但 Sleep(1) 实际平均 15.6 ms。
            // 只看「分辨率」会以为延迟很低，这正是必须实测的理由，显式讲给用户。
            var last = made[^1];
            if (last.TimerResolutionMs.HasValue &&
                last.TimerResolutionMs.Value > 0 &&
                last.MeanMs > last.TimerResolutionMs.Value * 3)
            {
                StatusText.Text += $"　⚠ 计时器分辨率 {last.TimerResolutionMs.Value:0.000} ms，" +
                                   $"但实际平均唤醒 {last.MeanMs:0.00} ms——" +
                                   "说明主导延迟的是时钟滴答/调度/电源策略，不是分辨率数值。请以实测为准。";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已停止。已完成的轮次都已保存。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "采样失败：" + ex.Message;
        }
        finally
        {
            SetMeasuring(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 采集内核 DPC/ISR。独立于用户态测量：它不改任何设置，也不参与「改一项测一轮」的对照，
    /// 只是把「内核里 DPC 到底占了多久」这个用户态看不到的事实补上。
    /// </summary>
    private async void OnCaptureKernel(object sender, RoutedEventArgs e)
    {
        if (_measuring) return;

        if (!KernelTraceService.IsElevated)
        {
            UiDialog.Show(this,
                "内核采集需要管理员权限。\n\n" +
                "ETW 的内核记录器会话是特权会话，普通权限无法开启——这不是本软件的限制。\n" +
                "请用管理员身份重新启动本软件后再点这个按钮。用户态测量不受影响，随时可用。",
                "内核 DPC/ISR", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var seconds = DurationCombo.SelectedValue is int s ? s : 10;

        // 先查占用：内核记录器全局唯一，被别的程序占着时开不了。
        // 把「接管」做成用户明确确认的动作——它会打断对方正在进行的录制。
        var takeover = false;
        var (state, stateNote) = KernelTraceService.QueryKernelSession();

        if (state == KernelTraceService.SessionState.Running)
        {
            var answer = UiDialog.Show(this,
                "内核记录器（NT Kernel Logger）正被其他程序占用。\n\n" +
                "它是全局唯一资源，同一时刻只能有一个程序使用。常见占用者：" +
                "Windows Performance Recorder、xperf、LatencyMon，以及部分带监控/反作弊组件的软件。\n\n" +
                "是否接管？接管会中断对方正在进行的录制或监控。\n" +
                "（选「否」也可以改用上面的用户态测量，它不需要这项资源。）",
                "内核 DPC/ISR", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes) return;
            takeover = true;
        }
        else if (state == KernelTraceService.SessionState.Unknown && stateNote.Length > 0)
        {
            StatusText.Text = "内核会话查询未成功：" + stateNote;
        }

        _cts = new CancellationTokenSource();
        SetMeasuring(true);
        KernelText.Text = "采集中…";

        var progress = new Progress<string>(text => StatusText.Text = text);

        try
        {
            var result = await System.Threading.Tasks.Task.Run(
                () => KernelTraceService.Capture(seconds, _cts.Token, progress, takeover), _cts.Token);

            if (!result.Ok)
            {
                KernelText.Text = "未采集";
                KernelNote = result.Message;
                StatusText.Text = "内核采集失败：" + result.Message;
                return;
            }

            KernelText.Text = $"P99 {result.P99Us:0.0} µs";
            KernelNote = result.Message;
            ShowKernelDetail(result);

            // 把内核数据挂到最近一轮上，这样导出报告时能一起带走。
            if (_current != null)
            {
                _current.KernelDpcCount = result.DpcCount;
                _current.KernelDpcP99Us = result.P99Us;
                _current.KernelDpcMaxUs = result.MaxUs;
                _current.KernelIsrCount = result.IsrCount;
                _current.KernelNote = result.Message;
                LatencyProbeService.Save(_samples);
                RefreshHistory();
            }

            StatusText.Text = result.Message + "　" +
                              $"平均 {result.MeanUs:0.0} µs　P99 {result.P99Us:0.0} µs　最大 {result.MaxUs:0.0} µs" +
                              (result.MaxUs > 1000 ? "　⚠ 有单次 DPC 超过 1 ms，值得查是哪个驱动。" : "");
        }
        catch (OperationCanceledException)
        {
            KernelText.Text = "未采集";
            StatusText.Text = "已停止内核采集。";
        }
        catch (Exception ex)
        {
            KernelText.Text = "未采集";
            StatusText.Text = "内核采集失败：" + ex.Message;
        }
        finally
        {
            SetMeasuring(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>上一次内核采集的说明文字，供详情区显示。</summary>
    private string KernelNote = "";

    private void ShowKernelDetail(KernelTraceService.KernelResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("内核 DPC/ISR（ETW）");
        sb.AppendLine();
        sb.AppendLine(r.Message);
        sb.AppendLine($"平均 {r.MeanUs:0.0} µs　P99 {r.P99Us:0.0} µs　最大 {r.MaxUs:0.0} µs");
        sb.AppendLine();
        sb.AppendLine("这是内核 DPC 的单次执行时长，单位微秒。");
        sb.AppendLine("Microsoft 的驱动建议值是单次 DPC 不超过 100 µs；超过 1 ms 的个别事件通常来自某个驱动。");
        sb.AppendLine();
        sb.AppendLine("边界：本版本不做模块归因（不给「是哪个 .sys」的结论），ISR 只计数不算时长。");
        sb.AppendLine("要归因到具体驱动，需要用 Windows Performance Analyzer 打开 ETW 跟踪并看 DPC Duration by Module。");
        sb.AppendLine("内核采集需要管理员，且与 WPR / LatencyMon 互斥（同一台机器只有一个 NT Kernel Logger）。");

        CompareText.Text = sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 从实验名称里提炼「这轮改了什么」。约定用「改了 X」「只改 X」「关闭 X」这类写法，
    /// 也支持逗号分隔列多项——列出多项时对照结论会明确提示不符合单变量要求。
    /// </summary>
    private static List<string> ParseChangedItems(string label)
    {
        var text = label;
        foreach (var prefix in new[] { "只改了", "只改", "改了", "关闭了", "关闭", "开启了", "开启", "改" })
        {
            if (text.StartsWith(prefix, StringComparison.Ordinal))
            {
                text = text[prefix.Length..];
                break;
            }
        }

        return text.Split(new[] { '，', ',', '、', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Take(8)
            .ToList();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在停止…";
    }

    private void SetMeasuring(bool on)
    {
        _measuring = on;
        MeasureBtn.IsEnabled = !on;
        DurationCombo.IsEnabled = !on;
        RunsCombo.IsEnabled = !on;
        LabelBox.IsEnabled = !on;
        CancelBtn.IsEnabled = on;
        KernelBtn.IsEnabled = !on;
        BaselineBtn.IsEnabled = !on && (HistoryList.SelectedItem is HistoryRow || _current != null);
        CompareBtn.IsEnabled = !on && _baseline != null && _current != null;
    }

    // ---------------------------------------------------------------- 显示

    private void ShowSample(LatencySample s)
    {
        ResultTitle.Text = $"{s.Label}　·　{s.DurationText}" +
                           (s.RunText.Length > 0 ? "　·　" + s.RunText : "");

        MeanText.Text = s.MeanMs.ToString("0.00") + " ms";
        P95Text.Text = s.P95Ms.ToString("0.00") + " ms";
        P99Text.Text = s.P99Ms.ToString("0.00") + " ms";
        MaxText.Text = s.MaxMs.ToString("0.00") + " ms";

        TimerText.Text = s.TimerText;
        DpcText.Text = (s.DpcPercent.HasValue ? s.DpcPercent.Value.ToString("0.0") : "—") + " / " +
                       (s.InterruptPercent.HasValue ? s.InterruptPercent.Value.ToString("0.0") : "—") + " %";

        LoadText.Text = s.ProcessCount.HasValue
            ? $"{s.ProcessCount} / {s.ThreadCount?.ToString("N0") ?? "—"} / {s.HandleCount?.ToString("N0") ?? "—"}"
            : "读不到";

        KernelText.Text = s.HasKernelData
            ? $"P99 {s.KernelDpcP99Us:0.0} µs"
            : "未采集";
    }

    private void RefreshHistory()
    {
        var rows = _samples.OrderByDescending(s => s.TakenAt)
            .Select(s => new HistoryRow { Sample = s })
            .ToList();

        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = rows;
    }

    // ---------------------------------------------------------------- 对照

    private void OnHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_measuring) return;
        BaselineBtn.IsEnabled = HistoryList.SelectedItem is HistoryRow || _current != null;
    }

    private void OnSetBaseline(object sender, RoutedEventArgs e)
    {
        _baseline = HistoryList.SelectedItem is HistoryRow row ? row.Sample : _current;
        if (_baseline == null) return;

        BaselineText.Text = $"基线：{_baseline.Label}（{_baseline.TakenAt:MM-dd HH:mm}）";
        CompareBtn.IsEnabled = _current != null;
        StatusText.Text = "基线已设置。改一项系统设置后，在相同负载下再测一轮，然后点「与本轮对照」。";
    }

    private void OnCompare(object sender, RoutedEventArgs e)
    {
        if (_baseline == null || _current == null) return;

        var c = LatencyProbeService.Compare(_baseline, _current);

        var sb = new StringBuilder();
        sb.AppendLine($"结论：{c.VerdictText}");
        sb.AppendLine();
        sb.AppendLine($"基线　{_baseline.Label}：P99 {_baseline.P99Ms:0.00} ms　平均 {_baseline.MeanMs:0.00} ms　最大 {_baseline.MaxMs:0.00} ms");
        sb.AppendLine($"本轮　{_current.Label}：P99 {_current.P99Ms:0.00} ms　平均 {_current.MeanMs:0.00} ms　最大 {_current.MaxMs:0.00} ms");
        sb.AppendLine();
        sb.AppendLine($"差值：P99 {c.P99DeltaMs:+0.00;-0.00;0.00} ms　平均 {c.MeanDeltaMs:+0.00;-0.00;0.00} ms　最大 {c.MaxDeltaMs:+0.00;-0.00;0.00} ms");
        sb.AppendLine();
        sb.AppendLine("判定依据与限制：");
        sb.AppendLine(c.Reason);

        CompareText.Text = sb.ToString();
        StatusText.Text = $"对照完成：{c.VerdictText}。请连同下方的判定依据一起看，不要只取结论。";
    }

    // ---------------------------------------------------------------- 记录

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_samples.Count == 0)
        {
            UiDialog.Show(this, "还没有实验记录。", "延迟实测",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出延迟实验记录",
            Filter = "Markdown (*.md)|*.md",
            FileName = "latency-experiments.md",
            InitialDirectory = AppPaths.OutputDir
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, LatencyProbeService.ExportMarkdown(_samples),
                new UTF8Encoding(false));
            StatusText.Text = "已导出：" + dialog.FileName;
        }
        catch (Exception ex)
        {
            UiDialog.Show(this, "导出失败：\n\n" + ex.Message, "延迟实测",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryRow row) return;

        var confirm = UiDialog.Show(this,
            $"删除这条实验记录？\n\n{row.Sample.Label}　{row.Sample.TakenAt:yyyy-MM-dd HH:mm:ss}",
            "延迟实测", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        _samples.Remove(row.Sample);
        LatencyProbeService.Save(_samples);

        if (ReferenceEquals(_baseline, row.Sample))
        {
            _baseline = null;
            BaselineText.Text = "未设基线";
            CompareBtn.IsEnabled = false;
        }

        RefreshHistory();
        StatusText.Text = "已删除该记录。";
    }
}
