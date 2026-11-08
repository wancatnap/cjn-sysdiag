namespace SysDiag.Models;

/// <summary>一个外部进程对目标进程的句柄汇总（按所有者进程聚合，权限取并集）。</summary>
public sealed class HandleOwner
{
    /// <summary>持有句柄的进程 PID。</summary>
    public int Pid { get; init; }

    /// <summary>持有句柄的进程名。</summary>
    public string Name { get; init; } = "";

    /// <summary>已知组件标签：系统组件 / 反作弊名。空串表示未知来源。</summary>
    public string Tag { get; init; } = "";

    /// <summary>该进程对目标的所有句柄权限并集。</summary>
    public uint Access { get; init; }

    /// <summary>权限说明文本（如「可写入内存 · 可读取内存」）。</summary>
    public string FlagsText { get; init; } = "";

    /// <summary>能力档位：可注入 / 可读内存 / 可窃取句柄 / 可控制进程 / 仅查询 / 低权限。</summary>
    public string Capability { get; init; } = "";

    /// <summary>访问掩码原文，便于对照 phnt / Win32 文档。</summary>
    public string AccessHex => $"0x{Access:X8}";

    /// <summary>技术等级：warn / hint / ok。白名单不改变技术等级，只改变呈现。</summary>
    public string Risk { get; init; } = "ok";

    public bool Known => Tag.Length > 0;

    public string Header => $"{Name}（{Pid}）";

    public string RiskText => Known ? "可信" : Risk switch
    {
        "warn" => "高危",
        "hint" => "提示",
        _ => "正常"
    };

    /// <summary>等级颜色：走 Palette 语义表。</summary>
    public string RiskBrush => Known ? Palette.LevelChipHex("ok") : Palette.LevelChipHex(Risk);
}

/// <summary>一次句柄扫描的完整结果。</summary>
public sealed class HandleScanResult
{
    /// <summary>被排查的目标进程 PID。</summary>
    public int TargetPid { get; init; }

    /// <summary>目标进程名。</summary>
    public string TargetName { get; init; } = "";

    /// <summary>系统全表句柄条数。</summary>
    public long TotalHandles { get; init; }

    /// <summary>外部进程对目标的句柄汇总。</summary>
    public List<HandleOwner> Findings { get; init; } = new();

    /// <summary>降级模式下无法确认归属的候选句柄条数。</summary>
    public int UnknownCount { get; init; }

    /// <summary>是否按内核对象地址精确归属（否则为复制句柄的受限归属）。</summary>
    public bool Precise { get; init; }

    /// <summary>扫描状态说明（受限原因等）。</summary>
    public string Note { get; init; } = "";

    public string ElapsedText { get; init; } = "";

    /// <summary>非白名单的高危（可写内存 / 可建线程）进程数。</summary>
    public int WarnCount => Findings.Count(x => x.Risk == "warn" && !x.Known);

    /// <summary>非白名单的提示（仅可读内存）进程数。</summary>
    public int HintCount => Findings.Count(x => x.Risk == "hint" && !x.Known);

    /// <summary>其余（白名单或低权限）进程数。</summary>
    public int OkCount => Findings.Count - WarnCount - HintCount;
}
