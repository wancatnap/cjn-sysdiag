using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 延迟实测：把「改完到底有没有降」变成可测量、可对照的数据。
///
/// 设计来源（只借思路，不复制代码）：
/// - PC-Optimization-Hub 的方法论：不要一次盲改一堆设置，要记录每次改动、做系统化对照。
/// - LatencyMon 的用途：采集 DPC/ISR 相关延迟做实验验证。
/// - denis-g 指南：计时器分辨率要实测确认，而不是改完就当它生效了。
/// - AtlasOS 的 Check Health / WinUtil 的逐项日志：记录「这轮实验时哪些设置是开着的」。
///
/// **测什么、不测什么**（界面必须原样讲清楚，不许含糊）：
/// 测的是用户态线程周期性 Sleep(1) 后的**实际唤醒间隔分布**。系统计时器分辨率、
/// DPC/中断抢占、后台负载都会反映到这个分布上，因此它能当作「输入/调度延迟」的
/// 一个可复现代理指标。
/// 不测：内核 ISR/DPC 单次执行时长、驱动级中断延迟、端到端输入延迟。
/// 那些需要内核驱动或 ETW 跟踪，普通权限拿不到，本程序不假装能拿。
///
/// 为什么用 Sleep(1)：它同时受计时器分辨率和调度抢占影响，所有 Windows 上都能跑，
/// 不需要管理员、不写注册表、不改系统状态，因此适合在改设置前后反复做对照。
/// </summary>
public static class LatencyProbeService
{
    private const string StoreName = "latency-experiments.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string StoreFile => Path.Combine(AppPaths.OutputDir, StoreName);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    // ---------------------------------------------------------------- 采样

    /// <summary>
    /// 跑一轮采样。必须在后台线程调用；UI 线程只负责显示进度。
    /// 测量期间刻意不做别的重活（不查 WMI、不读注册表），避免把自身开销算进抖动里。
    ///
    /// runIndex / runTotal：同一条件下连跑多轮时的轮次，只用于标注与组内比较，不影响算法。
    /// </summary>
    public static LatencySample Measure(int seconds, string label, List<string> changed,
        IProgress<string> progress, CancellationToken token,
        int runIndex = 1, int runTotal = 1, string batchId = "")
    {
        var gaps = new List<double>(seconds * 1000);
        var sw = Stopwatch.StartNew();
        var budget = TimeSpan.FromSeconds(Math.Clamp(seconds, 3, 60));

        // 用独立的高精度循环，尽量让测量线程自己不被 UI 影响。
        var prev = Stopwatch.GetTimestamp();
        long freq = Stopwatch.Frequency;
        var lastReport = 0;

        while (sw.Elapsed < budget)
        {
            token.ThrowIfCancellationRequested();

            Thread.Sleep(1);
            var now = Stopwatch.GetTimestamp();
            var gap = (now - prev) * 1000.0 / freq;
            prev = now;

            // 丢掉首轮预热，以及明显不合理的时钟异常值。
            if (gap > 0 && gap < 1000) gaps.Add(gap);

            var pct = (int)(sw.Elapsed.TotalSeconds / budget.TotalSeconds * 100);
            if (pct >= lastReport + 10)
            {
                lastReport = pct;
                progress?.Report($"采样中… {pct}%　（{gaps.Count} 个样本）");
            }
        }

        sw.Stop();
        gaps.Sort();

        var sample = new LatencySample
        {
            Id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4],
            TakenAt = DateTime.Now,
            Label = string.IsNullOrWhiteSpace(label) ? "未命名实验" : label.Trim(),
            BatchId = batchId ?? "",
            RunIndex = runIndex,
            RunTotal = runTotal,
            DurationSeconds = sw.Elapsed.TotalSeconds,
            SampleCount = gaps.Count,
            MeanMs = gaps.Count > 0 ? gaps.Average() : 0,
            P50Ms = Percentile(gaps, 0.50),
            P95Ms = Percentile(gaps, 0.95),
            P99Ms = Percentile(gaps, 0.99),
            MaxMs = gaps.Count > 0 ? gaps[^1] : 0,
            SpikeCount = gaps.Count > 0 ? gaps.Count(g => g > Percentile(gaps, 0.50) * 2) : 0,
            ChangedItems = changed ?? new List<string>(),
            ActiveTweakIds = SnapshotActiveTweaks()
        };

