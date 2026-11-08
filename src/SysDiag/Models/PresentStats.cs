using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace SysDiag.Models;

/// <summary>一段采集的帧率统计。</summary>
public sealed class PresentStats
{
    public int Frames { get; init; }

    public double Seconds { get; init; }

    public double AvgFps { get; init; }

    /// <summary>帧时间的 p50 / p99（毫秒）。</summary>
    public double MedianMs { get; init; }

    public double P99Ms { get; init; }

    /// <summary>1% low：把最慢的 1% 帧按平均算出来的 FPS。</summary>
    public double Low1PercentFps => P99Ms > 0 ? 1000.0 / P99Ms : 0;

    /// <summary>卡顿率：帧时间超过中位数 2 倍的帧占比。</summary>
    public double StutterPercent { get; init; }

    public double WorstMs { get; init; }

    public string Range => $"{Seconds:0.0} 秒 / {Frames} 帧";

    public string Summary =>
        $"{AvgFps:0.#} FPS 平均 · 1% low {Low1PercentFps:0.#} FPS · 卡顿 {StutterPercent:0.#}%";

    /// <summary>报告里的那段人话。</summary>
    public string Detail
    {
        get
        {
            if (Frames < 30) return "采样帧数太少，结果不可信（可能没抓到游戏画面或采集失败）。";

            var verdict = StutterPercent >= 8
                ? "卡顿明显：帧时间波动大，可能是后台抢 CPU/磁盘，或显存/内存吃紧。"
                : StutterPercent >= 3
                    ? "有轻微卡顿，在正常范围边缘。"
                    : "帧时间很稳，没有明显卡顿。";

            return $"{verdict}中位帧时间 {MedianMs:0.0} ms，最慢 1% 在 {P99Ms:0.0} ms，最慢一帧 {WorstMs:0.0} ms。";
        }
    }
}
