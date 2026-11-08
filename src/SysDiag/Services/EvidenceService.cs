using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.IO.Compression;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 取证类信息的采集：机器码、驱动/设备记录、USB 清单、PnP 事件、证据包。
/// 全部只读；不修改注册表、不安装任何东西。
/// </summary>
public static class EvidenceService
{
    // ==================== 1. 机器码（HWID） ====================

    public static List<HwidField> CollectHwid()
    {
        var list = new List<HwidField>();

        void Add(string group, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            if (value is "-" or "None" or "Default string" or "To be filled by O.E.M.") return;
            list.Add(new HwidField { Group = group, Name = name, Value = value });
        }

        try
        {
            var csp = Wmi("SELECT UUID, IdentifyingNumber, Vendor, Name FROM Win32_ComputerSystemProduct").FirstOrDefault();
            Add("系统标识", "SMBIOS UUID", Str(csp, "UUID"));
            Add("系统标识", "整机序列号", Str(csp, "IdentifyingNumber"));
            Add("系统标识", "整机型号", Str(csp, "Name"));
            Add("系统标识", "MachineGuid", RegStr(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid"));
            Add("系统标识", "Windows 产品 ID", RegStr(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductId"));
            Add("系统标识", "计算机名", Environment.MachineName);
        }
        catch { /* 采集失败不影响其它分组 */ }

        try
        {
            var bb = Wmi("SELECT Manufacturer, Product, SerialNumber FROM Win32_BaseBoard").FirstOrDefault();
            Add("主板与固件", "主板厂商", Str(bb, "Manufacturer"));
            Add("主板与固件", "主板型号", Str(bb, "Product"));
            Add("主板与固件", "主板序列号", Str(bb, "SerialNumber"));

            var bios = Wmi("SELECT Manufacturer, SMBIOSBIOSVersion, SerialNumber, ReleaseDate FROM Win32_BIOS").FirstOrDefault();
            Add("主板与固件", "BIOS 厂商", Str(bios, "Manufacturer"));
            Add("主板与固件", "BIOS 版本", Str(bios, "SMBIOSBIOSVersion"));
            Add("主板与固件", "BIOS 序列号", Str(bios, "SerialNumber"));
            Add("主板与固件", "BIOS 日期", WmiDate(Str(bios, "ReleaseDate")));
        }
        catch { }

        try
        {
            var cpu = Wmi("SELECT Name, ProcessorId, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor").FirstOrDefault();
            Add("处理器", "型号", Str(cpu, "Name"));
            Add("处理器", "ProcessorId", Str(cpu, "ProcessorId"));
            Add("处理器", "核心 / 线程", $"{Str(cpu, "NumberOfCores")} / {Str(cpu, "NumberOfLogicalProcessors")}");
        }
        catch { }

        try
        {
            var disks = Wmi("SELECT Index, Model, SerialNumber, Size FROM Win32_DiskDrive")
                .OrderBy(d => Str(d, "Index")).ToList();
            foreach (var d in disks)
            {
                var size = Str(d, "Size");
                var sizeText = long.TryParse(size, out var bytes) && bytes > 0
                    ? $"{bytes / 1024.0 / 1024 / 1024:N0} GB"
                    : "";
                Add("磁盘", $"磁盘 {Str(d, "Index")} · {Str(d, "Model")}",
                    $"{Str(d, "SerialNumber").TrimEnd('.')}  {sizeText}".Trim());
            }
        }
        catch { }

        try
        {
            // 只保留物理网卡：虚拟网卡（Wi-Fi Direct / Hyper-V / VPN / 蓝牙 PAN）的 MAC 不固定
            string[] virtualHints = { "Virtual", "Wi-Fi Direct", "Hyper-V", "Bluetooth", "VPN", "TAP-", "Npcap", "Loopback", "Tunnel" };
            var physical = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.GetPhysicalAddress().GetAddressBytes().Length == 6)
                .Where(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.Ethernet3Megabit)
                .Where(n => !virtualHints.Any(h => (n.Description ?? "").Contains(h, StringComparison.OrdinalIgnoreCase)))
                .Where(n => !virtualHints.Any(h => (n.Name ?? "").Contains(h, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(n => n.Name);

            foreach (var nic in physical)
            {
                Add("网络适配器", nic.Name,
                    $"{string.Join("-", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")))}  [{nic.NetworkInterfaceType}]");
            }
        }
        catch { }

        try
        {
            foreach (var v in Wmi("SELECT DeviceID, VolumeSerialNumber, Size FROM Win32_LogicalDisk WHERE DriveType=3"))
                Add("卷序列号", $"卷 {Str(v, "DeviceID")}", Str(v, "VolumeSerialNumber"));
        }
        catch { }

        return list;
    }

    public static ReportPayload HwidReport()
    {
        var fields = CollectHwid();
        var html = HtmlReportBuilder.BuildHwid(fields);
        var path = HtmlReportBuilder.Save(html, "机器码清单");

        var sb = new StringBuilder();
        sb.AppendLine($"【机器码清单】{DateTime.Now:yyyy-MM-dd HH:mm:ss}  共 {fields.Count} 项");
        sb.AppendLine();
        foreach (var g in fields.GroupBy(f => f.Group))
        {
            sb.AppendLine($"[{g.Key}]");
            foreach (var f in g) sb.AppendLine($"  {f.Name} : {f.Value}");
            sb.AppendLine();
        }
        sb.AppendLine("说明：游戏厂商的「机器码」通常是这些字段的组合哈希，各家算法不同，");
        sb.AppendLine("本页只是本机原始值，与封禁系统里的编号不一定一致。");

        return new ReportPayload
        {
            Title = "机器码（HWID）",
            Subtitle = $"{fields.Count} 个字段 · {string.Join(" / ", fields.GroupBy(f => f.Group).Select(g => $"{g.Key} {g.Count()}"))}",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    // ==================== 2. 驱动与设备记录 ====================

    private const string SetupLog = @"C:\Windows\INF\setupapi.dev.log";

    public static List<DeviceEvent> ReadDriverHistory(int max = 300)
    {
        var list = new List<DeviceEvent>();
        if (!File.Exists(SetupLog)) return list;

        string[] lines;
        try
        {
            var info = new FileInfo(SetupLog);
            if (info.Length > 40L * 1024 * 1024) return list;
            lines = File.ReadAllLines(SetupLog, Encoding.UTF8);
        }
        catch
        {
            return list;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith(">>>  [", StringComparison.Ordinal)) continue;

            var title = line[5..].Trim();
            if (title.StartsWith('[')) title = title[1..];
            var close = title.LastIndexOf(']');
            if (close > 0) title = title[..close];

            DateTime? time = null;
            string caller = "";
            string status = "";

            for (var j = i + 1; j < lines.Length; j++)
            {
                var l = lines[j];
                if (l.StartsWith(">>>  [", StringComparison.Ordinal)) break;

                if (time == null)
                {
                    var idx = l.IndexOf("Section start", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        var raw = l[(idx + "Section start".Length)..].Trim();
                        if (DateTime.TryParse(raw, out var dt)) time = dt;
                    }
                }

                if (caller.Length == 0)
                {
                    var ci = l.IndexOf("cmd:", StringComparison.Ordinal);
                    if (ci >= 0) caller = l[(ci + 4)..].Trim().Trim('"');
                }

                if (l.StartsWith("<<<  [", StringComparison.Ordinal))
                {
                    var s = l[5..].Trim();
                    if (s.StartsWith('[')) s = s[1..];
                    var sc = s.LastIndexOf(']');
                    if (sc > 0) s = s[..sc];
                    status = s;
                    break;
                }
            }

            var action = MapAction(title);
            if (action.Length == 0) continue;

            var target = title;
            var dash = title.IndexOf(" - ", StringComparison.Ordinal);
            if (dash > 0)
            {
                action = action.Length > 0 ? action : title[..dash].Trim();
                target = title[(dash + 3)..].Trim();
            }

            list.Add(new DeviceEvent
            {
                Time = time,
                Action = action,
                Target = target,
                Caller = caller,
                Status = status.Length == 0 ? "-" : status
            });
        }

        return list
            .OrderByDescending(e => e.Time ?? DateTime.MinValue)
            .Take(max)
            .ToList();
    }

    private static string MapAction(string title)
    {
        if (title.StartsWith("Device Install (DiInstallDriver)", StringComparison.OrdinalIgnoreCase)) return "安装驱动";
        if (title.StartsWith("Device Install (Hardware initiated)", StringComparison.OrdinalIgnoreCase)) return "接入设备";
        if (title.StartsWith("Device Install", StringComparison.OrdinalIgnoreCase)) return "安装设备";
        if (title.StartsWith("Device Remove", StringComparison.OrdinalIgnoreCase)) return "移除设备";
        if (title.StartsWith("Driver Uninstall", StringComparison.OrdinalIgnoreCase)) return "卸载驱动";
        if (title.StartsWith("Setup Import Driver Package", StringComparison.OrdinalIgnoreCase)) return "导入驱动包";
        if (title.StartsWith("Driver Install", StringComparison.OrdinalIgnoreCase)) return "安装驱动";
        return "";
    }

    public static ReportPayload DriverHistoryReport()
    {
        var events = ReadDriverHistory();
        var fail = events.Count(e => e.StatusCss == "t-bad");

        var html = HtmlReportBuilder.BuildDriverHistory(events);
        var path = HtmlReportBuilder.Save(html, "设备与驱动记录");

        var sb = new StringBuilder();
        sb.AppendLine($"【设备与驱动记录】{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"来源：{SetupLog}（Windows 设备安装日志，只保留最近一段，会被滚动覆盖）");
        sb.AppendLine($"共 {events.Count} 条，失败 {fail} 条。");
        sb.AppendLine();
        foreach (var e in events.Take(30))
            sb.AppendLine($"  {e.TimeText}  {e.Action,-6}  {Trim(e.Target, 70)}  [{e.Status}]");

        return new ReportPayload
        {
            Title = "设备与驱动记录",
            Subtitle = $"{events.Count} 条 · 失败 {fail} 条",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    // ==================== 3. USB 设备清单 ====================

    public static List<UsbDeviceInfo> ReadUsbDevices()
    {
        var raw = new List<UsbDeviceInfo>();

        try
        {
            using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB");
            if (usb == null) return raw;

            foreach (var vidPid in usb.GetSubKeyNames())
            {
                using var vidKey = usb.OpenSubKey(vidPid);
                if (vidKey == null) continue;

                foreach (var instance in vidKey.GetSubKeyNames())
                {
                    using var ik = vidKey.OpenSubKey(instance);
                    if (ik == null) continue;

                    var desc = CleanDesc(ik.GetValue("DeviceDesc") as string);
                    var friendly = ik.GetValue("FriendlyName") as string;
                    var name = string.IsNullOrWhiteSpace(friendly) ? desc : friendly;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var location = ik.GetValue("LocationInformation") as string ?? "";

                    // 灯光、虚拟设备之类不是真实外设，列进去只会让清单变噪音
                    if (location.Contains("Virtual", StringComparison.OrdinalIgnoreCase)) continue;

                    var note = "";
                    if (name.Contains("Device Descriptor Request Failed", StringComparison.OrdinalIgnoreCase))
                        note = "描述符读取失败";
                    else if (vidPid.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
                        note = "根集线器";
                    else if (vidPid.StartsWith("VID_0000", StringComparison.OrdinalIgnoreCase))
                        note = "无有效 VID";

                    raw.Add(new UsbDeviceInfo
                    {
                        Name = name,
                        VidPid = BaseVidPid(vidPid),
                        Instance = instance,
                        Serial = instance.Contains('&') ? "" : instance,
                        Service = ik.GetValue("Service") as string ?? "",
                        Location = location,
                        ContainerId = (ik.GetValue("ContainerID") as string ?? "").Trim('{', '}'),
                        Note = note
                    });
                }
            }
        }
        catch
        {
            return raw;
        }

        // 同一型号（同 VID/PID、同序列号）合并成一条，端口位置并列显示
        return raw
            .GroupBy(d => $"{d.VidPid}|{d.Name}|{d.Serial}|{d.Note}")
            .Select(g => new UsbDeviceInfo
            {
                Name = g.Select(x => x.Name).FirstOrDefault(n => n.Length > 0) ?? "",
                VidPid = g.Select(x => x.VidPid).OrderBy(v => v.Length).First(),
                Instance = g.Select(x => x.Instance).FirstOrDefault(i => !i.Contains('&')) ?? g.First().Instance,
                Serial = g.Select(x => x.Serial).FirstOrDefault(s => s.Length > 0) ?? "",
                Service = g.Select(x => x.Service).FirstOrDefault(s => s.Length > 0) ?? "",
                Location = MergeLocations(g.Select(x => x.Location)),
                Note = g.Select(x => x.Note).FirstOrDefault(n => n.Length > 0) ?? "",
                ContainerId = g.Key
            })
            .OrderBy(d => d.Note.Length > 0)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BaseVidPid(string vidPid)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            vidPid, @"^(VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4})");
        return m.Success ? m.Groups[1].Value : vidPid;
    }

    /// <summary>端口位置可能有多处（同一设备插过不同口），只显示前两个，其余计数。</summary>
    private static string MergeLocations(IEnumerable<string> locations)
    {
        var list = locations.Where(l => l.Length > 0).Distinct().ToList();
        return list.Count switch
        {
            0 => "",
            <= 2 => string.Join(" / ", list),
            _ => string.Join(" / ", list.Take(2)) + $" 等 {list.Count} 处"
        };
    }

    /// <summary>把 "@usb.inf,%generic.mfg%;(标准 USB 主控制器)" 里的说明取出来。</summary>
    private static string CleanDesc(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return "";
        var idx = desc.LastIndexOf(';');
        return (idx >= 0 ? desc[(idx + 1)..] : desc).Trim();
    }

    public static ReportPayload UsbHistoryReport()
    {
        var devices = ReadUsbDevices();
        var abnormal = devices.Count(d => d.Note.Length > 0);

        var html = HtmlReportBuilder.BuildUsbDevices(devices);
        var path = HtmlReportBuilder.Save(html, "USB设备清单");

        var sb = new StringBuilder();
        sb.AppendLine($"【USB 设备清单】{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("来源：注册表 Enum\\USB（本机记录过的 USB 设备；含当前未接入的历史条目）");
        sb.AppendLine($"共 {devices.Count} 个，需注意 {abnormal} 个。");
        sb.AppendLine();
        foreach (var d in devices.Take(30))
            sb.AppendLine($"  {d.VidPid,-28} {Trim(d.Name, 40),-40} 序列号 {(d.Serial.Length > 0 ? d.Serial : "(无)")}  {d.Location} {(d.Note.Length > 0 ? "[" + d.Note + "]" : "")}");
        sb.AppendLine();
        sb.AppendLine("说明：首次/最后接入时间存放在设备属性里，读取需要管理员权限；本清单不含时间字段。");

        return new ReportPayload
        {
            Title = "USB 设备清单",
            Subtitle = $"{devices.Count} 个设备 · 需注意 {abnormal} 个",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    // ==================== 4. PnP 事件分析 ====================

    private const string PnpLog = "Microsoft-Windows-Kernel-PnP/Configuration";

    public static List<PnpEventRow> ReadPnpEvents(int days = 14, int max = 300)
    {
        var list = new List<PnpEventRow>();
        try
        {
            var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var query = new EventLogQuery(PnpLog, PathType.LogName,
                $"*[System[TimeCreated[@SystemTime>='{since}']]]");

            using var reader = new EventLogReader(query);
            for (var i = 0; i < max; i++)
            {
                using var rec = reader.ReadEvent();
                if (rec == null) break;

                var msg = "";
                try { msg = rec.FormatDescription() ?? ""; }
                catch { msg = rec.ToXml() ?? ""; }
                msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();

                list.Add(new PnpEventRow
                {
                    Time = rec.TimeCreated ?? DateTime.MinValue,
                    Id = rec.Id,
                    Level = rec.Level switch
                    {
                        1 => "严重",
                        2 => "错误",
                        3 => "警告",
                        4 => "信息",
                        _ => "信息"
                    },
                    Device = ExtractDevice(msg),
                    Message = Trim(msg, 220)
                });
            }
        }
        catch
        {
            return list;
        }

        return list.OrderBy(e => e.Time).ToList();
    }

    private static string ExtractDevice(string msg)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            msg, @"(USB|PCI|HDAUDIO|SWD|ROOT|DISPLAY|BTH|ACPI|SCSI|IDE|STORAGE)\\VID_[0-9A-F]{4}&PID_[0-9A-F]{4}\\?[^\s,;]*",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return Trim(m.Value.TrimEnd('。', '，', '.', ',', ';'), 90);

        m = System.Text.RegularExpressions.Regex.Match(msg, @"(USB|PCI|HDAUDIO|SWD|ROOT|DISPLAY|BTH|ACPI|SCSI|IDE|STORAGE)\\[^\s,;]+",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? Trim(m.Value.TrimEnd('。', '，', '.', ',', ';'), 90) : "";
    }

    public static ReportPayload PnpReport()
    {
        var rows = ReadPnpEvents();
        var err = rows.Count(r => r.Level is "错误" or "严重");

        var html = HtmlReportBuilder.BuildPnp(rows);
        var path = HtmlReportBuilder.Save(html, "PnP事件分析");

        var sb = new StringBuilder();
        sb.AppendLine($"【PnP 事件分析】{DateTime.Now:yyyy-MM-dd HH:mm:ss}  近 14 天");
        sb.AppendLine($"来源：事件日志 {PnpLog}，共 {rows.Count} 条，错误 {err} 条。");
        sb.AppendLine();
        foreach (var g in rows.GroupBy(r => r.Id).OrderByDescending(g => g.Count()).Take(6))
            sb.AppendLine($"  事件 {g.Key} × {g.Count()}");
        sb.AppendLine();
        if (err > 0)
        {
            sb.AppendLine("错误明细：");
            foreach (var r in rows.Where(r => r.Level is "错误" or "严重").Take(10))
                sb.AppendLine($"  {r.TimeText}  {r.Id}  {r.Device}  {Trim(r.Message, 100)}");
        }
        else
        {
            sb.AppendLine("近 14 天没有设备启动失败或驱动报错的记录。");
        }

        return new ReportPayload
        {
            Title = "PnP 事件分析",
            Subtitle = $"近 14 天 · {rows.Count} 条 · 错误 {err} 条",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    // ==================== 5. 一键证据包 ====================

    public sealed class PackResult
    {
        public string ZipPath = "";
        public long ZipSize;
        public List<(string Name, long Size)> Files = new();
    }

    public static PackResult BuildPack()
    {
        AppPaths.Ensure();
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = Path.Combine(AppPaths.OutputDir, "证据包-" + stamp);
        Directory.CreateDirectory(dir);

        var result = new PackResult();

        void WriteText(string name, string content)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllText(p, content, HtmlReportBuilder.Utf8Bom);
            result.Files.Add((name, new FileInfo(p).Length));
        }

        // 1) 机器码
        var hwid = CollectHwid();
        var hwidText = new StringBuilder();
        hwidText.AppendLine($"机器码清单  采集时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        hwidText.AppendLine(new string('=', 50));
        foreach (var g in hwid.GroupBy(f => f.Group))
        {
            hwidText.AppendLine($"[{g.Key}]");
            foreach (var f in g) hwidText.AppendLine($"  {f.Name} : {f.Value}");
            hwidText.AppendLine();
        }
        WriteText("01-机器码.txt", hwidText.ToString());

        // 2) 驱动与设备记录
        var dev = ReadDriverHistory();
        var csv = new StringBuilder("时间,操作,对象,执行者,状态\r\n");
        foreach (var e in dev)
            csv.AppendLine($"{Csv(e.TimeText)},{Csv(e.Action)},{Csv(e.Target)},{Csv(e.Caller)},{Csv(e.Status)}");
        WriteText("02-设备与驱动记录.csv", csv.ToString());

        // 3) USB 清单
        var usb = ReadUsbDevices();
        var csv2 = new StringBuilder("名称,VID_PID,序列号,服务,端口位置,备注\r\n");
        foreach (var d in usb)
            csv2.AppendLine($"{Csv(d.Name)},{Csv(d.VidPid)},{Csv(d.Serial)},{Csv(d.Service)},{Csv(d.Location)},{Csv(d.Note)}");
        WriteText("03-USB设备清单.csv", csv2.ToString());

        // 4) PnP 事件
        var pnp = ReadPnpEvents();
        var csv3 = new StringBuilder("时间,事件ID,级别,设备,说明\r\n");
        foreach (var r in pnp)
            csv3.AppendLine($"{Csv(r.TimeText)},{r.Id},{Csv(r.Level)},{Csv(r.Device)},{Csv(r.Message)}");
        WriteText("04-PnP事件.csv", csv3.ToString());

        // 4.5) 深度硬件痕迹：DMA / 采集卡 / 输入模拟
        var deep = DeepTraceService.Scan(true);
        var csvDeep = new StringBuilder("分组,状态,检查项,结果,依据\r\n");
        foreach (var i in deep.Items)
            csvDeep.AppendLine($"{Csv(DeepTraceService.GroupName(i.Group))},{Csv(i.LevelText)},{Csv(i.Name)},{Csv(i.Detail)},{Csv(i.Basis)}");
        WriteText("08-深度硬件痕迹.csv", csvDeep.ToString());

        // 5) 系统体检 / 磁盘健康 / 已装更新（现成的 HTML 报告）
        foreach (var (title, payload, name) in new (string, Func<ReportPayload>, string)[]
                 {
                     ("体检", DiagnosticsService.SystemCheckup, "05-系统体检.html"),
                     ("磁盘", DiagnosticsService.DiskHealth, "06-磁盘健康.html"),
                     ("更新", DiagnosticsService.UpdateList, "07-已装更新.html")
                 })
        {
            try
            {
                var p = payload();
                if (p != null && File.Exists(p.HtmlPath))
                {
                    File.Copy(p.HtmlPath, Path.Combine(dir, name), true);
                    result.Files.Add((name, new FileInfo(Path.Combine(dir, name)).Length));
                }
            }
            catch
            {
                WriteText(name.Replace(".html", "-失败.txt"), $"{title} 报告生成失败：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            }
        }

        // 6) 说明
        var readme = new StringBuilder();
        readme.AppendLine("破烂优化 · 取证证据包");
        readme.AppendLine($"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        readme.AppendLine($"采集机器：{Environment.MachineName}（{Environment.OSVersion}）");
        readme.AppendLine();
        readme.AppendLine("文件说明：");
        readme.AppendLine("  01-机器码.txt           SMBIOS UUID、主板/BIOS 序列号、ProcessorId、磁盘序列号、MachineGuid、MAC 等原始值");
        readme.AppendLine("  02-设备与驱动记录.csv   Windows 设备安装日志（setupapi.dev.log）中的驱动安装/卸载事件");
        readme.AppendLine("  03-USB设备清单.csv      注册表 Enum\\USB 中记录过的 USB 设备");
        readme.AppendLine("  04-PnP事件.csv          近 14 天设备配置与启动事件（Kernel-PnP）");
        readme.AppendLine("  05~07                 系统体检、磁盘健康、已装更新报告");
        readme.AppendLine("  08-深度硬件痕迹.csv    DMA / 采集卡 / 输入模拟三条通路的只读检查");
        readme.AppendLine("  SHA256SUMS.txt         上述文件的 SHA256，用于证明打包后未被改动");
        readme.AppendLine();
        readme.AppendLine("已知限制：");
        readme.AppendLine("  · 本包不支持证明“没运行过某个程序”，只能呈现系统记录到的痕迹；");
        readme.AppendLine("  · 机器码是原始字段，游戏厂商的封禁编号是它们的组合哈希，算法不同，数值不一定相同；");
        readme.AppendLine("  · 文件删除记录（USN 日志）需要管理员权限，本包未包含。");
        WriteText("说明.txt", readme.ToString());

        // 7) SHA256 清单
        var sums = new StringBuilder();
        foreach (var f in Directory.GetFiles(dir).OrderBy(f => f))
        {
            var name = Path.GetFileName(f);
            if (name == "SHA256SUMS.txt") continue;
            sums.AppendLine($"{Hash(f)}  {name}");
        }
        File.WriteAllText(Path.Combine(dir, "SHA256SUMS.txt"), sums.ToString(), HtmlReportBuilder.Utf8Bom);

        // 打包
        var zip = Path.Combine(AppPaths.OutputDir, $"证据包-{stamp}.zip");
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(dir, zip, CompressionLevel.Optimal, false);
        result.ZipPath = zip;
        result.ZipSize = new FileInfo(zip).Length;

        try { Directory.Delete(dir, true); } catch { }

        return result;
    }

    public static ReportPayload EvidencePackReport()
    {
        var pack = BuildPack();

        var doc = new HtmlDoc("取证证据包",
            $"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　包含 {pack.Files.Count} 个文件　|　" +
            $"压缩后 {pack.ZipSize / 1024.0 / 1024:N2} MB");
        doc.Note("打包内容全部来自本机只读采集；<b>SHA256SUMS.txt</b> 可用于证明文件在打包后未被改动。<br>" +
                 "提交申诉时建议只提供必要文件，避免泄露与本问题无关的机器信息。");
        doc.H2("包内文件");
        doc.Table(new[] { "文件", "大小" },
            pack.Files.Select(f => new[]
            {
                HtmlDoc.E(f.Name),
                f.Size >= 1024 * 1024 ? $"{f.Size / 1024.0 / 1024:N1} MB" : $"{f.Size / 1024.0:N1} KB"
            }),
            new[] { "", "120px" });
        doc.H2("存放位置");
        doc.Note($"<code>{HtmlDoc.E(pack.ZipPath)}</code><br>可直接把整个 zip 发给客服或上传申诉通道。");
        doc.H2("未包含的内容");
        doc.Note("文件删除记录（NTFS USN 日志）读取需要管理员权限，未纳入本包；<br>" +
                 "事件日志（.evtx 原始文件）体积大且含全系统记录，也未纳入，需要时可用「事件查看器」单独导出。");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "取证证据包");

        var sb = new StringBuilder();
        sb.AppendLine($"【取证证据包】{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"文件 {pack.Files.Count} 个，压缩后 {pack.ZipSize / 1024.0 / 1024:N2} MB");
        sb.AppendLine($"路径：{pack.ZipPath}");
        sb.AppendLine();
        foreach (var f in pack.Files)
            sb.AppendLine($"  {f.Name}  ({f.Size / 1024.0:N1} KB)");
        sb.AppendLine();
        sb.AppendLine("SHA256SUMS.txt 可用于证明文件未被改动。");

        return new ReportPayload
        {
            Title = "取证证据包",
            Subtitle = $"{pack.Files.Count} 个文件 · {pack.ZipSize / 1024.0 / 1024:N2} MB · {Path.GetFileName(pack.ZipPath)}",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    // ==================== 工具 ====================

    private static string Hash(string path)
    {
        using var sha = SHA256.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string Csv(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    private static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    internal static string Str(Dictionary<string, object> dict, string key)
        => dict != null && dict.TryGetValue(key, out var v) && v != null ? v.ToString() : "";

    /// <summary>WMI 的 CIM 时间（20251013000000.000000+000）转成 yyyy-MM-dd。</summary>
    internal static string WmiDate(string cimDate)
    {
        if (string.IsNullOrWhiteSpace(cimDate)) return "";
        try { return ManagementDateTimeConverter.ToDateTime(cimDate).ToString("yyyy-MM-dd"); }
        catch { return cimDate.Length >= 8 ? cimDate[..8] : cimDate; }
    }

    internal static List<Dictionary<string, object>> Wmi(string query)
    {
        var list = new List<Dictionary<string, object>>();
        try
        {
            using var searcher = new ManagementObjectSearcher(query);
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in mo.Properties) d[p.Name] = p.Value;
                list.Add(d);
            }
        }
        catch
        {
            // WMI 个别类不可用属正常，返回已取到的部分
        }
        return list;
    }

    internal static string RegStr(string path, string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            return key?.GetValue(name) as string ?? "";
        }
        catch
        {
            return "";
        }
    }
}
