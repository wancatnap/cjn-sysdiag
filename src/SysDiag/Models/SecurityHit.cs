namespace SysDiag.Models;

/// <summary>一条安全相关线索。注意：只是日志里出现过的痕迹，不等同于作弊事实。</summary>
public sealed class SecurityHit
{
    public string Category { get; set; } = "";
    public string Indicator { get; set; } = "";
    public string File { get; set; } = "";
    public int Line { get; set; }
    public string Content { get; set; } = "";
    /// <summary>强 / 中 / 弱 —— 表示这条线索的可信程度，不是处罚依据。</summary>
    public string Strength { get; set; } = "弱";

    public string CssClass => Strength switch
    {
        "强" => "t-bad",
        "中" => "t-warn",
        _ => "t-bin"
    };
}
