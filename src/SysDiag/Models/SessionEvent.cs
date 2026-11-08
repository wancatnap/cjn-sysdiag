namespace SysDiag.Models;

/// <summary>同一时间窗内本机发生的系统事件（用于与游戏日志做时间关联）。</summary>
public sealed class SessionEvent
{
    public DateTime Time { get; set; }
    public string Category { get; set; } = "";
    public string Source { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public int Count { get; set; } = 1;
    public string Level { get; set; } = "";
    public bool Relevant { get; set; }

    public string CssClass => Level switch
    {
        "错误" => "t-bad",
        "警告" => "t-warn",
        _ => "t-bin"
    };

    public string TimeText => Time.ToString("MM-dd HH:mm:ss");
}
