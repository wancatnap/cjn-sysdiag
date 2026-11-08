using System;
using System.Collections.Generic;

namespace SysDiag.Models;

/// <summary>
/// 一次延迟采样的结果。
///
/// 重要边界：这里测的是**用户态调度抖动**——线程 Sleep(1) 之后实际被唤醒的间隔，
/// 加上系统计时器分辨率、DPC/中断占用 CPU 的比例。它**不是** LatencyMon 那种
/// 内核驱动采集的 ISR/DPC 单次执行时长。用户态能观测到的抖动是前者的下游表现，
/// 有参考价值，但不能等同、不能拿来宣称「DPC 延迟降低了多少微秒」。
/// </summary>
public sealed class LatencySample
{
    public string Id { get; init; } = "";
    public DateTime TakenAt { get; init; }

    /// <summary>用户给这轮实验起的名字，例如「只关了 Game Bar」。</summary>
    public string Label { get; init; } = "";

    /// <summary>同一批连续采样的标识。多轮连跑时各轮共享，用于组内比较。</summary>
    public string BatchId { get; init; } = "";

    /// <summary>这批里的第几轮（从 1 开始）。单轮时为 1。</summary>
    public int RunIndex { get; init; } = 1;

    /// <summary>这批总共跑几轮。</summary>
    public int RunTotal { get; init; } = 1;

    /// <summary>轮次后缀，例如「 · 第2/3轮」。单轮时为空。</summary>
    public string RunText => RunTotal > 1 ? $"第 {RunIndex}/{RunTotal} 轮" : "";

    /// <summary>采样时长（秒）。</summary>
    public double DurationSeconds { get; init; }

    /// <summary>有效间隔样本数。</summary>
    public int SampleCount { get; init; }

    /// <summary>唤醒间隔，单位毫秒。</summary>
    public double MeanMs { get; init; }
    public double P50Ms { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }

    /// <summary>超过 2 倍中位数的大尖峰个数（调度被抢占的粗略信号）。</summary>
    public int SpikeCount { get; init; }

    /// <summary>系统计时器分辨率，毫秒。读不到为 null。采样结束后回填。</summary>
    public double? TimerResolutionMs { get; set; }

    /// <summary>DPC 占用 CPU 百分比（WMI 尽力而为，读不到为 null）。采样结束后回填。</summary>
    public double? DpcPercent { get; set; }

    /// <summary>中断占用 CPU 百分比（WMI 尽力而为，读不到为 null）。采样结束后回填。</summary>
    public double? InterruptPercent { get; set; }

    /// <summary>这轮实验里，用户自己声明改动的优化项（单变量实验时只应有一个）。</summary>
    public List<string> ChangedItems { get; init; } = new();

    /// <summary>采样时已生效的系统优化项 Id 快照，用来核对两次实验之间到底差了什么。</summary>
    public List<string> ActiveTweakIds { get; init; } = new();

    // ---------------------------------------------------------------- 系统负载
    //
    // 「停掉一些服务能不能让系统更轻」这类说法，只有量出来才算证据。
    // 这四项就是可量的部分：停服务前存一次、停服务后再存一次，差值就是实际回收量。
    // 它们是**静态时刻的快照**，不是区间统计；进程会随时创建销毁，所以判读时看趋势不看个位。

    /// <summary>采样结束时的进程数。</summary>
    public int? ProcessCount { get; set; }

    /// <summary>全部进程的线程总数。部分系统进程读不到，按可读到的累加。</summary>
    public int? ThreadCount { get; set; }

    /// <summary>全部进程的句柄总数。同上，权限不足的进程会被跳过。</summary>
    public int? HandleCount { get; set; }

    /// <summary>已提交内存（MB），来自性能计数器，读不到为 null。</summary>
    public double? CommitUsedMB { get; set; }

    // ---------------------------------------------------------------- 内核 DPC/ISR
    //
    // 这一组需要管理员权限：ETW 的 NT Kernel Logger 会话只能提权开启。
    // 拿到的是**内核 DPC 单次执行时长**，来源是 ETW 的 PERFINFO_DPC 事件（单位 100ns，已换算成微秒）。
    // 拿不到就全为 null，界面显示「未采集」，绝不填估算值。

