namespace SysDiag.Models;

/// <summary>系统调优的可切换项。</summary>
public enum TuneKind
{
    GameBar,
    HwScheduling,
    Services,
    CpuScheduling,
    MouseAcceleration
}

/// <summary>一项系统调优的当前状态。</summary>
public sealed class TuneEntry
{
    public TuneKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Desc { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Warning { get; init; } = "";

    /// <summary>优化项是否已生效（true = 已开启/已优化）。</summary>
    public bool Applied { get; init; }

    public bool NeedsRestart { get; init; }
    public bool NeedsAdmin { get; init; }

    public string StateText => Applied ? "已开启" : "未开启";

    public bool HasWarning => !string.IsNullOrEmpty(Warning);
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary>CPU 前后台调度档位。Win32PrioritySeparation 的位含义：
/// bit0-1 时间片长短、bit2-3 是否可变、bit4-5 前台加成倍数。</summary>
public sealed record CpuTuneProfile(string Name, int Value, string Summary)
{
    public override string ToString() => $"{Name}（{Value}）";
}

/// <summary>系统服务项的启动类型原值，用于还原。</summary>
public sealed class ServiceStartBackup
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int OriginalStart { get; set; } = -1;
}
