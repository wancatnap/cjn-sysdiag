namespace SysDiag.Models;

/// <summary>一个 Windows 服务（services.msc 里的一行）。</summary>
public sealed class ServiceEntry
{
    public string Name { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>Running / Stopped / StartPending / StopPending / Paused / Unknown。</summary>
    public string State { get; set; } = "";

    /// <summary>Start 注册表值的原始数字：0 引导 1 系统 2 自动 3 手动 4 禁用。</summary>
    public int StartRaw { get; set; }

    public bool DelayedAutoStart { get; set; }

    /// <summary>自动 / 自动（延迟）/ 手动 / 禁用 / 引导 / 系统。</summary>
    public string StartTypeText { get; set; } = "";

    /// <summary>登录身份：LocalSystem / NT AUTHORITY\NetworkService / 用户名。</summary>
    public string Account { get; set; } = "";

    public string ImagePath { get; set; } = "";

    public int ProcessId { get; set; }

    /// <summary>依赖的服务（逗号分隔）。</summary>
    public string Dependencies { get; set; } = "";

    public bool CanStop { get; set; }

    public bool Running => State == "Running";

    /// <summary>
    /// 状态等级：运行 = ok，已停止 = none（中性），处理中 = hint，
    /// 按用户模板 = known（不是异常，只是登录后才实例化），状态读不到 = warn。
    /// </summary>
    public string Level => State switch
    {
        "Running" => "ok",
        "StartPending" or "StopPending" or "ContinuePending" or "PausePending" => "hint",
        "Template" => "known",
        "Unknown" or "" => "warn",
        _ => "none"
    };

    public string LevelText => State switch
    {
        "Running" => "运行中",
        "Stopped" => "已停止",
        "StartPending" => "启动中",
        "StopPending" => "停止中",
        "ContinuePending" => "恢复中",
        "PausePending" => "暂停中",
        "Paused" => "已暂停",
        "Template" => "按用户启动",
        _ => "状态未知"
    };

    /// <summary>「按用户」服务的模板：SCM 里没有常驻实例，登录后按账户各实例化一份。</summary>
    public bool IsPerUserTemplate => State == "Template";
}
