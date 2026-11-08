namespace SysDiag.Models;

/// <summary>深度痕迹的三类检查面。</summary>
public enum DeepGroup
{
    Dma,
    Capture,
    Input
}

/// <summary>一条深度痕迹检查结果。</summary>
public sealed class DeepTraceItem
{
    public DeepGroup Group { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>ok / hint / warn</summary>
    public string Level { get; init; } = "ok";

    /// <summary>数据来源，写清楚便于复核。</summary>
    public string Basis { get; init; } = "";

    /// <summary>只有深度扫描才会产出。</summary>
    public bool DeepOnly { get; init; }

    public bool HasBasis => !string.IsNullOrEmpty(Basis);

    public string LevelText => Level switch
    {
        "warn" => "线索",
        "hint" => "提示",
        _ => "正常"
    };

    public string LevelColor => Level switch
    {
        "warn" => "#E03131",
        "hint" => "#F08C00",
        _ => "#2F9E44"
    };

    public string LevelBg => Level switch
    {
        "warn" => "#FDECEC",
        "hint" => "#FFF4E6",
        _ => "#EBFBEE"
    };

    public string GroupText => Group switch
    {
        DeepGroup.Dma => "DMA 攻击面",
        DeepGroup.Capture => "采集卡链路",
        _ => "输入模拟设备"
    };
}

/// <summary>一次扫描的整体结果。</summary>
public sealed class DeepTraceResult
{
    public List<DeepTraceItem> Items { get; init; } = new();

    public bool IsDeep { get; init; }

    /// <summary>扫描耗时（毫秒）。</summary>
    public long ElapsedMs { get; init; }

    public int WarnCount => Items.Count(i => i.Level == "warn");
    public int HintCount => Items.Count(i => i.Level == "hint");
    public int OkCount => Items.Count(i => i.Level == "ok");

    public IEnumerable<DeepTraceItem> Of(DeepGroup g) => Items.Where(i => i.Group == g);

    public int CountOf(DeepGroup g) => Items.Count(i => i.Group == g);

    public string Verdict => WarnCount > 0
        ? $"发现 {WarnCount} 处需要人工确认的线索"
        : HintCount > 0
            ? $"有 {HintCount} 处提示项，均属常见配置"
            : "未发现明显痕迹";

    /// <summary>一句话解释结论依据，避免把“线索”读成“判定”。</summary>
    public string VerdictDetail => WarnCount > 0
        ? "线索指这台机器上同时出现了某项组合特征，可能用于外部辅助，也可能是正常设备组合。本工具只列证据，不判定作弊。"
        : HintCount > 0
            ? "提示项是单独看并不异常的设备或设置，列出来是为了让结论可复核。"
            : "所有检查项都在常见范围内。查不到不代表没有，只代表本机没有留下可读的记录。";

    public string VerdictColor => WarnCount > 0 ? "#C92A2A" : HintCount > 0 ? "#D9480F" : "#2B8A3E";

    public string VerdictBg => WarnCount > 0 ? "#FDECEC" : HintCount > 0 ? "#FFF4E6" : "#EBFBEE";

    public string ScopeText => IsDeep ? "深度扫描" : "快速体检";
}
