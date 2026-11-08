namespace SysDiag.Models;

/// <summary>游戏 Saved 下的一个可清理缓存目录。</summary>
public sealed class DeltaCacheItem
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string SavedRoot { get; init; } = "";
    public int FileCount { get; init; }
    public long Size { get; init; }
    public bool Selected { get; set; }

    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024.0 / 1024.0:0.0} MB"
        : $"{Size / 1024.0:0} KB";

    public string CountText => $"{FileCount} 个文件";

    public string Detail => Path;
}
