namespace SysDiag.Models;

/// <summary>网络链路的一项体检结果。</summary>
public sealed class NetCheckItem
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";

    /// <summary>ok / warn / bad</summary>
    public string Level { get; init; } = "ok";

    public string FixHint { get; init; } = "";

    public bool HasFixHint => !string.IsNullOrEmpty(FixHint);

    public string LevelText => Level switch
    {
        "bad" => "异常",
        "warn" => "注意",
        _ => "正常"
    };

    public string LevelColor => Level switch
    {
        "bad" => "#C92A2A",
        "warn" => "#B08900",
        _ => "#2F9E44"
    };

    public string LevelBg => Level switch
    {
        "bad" => "#FFF5F5",
        "warn" => "#FFF9DB",
        _ => "#EBFBEE"
    };
}

/// <summary>一种网络修复动作。</summary>
public enum NetFixKind
{
    FlushDns,
    ResetWinsock,
    ResetTcpIp,
    RenewDhcp,
    ResetProxy,
    RestoreHosts,
    ResetFirewall
}

/// <summary>修复步骤。</summary>
public sealed class NetFixStep
{
    public NetFixKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Effect { get; init; } = "";
    public string Command { get; init; } = "";
    public bool NeedsRestart { get; init; }
    public bool NeedsAdmin { get; init; } = true;
    public bool Selected { get; set; }

    public string Tag => NeedsRestart ? "需重启" : "";
}

/// <summary>一步的执行结果。</summary>
public sealed class NetFixResult
{
    public string Name { get; init; } = "";
    public bool Ok { get; init; }
    public bool Skipped { get; init; }
    public string Message { get; init; } = "";

    public string Line => (Skipped ? "跳过" : Ok ? "完成" : "失败") + " · " + Name +
                          (string.IsNullOrEmpty(Message) ? "" : "：" + Message);
}
