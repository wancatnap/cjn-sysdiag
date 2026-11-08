namespace SysDiag.Models;

/// <summary>一个电源计划。</summary>
public sealed class PowerPlanEntry
{
    public Guid Guid { get; set; }

    public string Name { get; set; } = "";

    public bool IsActive { get; set; }

    public bool IsBuiltIn { get; set; }

    /// <summary>高性能 / 平衡 / 节能 / 自定义。</summary>
    public string Kind { get; set; } = "";

    /// <summary>关闭显示器分钟数，-1 表示读不到，0 表示从不。</summary>
    public int MonitorOffMinutes { get; set; } = -1;

    public int SleepMinutes { get; set; } = -1;

    /// <summary>处理器最小状态百分比，-1 表示读不到。</summary>
    public int ProcessorMin { get; set; } = -1;

    public int ProcessorMax { get; set; } = -1;

    public string Level => IsActive ? "ok" : "none";

    public string LevelText => IsActive ? "当前使用" : "";

    /// <summary>「关闭显示器 15 分钟 · 睡眠 30 分钟 · 处理器 5%~100%」。</summary>
    public string SettingsText
    {
        get
        {
            var parts = new List<string>
            {
                "关闭显示器 " + TimeText(MonitorOffMinutes),
                "睡眠 " + TimeText(SleepMinutes)
            };

            if (ProcessorMin >= 0 && ProcessorMax >= 0)
                parts.Add($"处理器 {ProcessorMin}%~{ProcessorMax}%");

            return string.Join("　·　", parts);
        }
    }

    private static string TimeText(int minutes) => minutes switch
    {
        < 0 => "读不到",
        0 => "从不",
        < 60 => $"{minutes} 分钟",
        _ => minutes % 60 == 0 ? $"{minutes / 60} 小时" : $"{minutes / 60} 小时 {minutes % 60} 分"
    };
}
