namespace SysDiag.Models;

/// <summary>一个 DNS 服务商方案。</summary>
public sealed class DnsPreset
{
    public string Name { get; init; } = "";
    public string Primary { get; init; } = "";
    public string Secondary { get; init; } = "";
    public string Note { get; init; } = "";
    public bool IsCurrent { get; init; }

    public string IpHint => string.IsNullOrEmpty(Secondary) ? Primary : $"{Primary} / {Secondary}";

    public override string ToString() => Name;
}

/// <summary>一次测速结果。</summary>
public sealed class DnsProbeRow
{
    public DnsPreset Preset { get; init; } = new();

    /// <summary>最小往返毫秒；-1 表示全部超时。</summary>
    public int LatencyMs { get; set; } = -1;

    public string Name => Preset.Name;
    public string IpHint => Preset.IpHint;
    public string Note => Preset.Note;

    public bool Ok => LatencyMs >= 0;

    public string LatencyText => LatencyMs < 0
        ? "超时"
        : LatencyMs == 0 ? "<1 ms" : LatencyMs + " ms";

    /// <summary>延迟评级：快的绿、中的黄、慢的红。</summary>
    public string Grade => LatencyMs switch
    {
        < 0 => "timeout",
        <= 30 => "fast",
        <= 80 => "mid",
        _ => "slow"
    };

    public bool IsRecommended { get; set; }

    public string TagText => IsRecommended ? "推荐" : "";

    /// <summary>延迟文字颜色，供界面直接绑定。</summary>
    public string GradeColor => Grade switch
    {
        "fast" => "#2F9E44",
        "mid" => "#B08900",
        "slow" => "#C92A2A",
        _ => "#868E96"
    };

    public string GradeBg => Grade switch
    {
        "fast" => "#EBFBEE",
        "mid" => "#FFF9DB",
        "slow" => "#FFF5F5",
        _ => "#F1F3F5"
    };
}

/// <summary>一块可改 DNS 的网卡。</summary>
public sealed class DnsAdapter
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Id { get; init; } = "";
    public List<string> CurrentDns { get; init; } = new();
    public bool IsDhcp { get; init; }
    public bool Selected { get; set; }

    public string DnsText => CurrentDns.Count == 0
        ? (IsDhcp ? "自动获取（未取到）" : "未设置")
        : string.Join(" / ", CurrentDns);

    public string ModeText => IsDhcp ? "自动" : "手动";

    public string Detail => $"{Description}　·　{DnsText}";
}

/// <summary>优化前的 DNS 备份，用于「恢复到优化前」。</summary>
public sealed class DnsBackup
{
    public DateTime Time { get; set; } = DateTime.Now;
    public List<DnsBackupEntry> Entries { get; set; } = new();
}

public sealed class DnsBackupEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool WasDhcp { get; set; }
    public List<string> Dns { get; set; } = new();
}
