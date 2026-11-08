using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 断网急救：先体检网络链路，再按勾选的步骤修复。
///
/// 修复动作都是 Windows 自带命令（netsh / ipconfig）+ 注册表，不装驱动、不改游戏；
/// 动手前先备份 hosts 与代理设置，随时能还原。
/// </summary>
public static class NetRepairService
{
    private const string ProxyKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string InterfacesKey = @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces";
    private const string WinsockCatalog = @"SYSTEM\CurrentControlSet\Services\WinSock2\Parameters\Protocol_Catalog9\Catalog_Entries";

    public static bool IsAdmin => LoggingService.IsAdmin();

    public static string BackupDir => Path.Combine(AppPaths.DataDir, "netrepair-backup");
    public static string HostsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        @"drivers\etc\hosts");

    // ---------------------------------------------------------------- 体检

    public static List<NetCheckItem> Check()
    {
        var list = new List<NetCheckItem>();

        // ① 网卡
        List<NetworkInterface> nics;
        try
        {
            nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .ToList();
        }
        catch
        {
            nics = new List<NetworkInterface>();
        }

        var up = nics.Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();
        var physicalUp = up.Where(n =>
            !(n.Description ?? "").Contains("Radmin", StringComparison.OrdinalIgnoreCase)
            && !(n.Description ?? "").Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
            && !(n.Description ?? "").Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();

        list.Add(new NetCheckItem
        {
            Name = "网卡状态",
            Detail = $"{nics.Count} 块可用，{up.Count} 块已连接" +
                     (physicalUp.Count > 0 ? "（" + string.Join("、", physicalUp.Take(3).Select(n => n.Name)) + "）" : ""),
            Level = up.Count == 0 ? "bad" : "ok",
            FixHint = up.Count == 0 ? "检查网线 / Wi-Fi 开关，或先启用网卡" : ""
        });

        // ② IP 与网关
        var addresses = new List<IPAddress>();
        var gateways = new List<IPAddress>();

        foreach (var nic in up)
        {
            try
            {
                var props = nic.GetIPProperties();
                addresses.AddRange(props.UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(a => a.Address));
                gateways.AddRange(props.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork
                                && !g.Address.Equals(IPAddress.Any))
                    .Select(g => g.Address));
            }
            catch { }
        }

        var apipa = addresses.Where(a => a.ToString().StartsWith("169.254.")).ToList();
        var realIp = addresses.Where(a => !a.ToString().StartsWith("169.254.")).ToList();

        list.Add(new NetCheckItem
        {
            Name = "IP 地址",
            Detail = realIp.Count > 0
                ? string.Join(" / ", realIp.Select(a => a.ToString()))
                : apipa.Count > 0 ? "只有 169.254.x（DHCP 没拿到地址）" : "没有 IPv4 地址",
            Level = realIp.Count > 0 ? "ok" : "bad",
            FixHint = realIp.Count == 0 ? "重新获取 IP，或检查路由器 DHCP" : ""
        });

        list.Add(new NetCheckItem
        {
            Name = "默认网关",
            Detail = gateways.Count > 0 ? string.Join(" / ", gateways.Distinct()) : "没有默认网关",
            Level = gateways.Count > 0 ? "ok" : "bad",
            FixHint = gateways.Count == 0 ? "检查路由器 / 重新获取 IP" : ""
        });

        // ③ DNS
        var dnsServers = new List<string>();
        try
        {
            foreach (var nic in up)
            {
                var dns = nic.GetIPProperties().DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString());
                dnsServers.AddRange(dns);
            }
        }
        catch { }

        list.Add(new NetCheckItem
        {
            Name = "DNS 服务器",
            Detail = dnsServers.Count > 0 ? string.Join(" / ", dnsServers.Distinct().Take(4)) : "没有配置 DNS",
            Level = dnsServers.Count > 0 ? "ok" : "warn",
            FixHint = dnsServers.Count == 0 ? "用「DNS 优化」设一个公共 DNS，或恢复自动获取" : ""
        });

        // ④ 代理
        var (proxyOn, proxyServer) = ReadProxy();
        list.Add(new NetCheckItem
        {
            Name = "系统代理",
            Detail = proxyOn
                ? "已开启" + (string.IsNullOrEmpty(proxyServer) ? "" : "：" + proxyServer)
                : "未开启",
            Level = proxyOn ? "warn" : "ok",
            FixHint = proxyOn ? "如果代理已经不用了，残留设置会让浏览器全部走代理" : ""
        });

        // ⑤ hosts
        var (hostEntries, hostError) = ReadHostsSummary();
        list.Add(new NetCheckItem
        {
            Name = "hosts 文件",
            Detail = hostError != null ? hostError : $"{hostEntries} 条生效记录",
            Level = hostError != null ? "warn" : hostEntries > 0 ? "warn" : "ok",
            FixHint = hostEntries > 0 ? "自定义 hosts 可能屏蔽了某些域名" : ""
        });

        // ⑥ Winsock 目录
        var winsockCount = CountSubKeys(Registry.LocalMachine, WinsockCatalog);
        list.Add(new NetCheckItem
        {
            Name = "Winsock 目录",
            Detail = winsockCount > 0 ? $"{winsockCount} 项" : "读不到（需要管理员）",
            Level = winsockCount == 0 ? "warn" : "ok",
            FixHint = ""
        });

        // ⑦ TCP/IP 接口数
        var ifCount = CountSubKeys(Registry.LocalMachine, InterfacesKey);
        list.Add(new NetCheckItem
        {
            Name = "TCP/IP 接口",
            Detail = ifCount > 0 ? $"{ifCount} 个接口配置" : "读不到",
            Level = ifCount == 0 ? "warn" : "ok",
            FixHint = ""
        });

        // ⑧ 连通性（ICMP，失败不一定代表断网）
        var pingOk = false;
        var pingMs = -1;
        try
        {
            using var ping = new Ping();
            var reply = ping.Send("223.5.5.5", 1500);
            if (reply?.Status == IPStatus.Success)
            {
                pingOk = true;
                pingMs = (int)reply.RoundtripTime;
            }
        }
        catch { }

        list.Add(new NetCheckItem
        {
            Name = "外网连通",
            Detail = pingOk ? $"223.5.5.5 有响应（{pingMs} ms）" : "223.5.5.5 没有响应",
            Level = pingOk ? "ok" : "warn",
            FixHint = pingOk ? "" : "ICMP 可能被拦截，能上网就不必管；确实上不了网再修复"
        });

        return list;
    }

    // ---------------------------------------------------------------- 修复步骤

    public static List<NetFixStep> BuildSteps() => new()
    {
        new NetFixStep
        {
            Kind = NetFixKind.FlushDns, Name = "刷新 DNS 缓存",
            Effect = "清掉本机 DNS 缓存，域名解析失败时最常用，不影响网络配置",
            Command = "ipconfig /flushdns", NeedsRestart = false, Selected = true
        },
        new NetFixStep
        {
            Kind = NetFixKind.ResetWinsock, Name = "重置 Winsock",
            Effect = "把 Winsock 目录恢复默认，修复被第三方软件（VPN、加速器、抓包工具）改坏的协议栈",
            Command = "netsh winsock reset", NeedsRestart = true, Selected = true
        },
        new NetFixStep
        {
            Kind = NetFixKind.ResetTcpIp, Name = "重置 TCP/IP 栈",
            Effect = "重写 TCP/IP 注册表配置，修复 IP 配置错乱、网卡异常",
            Command = "netsh int ip reset", NeedsRestart = true, Selected = true
        },
        new NetFixStep
        {
            Kind = NetFixKind.ResetProxy, Name = "清除系统代理",
            Effect = "关闭残留的代理设置，避免浏览器 / 应用全部走一个已经不可用的代理",
            Command = "ProxyEnable = 0，删除 ProxyServer", NeedsRestart = false, Selected = true
        },
        new NetFixStep
        {
            Kind = NetFixKind.RenewDhcp, Name = "重新获取 IP",
            Effect = "释放并重新向路由器申请地址，会短暂断网几秒",
            Command = "ipconfig /release && ipconfig /renew", NeedsRestart = false, Selected = false
        },
        new NetFixStep
        {
            Kind = NetFixKind.RestoreHosts, Name = "重置 hosts 文件",
            Effect = "先备份当前 hosts，再写回系统默认内容；自定义屏蔽规则会失效",
            Command = "备份 → 写入默认 hosts", NeedsRestart = false, Selected = false
        },
        new NetFixStep
        {
            Kind = NetFixKind.ResetFirewall, Name = "重置防火墙规则",
            Effect = "清空所有自定义防火墙规则（含第三方软件加的），回到默认状态",
            Command = "netsh advfirewall reset", NeedsRestart = false, Selected = false
        }
    };

    // ---------------------------------------------------------------- 执行

    public static async Task<List<NetFixResult>> RunAsync(
        List<NetFixStep> steps, IProgress<string> log, CancellationToken token = default)
    {
        var results = new List<NetFixResult>();

        if (!IsAdmin)
        {
            results.Add(new NetFixResult
            {
                Name = "权限检查", Ok = false,
                Message = "需要管理员权限，请点右下角「以管理员身份重启」后再试。"
            });
            return results;
        }

        var backup = CreateBackup();
        log?.Report("① 已备份 hosts 与代理设置：" + backup);

        foreach (var step in steps)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var (ok, msg) = step.Kind switch
                {
                    NetFixKind.FlushDns => await RunAsync("ipconfig", "/flushdns"),
                    NetFixKind.ResetWinsock => await RunAsync("netsh", "winsock reset"),
                    NetFixKind.ResetTcpIp => await RunAsync("netsh",
                        $"int ip reset \"{Path.Combine(Path.GetTempPath(), "netrepair_ip.log")}\""),
                    NetFixKind.RenewDhcp => await RunRenewAsync(),
                    NetFixKind.ResetProxy => ClearProxy(),
                    NetFixKind.RestoreHosts => ResetHosts(),
                    NetFixKind.ResetFirewall => await RunAsync("netsh", "advfirewall reset"),
                    _ => (false, "未知步骤")
                };

                results.Add(new NetFixResult { Name = step.Name, Ok = ok, Message = msg });
                log?.Report((ok ? "✓ " : "✗ ") + step.Name + "：" + msg);
            }
            catch (Exception ex)
            {
                results.Add(new NetFixResult { Name = step.Name, Ok = false, Message = ex.Message });
                log?.Report("✗ " + step.Name + "：" + ex.Message);
            }

            await Task.Delay(200, token);
        }

        var needRestart = steps.Any(s => s.NeedsRestart);
        if (needRestart)
            log?.Report("⚠ 含需要重启的步骤，重启电脑后才会完全生效。");

        return results;
    }

    private static async Task<(bool Ok, string Message)> RunRenewAsync()
    {
        var (ok1, m1) = await RunAsync("ipconfig", "/release");
        var (ok2, m2) = await RunAsync("ipconfig", "/renew");

        return ok2
            ? (true, "已重新获取地址")
            : (false, string.IsNullOrEmpty(m2) ? m1 : m2);
    }

    private static async Task<(bool, string)> RunAsync(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return (false, "启动失败");

            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            var error = (await p.StandardError.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();

            var text = string.IsNullOrEmpty(error) ? output : (output + " " + error).Trim();
            // 这些命令经常返回 0 但输出「需要重启」，所以只看退出码
            return (p.ExitCode == 0, Shorten(text));
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 代理

    public static (bool Enabled, string Server) ReadProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProxyKey);
            var on = Convert.ToInt32(key?.GetValue("ProxyEnable") ?? 0) == 1;
            var server = key?.GetValue("ProxyServer") as string ?? "";
            return (on, server);
        }
        catch
        {
            return (false, "");
        }
    }

    private static (bool Ok, string Message) ClearProxy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProxyKey, writable: true);
            if (key == null) return (false, "打不开 Internet 设置项。");

            var was = Convert.ToInt32(key.GetValue("ProxyEnable") ?? 0) == 1;
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);

            return (true, was ? "已关闭并清除代理地址" : "本来就没开启代理，已确保为关闭");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- hosts

    private static (int Count, string Error) ReadHostsSummary()
    {
        try
        {
            if (!File.Exists(HostsPath)) return (0, "文件不存在");

            var count = 0;
            foreach (var line in File.ReadAllLines(HostsPath))
            {
                var t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                count++;
            }

            return (count, null);
        }
        catch (Exception ex)
        {
            return (0, "读取失败：" + ex.Message);
        }
    }

    private static (bool Ok, string Message) ResetHosts()
    {
        try
        {
            var backup = Path.Combine(BackupDir, $"hosts.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
            Directory.CreateDirectory(BackupDir);

            if (File.Exists(HostsPath))
                File.Copy(HostsPath, backup, overwrite: true);

            // 微软默认 hosts 只有注释
            var defaultHosts = new StringBuilder()
                .AppendLine("# Copyright (c) 1993-2009 Microsoft Corp.")
                .AppendLine("#")
                .AppendLine("# This is a sample HOSTS file used by Microsoft TCP/IP for Windows.")
                .AppendLine("#")
                .AppendLine("# localhost name resolution is handled within DNS itself.")
                .AppendLine("#\t127.0.0.1\tlocalhost")
                .AppendLine("#\t::1\t\tlocalhost")
                .ToString();

            // hosts 常被设为只读，先去掉属性
            try
            {
                var attrs = File.GetAttributes(HostsPath);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(HostsPath, attrs & ~FileAttributes.ReadOnly);
            }
            catch { }

            File.WriteAllText(HostsPath, defaultHosts, Encoding.ASCII);

            return (true, "已重置为系统默认（原文件备份在 " + Path.GetFileName(backup) + "）");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 备份

    public static string CreateBackup()
    {
        try
        {
            Directory.CreateDirectory(BackupDir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

            if (File.Exists(HostsPath))
            {
                try { File.Copy(HostsPath, Path.Combine(BackupDir, $"hosts.{stamp}.bak"), true); }
                catch { }
            }

            var (on, server) = ReadProxy();
            var info = new
            {
                time = DateTime.Now,
                proxyEnable = on,
                proxyServer = server,
                dns = GetDnsSnapshot()
            };

            File.WriteAllText(Path.Combine(BackupDir, $"net.{stamp}.json"),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));

            return BackupDir;
        }
        catch
        {
            return "（备份失败，但流程继续）";
        }
    }

    private static List<string> GetDnsSnapshot()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                var dns = nic.GetIPProperties().DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString());
                foreach (var d in dns) list.Add($"{nic.Name}={d}");
            }
        }
        catch { }
        return list;
    }

    // ---------------------------------------------------------------- 工具

    private static int CountSubKeys(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            return key?.SubKeyCount ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .LastOrDefault()?.Trim() ?? "";
        return line.Length > 90 ? line[..90] + "…" : line;
    }
}
