namespace SysDiag.Models;

/// <summary>一块显示适配器：WMI 信息 + 注册表显示名。</summary>
public sealed class GpuAdapter
{
    public string Name { get; init; } = "";
    public string DriverDesc { get; init; } = "";
    public string DriverVersion { get; init; } = "";
    public string DriverDate { get; init; } = "";
    public string Memory { get; init; } = "";
    public string Status { get; init; } = "";
    public string PnpId { get; init; } = "";
    public string RegistrySubKey { get; init; } = "";

    public bool CanRename => !string.IsNullOrEmpty(RegistrySubKey);

    public string DisplayName => string.IsNullOrEmpty(DriverDesc) ? Name : DriverDesc;

    public string Detail => string.Join(" · ", new[] { DriverVersion, DriverDate, Memory, Status }
        .Where(s => !string.IsNullOrWhiteSpace(s)));

    public string RegistryText => CanRename
        ? $@"Class\{{4D36E968-...}}\{RegistrySubKey}"
        : "未找到注册表项";
}
