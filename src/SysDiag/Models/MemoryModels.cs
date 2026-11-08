namespace SysDiag.Models;

/// <summary>
/// 一次内存快照。单位全部是字节，格式化交给下面的 Text 属性——
/// 窗口只负责排版，不再各自算一遍 GB/MB，避免两处口径不一致。
/// </summary>
public sealed class MemorySnapshot
{
    // ---------------- 物理与提交（GlobalMemoryStatusEx）

    public ulong TotalPhys { get; init; }
    public ulong AvailPhys { get; init; }
    public ulong TotalPageFile { get; init; }
    public ulong AvailPageFile { get; init; }

    // ---------------- 提交与内核池（GetPerformanceInfo）

    public ulong CommitTotal { get; init; }
    public ulong CommitLimit { get; init; }
    public ulong CommitPeak { get; init; }
    public ulong SystemCache { get; init; }
    public ulong KernelPaged { get; init; }
    public ulong KernelNonpaged { get; init; }
    public ulong PageSize { get; init; }

    // ---------------- 页列表（NtQuerySystemInformation / SystemMemoryListInformation）
    // 读不到时这四项为 0，ListInfoAvailable = false。精简系统上确实可能读不到，
    // 界面必须显示「读不到」而不是显示 0，否则会被当成「待机缓存是 0」。

    public ulong StandbyPages { get; init; }
    public ulong ModifiedPages { get; init; }
    public ulong FreePages { get; init; }
    public ulong ZeroPages { get; init; }
    public bool ListInfoAvailable { get; init; }

    public DateTime Stamp { get; init; } = DateTime.Now;

    // ---------------- 派生量

    /// <summary>已用 = 物理总量 − 可用。Windows 的「可用」包含待机缓存。</summary>
    public ulong Used => TotalPhys > AvailPhys ? TotalPhys - AvailPhys : 0;

    public ulong StandbyBytes => PageSize == 0 ? 0 : StandbyPages * PageSize;
    public ulong ModifiedBytes => PageSize == 0 ? 0 : ModifiedPages * PageSize;
    public ulong FreeBytes => PageSize == 0 ? 0 : (FreePages + ZeroPages) * PageSize;

    /// <summary>
    /// 真正的「进程 + 内核 + 驱动」占用 = 已用 − 已修改页。
    ///
    /// 注意待机页**不在已用里**：GlobalMemoryStatusEx 的「可用」已经包含待机列表，
    /// 所以把待机再从已用里减一次，会算出一个远小于实际进程占用总和的假数字（实测踩过：
    /// 223 个进程的工作集加起来 10 GB 级，公式却给出 2.6 GB）。
    /// </summary>
    public ulong LiveBytes
    {
        get
        {
            var modified = ModifiedBytes;
            return Used > modified ? Used - modified : Used;
        }
    }

    public double UsedPercent => TotalPhys == 0 ? 0 : Used * 100.0 / TotalPhys;

    public string TotalText => Bytes(TotalPhys);
    public string UsedText => Bytes(Used) + " / " + Bytes(TotalPhys);
    public string UsedPercentText => UsedPercent.ToString("0.0") + " %";
    public string AvailText => Bytes(AvailPhys);
    public string CommitText => Bytes(CommitTotal) + " / " + Bytes(CommitLimit);
    public string CommitPercentText => CommitLimit == 0 ? "--" : (CommitTotal * 100.0 / CommitLimit).ToString("0.0") + " %";
    public string StandbyText => ListInfoAvailable ? Bytes(StandbyBytes) : "读不到";
    public string ModifiedText => ListInfoAvailable ? Bytes(ModifiedBytes) : "读不到";
    public string KernelPoolText => Bytes(KernelPaged + KernelNonpaged);
    public string CacheText => Bytes(SystemCache);

