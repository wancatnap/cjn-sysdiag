using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>清理风险等级：安全=可直接删；谨慎=可能影响排障/需重新下载；仅展示=只统计不提供删除。</summary>
public enum DiskRisk
{
    Safe,
    Careful,
    Keep
}

/// <summary>一个可清理项（一组同类的目录或文件）。</summary>
public sealed class DiskCleanTarget : INotifyPropertyChanged
{
    /// <summary>稳定标识，自检与记录用。</summary>
    public string Key { get; set; } = "";

    public string Category { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>这一项是什么、删了会怎样。</summary>
    public string Note { get; set; } = "";

    /// <summary>目录列表（IsDirectory=true）或文件列表。</summary>
    public List<string> Paths { get; set; } = new();

    public bool IsDirectory { get; set; }

    /// <summary>回收站整体统计（用 Shell API，不走目录枚举）。</summary>
    public bool IsRecycleBin { get; set; }

    public long Size { get; set; }

    public int FileCount { get; set; }

    public DiskRisk Risk { get; set; } = DiskRisk.Safe;

    private bool _selected;

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>有内容且不是「仅展示」才允许勾选。</summary>
    public bool CanClean => Risk != DiskRisk.Keep && Size > 0;

    public string LevelText => Risk switch
    {
        DiskRisk.Safe => "安全",
        DiskRisk.Careful => "谨慎",
        _ => "仅展示"
    };

    public string LevelHex => Risk switch
    {
        DiskRisk.Safe => "#34D399",
        DiskRisk.Careful => "#FBBF24",
        _ => "#63758F"
    };

    public Brush LevelBrush => Palette.Get(LevelHex);

    public string SizeText => LogFileItem.FormatSize(Size);

    public string CountText => FileCount > 0 ? $"{FileCount:N0} 个文件" : "";

    /// <summary>路径展示：两行以内，避免长路径撑破布局。</summary>
    public string PathText
    {
        get
        {
            if (IsRecycleBin) return @"C:\$Recycle.Bin";
            if (Paths.Count == 0) return "";
            if (Paths.Count == 1) return Paths[0];
            return Paths[0] + (Paths.Count > 1 ? $"　等 {Paths.Count} 处" : "");
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>C 盘一次扫描的结果。</summary>
public sealed class DiskScanResult
{
    public string DriveLetter { get; set; } = "C";

    public long TotalBytes { get; set; }

    public long FreeBytes { get; set; }

    public List<DiskCleanTarget> Targets { get; set; } = new();

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedPercent => TotalBytes <= 0 ? 0 : UsedBytes * 100.0 / TotalBytes;

    /// <summary>所有可清理项合计。</summary>
    public long CleanableBytes => Targets.Where(t => t.CanClean).Sum(t => t.Size);

    /// <summary>其中「安全」级合计（一键清理默认只勾这些）。</summary>
    public long SafeBytes => Targets.Where(t => t.CanClean && t.Risk == DiskRisk.Safe).Sum(t => t.Size);

    public int SafeCount => Targets.Count(t => t.CanClean && t.Risk == DiskRisk.Safe);

    /// <summary>扫描耗时（毫秒），用于在界面上说明扫描成本。</summary>
    public long ElapsedMs { get; set; }

    public string TotalText => LogFileItem.FormatSize(TotalBytes);
    public string FreeText => LogFileItem.FormatSize(FreeBytes);
    public string CleanableText => LogFileItem.FormatSize(CleanableBytes);
}

/// <summary>清理执行结果。</summary>
public sealed class DiskCleanOutcome
{
    public int RemovedTargets { get; set; }

    public int RemovedFiles { get; set; }

    /// <summary>真正删除（不经回收站）的字节数。</summary>
    public long DeletedBytes { get; set; }

    /// <summary>移入回收站的字节数（清空回收站后才真正释放）。</summary>
    public long RecycledBytes { get; set; }

    public long TotalBytes => DeletedBytes + RecycledBytes;

    public List<string> Errors { get; set; } = new();

    public long ElapsedMs { get; set; }
}

/// <summary>C 盘顶层目录占用（深度分析用）。</summary>
public sealed class RootUsageItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public double Percent { get; set; }
    public string SizeText => LogFileItem.FormatSize(Size);
    public string PercentText => Percent <= 0 ? "" : $"{Percent:N1}%";

    /// <summary>条形图宽度（像素）：按占比放大到 220px 基准。</summary>
    public double BarWidth => Math.Max(2, Math.Min(100, Percent) * 2.2);
}
