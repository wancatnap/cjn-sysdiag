using System.Management;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>网卡清单（「网络连接」的软件内版本）。</summary>
public static class AdapterInventoryService
{
    public sealed class AdapterEntry
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string Mac { get; set; } = "";
        public string Type { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Enabled { get; set; }
        public string Ipv4 { get; set; } = "";
        public string Ipv6 { get; set; } = "";
        public string Gateway { get; set; } = "";
        public string Dns { get; set; } = "";
        public bool Dhcp { get; set; }
        public string Speed { get; set; } = "";
        public string Traffic { get; set; } = "";
        public string Index { get; set; } = "";

        public bool Up => Status is "Up" or "已连接";

        public string Level => !Enabled ? "none" : Up ? "ok" : "hint";

        public string LevelText => !Enabled ? "已禁用" : Up ? "已连接" : "未连接";
    }

    public static List<AdapterEntry> Scan()
    {
        var list = new List<AdapterEntry>();

        // 物理网卡由 WMI 给出（虚拟网卡在这层就被过滤掉），
        // 流量与链路状态由 NetworkInterface 补，两边按名称对齐。
        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToList();

        // PhysicalAdapter 这个字段并不可靠：Radmin VPN、TAP 之类的虚拟网卡也会报 TRUE。
        // 再按 PNPDeviceID 与名称过一遍，界面标题里承诺了「虚拟网卡已过滤」，就得做到。
        var adapters = EvidenceService.Wmi(
                "SELECT Name, NetConnectionID, MACAddress, NetEnabled, Speed, InterfaceIndex, AdapterType, PNPDeviceID " +
                "FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE")
            .Where(a => !IsVirtualAdapter(
                EvidenceService.Str(a, "NetConnectionID"),
                EvidenceService.Str(a, "Name"),
                EvidenceService.Str(a, "PNPDeviceID")))
            .ToList();

        foreach (var a in adapters)
        {
            var name = EvidenceService.Str(a, "NetConnectionID");
            var wmiName = EvidenceService.Str(a, "Name");
            var mac = EvidenceService.Str(a, "MACAddress");
            var enabled = EvidenceService.Str(a, "NetEnabled");

            if (name.Length == 0 && wmiName.Length == 0) continue;

            var nic = nics.FirstOrDefault(n =>
                          n.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ??
                      nics.FirstOrDefault(n => SameMac(n.GetPhysicalAddress().ToString(), mac));

            var entry = new AdapterEntry
            {
                Name = name.Length > 0 ? name : wmiName,
                Description = wmiName,
                Mac = FormatMac(mac.Length > 0 ? mac : nic?.GetPhysicalAddress().ToString() ?? ""),
                Enabled = enabled.Equals("True", StringComparison.OrdinalIgnoreCase),
                Index = EvidenceService.Str(a, "InterfaceIndex"),
                Status = nic?.OperationalStatus.ToString() ?? (enabled.Equals("True", StringComparison.OrdinalIgnoreCase) ? "Unknown" : "Disabled"),
                Type = TypeText(nic?.NetworkInterfaceType, EvidenceService.Str(a, "AdapterType"))
            };

            if (nic != null)
            {
                try
                {
                    var props = nic.GetIPProperties();

                    entry.Ipv4 = string.Join("，", props.UnicastAddresses
                        .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(x => x.Address.ToString()));

                    entry.Ipv6 = string.Join("，", props.UnicastAddresses
                        .Where(x => x.Address.AddressFamily == AddressFamily.InterNetworkV6 && !x.Address.IsIPv6LinkLocal)
                        .Select(x => x.Address.ToString()));

                    entry.Gateway = string.Join("，", props.GatewayAddresses
                        .Select(x => x.Address.ToString()));

                    entry.Dns = string.Join("，", props.DnsAddresses
                        .Where(x => x.AddressFamily == AddressFamily.InterNetwork)
                        .Select(x => x.Address.ToString()));

                    entry.Dhcp = props.DhcpServerAddresses.Count > 0;

                    if (nic.Speed > 0)
                        entry.Speed = nic.Speed >= 1_000_000_000
                            ? $"{nic.Speed / 1_000_000_000.0:0.#} Gbps"
                            : $"{nic.Speed / 1_000_000.0:0.#} Mbps";

                    var stats = nic.GetIPStatistics();
                    entry.Traffic = $"↓ {Bytes(stats.BytesReceived)}　↑ {Bytes(stats.BytesSent)}";
                }
                catch { /* 网卡刚被拔出时读属性会抛 */ }
            }

            list.Add(entry);
        }

        return list
            .OrderByDescending(x => x.Up)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>虚拟网卡识别：ROOT 枚举 + 常见虚拟/VPN 驱动关键字。</summary>
    private static bool IsVirtualAdapter(string name, string description, string pnpId)
    {
        if (pnpId.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)) return true;
        if (pnpId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)) return true;

        var text = (name + " " + description).ToLowerInvariant();

        string[] words =
        {
            "vpn", "tap-", "tap adapter", "tun ", "virtual", "loopback", "hyper-v",
            "vmware", "virtualbox", "wan miniport", "npcap", "wireguard", "zerotier",
            "tailscale", "radmin", "hamachi", "openvpn", "sangfor", "easyconnect"
        };

        return words.Any(w => text.Contains(w, StringComparison.Ordinal));
    }

    private static bool SameMac(string a, string b)
        => a.Length > 0 && b.Length > 0 &&
           a.Replace("-", "").Replace(":", "").Equals(b.Replace("-", "").Replace(":", ""),
               StringComparison.OrdinalIgnoreCase);

    private static string FormatMac(string raw)
    {
        var hex = raw.Replace("-", "").Replace(":", "").Trim();
        if (hex.Length != 12) return raw;

        return string.Join("-", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    private static string TypeText(NetworkInterfaceType? type, string adapterType)
    {
        if (adapterType.Length > 0 && adapterType.Contains("Wireless", StringComparison.OrdinalIgnoreCase))
            return "无线";

        return type switch
        {
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet => "以太网",
            NetworkInterfaceType.Wireless80211 => "无线",
            NetworkInterfaceType.Ppp => "拨号",
            NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => "移动网络",
            null => "未知",
            _ => type.ToString()
        };
    }

    private static string Bytes(long value) => value switch
    {
        >= 1L << 30 => $"{value / 1024.0 / 1024 / 1024:0.##} GB",
        >= 1L << 20 => $"{value / 1024.0 / 1024:0.#} MB",
        >= 1024 => $"{value / 1024.0:0.#} KB",
        _ => value + " B"
    };

    /// <summary>启用 / 禁用网卡（WMI 方法，需要管理员）。</summary>
    public static (bool Ok, string Message) SetEnabled(AdapterEntry entry, bool enable)
    {
        if (!ElevationService.IsAdmin)
            return (false, "启用或禁用网卡需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        if (entry.Name.Length == 0)
            return (false, "这块网卡没有连接名称，无法定位。");

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT * FROM Win32_NetworkAdapter WHERE NetConnectionID = '{entry.Name.Replace("'", "''")}'");

            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var result = mo.InvokeMethod(enable ? "Enable" : "Disable", null, null);

                var code = result?["ReturnValue"]?.ToString() ?? "?";

                return code == "0"
                    ? (true, $"已{(enable ? "启用" : "禁用")} {entry.Name}。")
                    : (false, $"操作返回代码 {code}（0 才是成功）。这块网卡可能被厂商驱动锁定。");
            }

            return (false, "找不到对应的网卡对象。");
        }
        catch (Exception ex)
        {
            return (false, "操作失败：" + ex.Message);
        }
    }
}
