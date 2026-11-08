using System.Text.Json;
using System.Text.Json.Serialization;

namespace SysDiag.Models;

/// <summary>
/// 报告的结构化数据。导出的 .sdreport 里 report.json 就是它的序列化结果，
/// 也是导入后重建界面与做对比的唯一可信源（不解析 HTML）。
/// </summary>
public sealed class ReportData
{
    /// <summary>当前 schema 版本；主版本不兼容时只读展示。</summary>
    public const string CurrentSchema = "sysdiag.report/1";

    public string Schema { get; set; } = CurrentSchema;

    /// <summary>cheat-scan / sigma / usn / disk-clean / log-analysis / generic</summary>
    public string Kind { get; set; } = "generic";

    public string Title { get; set; } = "";

    public string Subtitle { get; set; } = "";

    /// <summary>ISO 8601 本地时间带时区。</summary>
    public string GeneratedAt { get; set; } = "";

    public ToolInfo Tool { get; set; } = new();

    public MachineInfo Machine { get; set; } = new();

    public VerdictInfo Verdict { get; set; } = new();

    public List<ReportSection> Sections { get; set; } = new();

    public List<ReportStageInfo> Stages { get; set; } = new();

    public List<ReportEventInfo> Timeline { get; set; } = new();

    [JsonIgnore]
    public int ItemCount => Sections.Sum(s => s.Items.Count);

    [JsonIgnore]
    public string GeneratedText
    {
        get
        {
            return DateTime.TryParse(GeneratedAt, out var dt)
                ? dt.ToString("yyyy-MM-dd HH:mm:ss")
                : GeneratedAt;
        }
    }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static ReportData FromJson(string json)
        => JsonSerializer.Deserialize<ReportData>(json, JsonOptions);

    /// <summary>主版本是否兼容（major 相同即可读）。</summary>
    public bool IsCompatible()
    {
        var mine = CurrentSchema.Split('/')[^1].Split('.')[0];
        var theirs = (Schema ?? "").Split('/')[^1].Split('.')[0];

        return mine == theirs;
    }

    public static ReportData From(ReportPayload payload)
    {
        if (payload == null) return null;

        // 已经带了结构化数据就直接用，只补齐缺的字段
        var data = payload.Data ?? new ReportData();

        if (string.IsNullOrEmpty(data.Title)) data.Title = payload.Title ?? "";
        if (string.IsNullOrEmpty(data.Subtitle)) data.Subtitle = payload.Subtitle ?? "";
        if (string.IsNullOrEmpty(data.GeneratedAt)) data.GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");

        return data;
    }
}

public sealed class ToolInfo
{
    public string Name { get; set; } = "破烂优化";

    public string Version { get; set; } = SysDiag.Services.AppInfo.Version;
}

public sealed class MachineInfo
{
    public string Name { get; set; } = "";

    public string User { get; set; } = "";

    public string Os { get; set; } = "";

    public bool Admin { get; set; }
}

public sealed class VerdictInfo
{
    /// <summary>-1 表示这类报告没有分数。</summary>
    public int Score { get; set; } = -1;

    public string Level { get; set; } = "";

    public string Text { get; set; } = "";

    public ReportCounts Counts { get; set; } = new();
}

public sealed class ReportCounts
{
    public int Warn { get; set; }

    public int Hint { get; set; }

    public int Ok { get; set; }
}

public sealed class ReportSection
{
    public string Group { get; set; } = "";

    public List<ReportItem> Items { get; set; } = new();
}

public sealed class ReportItem
{
    /// <summary>ok / hint / warn</summary>
    public string Level { get; set; } = "ok";

    public string Title { get; set; } = "";

    public string Detail { get; set; } = "";

    public string Basis { get; set; } = "";

    public int Weight { get; set; }

    public List<string> Tags { get; set; } = new();

    /// <summary>对比用的稳定键：分组 + 标题。</summary>
    public string Key => $"{Level}|{Title}";
}

public sealed class ReportStageInfo
{
    public string Name { get; set; } = "";

    public string State { get; set; } = "done";

    public int Items { get; set; }

    public string Note { get; set; } = "";
}

public sealed class ReportEventInfo
{
    public string Time { get; set; } = "";

    public string Source { get; set; } = "";

    public string Text { get; set; } = "";

    public string Level { get; set; } = "ok";
}
