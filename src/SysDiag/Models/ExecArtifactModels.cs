using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>一条 Prefetch 记录：某个程序运行过，以及运行了几次。</summary>
public sealed class PrefetchEntry
{
    public string Name { get; set; } = "";

    public string FilePath { get; set; } = "";

    /// <summary>Prefetch 格式版本：17 / 23 / 26 / 30 / 31。</summary>
    public int Version { get; set; }

    /// <summary>-1 表示解析不出来（布局不认识），不做猜测。</summary>
    public int RunCount { get; set; } = -1;

    /// <summary>最后运行时间，取 .pf 的修改时间（进程退出后才写盘）。</summary>
    public DateTime? LastRun { get; set; }

    public long FileSize { get; set; }

    public string LastRunText => LastRun?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    public string RunCountText => RunCount >= 0 ? RunCount.ToString("N0") : "—";

    public string SizeText => LogFileItem.FormatSize(FileSize);

    public string VersionText => Version > 0 ? $"v{Version}" : "—";

    public bool IsSuspiciousPath =>
        Name.Contains("temp", StringComparison.OrdinalIgnoreCase)
        || FilePath.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
        || FilePath.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>一条 ShimCache 记录：某个文件曾在系统里出现过。</summary>
public sealed class ShimCacheEntry
{
    public string Path { get; set; } = "";

    public DateTime? LastModified { get; set; }

    public long Size { get; set; }

    public string LastModifiedText => LastModified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    public string SizeText => Size > 0 ? LogFileItem.FormatSize(Size) : "";

    public bool IsSuspiciousPath =>
        Path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
        || Path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase)
        || Path.Contains(@"\Users\Public\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>本地执行痕迹的扫描结果。</summary>
public sealed class ExecArtifactResult
{
    public List<PrefetchEntry> Prefetch { get; set; } = new();

    public List<ShimCacheEntry> ShimCache { get; set; } = new();

    /// <summary>done / limited / missing</summary>
    public string PrefetchState { get; set; } = "done";

    public string PrefetchNote { get; set; } = "";

    public string ShimCacheState { get; set; } = "done";

    public string ShimCacheNote { get; set; } = "";

    /// <summary>压缩格式未能解析的 .pf 数量。</summary>
    public int SkippedCompressed { get; set; }

    /// <summary>解析失败（布局不认识）的 .pf 数量。</summary>
    public int Failed { get; set; }

    public long ElapsedMs { get; set; }

    public bool HasData => Prefetch.Count > 0 || ShimCache.Count > 0;

    /// <summary>运行次数最多的前几条，报告里用。</summary>
    public IEnumerable<PrefetchEntry> Top => Prefetch
        .OrderByDescending(p => p.RunCount)
        .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);

    public string ElapsedText => $"{ElapsedMs / 1000.0:N1}s";

    public string PrefetchStateText => PrefetchState switch
    {
        "limited" => "受限",
        "missing" => "不可用",
        _ => "完成"
    };

    public string ShimCacheStateText => ShimCacheState switch
    {
        "limited" => "受限",
        "missing" => "不可用",
        _ => "完成"
    };

    public Brush PrefetchStateBrush => Palette.Get(PrefetchState switch
    {
        "limited" => "#FBBF24",
        "missing" => "#63758F",
        _ => "#34D399"
    });

    public Brush ShimCacheStateBrush => Palette.Get(ShimCacheState switch
    {
        "limited" => "#FBBF24",
        "missing" => "#63758F",
        _ => "#34D399"
    });
}
