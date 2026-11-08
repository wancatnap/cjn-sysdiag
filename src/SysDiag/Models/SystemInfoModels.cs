using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>一条系统信息项的可用状态。</summary>
public enum InfoState
{
    /// <summary>正常取到。</summary>
    Ok,

    /// <summary>取不到，但原因已知（组件被移除 / 权限不足）。</summary>
    Unavailable,

    /// <summary>值存在但可疑（例如版本号与产品名矛盾）。</summary>
    Suspect
}

/// <summary>系统信息里的一行：名称 + 值 + 状态。</summary>
public sealed class InfoItem
{
    public string Name { get; init; } = "";

    public string Value { get; init; } = "";

    public InfoState State { get; init; } = InfoState.Ok;

    /// <summary>取不到 / 可疑时的原因，直接显示给用户。</summary>
    public string Note { get; init; } = "";

    public string Display => State == InfoState.Ok
        ? Value
        : Value.Length > 0 ? $"{Value}　（{Note}）" : $"不可用：{Note}";

    public string Level => State switch
    {
        InfoState.Ok => "ok",
        InfoState.Suspect => "hint",
        _ => "none"
    };

    public string LevelText => State switch
    {
        InfoState.Ok => "正常",
        InfoState.Suspect => "可疑",
        _ => "不可用"
    };
}

/// <summary>一个信息分组（操作系统 / 处理器 / 内存 …）。</summary>
public sealed class InfoGroup
{
    public string Name { get; init; } = "";

    public List<InfoItem> Items { get; init; } = new();

    public int UnavailableCount => Items.Count(i => i.State == InfoState.Unavailable);

    public string Note => UnavailableCount == 0
        ? $"{Items.Count} 项"
        : $"{Items.Count} 项 · {UnavailableCount} 项不可用";
}

/// <summary>精简系统的结论档位。</summary>
public enum TrimVerdict
{
    /// <summary>关键组件都在，判断为完整系统。</summary>
    Complete,

    /// <summary>有零星缺失，但不足以断定是精简镜像（也可能是企业定制或手工删过东西）。</summary>
    MinorMissing,

    /// <summary>命中多个强项（Defender/更新组件/恢复环境/关键目录），几乎可以确定是精简镜像。</summary>
    Trimmed,

    /// <summary>权限不够，大部分检测项读不到，不下结论。</summary>
    Unknown
}

/// <summary>精简系统检测里的一条检查。</summary>
public sealed class TrimCheck
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    /// <summary>分类：组件 / 服务 / 更新 / 安全 / 镜像来源 / 版本。</summary>
    public string Category { get; init; } = "";

    /// <summary>是否缺失/异常。</summary>
    public bool Missing { get; init; }

    /// <summary>命中时算几分：3 = 强证据（Defender/更新/恢复环境这类被删基本只有精简镜像会做），1 = 弱证据。</summary>
    public int Weight { get; init; }

    /// <summary>结论说明（缺失时列出具体缺了什么）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>依据（文件路径 / 注册表键）。</summary>
    public string Basis { get; init; } = "";

    public bool Skipped { get; init; }

    public string Level => Skipped ? "none" : !Missing ? "ok" : Weight >= 3 ? "warn" : "hint";

    public string LevelText => Skipped ? "未检查" : !Missing ? "正常" : Weight >= 3 ? "缺失" : "有缺失";

    public string LevelBrush => Palette.LevelChipHex(Level);
}

/// <summary>一次系统信息采集的完整结果。</summary>
public sealed class SystemInfoResult
{
    public List<InfoGroup> Groups { get; init; } = new();

    public List<TrimCheck> TrimChecks { get; init; } = new();

    /// <summary>精简系统风险分（命中项权重之和）。</summary>
    public int TrimScore { get; init; }

    public TrimVerdict Verdict { get; init; }

    public bool Admin { get; init; }

    public string ElapsedText { get; init; } = "";

    public List<TrimCheck> MissingChecks => TrimChecks.Where(c => c.Missing).ToList();

    public int StrongMissing => TrimChecks.Count(c => c.Missing && c.Weight >= 3);

    public int WeakMissing => TrimChecks.Count(c => c.Missing && c.Weight < 3);

    public int CheckedCount => TrimChecks.Count(c => !c.Skipped);

    public int UnavailableItems => Groups.Sum(g => g.UnavailableCount);

    public int TotalItems => Groups.Sum(g => g.Items.Count);

    public string VerdictText => Verdict switch
    {
        TrimVerdict.Complete => "完整系统",
        TrimVerdict.MinorMissing => "有零星缺失",
        TrimVerdict.Trimmed => "疑似精简/魔改系统",
        _ => "无法判断"
    };

    public string VerdictColor => Verdict switch
    {
        TrimVerdict.Complete => Palette.OkHex,
        TrimVerdict.MinorMissing => Palette.HintHex,
        TrimVerdict.Trimmed => Palette.WarnHex,
        _ => Palette.NoneHex
    };

    public Brush VerdictBrush => Palette.Get(VerdictColor);

    public string Headline => Verdict switch
    {
        TrimVerdict.Complete => "关键系统组件齐全，判断为完整系统",
        TrimVerdict.MinorMissing => "关键组件大部分在，但有零星缺失",
        TrimVerdict.Trimmed => "多个关键组件被移除或标识被改过，符合精简/魔改镜像的特征",
        _ => "权限不足，无法完成判断"
    };

    public string VerdictDetail
    {
        get
        {
            var strong = MissingChecks.Where(c => c.Weight >= 3).Select(c => c.Name).ToList();
            var weak = MissingChecks.Where(c => c.Weight < 3).Select(c => c.Name).ToList();

            var head = Verdict switch
            {
                TrimVerdict.Complete =>
                    $"共检查 {CheckedCount} 项，全部正常：关键目录、系统服务、更新与安全组件、恢复环境都在。",

                TrimVerdict.MinorMissing =>
                    $"共检查 {CheckedCount} 项，{MissingChecks.Count} 项有缺失（{string.Join("、", MissingChecks.Select(c => c.Name))}）。" +
                    "这些项也可能是企业定制镜像或手工清理造成的，单凭它们不足以断定是精简版。",

                TrimVerdict.Trimmed =>
                    $"共检查 {CheckedCount} 项，其中 {StrongMissing} 项属于强证据：{string.Join("、", strong)}。" +
                    "正常安装的 Windows 不会缺这些组件——它们对应的功能正是精简镜像主要删的东西。",

                _ => "大部分检查项因权限或路径不可读而跳过，不做结论。请以管理员身份重试。"
            };

            var tail = weak.Count > 0 && Verdict != TrimVerdict.Complete
                ? $"　另有弱信号：{string.Join("、", weak.Take(5))}{(weak.Count > 5 ? " 等" : "")}。"
                : "";

            return head + tail;
        }
    }
}
