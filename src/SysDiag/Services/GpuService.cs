using System.Management;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 显卡检测与显示名修改。
/// 改名只写注册表 Class\{4D36E968-...}\&lt;NNNN&gt;\DriverDesc（设备管理器里显示的名字），
/// 不改驱动、不改性能；需要管理员，改坏了可以一键恢复。
/// </summary>
public static class GpuService
{
    private const string DisplayClass =
        @"SYSTEM\CurrentControlSet\Control\Class\{4D36E968-E325-11CE-BFC1-08002BE10318}";

    public static bool IsAdmin => LoggingService.IsAdmin();

    public static List<GpuAdapter> List()
    {
        var result = new List<GpuAdapter>();
        var regs = ReadRegistryEntries();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, DriverDate, AdapterRAM, Status, PNPDeviceID FROM Win32_VideoController");

            foreach (ManagementObject mo in searcher.Get())
            {
                var name = (mo["Name"] as string ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                var pnp = mo["PNPDeviceID"] as string ?? "";
                var version = mo["DriverVersion"] as string ?? "";
                var date = "";
                try
                {
                    var raw = mo["DriverDate"] as string;
                    if (!string.IsNullOrEmpty(raw))
                        date = ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd");
                }
                catch { }

                var memory = "";
                try
                {
                    var ram = Convert.ToUInt64(mo["AdapterRAM"] ?? 0UL);
                    if (ram > 0) memory = $"{ram / 1024.0 / 1024.0 / 1024.0:0.#} GB 显存";
                }
                catch { }

                var status = mo["Status"] as string ?? "";
                var match = regs.FirstOrDefault(r =>
                    string.Equals(r.DriverDesc, name, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(r.DriverDesc, StringComparison.OrdinalIgnoreCase)
                    || r.DriverDesc.Contains(name, StringComparison.OrdinalIgnoreCase));

                result.Add(new GpuAdapter
                {
                    Name = name,
                    DriverDesc = match.DriverDesc ?? "",
                    DriverVersion = string.IsNullOrWhiteSpace(version) ? "" : "驱动 " + version,
                    DriverDate = date,
                    Memory = memory,
                    Status = status switch
                    {
                        "OK" => "状态正常",
                        "" => "",
                        _ => status
                    },
                    PnpId = pnp,
                    RegistrySubKey = match.SubKey ?? ""
                });
            }
        }
        catch
        {
            // WMI 不可用时返回已读到的部分
        }

        return result;
    }

    private static List<(string SubKey, string DriverDesc)> ReadRegistryEntries()
    {
        var list = new List<(string, string)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClass);
            if (root == null) return list;

            foreach (var sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key == null) continue;

                var desc = (key.GetValue("DriverDesc") as string ?? "").Trim();
                if (string.IsNullOrWhiteSpace(desc)) continue;

                list.Add((sub, desc));
            }
        }
        catch { }

        return list;
    }

    /// <summary>把设备管理器里的显示名改成 newName。</summary>
    public static (bool Ok, string Message) Rename(GpuAdapter gpu, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return (false, "名称不能为空。");

        newName = newName.Trim();
        if (newName.Length > 64)
            return (false, "名称太长（最多 64 个字符）。");

        if (string.IsNullOrEmpty(gpu.RegistrySubKey))
            return (false, "没找到这块显卡的注册表项，无法改名。");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                DisplayClass + "\\" + gpu.RegistrySubKey, writable: true);
            if (key == null) return (false, "注册表项打不开，需要以管理员身份运行。");

            key.SetValue("DriverDesc", newName, RegistryValueKind.String);

            return (true,
                $"已把显示名改为「{newName}」。\n\n" +
                "在设备管理器里按 F5 刷新，或重启后生效。\n" +
                $"想还原就点「恢复原名」，会写回「{gpu.Name}」。");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限：请点右下角「以管理员身份重启」后再试。");
        }
        catch (Exception ex)
        {
            return (false, "改名失败：" + ex.Message);
        }
    }

    /// <summary>恢复成 WMI 报告的原始名称。</summary>
    public static (bool Ok, string Message) Restore(GpuAdapter gpu)
    {
        if (string.IsNullOrEmpty(gpu.RegistrySubKey))
            return (false, "没找到这块显卡的注册表项。");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                DisplayClass + "\\" + gpu.RegistrySubKey, writable: true);
            if (key == null) return (false, "注册表项打不开，需要以管理员身份运行。");

            key.SetValue("DriverDesc", gpu.Name, RegistryValueKind.String);
            return (true, $"已恢复显示名为「{gpu.Name}」，刷新设备管理器或重启后生效。");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限：请点右下角「以管理员身份重启」后再试。");
        }
        catch (Exception ex)
        {
            return (false, "恢复失败：" + ex.Message);
        }
    }
}
