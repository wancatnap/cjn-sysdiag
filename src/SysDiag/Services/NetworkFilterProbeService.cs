using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 网络层过滤驱动清点（WFP / NDIS 轻量过滤）。
///
/// 为什么查这个：用户提到的「更隐蔽的网络层手段」在 Windows 上主要是两套机制——
/// <list type="bullet">
/// <item><b>WFP（Windows Filtering Platform）</b>：内核里的包过滤框架，驱动可以注册 callout
/// 在任意层拦截、改写、复制数据包。杀软、防火墙、VPN 都用它，外挂也用它。</item>
/// <item><b>NDIS 轻量过滤驱动（LWF）</b>：挂在网卡协议栈上的过滤层，能看全部收发流量。</item>
/// </list>
///
/// 外挂用它做什么：拦截游戏客户端与服务端的通信做「透视」、改包、或把作弊进程的流量
/// 混进正常流量里。这类手段比进程注入隐蔽，因为**游戏进程本身没有被碰过**。
///
/// 但必须说清楚：**这一项只能清点，不能判定**。VPN、杀软、虚拟机、抓包工具、
/// 甚至某些驱动的「网络加速」功能都会装第三方过滤驱动。所以判 hint，不判 warn。
/// 真正能定罪的是「某个过滤驱动的签名者不明 + 只在游戏运行时加载」，那需要内核态与时间关联，
/// 本程序不做。
/// </summary>
public static class NetworkFilterProbeService
{
    public sealed class Binding
    {
        public string AdapterName { get; init; } = "";
        public string ComponentId { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool Enabled { get; init; }

        /// <summary>微软自带（ComponentID 以 ms_ 开头）。</summary>
        public bool IsMicrosoft =>
            ComponentId.StartsWith("ms_", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 列出所有网卡绑定组件。优先用 WMI 的 MSFT_NetAdapterBinding（Get-NetAdapterBinding 的底层），
    /// 读不到返回空表——不猜。
    /// </summary>
    public static List<Binding> ReadBindings()
    {
        var list = new List<Binding>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\StandardCimv2"),
                new ObjectQuery("SELECT Name, ComponentID, DisplayName, Enabled FROM MSFT_NetAdapterBinding"));

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var component = mo["ComponentID"]?.ToString() ?? "";
                    if (component.Length == 0) continue;

                    list.Add(new Binding
                    {
                        AdapterName = mo["Name"]?.ToString() ?? "",
                        ComponentId = component,
                        DisplayName = mo["DisplayName"]?.ToString() ?? component,
                        Enabled = mo["Enabled"] is bool b && b
                    });
                }
            }
        }
        catch
        {
            // MSFT_NetAdapterBinding 在精简系统或旧版本上可能不存在
        }

