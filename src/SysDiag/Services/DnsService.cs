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
/// DNS 优化：并发测速多个公共 DNS，把最快的应用到指定网卡，支持还原。
///
/// 写入走 netsh（`netsh interface ip set dns`），它同时改注册表并让配置立即生效；
/// 改完再刷新一次 DNS 缓存。
/// </summary>
public static class DnsService
{
    private const string ProbeHost = "www.baidu.com";
    private const int ProbeTimeoutMs = 2000;
    private const int ProbeRounds = 3;

    public static bool IsAdmin => LoggingService.IsAdmin();

    public static string BackupFile => Path.Combine(AppPaths.DataDir, "dns-backup.json");

    // ------------------------------------------------------------ 候选方案

    /// <summary>常用公共 DNS。国内优先，国际的也列出来但不建议默认用。</summary>
    public static IReadOnlyList<DnsPreset> Presets { get; } = new List<DnsPreset>
    {
        new() { Name = "阿里 DNS",      Primary = "223.5.5.5",       Secondary = "223.6.6.6",       Note = "国内延迟低，解析稳定" },
        new() { Name = "腾讯 DNS",      Primary = "119.29.29.29",    Secondary = "182.254.116.116", Note = "国内节点多，游戏常用" },
        new() { Name = "114 DNS",       Primary = "114.114.114.114", Secondary = "114.114.115.115", Note = "老牌公共 DNS，覆盖广" },
        new() { Name = "百度 DNS",      Primary = "180.76.76.76",    Secondary = "",                Note = "单节点" },
        new() { Name = "CNNIC sDNS",    Primary = "1.2.4.8",         Secondary = "210.2.4.8",       Note = "官方公共 DNS" },
        new() { Name = "Google DNS",    Primary = "8.8.8.8",         Secondary = "8.8.4.4",         Note = "国内延迟通常偏高" },
        new() { Name = "Cloudflare",    Primary = "1.1.1.1",         Secondary = "1.0.0.1",         Note = "国内访问不稳定" }
    };

    // ------------------------------------------------------------ 网卡枚举

    /// <summary>只列能上网的物理网卡；VPN、蓝牙、虚拟网卡跳过（改它们的 DNS 没意义，还会断隧道）。</summary>
    public static List<DnsAdapter> ListAdapters()
    {
        var list = new List<DnsAdapter>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                or NetworkInterfaceType.Tunnel
                or NetworkInterfaceType.Unknown)
                continue;

            var desc = ni.Description ?? "";

            if (desc.Contains("Radmin", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                continue;

            var ipv4 = ni.GetIPProperties().UnicastAddresses
                .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (!ipv4) continue;

            var (dns, isDhcp) = ReadDns(ni.Id);

            list.Add(new DnsAdapter
            {
                Name = ni.Name,
                Description = desc,
                Id = ni.Id,
                CurrentDns = dns,
                IsDhcp = isDhcp,
                Selected = ni.OperationalStatus == OperationalStatus.Up
            });
        }

        return list.OrderByDescending(a => a.Selected).ThenBy(a => a.Name).ToList();
    }

    /// <summary>从注册表读该网卡的 DNS 与 DHCP 状态。NameServer 为空且 EnableDHCP=1 即自动获取。</summary>
    public static (List<string> Dns, bool IsDhcp) ReadDns(string adapterId)
    {
        var result = new List<string>();

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");

            if (key != null)
            {
                var nameServer = key.GetValue("NameServer") as string ?? "";
                foreach (var part in nameServer.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var ip = part.Trim();
                    if (IPAddress.TryParse(ip, out _)) result.Add(ip);
                }

                var dhcp = Convert.ToInt32(key.GetValue("EnableDHCP") ?? 0) == 1;
                if (result.Count == 0 && dhcp)
                {
                    // DHCP 下真实 DNS 由 DhcpNameServer 记录
                    var dhcpDns = key.GetValue("DhcpNameServer") as string ?? "";
                    foreach (var part in dhcpDns.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var ip = part.Trim();
                        if (IPAddress.TryParse(ip, out _)) result.Add(ip);
                    }
                }

                return (result, dhcp);
            }
        }
        catch { }

