using System.Diagnostics;
using System.Management;
using System.Net.NetworkInformation;
using SysDiag.Models;
using System.IO;

namespace SysDiag.Services;

public static class DiagnosticsService
{
    // ---------------- 系统体检 ----------------

    public static ReportPayload SystemCheckup()
    {
        var doc = new HtmlDoc("系统体检报告", $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var cpu = Wmi("Win32_Processor", "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed").FirstOrDefault();
        var cs = Wmi("Win32_ComputerSystem", "Manufacturer", "Model", "TotalPhysicalMemory").FirstOrDefault();
        var os = Wmi("Win32_OperatingSystem", "Caption", "Version", "BuildNumber", "LastBootUpTime").FirstOrDefault();

        var ramGb = 0.0;
        if (cs != null && double.TryParse(Get(cs, "TotalPhysicalMemory"), out var ramBytes)) ramGb = ramBytes / 1024 / 1024 / 1024;

        doc.Kpis(
            (cpu != null ? Get(cpu, "NumberOfCores") : "?", "CPU 核心", ""),
            ($"{ramGb:N1} GB", "物理内存", ""),
            (Process.GetProcesses().Length.ToString("N0"), "运行进程", ""),
            (Wmi("Win32_LogicalDisk", "DeviceID", "Size", "FreeSpace").Count.ToString("N0"), "逻辑磁盘", ""));

        doc.H2("硬件与系统");
        var rows = new List<string[]>();
        if (cpu != null)
        {
            rows.Add(new[] { "处理器", Get(cpu, "Name") });
            rows.Add(new[] { "核心 / 线程", $"{Get(cpu, "NumberOfCores")} / {Get(cpu, "NumberOfLogicalProcessors")}" });
            rows.Add(new[] { "基准频率", $"{Get(cpu, "MaxClockSpeed")} MHz" });
        }
        if (cs != null)
        {
            rows.Add(new[] { "厂商 / 型号", $"{Get(cs, "Manufacturer")} {Get(cs, "Model")}" });
            rows.Add(new[] { "物理内存", $"{ramGb:N1} GB" });
        }
        if (os != null)
        {
            rows.Add(new[] { "操作系统", Get(os, "Caption") });
            rows.Add(new[] { "版本 / 内部版本", $"{Get(os, "Version")} / {Get(os, "BuildNumber")}" });
            rows.Add(new[] { "上次启动", ToDate(Get(os, "LastBootUpTime")) });
        }
        doc.Table(new[] { "项目", "值" }, rows, new[] { "150px", "" });

        doc.H2("显卡");
        var gpus = Wmi("Win32_VideoController", "Name", "DriverVersion", "AdapterRAM");
        doc.Table(new[] { "设备", "驱动版本", "显存" },
            gpus.Select(g => new[]
            {
                Get(g, "Name"),
                Get(g, "DriverVersion"),
                FormatAdapterRam(Get(g, "AdapterRAM"))
            }), new[] { "", "170px", "110px" });

        doc.H2("磁盘分区");
        var disks = Wmi("Win32_LogicalDisk").Where(d => Get(d, "DriveType") == "3").ToList();
        doc.Table(new[] { "盘符", "卷标", "总容量", "可用", "使用率" },
            disks.Select(d =>
            {
                var size = ToLong(Get(d, "Size"));
                var free = ToLong(Get(d, "FreeSpace"));
                var pct = size > 0 ? (size - free) * 100.0 / size : 0;
                var tag = pct > 90 ? "t-bad" : pct > 75 ? "t-warn" : "t-ok";
                return new[]
                {
                    Get(d, "DeviceID"),
                    Get(d, "VolumeName"),
                    $"{size / 1024.0 / 1024 / 1024:N1} GB",
                    $"{free / 1024.0 / 1024 / 1024:N1} GB",
                    $"<span class=\"tag {tag}\">{pct:N1}%</span>"
                };
            }), new[] { "70px", "110px", "110px", "110px", "100px" });

        doc.H2("内存占用最高的 10 个进程");
        var procs = Process.GetProcesses()
            .OrderByDescending(p => SafeWs(p))
            .Take(10)
            .ToList();
        doc.Table(new[] { "进程", "PID", "内存", "线程" },
            procs.Select(p => new[]
            {
                HtmlDoc.E(SafeName(p)),
                SafePid(p),
                $"{SafeWs(p) / 1024.0 / 1024:N0} MB",
                SafeThreads(p)
            }), new[] { "", "80px", "110px", "80px" });

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "系统体检报告");
        return new ReportPayload { Title = "系统体检", Subtitle = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), HtmlPath = path };
    }

    // ---------------- 错误与警告 ----------------

    public static ReportPayload ErrorReport()
    {
        var since = DateTime.Now.AddDays(-3);
        var doc = new HtmlDoc("错误与警告报告", $"统计范围：{since:yyyy-MM-dd HH:mm} 至今　|　日志：系统 + 应用程序");
        var totalErr = 0;
        var totalWarn = 0;
        var blocks = new List<(string Log, List<EventLogEntry> Err, List<EventLogEntry> Warn)>();

        foreach (var logName in new[] { "System", "Application" })
        {
            var err = new List<EventLogEntry>();
            var warn = new List<EventLogEntry>();
            try
            {
                using var el = new EventLog(logName);
                foreach (EventLogEntry e in el.Entries)
                {
                    if (e.TimeGenerated < since) continue;
                    if (e.EntryType == EventLogEntryType.Error) { if (err.Count < 60) err.Add(e); totalErr++; }
                    else if (e.EntryType == EventLogEntryType.Warning) { if (warn.Count < 40) warn.Add(e); totalWarn++; }
                }
            }
            catch (Exception ex)
            {
                doc.Note($"读取「{logName}」日志失败：{HtmlDoc.E(ex.Message)}");
            }
            blocks.Add((logName, err, warn));
        }

        doc.Kpis(
            (totalErr.ToString("N0"), "错误事件", "#C92A2A"),
            (totalWarn.ToString("N0"), "警告事件", "#F08C00"),
            ("3 天", "统计窗口", ""),
            ("2 个", "日志通道", ""));

        foreach (var (log, err, warn) in blocks)
        {
            doc.H2($"{log} — 错误（最近 {err.Count} 条）");
            if (err.Count == 0) doc.Empty("无");
            else doc.Table(new[] { "时间", "来源", "事件 ID", "内容" },
                err.Select(e => new[]
                {
                    e.TimeGenerated.ToString("MM-dd HH:mm:ss"),
                    HtmlDoc.E(e.Source),
                    e.InstanceId.ToString(),
                    $"<code>{HtmlDoc.E(FirstLine(e.Message, 160))}</code>"
                }), new[] { "110px", "150px", "80px", "" });

            doc.H2($"{log} — 警告（最近 {warn.Count} 条）");
            if (warn.Count == 0) doc.Empty("无");
            else doc.Table(new[] { "时间", "来源", "事件 ID", "内容" },
                warn.Select(e => new[]
                {
                    e.TimeGenerated.ToString("MM-dd HH:mm:ss"),
                    HtmlDoc.E(e.Source),
                    e.InstanceId.ToString(),
                    $"<code>{HtmlDoc.E(FirstLine(e.Message, 140))}</code>"
                }), new[] { "110px", "150px", "80px", "" });
        }

        var path = HtmlReportBuilder.Save(doc.Build(), "错误与警告报告");
        return new ReportPayload { Title = "错误与警告", Subtitle = $"近 3 天 · 错误 {totalErr} / 警告 {totalWarn}", HtmlPath = path };
    }

    // ---------------- 磁盘健康 ----------------

    public static ReportPayload DiskHealth()
    {
        var doc = new HtmlDoc("磁盘健康报告", $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var rows = new List<string[]>();
        var healthy = 0;
        var warning = 0;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, HealthStatus, OperationalStatus, Size FROM MSFT_PhysicalDisk");
            foreach (var d in searcher.Get().Cast<ManagementObject>())
            {
                var status = ToInt(Get(d, "HealthStatus"));
                var media = ToInt(Get(d, "MediaType"));
                var size = ToLong(Get(d, "Size"));
                var tag = status switch { 0 => "t-ok", 1 => "t-warn", _ => "t-bad" };
                var label = status switch { 0 => "健康", 1 => "警告", 2 => "异常", _ => "未知" };
                if (status == 0) healthy++; else warning++;

                rows.Add(new[]
                {
                    HtmlDoc.E(Get(d, "FriendlyName")),
                    MediaLabel(media),
                    $"{size / 1024.0 / 1024 / 1024:N0} GB",
                    $"<span class=\"tag {tag}\">{label}</span>"
                });
            }
        }
        catch (Exception ex)
        {
            doc.Note($"读取物理磁盘状态失败（通常需要管理员权限）：{HtmlDoc.E(ex.Message)}");
        }

        doc.Kpis(
            (healthy.ToString("N0"), "健康磁盘", "#2F9E44"),
            (warning.ToString("N0"), "需关注", warning > 0 ? "#E8590C" : ""),
            ("SMART", "预测故障", ""),
            ("实时", "数据来源", ""));

        doc.H2("物理磁盘");
        if (rows.Count == 0) doc.Empty("未能读取物理磁盘信息。");
        else doc.Table(new[] { "设备", "介质", "容量", "健康状态" }, rows, new[] { "", "100px", "110px", "110px" });

        doc.H2("SMART 故障预测");
        var smart = new List<string[]>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\wmi", "SELECT InstanceName, PredictFailure, Reason FROM MSStorageDriver_FailurePredictStatus");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
            {
                var fail = Get(o, "PredictFailure");
                var isFail = string.Equals(fail, "True", StringComparison.OrdinalIgnoreCase);
                smart.Add(new[]
                {
                    HtmlDoc.E(ShortInstance(Get(o, "InstanceName"))),
                    isFail ? "<span class=\"tag t-bad\">预测将故障</span>" : "<span class=\"tag t-ok\">正常</span>",
                    Get(o, "Reason")
                });
            }
        }
        catch
        {
            // 无权限或驱动不支持，静默降级
        }
        if (smart.Count == 0) doc.Empty("该设备不支持或需要管理员权限，未取到 SMART 预测数据。");
        else doc.Table(new[] { "设备实例", "预测", "原因码" }, smart, new[] { "", "110px", "90px" });

        doc.H2("卷状态");
        var vols = Wmi("Win32_LogicalDisk").Where(v => Get(v, "DriveType") == "3").ToList();
        doc.Table(new[] { "盘符", "文件系统", "总容量", "可用", "可用占比" },
            vols.Select(v =>
            {
                var size = ToLong(Get(v, "Size"));
                var free = ToLong(Get(v, "FreeSpace"));
                var pct = size > 0 ? free * 100.0 / size : 0;
                var tag = pct < 10 ? "t-bad" : pct < 20 ? "t-warn" : "t-ok";
                return new[]
                {
                    Get(v, "DeviceID"),
                    Get(v, "FileSystem"),
                    $"{size / 1024.0 / 1024 / 1024:N1} GB",
                    $"{free / 1024.0 / 1024 / 1024:N1} GB",
                    $"<span class=\"tag {tag}\">{pct:N1}%</span>"
                };
            }), new[] { "70px", "100px", "110px", "110px", "110px" });

        var path = HtmlReportBuilder.Save(doc.Build(), "磁盘健康报告");
        return new ReportPayload { Title = "磁盘健康", Subtitle = $"{healthy} 块健康 · {warning} 块需关注", HtmlPath = path };
    }

    // ---------------- 网络诊断 ----------------

    public static ReportPayload NetworkReport()
    {
        var doc = new HtmlDoc("网络诊断报告", $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToList();

        var rows = new List<string[]>();
        foreach (var a in adapters)
        {
            IPv4InterfaceStatistics st;
            try { st = a.GetIPv4Statistics(); } catch { continue; }
            var speed = a.Speed > 0 ? $"{a.Speed / 1_000_000.0:N0} Mbps" : "-";
            rows.Add(new[]
            {
                HtmlDoc.E(a.Name),
                HtmlDoc.E(a.NetworkInterfaceType.ToString()),
                speed,
                st.UnicastPacketsReceived.ToString("N0"),
                st.UnicastPacketsSent.ToString("N0"),
                TagByZero(st.IncomingPacketsDiscarded, "收丢弃"),
                TagByZero(st.OutgoingPacketsDiscarded, "发丢弃")
            });
        }

        var totalDiscard = adapters.Sum(a =>
        {
            try { var s = a.GetIPv4Statistics(); return s.IncomingPacketsDiscarded + s.OutgoingPacketsDiscarded; }
            catch { return 0L; }
        });

        doc.Kpis(
            (adapters.Count.ToString("N0"), "活动网卡", ""),
            (totalDiscard.ToString("N0"), "累计丢弃包", totalDiscard > 0 ? "#E8590C" : "#2F9E44"),
            (IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Length.ToString("N0"), "TCP 连接", ""),
            (IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners().Length.ToString("N0"), "UDP 端点", ""));

        doc.H2("网卡与底层计数");
        doc.Table(new[] { "网卡", "类型", "链路速率", "收包", "发包", "收侧丢弃", "发侧丢弃" },
            rows, new[] { "", "100px", "110px", "90px", "90px", "100px", "100px" });

        doc.H2("连通性与丢包（各 4 次）");
        var probes = new (string Label, string Host)[]
        {
            ("默认网关", DefaultGateway()),
            ("阿里 DNS", "223.5.5.5"),
            ("腾讯 DNS", "119.29.29.29"),
            ("百度", "www.baidu.com")
        };
        var probeRows = new List<string[]>();
        using var ping = new Ping();
        foreach (var (label, host) in probes)
        {
            if (string.IsNullOrEmpty(host)) { probeRows.Add(new[] { label, "-", "未取到", "", "", "" }); continue; }
            var ok = 0;
            var times = new List<long>();
            for (var i = 0; i < 4; i++)
            {
                try
                {
                    var reply = ping.Send(host, 1200);
                    if (reply is { Status: IPStatus.Success })
                    {
                        ok++;
                        times.Add(reply.RoundtripTime);
                    }
                }
                catch { /* 单次失败计入丢包 */ }
            }
            var loss = (4 - ok) * 100 / 4;
            var avg = times.Count > 0 ? times.Average() : 0;
            var jitter = times.Count > 1 ? times.Max() - times.Min() : 0;
            var tag = loss >= 50 ? "t-bad" : loss > 0 ? "t-warn" : "t-ok";
            probeRows.Add(new[]
            {
                label,
                host,
                $"<span class=\"tag {tag}\">{loss}%</span>",
                times.Count > 0 ? $"{avg:N1} ms" : "-",
                times.Count > 0 ? $"{times.Min()} ms" : "-",
                times.Count > 0 ? $"{times.Max()} ms" : "-"
            });
        }
        doc.Table(new[] { "目标", "地址", "丢包", "平均延迟", "最小", "最大" },
            probeRows, new[] { "110px", "150px", "80px", "110px", "90px", "90px" });

        doc.H2("TCP 连接数最多的进程");
        var top = TcpConnectionsByPid()
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .Select(kv => new { Name = ProcessNameSafe(kv.Key), Pid = kv.Key, Count = kv.Value })
            .ToList();
        if (top.Count == 0) doc.Empty("未能统计到进程级连接分布。");
        else doc.Table(new[] { "进程", "PID", "连接数" },
            top.Select(x => new[] { HtmlDoc.E(x.Name), x.Pid.ToString(), x.Count.ToString() }),
            new[] { "", "90px", "90px" });

        doc.Note("说明：网卡的「丢弃包」计数若长期为 0，说明链路层本身没有丢包；" +
                 "若游戏内仍丢包，重点检查网卡节能（EEE / Green Ethernet）、双网卡并存与加速器节点。");

        var path = HtmlReportBuilder.Save(doc.Build(), "网络诊断报告");
        return new ReportPayload { Title = "网络诊断", Subtitle = $"{adapters.Count} 张活动网卡 · 累计丢弃 {totalDiscard} 包", HtmlPath = path };
    }

    // ---------------- 已装更新 ----------------

    public static ReportPayload UpdateList()
    {
        var doc = new HtmlDoc("系统更新清单", $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var list = Wmi("Win32_QuickFixEngineering", "HotFixID", "Description", "InstalledOn", "InstalledBy");

        var ordered = list
            .OrderByDescending(o => ParseDate(Get(o, "InstalledOn")) ?? DateTime.MinValue)
            .ToList();

        doc.Kpis(
            (list.Count.ToString("N0"), "补丁总数", ""),
            (ordered.Count > 0 ? Friendly(Get(ordered[0], "InstalledOn")) : "-", "最近安装", ""),
            (Environment.OSVersion.Version.ToString(), "系统版本", ""));

        doc.H2("补丁清单（按安装时间倒序）");
        doc.Table(new[] { "补丁编号", "说明", "安装时间", "安装者" },
            ordered.Select(o => new[]
            {
                $"<b>{HtmlDoc.E(Get(o, "HotFixID"))}</b>",
                HtmlDoc.E(Get(o, "Description")),
                Friendly(Get(o, "InstalledOn")),
                HtmlDoc.E(Get(o, "InstalledBy"))
            }), new[] { "130px", "", "130px", "130px" });

        var path = HtmlReportBuilder.Save(doc.Build(), "系统更新清单");
        return new ReportPayload { Title = "已装更新", Subtitle = $"共 {list.Count} 个补丁", HtmlPath = path };
    }

    // ---------------- 通用辅助 ----------------

    private static List<ManagementObject> Wmi(string cls, params string[] props)
    {
        var list = new List<ManagementObject>();
        try
        {
            var fields = props.Length > 0 ? string.Join(",", props) : "*";
            using var searcher = new ManagementObjectSearcher($"SELECT {fields} FROM {cls}");
            foreach (var o in searcher.Get().Cast<ManagementObject>())
                list.Add(o);
        }
        catch
        {
            // WMI 不可用时返回空集合，报告里显示为无数据
        }
        return list;
    }

    private static string Get(ManagementBaseObject o, string prop)
    {
        try
        {
            var v = o[prop];
            return v?.ToString() ?? "";
        }
        catch { return ""; }
    }

    private static int ToInt(string s) => int.TryParse(s, out var v) ? v : -1;
    private static long ToLong(string s) => long.TryParse(s, out var v) ? v : 0;

    private static string MediaLabel(int media) => media switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "未知"
    };

    private static string FormatAdapterRam(string bytes)
        => long.TryParse(bytes, out var b) && b > 0
            ? $"{b / 1024.0 / 1024 / 1024:N1} GB"
            : "共享";

    private static string ToDate(string wmiDate)
    {
        try { return ManagementDateTimeConverter.ToDateTime(wmiDate).ToString("yyyy-MM-dd HH:mm:ss"); }
        catch { return wmiDate; }
    }

    private static DateTime? ParseDate(string s)
        => DateTime.TryParse(s, out var d) ? d : null;

    private static string Friendly(string s)
        => DateTime.TryParse(s, out var d) ? d.ToString("yyyy-MM-dd") : s;

    private static string FirstLine(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var line = text.Replace("\r", "").Split('\n')[0].Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    private static string ShortInstance(string instance)
        => string.IsNullOrEmpty(instance) ? "" : instance.Split('\\').LastOrDefault() ?? instance;

    private static string TagByZero(long value, string label)
        => value == 0
            ? $"<span class=\"tag t-ok\">{label} 0</span>"
            : $"<span class=\"tag t-warn\">{label} {value}</span>";

    private static string DefaultGateway()
    {
        try
        {
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up) continue;
                if (n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var gw = n.GetIPProperties().GatewayAddresses
                    .Select(g => g.Address)
                    .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                if (gw != null) return gw.ToString();
            }
        }
        catch { }
        return "";
    }

    private static long SafeWs(Process p)
    {
        try { return p.WorkingSet64; } catch { return 0; }
    }

    private static string SafeName(Process p)
    {
        try { return p.ProcessName; } catch { return "?"; }
    }

    private static string SafePid(Process p)
    {
        try { return p.Id.ToString(); } catch { return "?"; }
    }

    private static string SafeThreads(Process p)
    {
        try { return p.Threads.Count.ToString(); } catch { return "?"; }
    }

    /// <summary>借助 netstat -ano 建立 PID 到连接数的映射（.NET 的 IPGlobalProperties 不暴露 PID）。</summary>
    private static Dictionary<int, int> TcpConnectionsByPid()
    {
        var map = new Dictionary<int, int>();
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return map;
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;
                if (!int.TryParse(parts[^1], out var pid)) continue;
                map[pid] = map.TryGetValue(pid, out var n) ? n + 1 : 1;
            }
        }
        catch { }
        return map;
    }

    private static string ProcessNameSafe(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; }
        catch { return $"PID {pid}"; }
    }
}