    /// <summary>
    /// 构成条四段：实占 / 待机 / 已修改 / 空闲。合计等于物理总量。
    /// 读不到页列表时退化两段（已用 / 可用），不给假精度。
    /// </summary>
    public List<(string Label, ulong Bytes, string Hex)> Segments()
    {
        if (!ListInfoAvailable)
        {
            return new List<(string, ulong, string)>
            {
                ("已用", Used, "#DB4A7B"),
                ("可用", AvailPhys, "#E8E0E3")
            };
        }

        var modified = ModifiedBytes;
        var standby = StandbyBytes;
        var live = LiveBytes;
        var free = FreeBytes;

        return new List<(string, ulong, string)>
        {
            // 这一段的准确含义是「已用里除去已修改页」的部分，实际就是进程工作集 + 内核池 + 驱动。
            // 不叫「占用」——上面 KPI 的「已用」比它大一点，两个都叫占用会让人对不上账。
            ("进程与内核", live, "#DB4A7B"),
            ("已修改", modified, "#F08C00"),
            ("待机", standby, "#F08CAE"),
            ("空闲", free, "#E8E0E3")
        };
    }

    /// <summary>字节 → 人读的容量。1 GB = 1024 MB，与任务管理器口径一致。</summary>
    public static string Bytes(ulong bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return (bytes / GB).ToString(bytes >= 10 * GB ? "0.0" : "0.00") + " GB";
        if (bytes >= MB) return (bytes / MB).ToString(bytes >= 100 * MB ? "0" : "0.0") + " MB";
        if (bytes >= KB) return (bytes / KB).ToString("0") + " KB";
        return bytes + " B";
    }
}

/// <summary>内存排行里的一行。工作集 = 物理里现在压着的部分；提交 = 已经向系统「认领」的额度。</summary>
public sealed class MemProcessRow
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public long WorkingSetMb { get; init; }
    public long PrivateMb { get; init; }
    public long CommitMb { get; init; }

    /// <summary>系统关键进程：不允许在这里回收工作集（有些进程回收后会立刻缺页抖动）。</summary>
    public bool IsCritical { get; init; }

    public string WorkingSetText => MemorySnapshot.Bytes((ulong)Math.Max(0, WorkingSetMb) * 1024 * 1024);
    public string PrivateText => MemorySnapshot.Bytes((ulong)Math.Max(0, PrivateMb) * 1024 * 1024);
    public string CommitText => MemorySnapshot.Bytes((ulong)Math.Max(0, CommitMb) * 1024 * 1024);

    public string SubText => $"PID {Pid}" + (IsCritical ? " · 系统进程" : "");
}

/// <summary>内存窗口底部的只读诊断：压缩内存、SysMain、页面文件。</summary>
public sealed class MemoryDiagnostics
{
    public bool MemoryCompression { get; init; }
    public long CompressionWorkingSetMb { get; init; }
    public bool CompressionDetectable { get; init; }

    public string SysMainState { get; init; } = "读不到";
    public bool SysMainRunning => SysMainState.Contains("运行", StringComparison.Ordinal);

    public string PageFileText { get; init; } = "读不到";
    public double PageFilePercent { get; init; }

    /// <summary>LargeSystemCache 生效值：1 = 系统缓存优先。0 或读不到都按 0 处理。</summary>
    public int LargeSystemCache { get; init; }

    public string CompressionText => !CompressionDetectable
        ? "压缩内存 读不到"
        : MemoryCompression
            ? "压缩内存 开（" + MemorySnapshot.Bytes((ulong)Math.Max(0, CompressionWorkingSetMb) * 1024 * 1024) + "）"
            : "压缩内存 关";

    /// <summary>压缩内存不可判定时整段不显示——写「读不到」比不写更让人不安，但这里确实没有可读的源。</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (CompressionDetectable) parts.Add(CompressionText);
            parts.Add("SysMain " + SysMainState);
            parts.Add("页面文件 " + PageFileText);
            if (LargeSystemCache == 1) parts.Add("系统缓存优先 开");
            return string.Join("　·　", parts);
        }
    }
}

/// <summary>释放动作的结果。FreedMb 为负表示「没释放，反而少了」（正常现象，例如刚好有程序在分配）。</summary>
public sealed class MemoryReleaseResult
{
    public bool Ok { get; init; }
    public long FreedMb { get; init; }
    public string Message { get; init; } = "";
}
