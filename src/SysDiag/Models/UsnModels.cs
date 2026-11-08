using System.IO;

namespace SysDiag.Models;

/// <summary>USN 日志里的一条删除记录。</summary>
public sealed class UsnRecord
{
    public DateTime Time { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Reason { get; set; } = "";
    public ulong FileId { get; set; }

    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>一次 USN 读取的结果。</summary>
public sealed class UsnResult
{
    public string Drive { get; set; } = "";
    public DateTime Since { get; set; }

    /// <summary>区间终点（含）；null 表示到当前为止。</summary>
    public DateTime? Until { get; set; }
    public bool NeedAdmin { get; set; }
    public string Error { get; set; } = "";
    public long ScannedRecords { get; set; }
    public long JournalFirstUsn { get; set; }
    public long JournalNextUsn { get; set; }
    public int IndexCount { get; set; }
    public List<UsnRecord> Records { get; set; } = new();

    public bool Ok => !NeedAdmin && Error.Length == 0;
}