        return list;
    }

    /// <summary>
    /// 生成体检发现。只有「同一个第三方组件绑定在多个网卡上且处于启用」时才算提示，
    /// 单个第三方过滤驱动太常见（VPN 一个就够），不足以当信号。
    /// </summary>
    public static List<CheatFinding> Probe()
    {
        var findings = new List<CheatFinding>();

        // 先算这几项：hosts / 代理 / 抓包 / DNS 与「网卡上绑了哪些过滤驱动」完全无关，
        // 不能因为读不到绑定就把它们一起跳过（本机实测 MSFT_NetAdapterBinding 返回空，
        // 初版就是在这里提前 return，导致四类隐蔽手法一条结论都没有）。
        var stealth = ProbeStealth();

        var bindings = ReadBindings();

        if (bindings.Count == 0)
        {
            findings.Add(new CheatFinding
            {
                Group = "网络过滤层",
                Title = "读不到网卡绑定信息",
                Detail = "MSFT_NetAdapterBinding 查询为空。这个类在精简系统或很旧的版本上可能不存在。"
                         + "读不到不代表没有过滤驱动，只是这一项没能检查。"
                         + TrimAwareness.AttributionFor("联机服务", "网络", "WMI"),
                Level = "hint"
            });

            findings.AddRange(stealth);
            return findings;
        }

        var thirdParty = bindings
            .Where(b => !b.IsMicrosoft)
            .GroupBy(b => b.ComponentId, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Component = g.Key,
                Display = g.First().DisplayName,
                Count = g.Count(),
                Enabled = g.Count(x => x.Enabled)
            })
            .OrderByDescending(x => x.Enabled)
            .ThenByDescending(x => x.Count)
            .ToList();

        if (thirdParty.Count == 0)
        {
            findings.Add(new CheatFinding
            {
                Group = "网络过滤层",
                Title = "没有第三方网络过滤驱动",
                Detail = $"检查了 {bindings.Count} 个网卡绑定组件，全部是 Windows 自带的协议与过滤层。"
                         + "外挂若想拦截或改写游戏数据包，通常要在这里挂一个自己的驱动。",
                Level = "ok"
            });

            findings.AddRange(stealth);
            return findings;
        }

        // 绑在多个网卡上、且启用的第三方组件：VPN 客户端和杀软是这个特征，但也正是 WFP 外挂的形态
        var multi = thirdParty.Where(x => x.Enabled >= 2).ToList();

        var summary = string.Join("、", thirdParty.Take(8).Select(x => $"{x.Display}（{x.Component}）"));

        findings.Add(new CheatFinding
        {
            Group = "网络过滤层",
            Title = $"存在 {thirdParty.Count} 个第三方网络过滤组件",
            Detail = "这些组件挂在内核网络栈上，能看到、也能改写进出本机的数据包。"
                     + "常见来源：VPN 客户端、杀毒/防火墙、虚拟机网卡、抓包工具（Wireshark 的 npcap）、"
                     + "以及部分「网络加速」功能。"
                     + "外挂也会用同样的位置做透视或改包，但**单看这一项无法区分**——"
                     + "所以只列出，不下结论。判断办法：看这些名字能不能对上你装过的软件。",
            Basis = summary,
            Level = "hint",
            Weight = 5
        });

        if (multi.Count > 0)
        {
            findings.Add(new CheatFinding
            {
                Group = "网络过滤层",
                Title = $"{multi.Count} 个组件同时绑在多个网卡上",
                Detail = "同时接管所有网卡是「全局拦截」的典型配置。VPN 和杀软也会这么做（它们本来就该管所有流量），"
                         + "所以这不是判据，只是把范围收窄：如果这里面有你认不出的名字，那个才值得查。",
                Basis = string.Join("、", multi.Take(5).Select(x => $"{x.Display}（{x.Enabled} 个网卡）")),
                Level = "hint",
                Weight = 3
            });
        }

        healthCheck:
        findings.AddRange(stealth);

        return findings;
    }

    // -------------------------------------------------- 无驱动也能做的隐蔽手法

    /// <summary>已知的公共 DNS。命中这里不算异常。</summary>
    private static readonly HashSet<string> KnownPublicDns = new(StringComparer.OrdinalIgnoreCase)
    {
        "8.8.8.8", "8.8.4.4",                                    // Google
        "1.1.1.1", "1.0.0.1",                                    // Cloudflare
        "9.9.9.9", "149.112.112.112",                            // Quad9
        "208.67.222.222", "208.67.220.220",                      // OpenDNS
        "114.114.114.114", "114.114.115.115",                    // 114DNS
        "223.5.5.5", "223.6.6.6",                                // 阿里
        "119.29.29.29", "182.254.116.116",                       // 腾讯
        "180.76.76.76",                                          // 百度
        "117.50.10.10", "52.80.66.66"                            // OneDNS
    };

    /// <summary>
    /// 查四类「不装驱动也能做」的网络层手法。
    ///
    /// 为什么要单独做这一层：WFP / NDIS 过滤驱动要写驱动、要签名、要过反作弊的驱动枚举，
    /// 门槛高。而下面这四样只需要改一行配置就能生效，且**游戏进程完全不受影响**：
    /// <list type="bullet">
    /// <item><b>hosts 劫持</b>：把游戏厂商的更新/心跳域名指到别处，或指到回环让它连不上。</item>
    /// <item><b>系统代理</b>：所有 HTTP(S) 流量走第三方中转，中转那边能看到请求内容。</item>
    /// <item><b>抓包能力</b>（npcap / WinPcap）：不注入进程也能看到全部网络包，是「外部雷达」的前置条件。</item>
    /// <item><b>DNS 改指向</b>：把域名解析交给自己搭的服务器，效果同 hosts 但更隐蔽。</item>
    /// </list>
    ///
    /// 这四项全是提示级：装广告屏蔽脚本会改 hosts，公司网络会用内网 DNS，
    /// Wireshark 用户装了 npcap，翻墙软件会设代理。判据只能是「你认不认得」。
    /// </summary>
    private static List<CheatFinding> ProbeStealth()
    {
        var findings = new List<CheatFinding>();

        // ---------- 1. hosts ----------
        try
        {
            var hostsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "drivers", "etc", "hosts");

            if (File.Exists(hostsPath))
            {
                var entries = new List<(string Ip, string Host)>();

                foreach (var raw in File.ReadAllLines(hostsPath))
                {
                    var line = raw;
                    var hash = line.IndexOf('#');
                    if (hash >= 0) line = line[..hash];

                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;

                    var ip = parts[0];
                    if (!System.Net.IPAddress.TryParse(ip, out _)) continue;

                    foreach (var host in parts.Skip(1))
                        entries.Add((ip, host));
                }

                // localhost 这类是系统默认，不算
                var meaningful = entries
                    .Where(e => e.Host is not ("localhost" or "localhost.localdomain" or "local"))
                    .ToList();

                if (meaningful.Count == 0)
                {
                    findings.Add(new CheatFinding
                    {
                        Group = "网络隐蔽手法",
                        Title = "hosts 文件没有自定义条目",
                        Detail = "hosts 里只有注释或默认的 localhost 映射。"
                                 + "这是正常状态：域名解析完全交给 DNS，没有被本机改写。",
                        Level = "ok"
                    });
                }
                else
                {
                    var redirect = meaningful
                        .Where(e => e.Ip is not ("127.0.0.1" or "::1" or "0.0.0.0"))
                        .ToList();
                    var blocked = meaningful.Except(redirect).ToList();

                    // 全部指向回环、且域名集中在「被网络污染/需要加速」的那几家时，
                    // 基本可以确定是加速类工具写的（Steam++/Watt Toolkit、FastGithub 一类）。
                    // 这类工具的做法就是把域名先钉回本地再由自己的代理解析，属于预期行为，
                    // 报成「可疑」只会让用户白紧张一场。
                    var accelKeywords = new[]
                    {
                        "github", "steam", "akamai", "steampowered", "greasyfork",
                        "youtube", "huggingface", "twitch", "discord", "epicgames", "pixiv"
                    };

                    var accelCount = meaningful.Count(e =>
                        accelKeywords.Any(k => e.Host.Contains(k, StringComparison.OrdinalIgnoreCase)));

                    var accelPattern = redirect.Count == 0 && accelCount * 2 >= meaningful.Count;

                    findings.Add(new CheatFinding
                    {
                        Group = "网络隐蔽手法",
                        Title = accelPattern
                            ? $"hosts 有 {meaningful.Count} 条映射，符合加速工具特征"
                            : $"hosts 有 {meaningful.Count} 条自定义映射" +
                              (redirect.Count > 0 ? $"（其中 {redirect.Count} 条指向真实地址）" : ""),
                        Detail = accelPattern
                            ? $"这些域名全部指向 127.0.0.1，且集中在 GitHub / Steam / 资源站这类常被污染或限速的站点" +
                              $"（{accelCount}/{meaningful.Count} 条）。这是加速类工具（Steam++/Watt Toolkit、FastGithub 一类）" +
                              "的典型做法：先把域名钉回本地，再由它自己的代理去解析和加速。属于预期配置，不是异常。"
                              + "它同时意味着关掉那个工具后，这些站点会连不上——排查网络问题时先想到这一点。"
                            : "hosts 会先于 DNS 生效，改一行就能把某个域名指到任意地址，且不改任何服务、不装任何驱动。"
                              + "常见正当用途：屏蔽广告/遥测域名（通常指向 0.0.0.0 或 127.0.0.1）。"
                              + "指向**具体 IP** 的那类更值得看——它能把游戏厂商的登录或心跳域名转到别处。",
                        Basis = string.Join("　", meaningful.Take(6).Select(e => $"{e.Ip} → {e.Host}")),
                        Level = redirect.Count > 0 ? "warn" : "hint",
                        Weight = redirect.Count > 0 ? 8 : (accelPattern ? 2 : 3)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            findings.Add(new CheatFinding
            {
                Group = "网络隐蔽手法",
                Title = "读不到 hosts 文件",
                Detail = "hosts 读取失败：" + ex.Message + "。这一项没能检查，不代表没有改动。",
                Level = "hint"
            });
        }

        // ---------- 2. 系统代理 ----------
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");

            if (key != null)
            {
                var enabled = key.GetValue("ProxyEnable") is int v && v != 0;
                var server = key.GetValue("ProxyServer") as string ?? "";
                var autoConfig = key.GetValue("AutoConfigURL") as string ?? "";

                if (enabled && server.Length > 0)
                {
                    findings.Add(new CheatFinding
                    {
                        Group = "网络隐蔽手法",
                        Title = "系统代理已开启",
                        Detail = $"所有 WinINet 流量（浏览器、部分游戏客户端）会经 {server} 中转。"
                                 + "代理本身是正常工具（公司网络、加速器、翻墙都用），"
                                 + "但中转方能看到明文请求、也能改写响应——这是「不改本机、照样能看」的一种方式。"
                                 + "如果不是你自己设的，需要查清楚这个地址是谁的。",
                        Basis = server,
                        Level = "hint",
                        Weight = 4
                    });
                }
                else if (autoConfig.Length > 0)
                {
                    findings.Add(new CheatFinding
                    {
                        Group = "网络隐蔽手法",
                        Title = "系统代理使用自动配置脚本",
                        Detail = $"AutoConfigURL 指向 {autoConfig}。PAC 脚本可以按域名决定哪些流量走代理，"
                                 + "比固定代理更灵活，也更不容易被一眼看出来。",
                        Basis = autoConfig,
                        Level = "hint",
                        Weight = 4
                    });
                }
                else
                {
                    findings.Add(new CheatFinding
                    {
                        Group = "网络隐蔽手法",
                        Title = "没有设置系统代理",
                        Detail = "WinINet 代理未启用、也没有自动配置脚本。这是正常状态。",
                        Level = "ok"
                    });
                }
            }
        }
        catch { /* 读不到就不报，不猜 */ }

        // ---------- 3. 抓包能力 ----------
        try
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var candidates = new (string Path, string Name)[]
            {
                (Path.Combine(system32, "drivers", "npcap.sys"), "npcap 驱动"),
                (Path.Combine(system32, "drivers", "npf.sys"), "WinPcap 驱动"),
                (Path.Combine(system32, "Npcap", "wpcap.dll"), "npcap 抓包库"),
                (Path.Combine(system32, "wpcap.dll"), "WinPcap 抓包库")
            };

            var found = candidates.Where(c => File.Exists(c.Path)).Select(c => c.Name).ToList();

            foreach (var service in new[] { "npcap", "npf" })
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + service);
                if (key != null) found.Add($"服务 {service}");
            }

            if (found.Count > 0)
            {
                findings.Add(new CheatFinding
                {
                    Group = "网络隐蔽手法",
                    Title = "本机装有能力完整的抓包组件",
                    Detail = "存在 npcap / WinPcap：" + string.Join("、", found.Distinct()) + "。"
                             + "它让普通程序可以不经注入就读到全部进出网卡的数据包。"
                             + "装 Wireshark、部分加速器、部分安全工具都会带上它——"
                             + "所以这不是异常，只是说明「这台机器具备做流量分析的条件」。"
                             + "如果配合外部程序解析，就能做出不碰游戏进程的「雷达」。",
                    Basis = string.Join("、", found.Distinct()),
                    Level = "hint",
                    Weight = 5
                });
            }
            else
            {
                findings.Add(new CheatFinding
                {
                    Group = "网络隐蔽手法",
                    Title = "没有装抓包驱动",
                    Detail = "没有发现 npcap / WinPcap 的驱动或库文件。普通程序无法直接读取网卡上的原始数据包。",
                    Level = "ok"
                });
            }
        }
        catch { /* 读不到就不报 */ }

        // ---------- 4. DNS 指向 ----------
        try
        {
            var suspicious = new List<string>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT Description, DNSServerSearchOrder, DefaultIPGateway " +
                "FROM Win32_NetworkAdapterConfiguration WHERE IPEnabled = TRUE");

            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    if (mo["DNSServerSearchOrder"] is not string[] servers || servers.Length == 0)
                        continue;

                    var gateway = (mo["DefaultIPGateway"] as string[])?.FirstOrDefault() ?? "";
                    var nic = mo["Description"]?.ToString() ?? "网卡";

                    var odd = servers
                        .Where(s => !KnownPublicDns.Contains(s) && s != gateway && !IsSameSubnet(s, gateway))
                        .ToList();

                    if (odd.Count > 0)
                        suspicious.Add($"{nic}：{string.Join("/", odd)}");
                }
            }

            if (suspicious.Count > 0)
            {
                findings.Add(new CheatFinding
                {
                    Group = "网络隐蔽手法",
                    Title = "DNS 指向了非网关、非公共 DNS 的地址",
                    Detail = "下面这些网卡的 DNS 既不是网关，也不在常见公共 DNS 名单里：" +
                             string.Join("；", suspicious.Take(4)) +
                             "。自建 DNS 能把指定域名解析到任意地址，效果和改 hosts 一样，但本机看不出痕迹。"
                             + "注意：公司网络、校园网、部分运营商 DNS 也会落在这一档，先确认是不是你环境本身的配置。",
                    Basis = string.Join("　", suspicious.Take(4)),
                    Level = "hint",
                    Weight = 5
                });
            }
            else
            {
                findings.Add(new CheatFinding
                {
                    Group = "网络隐蔽手法",
                    Title = "DNS 指向正常",
                    Detail = "各网卡的 DNS 要么是网关，要么是常见公共 DNS。没有被改成来路不明的解析服务器。",
                    Level = "ok"
                });
            }
        }
        catch { /* WMI 不可用就不报 */ }

        return findings;
    }

    /// <summary>两个 IPv4 是否在同一 /24 网段。用来判断 DNS 是不是「网关那一片的地址」。</summary>
    private static bool IsSameSubnet(string a, string b)
    {
        try
        {
            var ia = System.Net.IPAddress.Parse(a).GetAddressBytes();
            var ib = System.Net.IPAddress.Parse(b).GetAddressBytes();
            if (ia.Length != 4 || ib.Length != 4) return false;

            return ia[0] == ib[0] && ia[1] == ib[1] && ia[2] == ib[2];
        }
        catch { return false; }
    }
}
