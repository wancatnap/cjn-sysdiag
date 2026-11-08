using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>一条体检发现。Level 与深度痕迹保持一致：ok / hint / warn。</summary>
public sealed class CheatFinding : INotifyPropertyChanged
{
    public string Group { get; set; } = "";

    public string Title { get; set; } = "";

    /// <summary>人话：这是什么、为什么列出来。</summary>
    public string Detail { get; set; } = "";

    /// <summary>原始依据，便于复核（路径、时间、设备号等）。</summary>
    public string Basis { get; set; } = "";

    public string Level { get; set; } = "ok";

    /// <summary>对风险分的贡献；提示类会被总额封顶，避免“提示多就高风险”。</summary>
    public int Weight { get; set; }

    public bool HasBasis => Basis.Length > 0;

    public bool HasDetail => Detail.Length > 0;

    public string LevelText => Level switch
    {
        "warn" => "线索",
        "hint" => "提示",
        _ => "正常"
    };

    public Brush LevelBrush => Palette.Get(Level switch
    {
        "warn" => "#C92A2A",
        "hint" => "#D9480F",
        _ => "#2B8A3E"
    });

    public string WeightText => Weight > 0 ? $"+{Weight}" : "";

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一个采集器的执行状态。</summary>
public sealed class CheatStage
{
    public string Name { get; set; } = "";

    /// <summary>done / limited / failed</summary>
    public string State { get; set; } = "done";

    public int Items { get; set; }

    public string Note { get; set; } = "";

    public string StateText => State switch
    {
        "limited" => "受限",
        "failed" => "失败",
        _ => "完成"
    };

    public Brush StateBrush => Palette.Get(State switch
    {
        "limited" => "#D9480F",
        "failed" => "#C92A2A",
        _ => "#2B8A3E"
    });
}

/// <summary>统一时间线上的一条事件。</summary>
public sealed class CheatEvent
{
    public DateTime? Time { get; set; }

    public string Source { get; set; } = "";

    public string Text { get; set; } = "";

    public string Level { get; set; } = "ok";

    public string TimeText => Time?.ToString("MM-dd HH:mm:ss") ?? "—";

    public Brush LevelBrush => Palette.Get(Level switch
    {
        "warn" => "#C92A2A",
        "hint" => "#D9480F",
        _ => "#2B8A3E"
    });
}

/// <summary>一次「外挂痕迹体检」的完整结果。</summary>
public sealed class CheatScanResult
{
    public List<CheatFinding> Findings { get; set; } = new();

    public List<CheatStage> Stages { get; set; } = new();

    public List<CheatEvent> Timeline { get; set; } = new();

    /// <summary>0-100，越高越可疑。只作参考，主结论看分级。</summary>
    public int Score { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.Now;

    public long ElapsedMs { get; set; }

    public string Machine { get; set; } = "";

    /// <summary>
    /// 机器特征码：稳定硬件字段（UUID / 主板 / BIOS / CPU / 系统盘）的 SHA256 前 16 位。
    /// 换内存条、加硬盘、改机器名都不变，用来把报告和具体机器对应起来。
    /// 空串表示读不到——报告里显示「读不到」，不编假码。
    /// </summary>
    public string MachineCode { get; set; } = "";

    /// <summary>填写者的名字或代号。留空则报告里显示「未填写」。</summary>
    public string Operator { get; set; } = "";

    /// <summary>报告生成时间（扫描结束那一刻），与 StartedAt 区分：开始时间可能比生成时间早十几秒。</summary>
    public DateTime FinishedAt { get; set; } = DateTime.Now;

    public string OperatorText => Operator.Trim().Length > 0 ? Operator.Trim() : "未填写";

    public string MachineCodeText => MachineCode.Length > 0 ? MachineCode : "读不到";

    public int WarnCount => Findings.Count(f => f.Level == "warn");

    public int HintCount => Findings.Count(f => f.Level == "hint");

    public int OkCount => Findings.Count(f => f.Level == "ok");

    public int GroupCount => Findings.Select(f => f.Group).Distinct().Count();

    public int DoneStages => Stages.Count(s => s.State == "done");

    public string ScoreLevel => Score >= 50 ? "高" : Score >= 20 ? "中" : "低";

    public Brush ScoreBrush => Palette.Get(ScoreColor);

    public Brush ScoreBgBrush => Palette.Get(ScoreBg);

    public string ScoreColor => Score >= 50 ? "#C92A2A" : Score >= 20 ? "#D9480F" : "#2B8A3E";

    public string ScoreBg => Score >= 50 ? "#FDECEC" : Score >= 20 ? "#FFF4E6" : "#EBFBEE";

    public string Verdict => WarnCount > 0
        ? $"发现 {WarnCount} 条需要人工确认的线索"
        : HintCount > 0
            ? $"未发现组合线索，有 {HintCount} 条提示项"
            : "未发现可疑痕迹";

    public string VerdictDetail => WarnCount > 0
        ? "线索表示多项特征同时出现，可能用于外部辅助，也可能是正常设备或调试环境。本工具只列证据，不判定作弊。"
        : HintCount > 0
            ? "提示项单独看都在常见范围内，列出来是为了让结论可复核。"
            : "本次检查的所有维度都在常见范围内。查不到不代表没有，只代表本机没有留下可读的记录。";

    public string ElapsedText => $"{ElapsedMs / 1000.0:N1}s";
}
