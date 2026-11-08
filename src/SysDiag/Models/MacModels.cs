namespace SysDiag.Models;

/// <summary>一块物理网卡，以及它在注册表里的 MAC 覆盖值。</summary>
public sealed class MacAdapter
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string HardwareMac { get; init; } = "";
    public string PermanentMac { get; init; } = "";
    public string OverrideMac { get; set; } = "";
    public string RegistrySubKey { get; init; } = "";
    public string Status { get; init; } = "";

    public bool HasOverride => !string.IsNullOrEmpty(OverrideMac);

    public string OverrideText => HasOverride ? OverrideMac : "（未修改）";

    public string HardwareText => "当前 " + HardwareMac;

    public string PermanentText => "固件 " + (string.IsNullOrEmpty(PermanentMac) ? "（读不到）" : PermanentMac);

    /// <summary>提示用：优先固件地址，读不到时退回当前地址。</summary>
    public string FirmwareMac => string.IsNullOrEmpty(PermanentMac) ? HardwareMac : PermanentMac;

    public string OverrideLabel => HasOverride ? "自定义：" + OverrideMac : "未修改";

    public string Summary => $"{Name} · {Description}";
}
