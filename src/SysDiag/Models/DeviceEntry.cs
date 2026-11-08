namespace SysDiag.Models;

/// <summary>一个即插即用设备（设备管理器里的一行）。</summary>
public sealed class DeviceEntry
{
    public string Name { get; set; } = "";

    /// <summary>设备实例 ID，如 PCI\VEN_10DE&amp;DEV_2482\4&amp;1A2B3C&amp;0&amp;0008。</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>设备类别中文名（显示适配器 / 网络适配器 / 键盘…）。</summary>
    public string Category { get; set; } = "";

    /// <summary>WMI 的 PNPClass（Display / Net / Keyboard…），英文，用于统计。</summary>
    public string PnpClass { get; set; } = "";

    public string Manufacturer { get; set; } = "";

    /// <summary>ConfigManagerErrorCode：0 = 正常，22 = 已禁用，28 = 未装驱动，其余多为故障。</summary>
    public int ErrorCode { get; set; }

    public string Status { get; set; } = "";

    public string ProblemText { get; set; } = "";

    public string DriverVersion { get; set; } = "";

    public string DriverProvider { get; set; } = "";

    public string DriverDate { get; set; } = "";

    public string InfName { get; set; } = "";

    /// <summary>服务的 ServiceName（内核驱动名），可用来关联「服务」页。</summary>
    public string ServiceName { get; set; } = "";

    /// <summary>正常 / 已禁用 / 故障 / 未装驱动。</summary>
    public bool HasProblem => ErrorCode != 0 && ErrorCode != 22 && ErrorCode != 45;

    public bool IsDisabled => ErrorCode == 22;

    public string Level => HasProblem ? "warn" : IsDisabled ? "hint" : "ok";

    public string LevelText => HasProblem ? "故障" : IsDisabled ? "已禁用" : "正常";
}
