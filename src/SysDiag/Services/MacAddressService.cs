using System.Management;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 网卡 MAC 的读取与改写。改写走注册表 NetworkAddress：
/// HKLM\SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002bE10318}\&lt;NNNN&gt;
/// 写入后需要重启网卡（或重启系统）才生效；删除该值即恢复硬件 MAC。
/// </summary>
public static class MacAddressService
{
    private const string ClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002bE10318}";

    /// <summary>虚拟/隧道/蓝牙等非物理网卡的关键词，命中即排除。</summary>
    private static readonly string[] VirtualWords =
    {
        "virtual", "vmware", "virtualbox", "hyper-v", "loopback", "tap-", "tap adapter",
        "tun", "wan miniport", "bluetooth", "wi-fi direct", "npcap", "vpn", "radmin",
        "zerotier", "tailscale", "wireguard", "microsoft wi-fi direct", "teredo",
        "isatap", "6to4", "pseudo"
    };

    public static bool IsAdmin => LoggingService.IsAdmin();

    /// <summary>列出物理网卡，附带注册表里的覆盖值（没有则为空）。</summary>
    public static List<MacAdapter> List()
    {
        var list = new List<MacAdapter>();
        var permanent = LoadPermanent();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsPhysical(ni)) continue;

                var sub = FindRegistryEntry(ni.Id, out var overrideMac);
                permanent.TryGetValue(ni.Id, out var perm);

                list.Add(new MacAdapter
                {
                    Id = ni.Id,
                    Name = ni.Name,
                    Description = ni.Description,
                    HardwareMac = FormatBytes(ni.GetPhysicalAddress().GetAddressBytes()),
                    PermanentMac = perm ?? "",
                    OverrideMac = overrideMac,
                    RegistrySubKey = sub,
                    Status = ni.OperationalStatus.ToString()
                });
            }
        }
        catch
        {
            // 枚举失败时返回空列表，界面会给出提示
        }

        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>从 CIM（MSFT_NetAdapter）读固件 MAC。读不到时返回空字典，不影响其他功能。</summary>
    private static Dictionary<string, string> LoadPermanent()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 优先 MSFT_NetAdapter：与 PowerShell 的 Get-NetAdapter 同源，PermanentAddress 更可靠
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\StandardCimv2"),
                new ObjectQuery("SELECT InterfaceGuid, PermanentAddress FROM MSFT_NetAdapter"));
            foreach (ManagementObject mo in searcher.Get())
            {
                var guid = mo["InterfaceGuid"] as string;
                var perm = mo["PermanentAddress"] as string;
                if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(perm)) continue;
                map[guid] = FormatHex(perm);
            }
            if (map.Count > 0) return map;
        }
        catch
        {
            // 某些系统/权限下 StandardCimv2 不可用，继续尝试旧接口
        }

        // 退回 Win32_NetworkAdapter（部分驱动这里为空）
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT GUID, PermanentAddress FROM Win32_NetworkAdapter WHERE GUID IS NOT NULL");
            foreach (ManagementObject mo in searcher.Get())
            {
                var guid = mo["GUID"] as string;
                var perm = mo["PermanentAddress"] as string;
                if (string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(perm)) continue;
                map[guid] = FormatHex(perm);
            }
        }
        catch
        {
            // 读不到就当作「固件地址未知」
        }

        return map;
    }

    /// <summary>把 "CC28AA49BF09" 这类无分隔串转成 AA-BB-... 显示。</summary>
    private static string FormatHex(string raw)
    {
        var hex = new string(raw.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return raw;
        return string.Join("-", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
    }

    private static bool IsPhysical(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet
            && ni.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet
            && ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211)
            return false;

        // 必须有真实的 GUID 标识（虚拟网卡常是 {GUID} 也算，但描述会命中关键词）
        if (string.IsNullOrWhiteSpace(ni.Id)) return false;

        var text = (ni.Name + " " + ni.Description).ToLowerInvariant();
        return !VirtualWords.Any(w => text.Contains(w));
    }

    /// <summary>在 Class 键下按 NetCfgInstanceId 找对应子键，并读出 NetworkAddress。</summary>
    private static string FindRegistryEntry(string id, out string overrideMac)
    {
        overrideMac = "";
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(ClassKey);
            if (root == null) return "";

            foreach (var sub in root.GetSubKeyNames())
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) continue;

                var instance = k.GetValue("NetCfgInstanceId") as string;
                if (!string.Equals(instance, id, StringComparison.OrdinalIgnoreCase)) continue;

                overrideMac = Normalize(k.GetValue("NetworkAddress") as string) ?? "";
                return sub;
            }
        }
        catch
        {
            // 无权限读取时静默返回空，界面会提示需要管理员
        }

        return "";
    }

    /// <summary>写入自定义 MAC。返回 (是否成功, 说明)。</summary>
    public static (bool Ok, string Message) Set(MacAdapter adapter, string mac)
    {
        var normalized = Normalize(mac);
        if (normalized == null)
            return (false, "MAC 格式不对：需要 12 位十六进制，例如 02AABBCCDDEE（可带 - 或 :）");

        if (string.IsNullOrEmpty(adapter.RegistrySubKey))
            return (false, "没找到这块网卡的注册表项，可能是虚拟网卡或系统版本差异。");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ClassKey + "\\" + adapter.RegistrySubKey, writable: true);
            if (key == null) return (false, "注册表项打不开，需要以管理员身份运行。");

            key.SetValue("NetworkAddress", normalized, RegistryValueKind.String);
            return (true,
                $"已写入 {normalized}。需要重启网卡（禁用再启用）或重启电脑才生效；" +
                $"固件地址 {adapter.FirmwareMac} 不会变，随时可以用「恢复网卡 MAC」还原。");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限：请点右下角「以管理员身份重启」后再写入。");
        }
        catch (Exception ex)
        {
            return (false, "写入失败：" + ex.Message);
        }
    }

    /// <summary>删除 NetworkAddress，恢复硬件 MAC。</summary>
    public static (bool Ok, string Message) Restore(MacAdapter adapter)
    {
        if (string.IsNullOrEmpty(adapter.RegistrySubKey))
            return (false, "没找到这块网卡的注册表项。");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                ClassKey + "\\" + adapter.RegistrySubKey, writable: true);
            if (key == null) return (false, "注册表项打不开，需要以管理员身份运行。");

            var existed = key.GetValue("NetworkAddress") != null;
            if (existed) key.DeleteValue("NetworkAddress", throwOnMissingValue: false);

            return (true, existed
                ? $"注册表已清理。但网卡驱动仍在用旧地址，需要重启网卡（禁用再启用）或重启电脑，才会换回固件地址 {adapter.FirmwareMac}。"
                : "这块网卡本来就没有自定义 MAC，无需恢复。");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限：请点右下角「以管理员身份重启」后再恢复。");
        }
        catch (Exception ex)
        {
            return (false, "恢复失败：" + ex.Message);
        }
    }

    /// <summary>规范成 12 位大写十六进制；非法返回 null。全 0 / 全 F 视为非法。</summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var hex = new string(raw.Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (hex.Length != 12) return null;
        if (hex.All(c => c == '0') || hex.All(c => c == 'F')) return null;

        return hex;
    }

    /// <summary>随机 MAC：首字节设为本地管理 + 单播（0x02），避免撞厂商前缀。</summary>
    public static string Random()
    {
        var bytes = new byte[6];
        System.Random.Shared.NextBytes(bytes);
        bytes[0] = (byte)((bytes[0] & 0xFC) | 0x02);
        return Convert.ToHexString(bytes);
    }

    /// <summary>AA-BB-CC-DD-EE-FF 形式，仅用于显示。</summary>
    public static string FormatBytes(byte[] bytes)
        => bytes.Length == 0 ? "（无）" : string.Join("-", bytes.Select(b => b.ToString("X2")));

    /// <summary>把 12 位十六进制转成显示格式。</summary>
    public static string Pretty(string mac)
    {
        var n = Normalize(mac);
        if (n == null) return mac ?? "";
        return string.Join("-", Enumerable.Range(0, 6).Select(i => n.Substring(i * 2, 2)));
    }
}
