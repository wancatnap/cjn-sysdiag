namespace SysDiag.Models;

/// <summary>机器码伪造检测的结论档位。</summary>
public enum HwidVerdict
{
    /// <summary>没有命中任何可疑项。</summary>
    Clean,

    /// <summary>命中少量弱信号，多数属于「厂商没写值」这类正常情况。</summary>
    Notice,

    /// <summary>命中较强信号，需要人工核实。</summary>
    Suspicious,

    /// <summary>命中「改机/伪造」的硬信号。</summary>
    Alarming
}

/// <summary>
/// 一项不依赖出厂基线的检查。
///
/// 权重沿用 VMAware 的评分思路（可靠度 + 特异性，再按误报概率打折），
/// 数值落在 0~30，命中即累加；<see cref="Triggered"/> 为 false 时权重必须为 0。
/// </summary>
public sealed class HwidCheck
{
    /// <summary>稳定标识，自检与报告用它做锚点。</summary>
    public string Id { get; init; } = "";

    /// <summary>检查项名称（表格主标题）。</summary>
    public string Title { get; init; } = "";

    /// <summary>分类：标识符 / 磁盘 / 网络 / 内核 / 环境。</summary>
    public string Category { get; init; } = "";

    /// <summary>结论说明（命中时的证据，或通过时的现状）。</summary>
    public string Detail { get; init; } = "";

    /// <summary>依据来源（表名 / 注册表路径 / 命令）。</summary>
    public string Source { get; init; } = "";

    /// <summary>命中时的风险分；未命中一律 0。</summary>
    public int Weight { get; init; }

    public bool Triggered { get; init; }

    /// <summary>没跑成（如需要管理员权限），既不算通过也不算命中。</summary>
    public bool Skipped { get; init; }

    public string Level => Skipped ? "none" : !Triggered ? "ok" : Weight >= 22 ? "warn" : "hint";

    public string LevelText => Skipped ? "未检查" : !Triggered ? "通过" : Weight >= 22 ? "可疑" : "提示";

    public string ScoreText => Skipped ? "—" : Triggered ? $"+{Weight}" : "0";
}

/// <summary>一次机器码伪造检测的完整结果。</summary>
public sealed class HwidIntegrityResult
{
    public List<HwidCheck> Checks { get; init; } = new();

    /// <summary>累计风险分（0~100，命中权重直接相加后封顶）。</summary>
    public int Score { get; init; }

    /// <summary>是否以管理员身份运行（决定 bcdedit 那类检查能不能跑）。</summary>
    public bool Admin { get; init; }

    public int HitCount => Checks.Count(c => c.Triggered);

    public int WeakCount => Checks.Count(c => c.Triggered && c.Weight < 22);

    public int StrongCount => Checks.Count(c => c.Triggered && c.Weight >= 22);

    public int CheckedCount => Checks.Count(c => !c.Skipped);

    public int SkippedCount => Checks.Count(c => c.Skipped);

    /// <summary>
    /// 分数 → 结论。阈值刻意调得保守：单条弱信号（+8 ~ +16）不足以升级到「需留意」，
    /// 因为真机上「厂商没写序列号」「换过硬盘」太常见，误伤代价比漏报大。
    /// </summary>
    public HwidVerdict Verdict => Score switch
    {
        < 10 => HwidVerdict.Clean,
        < 24 => HwidVerdict.Notice,
        < 55 => HwidVerdict.Suspicious,
        _ => HwidVerdict.Alarming
    };

    public string VerdictText => Verdict switch
    {
        HwidVerdict.Clean => "未见改机迹象",
        HwidVerdict.Notice => "有弱信号，需留意",
        HwidVerdict.Suspicious => "存在可疑改动",
        _ => "高度疑似被改过"
    };

    public string VerdictColor => Verdict switch
    {
        HwidVerdict.Clean => Palette.OkHex,
        HwidVerdict.Notice => Palette.HintHex,
        HwidVerdict.Suspicious => Palette.WarnHex,
        _ => Palette.WarnHex
    };

    public string Headline => Verdict switch
    {
        HwidVerdict.Clean => "没有发现改机 / 伪造的痕迹",
        HwidVerdict.Notice => "有少量弱信号，多数属于厂商未写入的正常情况",
        HwidVerdict.Suspicious => "命中较多异常，建议结合原始值人工核实",
        _ => "命中改机的硬信号，这台机器的标识符不可信"
    };

    public string Detail
    {
        get
        {
            var hit = Checks.Where(c => c.Triggered).Select(c => c.Title).ToList();
            var tail = SkippedCount > 0
                ? $"另有 {SkippedCount} 项因权限不足没跑成（点右上角「以管理员身份重启」可补全）。"
                : "";

            return Verdict switch
            {
                HwidVerdict.Clean when HitCount == 0 =>
                    $"共检查 {CheckedCount} 项，全部通过：标识符格式合法、没有重复值、内核环境没有被改造的迹象。" + tail,

                HwidVerdict.Clean =>
                    $"共检查 {CheckedCount} 项，命中 {HitCount} 项弱信号：{string.Join("、", hit)}。" +
                    $"合计 {Score} 分，未达到关注阈值（10 分）——这类情况在真机上很常见。" + tail,

                HwidVerdict.Notice =>
                    $"共检查 {CheckedCount} 项，命中 {HitCount} 项弱信号：{string.Join("、", hit)}。" +
                    "这类信号在低端主板、USB 网卡、二手硬盘上很常见，通常不构成「改过机器码」的证据，建议往上再看强信号项。" + tail,

                _ =>
                    $"共检查 {CheckedCount} 项，命中 {HitCount} 项（强信号 {StrongCount} 项）：{string.Join("、", hit)}。" +
                    "强信号一般意味着机器标识被程序化改写过，或内核环境被改造成可加载未签名驱动。" + tail
            };
        }
    }
}
