using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>Sigma 规则的字段条件（detection 里的一行）。</summary>
public sealed class SigmaMatcher
{
    public string Field { get; init; } = "";

    /// <summary>contains / startswith / endswith / re / all / gt / lt / 空（等于）</summary>
    public List<string> Modifiers { get; init; } = new();

    public List<string> Values { get; init; } = new();

    public string Display => $"{Field}{(Modifiers.Count > 0 ? "|" + string.Join("|", Modifiers) : "")}";
}

/// <summary>一条已解析的 Sigma 规则（子集）。</summary>
public sealed class SigmaRule
{
    public string Id { get; set; } = "";

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>critical / high / medium / low / informational</summary>
    public string Level { get; set; } = "medium";

    public string Service { get; set; } = "";

    /// <summary>规则来源：内置 / 外部规则目录。</summary>
    public string Source { get; set; } = "内置";

    public List<string> Tags { get; set; } = new();

    public string Condition { get; set; } = "selection";

    public Dictionary<string, List<SigmaMatcher>> Selections { get; set; } = new();

    /// <summary>规则用到的所有字段名，供界面展示。</summary>
    public string FieldsText => string.Join(" · ",
        Selections.Values.SelectMany(v => v).Select(m => m.Field).Distinct().Take(6));

    public Brush LevelBrush => Palette.Get(LevelColor);

    public string LevelColor => Level switch
    {
        "critical" => "#FB7185",
        "high" => "#F97316",
        "medium" => "#FBBF24",
        "low" => "#60A5FA",
        _ => "#8698B4"
    };

    public string LevelText => Level switch
    {
        "critical" => "严重",
        "high" => "高",
        "medium" => "中",
        "low" => "低",
        _ => "信息"
    };

    public string ServiceText => Service switch
    {
        "security" => "安全日志",
        "system" => "系统日志",
        "application" => "应用日志",
        "powershell" => "PowerShell",
        "sysmon" => "Sysmon",
        "defender" => "Defender",
        "taskscheduler" => "计划任务",
        _ => Service
    };
}

/// <summary>一条结构化事件：只保留匹配需要的字段。</summary>
public sealed class SigmaEvent
{
    public string Channel { get; set; } = "";

    public string Provider { get; set; } = "";

    public int EventId { get; set; }

    public DateTime? Time { get; set; }

    public long RecordId { get; set; }

    public string Level { get; set; } = "";

    public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一条能放进报告的摘要。</summary>
    public string Summary { get; set; } = "";

    public string TimeText => Time?.ToString("MM-dd HH:mm:ss") ?? "—";

    public string Get(string field)
    {
        if (string.IsNullOrEmpty(field)) return "";

        if (field.Equals("EventID", StringComparison.OrdinalIgnoreCase)) return EventId.ToString();
        if (field.Equals("Channel", StringComparison.OrdinalIgnoreCase)) return Channel;
        if (field.Equals("Provider", StringComparison.OrdinalIgnoreCase)
            || field.Equals("Provider_Name", StringComparison.OrdinalIgnoreCase)) return Provider;
        if (field.Equals("Level", StringComparison.OrdinalIgnoreCase)) return Level;

        return Fields.TryGetValue(field, out var value) ? value : "";
    }
}

/// <summary>一条规则命中。</summary>
public sealed class SigmaHit
{
    public SigmaRule Rule { get; init; }

    public SigmaEvent Event { get; init; }

    /// <summary>命中的字段与值，便于复核。</summary>
    public string Why { get; init; } = "";

    /// <summary>识别出的反作弊组件名（腾讯 ACE、BattlEye 等）；普通命中为空。</summary>
    public string Tag { get; init; } = "";

    /// <summary>命中来自已知反作弊组件：属于正常环境，不作为线索计分。</summary>
    public bool IsKnownGood => Tag.Length > 0;

    public string TagText => Tag.Length > 0 ? $"已知反作弊组件 · {Tag}" : "";

    public string TimeText => Event?.TimeText ?? "—";

    public string EventText => Event == null ? "" : $"#{Event.RecordId} {Event.Channel}";

    public string Summary => Event?.Summary ?? "";

    public string LevelText => Rule?.LevelText ?? "";

    public Brush LevelBrush => Rule?.LevelBrush ?? Palette.InkTertiary;
}

/// <summary>一个事件通道的读取情况。</summary>
public sealed class SigmaChannel
{
    public string Name { get; set; } = "";

    /// <summary>done / limited / missing</summary>
    public string State { get; set; } = "done";

    public int Count { get; set; }

    public string Note { get; set; } = "";

    public string StateText => State switch
    {
        "limited" => "受限",
        "missing" => "不可用",
        _ => "完成"
    };

    public Brush StateBrush => Palette.Get(State switch
    {
        "limited" => "#FBBF24",
        "missing" => "#63758F",
        _ => "#34D399"
    });
}

/// <summary>一次规则扫描的完整结果。</summary>
public sealed class SigmaScanResult
{
    public List<SigmaHit> Hits { get; set; } = new();

    public List<SigmaChannel> Channels { get; set; } = new();

    public List<SigmaRule> Rules { get; set; } = new();

    public int EventsScanned { get; set; }

    public long ElapsedMs { get; set; }

    public string RuleError { get; set; } = "";

    public int HighCount => Hits.Count(h => h.Rule.Level is "critical" or "high");

    public int MediumCount => Hits.Count(h => h.Rule.Level == "medium");

    public int LowCount => Hits.Count - HighCount - MediumCount;

    public bool HasData => EventsScanned > 0;

    public string ElapsedText => $"{ElapsedMs / 1000.0:N1}s";

    public string Verdict => Hits.Count == 0
        ? "没有命中任何规则"
        : $"命中 {Hits.Count} 条规则（高 {HighCount} / 中 {MediumCount}）";
}
