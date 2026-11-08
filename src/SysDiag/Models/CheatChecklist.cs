namespace SysDiag.Models;

/// <summary>
/// 一项检查。Result 必须区分「通过」与「没查到」（数据源不可用），
/// 否则清单会替用户把「没检查」当成「没问题」。
/// </summary>
public sealed class CheatCheck
{
    /// <summary>证据链层级：硬件 / 系统 / 运行 / 历史。</summary>
    public string Layer { get; init; } = "";

    /// <summary>检查项名称。</summary>
    public string Name { get; init; } = "";

    /// <summary>pass / watch / fail / skip。</summary>
    public string Result { get; init; } = "pass";

    /// <summary>证据摘要（数量与代表性样本）。</summary>
    public string Evidence { get; init; } = "";

    /// <summary>这项检查能说明什么（给被查人/客服看）。</summary>
    public string Why { get; init; } = "";

    public string ResultText => Result switch
    {
        "fail" => "异常",
        "watch" => "关注",
        "skip" => "未检查",
        _ => "通过"
    };

    public string ResultColor => Result switch
    {
        "fail" => Palette.WarnHex,
        "watch" => Palette.HintHex,
        "skip" => Palette.NoneHex,
        _ => Palette.OkHex
    };

    public bool IsSkip => Result == "skip";
    public bool IsFail => Result == "fail";

    /// <summary>给统一表格用的等级映射。</summary>
    public string Level => Result switch
    {
        "fail" => "warn",
        "watch" => "hint",
        "skip" => "none",
        _ => "ok"
    };
}

/// <summary>四层证据链的完整检查清单。</summary>
public sealed class CheatChecklist
{
    public List<CheatCheck> Items { get; init; } = new();

    public int PassCount => Items.Count(x => x.Result == "pass");
    public int WatchCount => Items.Count(x => x.Result == "watch");
    public int FailCount => Items.Count(x => x.Result == "fail");
    public int SkipCount => Items.Count(x => x.Result == "skip");

    public bool HasFail => FailCount > 0;

    /// <summary>一句话结论：多少项通过、多少异常、多少没查成。</summary>
    public string SummaryText =>
        $"共 {Items.Count} 项：通过 {PassCount} · 关注 {WatchCount} · 异常 {FailCount} · 未检查 {SkipCount}";

    /// <summary>层级顺序（固定，避免按字典序把「硬件」排到最后）。</summary>
    public static readonly string[] LayerOrder = { "硬件层", "系统层", "运行层", "历史层" };

    public IEnumerable<IGrouping<string, CheatCheck>> ByLayer() =>
        Items.GroupBy(x => x.Layer)
             .OrderBy(g => Array.IndexOf(LayerOrder, g.Key) is var i && i >= 0 ? i : 99);
}
