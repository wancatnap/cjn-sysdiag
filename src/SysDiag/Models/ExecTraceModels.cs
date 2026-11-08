namespace SysDiag.Models;

/// <summary>一条程序执行 / 安装记录。</summary>
public sealed class ExecRecord
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string Source { get; init; } = "";
    public DateTime? Time { get; init; }
    public string Version { get; init; } = "";
    public string Publisher { get; init; } = "";
    public string Extra { get; init; } = "";

    public string TimeText => Time.HasValue ? Time.Value.ToString("yyyy-MM-dd HH:mm:ss") : "—";

    public string SourceText => Source switch
    {
        "BAM" => "BAM 最近执行",
        "UserAssist" => "UserAssist",
        "AmCache" => "AmCache 安装记录",
        _ => Source
    };

    /// <summary>右侧次要信息：版本 / 发布者 / 其他，用 · 拼接。</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (Version.Length > 0) parts.Add("v" + Version);
            if (Publisher.Length > 0) parts.Add(Publisher);
            if (Extra.Length > 0) parts.Add(Extra);
            return string.Join("　·　", parts);
        }
    }

    public bool HasMeta => MetaText.Length > 0;

    /// <summary>路径过长时保留盘符与文件名。</summary>
    public string PathText
    {
        get
        {
            if (Path.Length <= 96) return Path;
            var idx = Path.LastIndexOf('\\');
            var tail = idx > 0 ? Path[idx..] : Path;
            var head = Path[..Math.Min(28, Path.Length)];
            return head + "…" + tail;
        }
    }
}

/// <summary>SRUM（每应用网络用量数据库）的可用性状态。</summary>
public sealed class SrumStatus
{
    public string FilePath { get; init; } = "";

    /// <summary>ok / denied / missing</summary>
    public string Access { get; init; } = "missing";

    public bool Exists { get; init; }
    public long SizeBytes { get; init; }
    public DateTime? LastWrite { get; init; }
    public bool Readable { get; init; }
    public string ServiceState { get; init; } = "";

    public string SizeText => SizeBytes >= 1024L * 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024 / 1024:N1} GB"
        : $"{SizeBytes / 1024.0 / 1024:N1} MB";

    public string LastWriteText => LastWrite?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    /// <summary>状态色：ok / hint。</summary>
    public string Level => Access == "ok" ? "ok" : "hint";

    public string Summary
    {
        get
        {
            if (Access == "denied")
                return "库文件存在，但当前权限读不到——SRUDB.dat 在 System32\\sru 下，需要管理员。";
            if (!Exists) return "SRUDB.dat 不存在，这台机器没有 SRUM 数据。";
            if (!Readable) return $"库文件存在（{SizeText}），但打不开。";
            return $"库文件存在（{SizeText}），最后写入 {LastWriteText}。";
        }
    }

    public string LevelColor => Level == "ok" ? "#2F9E44" : "#E8590C";
    public string LevelBg => Level == "ok" ? "#EBFBEE" : "#FFF4E6";
    public string LevelText => Access switch
    {
        "ok" => "可用",
        "denied" => "受限",
        _ => "缺失"
    };
}

/// <summary>一次读取的结果：三处记录 + 各自读不到时的原因。</summary>
public sealed class ExecScanResult
{
    public List<ExecRecord> Bam { get; set; } = new();
    public string BamNote { get; set; } = "";
    public List<ExecRecord> Amc { get; set; } = new();
    public string AmcNote { get; set; } = "";
    public SrumStatus Srum { get; set; } = new();

    public bool HasData => Bam.Count + Amc.Count > 0;
}