    /// <summary>内核 DPC 事件数。</summary>
    public int? KernelDpcCount { get; set; }

    /// <summary>内核 DPC 单次执行时长的 P99，微秒。</summary>
    public double? KernelDpcP99Us { get; set; }

    /// <summary>内核 DPC 单次执行时长的最大值，微秒。</summary>
    public double? KernelDpcMaxUs { get; set; }

    /// <summary>内核中断（ISR）事件数。本版本只计数，不计算单次时长。</summary>
    public int? KernelIsrCount { get; set; }

    /// <summary>内核采集失败原因。空串表示成功或未尝试。</summary>
    public string KernelNote { get; set; } = "";

    /// <summary>是否拿到了内核级数据。</summary>
    public bool HasKernelData => KernelDpcCount.HasValue;

    public string KernelText => HasKernelData
        ? $"DPC {KernelDpcCount} 次 / P99 {KernelDpcP99Us:0.0} µs"
        : "未采集";

    public string DurationText => $"{DurationSeconds:0} 秒 / {SampleCount} 个样本";

    public string TimerText => TimerResolutionMs.HasValue
        ? $"{TimerResolutionMs.Value:0.000} ms"
        : "读不到";
}

/// <summary>两次采样的对照结论。刻意不叫「优化有效」，因为单次对照不构成因果证明。</summary>
public sealed class LatencyComparison
{
    public LatencySample Baseline { get; init; } = new();
    public LatencySample Current { get; init; } = new();

    public double P99DeltaMs { get; init; }
    public double MeanDeltaMs { get; init; }
    public double MaxDeltaMs { get; init; }

    /// <summary>improve / worse / flat / insufficient。</summary>
    public string Verdict { get; init; } = "insufficient";

    public string VerdictText => Verdict switch
    {
        "improve" => "复测更低",
        "worse" => "复测更高",
        "flat" => "看不出差异",
        _ => "样本不足"
    };

    /// <summary>判定依据与注意事项，界面必须原样显示，不能只显示结论。</summary>
    public string Reason { get; init; } = "";

    /// <summary>两次实验之间实际变化的优化项；多于一个就无法归因到单项。</summary>
    public List<string> DifferingTweakIds { get; init; } = new();
}

/// <summary>
/// 一批连续采样（同一条件下连跑 N 轮）的汇总。
///
/// 多轮的价值不是「取平均更准」，而是**能反过来检验测量本身可不可信**：
/// 如果同一条件、同一台机器上连跑 3 轮，P99 极差就超过基线 P99 的 15%，
/// 那说明这台机器的抖动主要来自背景噪声，单轮对照根本分不出优化有没有效。
/// 这时软件会明说「本机单轮对照不可靠，请提高轮数或降低背景负载」，
/// 而不是随便给一个看起来像结论的数字。
/// </summary>
public sealed class LatencyBatchSummary
{
    public List<LatencySample> Runs { get; init; } = new();

    /// <summary>第一轮，作为默认基线。</summary>
    public LatencySample Baseline => Runs.Count > 0 ? Runs[0] : null;

    /// <summary>各轮 P99 的中位数。跨批次比较时用它，比单轮稳。</summary>
    public double MedianP99Ms { get; init; }

    /// <summary>各轮 P99 的极差（最大 - 最小），衡量同条件重复性。</summary>
    public double P99SpreadMs { get; init; }

    /// <summary>同条件重复性是否可接受：极差不超过基线 P99 的 15%。</summary>
    public bool RepeatsConsistent { get; init; }

    /// <summary>
    /// 这一批能不能判断重复性。只跑一轮时**不能**——没有轮间差异可比，
    /// 此时说「一致」就是假话，所以单独用一个属性表达，不靠 RepeatsConsistent 兼表两义。
    /// </summary>
    public bool CanJudgeRepeats => Runs.Count >= 2;

    /// <summary>这批的实际轮数。</summary>
    public int RunCount => Runs.Count;

    /// <summary>给人看的判读文字，界面直接显示。</summary>
    public string Text { get; init; } = "";
}