        // 采样结束后才读这些附加指标：它们各有几十毫秒开销，不能算进抖动里。
        return AttachAmbient(sample, progress);
    }

    /// <summary>补上计时器分辨率与 DPC/中断占比；读不到就留 null，不猜。</summary>
    private static LatencySample AttachAmbient(LatencySample s, IProgress<string> progress)
    {
        progress?.Report("读取计时器分辨率与处理器占用…");

        try
        {
            if (NtQueryTimerResolution(out _, out _, out var current) == 0 && current > 0)
                s.TimerResolutionMs = current / 10000.0;
        }
        catch { /* 保留 null */ }

        try
        {
            // Win32_PerfFormattedData_PerfOS_Processor 的 _Total 实例：
            // PercentDPCTime / PercentInterruptTime 是内核 DPC 与中断占 CPU 的百分比。
            // 这是「占比」不是「单次执行时长」，两者不能混淆。
            foreach (var row in EvidenceService.Wmi(
                         "SELECT Name, PercentDPCTime, PercentInterruptTime " +
                         "FROM Win32_PerfFormattedData_PerfOS_Processor WHERE Name = '_Total'"))
            {
                if (row.TryGetValue("PercentDPCTime", out var d) && d != null &&
                    double.TryParse(d.ToString(), out var dp))
                    s.DpcPercent = dp;

                if (row.TryGetValue("PercentInterruptTime", out var i) && i != null &&
                    double.TryParse(i.ToString(), out var ip))
                    s.InterruptPercent = ip;
            }
        }
        catch { /* 保留 null */ }

        AttachLoad(s);

        return s;
    }

    /// <summary>
    /// 记录采样结束那一刻的进程/线程/句柄总量。
    ///
    /// 用途很具体：验证「停掉几个服务能让系统更轻」这类说法。停之前存一次、停之后存一次，
    /// 差值就是实际回收量——如果句柄一个没少，那所谓「瘦身」只是心理作用。
    /// 遍历进程要几百毫秒，所以放在采样窗口之外做。
    /// </summary>
    private static void AttachLoad(LatencySample s)
    {
        try
        {
            var processes = Process.GetProcesses();
            s.ProcessCount = processes.Length;

            long threads = 0;
            long handles = 0;

            foreach (var p in processes)
            {
                try
                {
                    threads += p.Threads.Count;
                    handles += p.HandleCount;
                }
                catch
                {
                    // 受保护进程（系统进程、反作弊进程）会拒绝读取，跳过即可，
                    // 不因为个别进程失败就整项作废。
                }
                finally
                {
                    p.Dispose();
                }
            }

            s.ThreadCount = (int)threads;
            s.HandleCount = (int)handles;
        }
        catch
        {
            // 全盘失败就留 null，界面显示「读不到」，不填假数字。
        }

        try
        {
            foreach (var row in EvidenceService.Wmi(
                         "SELECT CommittedBytes FROM Win32_PerfFormattedData_PerfOS_Memory"))
            {
                if (row.TryGetValue("CommittedBytes", out var v) && v != null &&
                    double.TryParse(v.ToString(), out var bytes) && bytes > 0)
                    s.CommitUsedMB = bytes / 1024.0 / 1024.0;
            }
        }
        catch { /* 保留 null */ }
    }

    /// <summary>已经排好序的列表上取分位数，线性插值。</summary>
    public static double Percentile(List<double> sorted, double q)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];

        var pos = (sorted.Count - 1) * Math.Clamp(q, 0, 1);
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sorted[lo];

        return sorted[lo] + (sorted[hi] - sorted[lo]) * (pos - lo);
    }

    /// <summary>记录这轮实验时哪些优化项处于「已生效」，用于两次实验之间的差异归因。</summary>
    private static List<string> SnapshotActiveTweaks()
    {
        try
        {
            return TweakService.ReadAll(TweakCatalog.All)
                .Where(r => r.IsOn)
                .Select(r => r.Item.Id)
                .OrderBy(x => x)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    // ---------------------------------------------------------------- 对照

    /// <summary>
    /// 对照判定刻意保守：抖动本身波动大，只有差值超过「基线 P99 的 15% 或 0.5ms」
    /// 才认为出现了值得注意的变化；即使如此也只说「复测更低」，不说「优化有效」。
    /// </summary>
    public static LatencyComparison Compare(LatencySample baseline, LatencySample current)
    {
        var a = baseline ?? throw new ArgumentNullException(nameof(baseline));
        var b = current ?? throw new ArgumentNullException(nameof(current));

        // .NET 8 的 LINQ 没有 SymmetricExcept，手写「只在其中一边出现」的项。
        var onlyInA = a.ActiveTweakIds.Except(b.ActiveTweakIds);
        var onlyInB = b.ActiveTweakIds.Except(a.ActiveTweakIds);
        var diff = onlyInA.Concat(onlyInB).Distinct().OrderBy(x => x).ToList();

        var p99Delta = b.P99Ms - a.P99Ms;
        var meanDelta = b.MeanMs - a.MeanMs;
        var maxDelta = b.MaxMs - a.MaxMs;

        var insufficient = a.SampleCount < 30 || b.SampleCount < 30;
        var threshold = Math.Max(0.5, Math.Abs(a.P99Ms) * 0.15);

        var verdict = insufficient ? "insufficient"
            : p99Delta < -threshold ? "improve"
            : p99Delta > threshold ? "worse"
            : "flat";

        var notes = new List<string>
        {
            $"判定阈值：P99 变化需超过 {threshold:0.00} ms（基线 P99 的 15% 与 0.5 ms 取较大者）才计入，用来滤掉正常波动。",
            "单次对照不能证明因果：两次采样的后台负载、温度、电源状态都可能不同。建议同负载重复 3 轮再下结论。",
            "本测量是用户态调度抖动，不等于内核 DPC/ISR 执行时长，也不能代表游戏帧延迟或鼠标到屏幕的端到端延迟。"
        };

        if (diff.Count == 0)
            notes.Add("两次实验之间，系统优化目录里没有任何项的状态变化——两轮数据可直接对比。");
        else if (diff.Count == 1)
            notes.Add($"两次之间只有 1 项发生变化（{diff[0]}），这是一个干净的单变量对照。");
        else
            notes.Add($"⚠ 两次之间有 {diff.Count} 项优化状态不同，无法把差异归因到任何单项：{string.Join("、", diff.Take(8))}。");

        if (b.ChangedItems.Count > 1)
            notes.Add("⚠ 本轮声明改动了多个项目，不符合单变量实验的要求。下一次请一次只改一项。");

        if (a.TimerResolutionMs.HasValue && b.TimerResolutionMs.HasValue &&
            Math.Abs(a.TimerResolutionMs.Value - b.TimerResolutionMs.Value) > 0.01)
            notes.Add($"计时器分辨率从 {a.TimerResolutionMs.Value:0.000} ms 变为 {b.TimerResolutionMs.Value:0.000} ms，这会直接改变抖动基线，两轮不完全同条件。");

        return new LatencyComparison
        {
            Baseline = a,
            Current = b,
            P99DeltaMs = p99Delta,
            MeanDeltaMs = meanDelta,
            MaxDeltaMs = maxDelta,
            Verdict = verdict,
            Reason = string.Join("\n", notes),
            DifferingTweakIds = diff
        };
    }

    // ---------------------------------------------------------------- 批次汇总

    /// <summary>
    /// 汇总同一条件下连跑的 N 轮。
    ///
    /// 第一轮就是默认基线——这是用户最直觉的用法：按现状先测一轮，再去改设置。
    /// 但多轮真正的作用是检验**重复性**：同条件各轮之间差多少。
    /// 如果连跑 3 轮 P99 就差了 30%，那么之后「改完降到 0.1ms」这种结论毫无意义，
    /// 因为噪声本身比效应大。软件必须把这个说出来，而不是只给一个漂亮的差值。
    /// </summary>
    public static LatencyBatchSummary SummarizeBatch(List<LatencySample> runs)
    {
        var valid = (runs ?? new List<LatencySample>())
            .Where(r => r != null && r.SampleCount > 0)
            .OrderBy(r => r.RunIndex)
            .ToList();

        if (valid.Count == 0)
            return new LatencyBatchSummary { Text = "没有有效轮次。" };

        var p99S = valid.Select(r => r.P99Ms).OrderBy(x => x).ToList();
        var median = Percentile(p99S, 0.5);
        var spread = p99S[^1] - p99S[0];
        var baseline = valid[0];
        var limit = Math.Max(0.5, Math.Abs(baseline.P99Ms) * 0.15);
        var consistent = valid.Count == 1 || spread <= limit;

        var sb = new StringBuilder();
        sb.AppendLine($"同条件 {valid.Count} 轮：P99 中位数 {median:0.00} ms，极差 {spread:0.00} ms。");

        if (valid.Count == 1)
        {
            sb.AppendLine("只跑了一轮，无法判断测量本身的重复性。要下结论建议至少 3 轮。");
            sb.AppendLine("基线＝本轮。去改一项设置后，用相同轮数与负载再跑一批，两批的第一轮直接对比。");
        }
        else if (consistent)
        {
            sb.AppendLine($"✓ 各轮差异在噪声范围内（判据：极差 ≤ {limit:0.00} ms）。这批数据可以作为对照的基线。");
            sb.AppendLine($"跨批次比较时请用「P99 中位数 {median:0.00} ms」，它比任何单轮都稳。");
        }
        else
        {
            sb.AppendLine($"⚠ 同条件各轮之间就差了 {spread:0.00} ms，超过噪声上限 {limit:0.00} ms。");
            sb.AppendLine("意思是：这台机器上的抖动主要来自背景负载，而不是你要测的那项设置。");
            sb.AppendLine("这种情况下单轮对照分不出真假——请关掉下载/渲染/录制后重测，或把轮数提到 5 轮看中位数。");
        }

        sb.AppendLine("多轮之间系统状态没有变化，所以轮间差异只反映测量噪声，不反映任何优化效果。");

        return new LatencyBatchSummary
        {
            Runs = valid,
            MedianP99Ms = median,
            P99SpreadMs = spread,
            RepeatsConsistent = consistent,
            Text = sb.ToString().TrimEnd()
        };
    }

    /// <summary>批内每一轮相对第一轮的差值，用于逐轮显示。</summary>
    public static List<(LatencySample Run, double P99DeltaMs)> BatchDeltas(LatencyBatchSummary summary)
    {
        if (summary?.Baseline == null) return new List<(LatencySample, double)>();

        return summary.Runs
            .Select(r => (r, r.P99Ms - summary.Baseline.P99Ms))
            .ToList();
    }

    // ---------------------------------------------------------------- 存取

    public static List<LatencySample> Load()
    {
        try
        {
            if (!File.Exists(StoreFile)) return new List<LatencySample>();
            return JsonSerializer.Deserialize<List<LatencySample>>(File.ReadAllText(StoreFile), Json)
                   ?? new List<LatencySample>();
        }
        catch
        {
            return new List<LatencySample>();
        }
    }

    public static void Save(List<LatencySample> samples)
    {
        AppPaths.Ensure();
        Directory.CreateDirectory(AppPaths.OutputDir);
        File.WriteAllText(StoreFile,
            JsonSerializer.Serialize(samples.OrderByDescending(s => s.TakenAt).ToList(), Json),
            new UTF8Encoding(false));
    }

    /// <summary>导出成 Markdown 报告，方便贴给别人或留档。</summary>
    public static string ExportMarkdown(List<LatencySample> samples)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 延迟对照实验记录");
        sb.AppendLine();
        sb.AppendLine("> 指标为用户态 Sleep(1) 唤醒间隔分布，是调度延迟的代理指标，");
        sb.AppendLine("> 不是 LatencyMon 的内核 DPC/ISR 执行时长，也不是端到端输入延迟。");
        sb.AppendLine();
        sb.AppendLine("| 时间 | 实验名称 | 轮次 | 时长/样本 | 平均 | P95 | P99 | 最大 | 尖峰 | 计时器 | DPC% | 中断% | 进程/线程/句柄 | 内核DPC P99 | 声明改动 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");

        foreach (var s in samples.OrderBy(s => s.TakenAt))
        {
            var load = s.ProcessCount.HasValue
                ? $"{s.ProcessCount} / {s.ThreadCount?.ToString("N0") ?? "—"} / {s.HandleCount?.ToString("N0") ?? "—"}"
                : "读不到";

            var kernel = s.HasKernelData ? $"{s.KernelDpcP99Us:0.0} µs" : "未采集";

            sb.AppendLine($"| {s.TakenAt:yyyy-MM-dd HH:mm:ss} | {s.Label} | {(s.RunText.Length > 0 ? s.RunText : "—")} | " +
                          $"{s.DurationText} | " +
                          $"{s.MeanMs:0.00} | {s.P95Ms:0.00} | {s.P99Ms:0.00} | {s.MaxMs:0.00} | " +
                          $"{s.SpikeCount} | {s.TimerText} | " +
                          $"{(s.DpcPercent.HasValue ? s.DpcPercent.Value.ToString("0.0") : "读不到")} | " +
                          $"{(s.InterruptPercent.HasValue ? s.InterruptPercent.Value.ToString("0.0") : "读不到")} | " +
                          $"{load} | {kernel} | " +
                          $"{(s.ChangedItems.Count > 0 ? string.Join("、", s.ChangedItems) : "—")} |");
        }

        sb.AppendLine();
        sb.AppendLine("单位：毫秒（内核 DPC 列为微秒）。判定规则见软件内「对照」页；单次测量不构成因果结论。");
        sb.AppendLine("内核 DPC 列来自 ETW 的 NT Kernel Logger，需管理员权限；普通权限运行时该列为「未采集」。");
        sb.AppendLine("进程/线程/句柄是采样结束那一刻的快照，用来量化「停服务后系统有没有真的变轻」，不是区间统计。");
        return sb.ToString();
    }
}
