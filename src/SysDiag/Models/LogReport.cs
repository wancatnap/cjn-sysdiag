namespace SysDiag.Models;

public sealed class LogFileRow
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public int? Lines { get; set; }
    public int Hits { get; set; }
    public string Kind { get; set; } = "文本";
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
}

public sealed class CrashHit
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Content { get; set; } = "";
}

public sealed class ErrorPattern
{
    public string Signature { get; set; } = "";
    public int Count { get; set; }
}

public sealed class LogReport
{
    public string Source { get; set; } = "";
    public string DisplayTitle { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public string WorkDir { get; set; } = "";
    public string Stamp { get; set; } = "";
    public string Engine { get; set; } = "未识别";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public long LineTotal { get; set; }
    public int CrashCount { get; set; }
    public int ErrorCount { get; set; }
    public int WarnCount { get; set; }
    public DateTime? TimeMin { get; set; }
    public DateTime? TimeMax { get; set; }
    public List<ErrorPattern> TopErrors { get; set; } = new();
    public List<ErrorPattern> AllErrors { get; set; } = new();
    public List<CrashHit> Crashes { get; set; } = new();
    public List<LogFileRow> Files { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    /// <summary>日志中出现的外部软件痕迹（不等同于作弊事实）。</summary>
    public List<SecurityHit> SecurityHits { get; set; } = new();
    /// <summary>本机侧检查结论（Prefetch / 运行中进程）。</summary>
    public List<string> SecurityNotes { get; set; } = new();

    /// <summary>本次游戏会话的时间窗（取体积最大的日志文件的首尾时间戳）。</summary>
    public DateTime? SessionStart { get; set; }
    public DateTime? SessionEnd { get; set; }
    /// <summary>时间窗取自哪个日志文件。</summary>
    public string SessionFile { get; set; } = "";
    /// <summary>同一时间窗内本机的系统事件。</summary>
    public List<SessionEvent> SessionEvents { get; set; } = new();
    public List<string> SessionNotes { get; set; } = new();
}
