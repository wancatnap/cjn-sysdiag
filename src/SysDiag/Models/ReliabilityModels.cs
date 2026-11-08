namespace SysDiag.Models;

/// <summary>可靠性记录的一条（崩溃 / 挂起 / 服务失败 / 安装 / 更新）。</summary>
public sealed class ReliabilityRecord
{
    public DateTime Time { get; set; }

    /// <summary>来源，如 Application Error / Service Control Manager / Microsoft-Windows-UserPnp。</summary>
    public string Source { get; set; } = "";

    public int EventId { get; set; }

    /// <summary>涉及的产品/程序名。</summary>
    public string Product { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>崩溃 / 挂起 / 服务失败 / 设备安装 / 软件安装 / 更新 / 其他。</summary>
    public string Category { get; set; } = "其他";

    /// <summary>warn / hint / ok。</summary>
    public string Level { get; set; } = "ok";

    public string LevelText => Level switch
    {
        "warn" => "异常",
        "hint" => "变更",
        _ => "记录"
    };
}

/// <summary>某一天的稳定性指数（0~10，10 最好），可靠性监视器上方那排柱子就是它。</summary>
public sealed class StabilityDay
{
    public DateTime Day { get; set; }

    /// <summary>当天平均稳定性指数。</summary>
    public double Index { get; set; }

    /// <summary>当天最低指数（小时级）。</summary>
    public double Min { get; set; }

    /// <summary>当天异常记录条数。</summary>
    public int ProblemCount { get; set; }

    public string Level => Index >= 8 ? "ok" : Index >= 5 ? "hint" : "warn";

    public string LevelText => Index >= 8 ? "稳定" : Index >= 5 ? "有波动" : "不稳定";
}