        return (result, false);
    }

    // ------------------------------------------------------------ 测速

    /// <summary>构造一个标准 DNS 查询报文（A 记录）。</summary>
    public static byte[] BuildQuery(string host, ushort id)
    {
        var buf = new List<byte>
        {
            (byte)(id >> 8), (byte)(id & 0xFF),
            0x01, 0x00,             // flags: 标准查询 + 期望递归
            0x00, 0x01,             // QDCOUNT = 1
            0x00, 0x00,             // ANCOUNT
            0x00, 0x00,             // NSCOUNT
            0x00, 0x00              // ARCOUNT
        };

        foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            if (bytes.Length > 63) continue;
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }

        buf.Add(0x00);              // 名称结束
        buf.Add(0x00); buf.Add(0x01); // QTYPE = A
        buf.Add(0x00); buf.Add(0x01); // QCLASS = IN

        return buf.ToArray();
    }

    /// <summary>对单个 DNS 服务器做一次查询，返回毫秒；超时返回 -1。</summary>
    public static async Task<int> ProbeOnceAsync(string server, int timeoutMs = ProbeTimeoutMs)
    {
        if (!IPAddress.TryParse(server, out var addr)) return -1;

        try
        {
            using var udp = new UdpClient(addr.AddressFamily);
            var query = BuildQuery(ProbeHost, (ushort)Random.Shared.Next(1, 65535));
            var endpoint = new IPEndPoint(addr, 53);

            var sw = Stopwatch.StartNew();
            await udp.SendAsync(query, query.Length, endpoint);

            using var cts = new CancellationTokenSource(timeoutMs);
            var received = await udp.ReceiveAsync(cts.Token);
            sw.Stop();

            // 至少要有 12 字节头，且截断/为空都算失败
            if (received.Buffer.Length < 12) return -1;

            return (int)sw.ElapsedMilliseconds;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (SocketException)
        {
            return -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>多轮取最小值，规避偶发抖动。并发测所有方案。</summary>
    public static async Task<List<DnsProbeRow>> ProbeAllAsync(
        IProgress<string> progress = null, CancellationToken token = default)
    {
        var rows = Presets.Select(p => new DnsProbeRow { Preset = p }).ToList();
        var done = 0;

        var tasks = rows.Select(async row =>
        {
            var best = -1;

            for (var round = 0; round < ProbeRounds; round++)
            {
                token.ThrowIfCancellationRequested();

                var ms = await ProbeOnceAsync(row.Preset.Primary, ProbeTimeoutMs);
                if (ms >= 0 && (best < 0 || ms < best)) best = ms;

                // 通了就用，不再重复请求，避免整体测速被单个服务器拖慢
                if (best >= 0) break;
            }

            row.LatencyMs = best;

            var finished = Interlocked.Increment(ref done);
            progress?.Report($"测速中… {finished}/{rows.Count}");
        });

        await Task.WhenAll(tasks);

        var ordered = rows.OrderBy(r => r.LatencyMs < 0 ? int.MaxValue : r.LatencyMs).ToList();

        // 只给第一名打推荐标（且必须真的通）
        if (ordered.Count > 0 && ordered[0].Ok)
            ordered[0].IsRecommended = true;

        progress?.Report($"测速完成，最快：{(ordered.FirstOrDefault()?.Ok == true ? ordered[0].Name + " " + ordered[0].LatencyText : "全部超时")}");
        return ordered;
    }

    // ------------------------------------------------------------ 应用与还原

    /// <summary>把方案应用到网卡。需要管理员。</summary>
    public static async Task<(bool Ok, string Message)> ApplyAsync(DnsAdapter adapter, DnsPreset preset)
    {
        if (!IsAdmin)
            return (false, "改 DNS 需要管理员权限，请点右下角「以管理员身份重启」后再试。");

        if (string.IsNullOrEmpty(preset.Primary))
            return (false, "这个方案没有主 DNS。");

        var first = await RunNetsh($"interface ip set dns name=\"{adapter.Name}\" static {preset.Primary} primary");
        if (!first.Ok)
            return (false, $"设置主 DNS 失败：{first.Output}");

        var note = "";

        if (!string.IsNullOrEmpty(preset.Secondary))
        {
            var second = await RunNetsh(
                $"interface ip add dns name=\"{adapter.Name}\" {preset.Secondary} index=2");
            if (!second.Ok) note = "（备用 DNS 未设置成功，主 DNS 已生效）";
        }

        await FlushDns();

        return (true, $"{adapter.Name} 已设为 {preset.Name}（{preset.IpHint}）。{note}");
    }

    /// <summary>恢复为「自动获取 DNS」。</summary>
    public static async Task<(bool Ok, string Message)> RestoreDhcpAsync(DnsAdapter adapter)
    {
        if (!IsAdmin)
            return (false, "改 DNS 需要管理员权限，请点右下角「以管理员身份重启」后再试。");

        var r = await RunNetsh($"interface ip set dns name=\"{adapter.Name}\" dhcp");
        if (!r.Ok)
            return (false, $"恢复自动获取失败：{r.Output}");

        await FlushDns();
        return (true, $"{adapter.Name} 已恢复为自动获取 DNS。");
    }

    /// <summary>按备份写回静态 DNS（原来就是自动获取的会改回 dhcp）。</summary>
    public static async Task<(bool Ok, string Message)> RestoreBackupAsync()
    {
        var backup = LoadBackup();
        if (backup == null)
            return (false, "没有找到优化前的备份。");

        if (!IsAdmin)
            return (false, "还原 DNS 需要管理员权限。");

        var ok = 0;
        var failed = new List<string>();

        foreach (var entry in backup.Entries)
        {
            try
            {
                if (entry.WasDhcp || entry.Dns.Count == 0)
                {
                    var r = await RunNetsh($"interface ip set dns name=\"{entry.Name}\" dhcp");
                    if (r.Ok) ok++; else failed.Add(entry.Name + "：" + r.Output);
                    continue;
                }

                var first = await RunNetsh(
                    $"interface ip set dns name=\"{entry.Name}\" static {entry.Dns[0]} primary");
                if (!first.Ok) { failed.Add(entry.Name + "：" + first.Output); continue; }

                for (var i = 1; i < entry.Dns.Count; i++)
                {
                    await RunNetsh(
                        $"interface ip add dns name=\"{entry.Name}\" {entry.Dns[i]} index={i + 1}");
                }

                ok++;
            }
            catch (Exception ex)
            {
                failed.Add(entry.Name + "：" + ex.Message);
            }
        }

        await FlushDns();

        return failed.Count == 0
            ? (true, $"已还原 {ok} 块网卡的 DNS（备份时间 {backup.Time:yyyy-MM-dd HH:mm}）。")
            : (ok > 0, $"还原 {ok} 块，失败 {failed.Count} 块：{string.Join("；", failed.Take(3))}");
    }

    // ------------------------------------------------------------ 备份

    public static (bool Ok, string Message) CreateBackup(List<DnsAdapter> adapters)
    {
        try
        {
            var backup = new DnsBackup
            {
                Entries = adapters.Select(a => new DnsBackupEntry
                {
                    Id = a.Id,
                    Name = a.Name,
                    WasDhcp = a.IsDhcp,
                    Dns = a.CurrentDns.ToList()
                }).ToList()
            };

            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(backup,
                new JsonSerializerOptions { WriteIndented = true }));

            return (true, "已备份当前 DNS：" + backup.Time.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex)
        {
            return (false, "备份失败：" + ex.Message);
        }
    }

    public static DnsBackup LoadBackup()
    {
        try
        {
            if (File.Exists(BackupFile))
                return JsonSerializer.Deserialize<DnsBackup>(File.ReadAllText(BackupFile));
        }
        catch { }
        return null;
    }

    public static string BackupTimeText
    {
        get
        {
            var b = LoadBackup();
            return b == null ? "还没有备份" : $"备份时间：{b.Time:yyyy-MM-dd HH:mm:ss}";
        }
    }

    // ------------------------------------------------------------ 命令

    private static async Task<(bool Ok, string Output)> RunNetsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return (false, "netsh 启动失败。");

            var output = (await p.StandardOutput.ReadToEndAsync()).Trim();
            var error = (await p.StandardError.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();

            var text = string.IsNullOrEmpty(error) ? output : output + " " + error;
            return (p.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static async Task FlushDns()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig", "/flushdns")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p != null) await p.WaitForExitAsync();
        }
        catch { }
    }
}
