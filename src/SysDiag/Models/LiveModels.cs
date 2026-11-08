namespace SysDiag.Models;

/// <summary>直播伴侣优化窗口用的一次采样结果。</summary>
public sealed class LiveSnapshot
{
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public string TemperatureText { get; init; } = "";
    public bool Running { get; init; }
    public int ProcessId { get; init; }

    public string CpuText => CpuPercent < 0 ? "采样中…" : $"{CpuPercent:0.0} %";
    public string MemoryText => MemoryPercent < 0 ? "采样中…" : $"{MemoryPercent:0.0} %";
    public string ProcessText => Running ? $"运行中（PID {ProcessId}）" : "未运行";
}
