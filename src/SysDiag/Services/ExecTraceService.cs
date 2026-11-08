using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 程序执行记录：把 Windows 里三处“谁在什么时候跑过”的记录汇总起来。
/// <para>
/// BAM / DAM：注册表里每个程序最后执行时间，精度到秒，无需管理员。
/// AmCache：程序安装与文件清单（路径、版本、发布者、链接时间），需要挂载 hive。
/// SRUM：每应用网络用量，库文件存在性体检（逐应用流量需解析 ESE，本工具不猜数值）。
/// </para>
/// 全部只读：AmCache 是加载副本 hive，读完立刻卸载，不改动原文件。
/// </summary>
public static class ExecTraceService
{
    private const string AmcacheMount = "SysDiagAmcache";

    private static readonly UIntPtr Hklm = new(0x80000002);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegLoadKey(UIntPtr hKey, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegUnLoadKey(UIntPtr hKey, string subKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, uint max);

    // ---------- 公开入口 ----------

    /// <summary>BAM / DAM：每个程序最后一次执行时间。HKLM 下的记录需要管理员权限。</summary>
    public static List<ExecRecord> ReadBam(out string note, int max = 400)
    {
        note = "";
        var list = new List<ExecRecord>();

        if (!IsAdmin)
        {
            note = "BAM 记录在 HKLM\\SYSTEM 下，普通权限读不到。点「以管理员身份重启」后再看。";
            return list;
        }

        string[] roots =
        {
            @"SYSTEM\CurrentControlSet\Services\bam\State\UserSettings",
            @"SYSTEM\CurrentControlSet\Services\bam\UserSettings",
            @"SYSTEM\CurrentControlSet\Services\dam\State\UserSettings"
        };

        var devices = BuildDeviceMap();

        foreach (var root in roots)
        {
            try
            {
                using var rootKey = Registry.LocalMachine.OpenSubKey(root);
                if (rootKey == null) continue;

                foreach (var sid in rootKey.GetSubKeyNames())
                {
                    using var userKey = rootKey.OpenSubKey(sid);
                    if (userKey == null) continue;

                    foreach (var rawName in userKey.GetValueNames())
                    {
                        var path = NormalizeDevicePath(rawName, devices);
                        var bytes = userKey.GetValue(rawName) as byte[];
                        var time = FromFileTime(bytes, 0);

                        list.Add(new ExecRecord
                        {
                            Name = FileNameOf(path),
                            Path = path,
                            Source = "BAM",
                            Time = time,
                            Extra = sid.Length > 12 ? "用户 " + sid[..12] + "…" : sid
                        });
                    }
                }
            }
            catch
            {
                // 单个根键读不到就跳过，继续试下一个
            }
        }

        if (list.Count == 0)
            note = "已具备管理员权限，但没有读到 BAM 记录（这台机器可能关闭了活动记录）。";

        return list
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Time ?? DateTime.MinValue).First())
            .OrderByDescending(r => r.Time ?? DateTime.MinValue)
            .Take(max)
            .ToList();
    }

    /// <summary>AmCache：程序安装与文件清单。需要管理员（要挂载 hive）；失败时返回原因。</summary>
    public static (List<ExecRecord> Items, string Note) ReadAmCache(int max = 200)
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var hive = Path.Combine(win, @"AppCompat\Programs\Amcache.hve");

        var probe = ProbeFile(hive);
        if (probe.State == "denied")
            return (new List<ExecRecord>(), "Amcache.hve 存在但拒绝访问——这个文件在 Windows\\AppCompat 下，需要管理员权限。");

        if (probe.State != "ok")
            return (new List<ExecRecord>(), "未找到 Amcache.hve（精简系统常见，也可能还没生成过记录）。");

        // 上次异常退出可能留下挂载点，先清掉
        try { RegUnLoadKey(Hklm, AmcacheMount); } catch { }

        var rc = RegLoadKey(Hklm, AmcacheMount, hive);
        if (rc != 0)
        {
            var hint = rc switch
            {
                5 => "拒绝访问——需要管理员权限。",
                32 => "文件正被系统占用（可能刚被别的工具读过），稍后重试。",
                _ => "需要管理员权限，或 hive 正被占用。"
            };

            return (new List<ExecRecord>(), $"加载 Amcache.hve 失败（错误 {rc}）：{hint}");
        }

        var list = new List<ExecRecord>();
        var subKeyCount = 0;

        try
        {
            // Win10 / 11：Root\InventoryApplicationFile，子键名是文件标识
            using (var root = Registry.LocalMachine.OpenSubKey($@"{AmcacheMount}\Root\InventoryApplicationFile"))
            {
                if (root != null)
                {
                    var ids = root.GetSubKeyNames();
                    subKeyCount = ids.Length;

                    foreach (var id in ids)
                    {
                        using var item = root.OpenSubKey(id);
                        if (item == null) continue;

                        // Win11 起这些值常以 REG_BINARY（UTF-16LE）存放，只按 string 读会全部拿不到
                        var path = ReadText(item, "LowerCaseLongPath", "Name", "FullPath");
                        if (path.Length == 0) continue;

                        var name = ReadText(item, "Name", "ProductName");

                        list.Add(new ExecRecord
                        {
                            Name = name.Length > 0 ? name : FileNameOf(path),
                            Path = path,
                            Source = "AmCache",
                            Version = ReadText(item, "Version", "FileVersion", "ProductVersion"),
                            Publisher = ReadText(item, "Publisher", "CompanyName"),
                            Extra = ReadText(item, "LinkDate"),
                            Time = ReadTime(item, "LinkDate", "CompileTime")
                        });
                    }
                }
            }

            if (list.Count == 0)
            {
                // Win8 / 8.1：Root\File\{卷 GUID}\<id>
                using var files = Registry.LocalMachine.OpenSubKey($@"{AmcacheMount}\Root\File");

                if (files != null)
                {
                    foreach (var vol in files.GetSubKeyNames())
                    {
                        using var volKey = files.OpenSubKey(vol);
                        if (volKey == null) continue;

                        foreach (var id in volKey.GetSubKeyNames())
                        {
                            using var item = volKey.OpenSubKey(id);
                            if (item == null) continue;

                            var path = ReadText(item, "15", "LowerCaseLongPath");
                            if (path.Length == 0) continue;

                            subKeyCount++;

                            list.Add(new ExecRecord
                            {
                                Name = FileNameOf(path),
                                Path = path,
                                Source = "AmCache",
                                Version = ReadText(item, "1", "Version"),
                                Publisher = ReadText(item, "6", "Publisher")
                            });
                        }
                    }
                }
            }

            var usedInventoryApp = false;

            if (list.Count == 0)
            {
                // 再退一层：Root\InventoryApplication（已安装程序清单，含安装时间）
                using var apps = Registry.LocalMachine.OpenSubKey($@"{AmcacheMount}\Root\InventoryApplication");

                if (apps != null)
                {
                    foreach (var id in apps.GetSubKeyNames())
                    {
                        using var item = apps.OpenSubKey(id);
                        if (item == null) continue;

                        var name = ReadText(item, "Name", "ProductName");
                        var path = ReadText(item, "RootDirPath", "InstallLocation");
                        if (name.Length == 0 && path.Length == 0) continue;

                        subKeyCount++;

                        list.Add(new ExecRecord
                        {
                            Name = name.Length > 0 ? name : FileNameOf(path),
                            Path = path,
                            Source = "AmCache",
                            Version = ReadText(item, "Version", "ProductVersion"),
                            Publisher = ReadText(item, "Publisher"),
                            Extra = "安装记录",
                            Time = ReadTime(item, "InstallDate", "InstallTime")
                        });

                        usedInventoryApp = true;
                    }
                }
            }

            var note = list.Count > 0
                ? usedInventoryApp
                    ? $"hive 已加载：文件清单为空，改取安装记录 InventoryApplication，共 {list.Count} 条。"
                    : $"hive 已加载并读取完成，共 {list.Count} 条。"
                : subKeyCount > 0
                    ? $"hive 已加载：InventoryApplicationFile 有 {subKeyCount} 个子键，但没解析出路径字段。"
                    : "hive 已加载，但里面没有程序条目（精简系统常见，也可能还没生成过记录）。";

            return (list
                .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .ToList(), note);
        }
        catch (Exception ex)
        {
            return (new List<ExecRecord>(), "读取 AmCache 失败：" + ex.Message);
        }
        finally
        {
            RegUnLoadKey(Hklm, AmcacheMount);
        }
    }

    /// <summary>SRUM 库文件可用性体检。不解析 ESE，只报告能不能读。</summary>
    public static SrumStatus ReadSrumStatus()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(win, @"System32\sru\SRUDB.dat");

        var probe = ProbeFile(path);

        if (probe.State == "denied")
        {
            return new SrumStatus
            {
                FilePath = path,
                Access = "denied",
                Exists = true,
                ServiceState = ServiceState("DPS")
            };
        }

        if (probe.State != "ok")
        {
            return new SrumStatus
            {
                FilePath = path,
                Access = "missing",
                Exists = false,
                ServiceState = ServiceState("DPS")
            };
        }

        return new SrumStatus
        {
            FilePath = path,
            Access = "ok",
            Exists = true,
            SizeBytes = probe.Size,
            LastWrite = probe.LastWrite,
            Readable = true,
            ServiceState = ServiceState("DPS")
        };
    }

    /// <summary>一次读完三处记录，界面与自检共用。</summary>
    public static ExecScanResult Scan()
    {
        var result = new ExecScanResult();

        result.Bam = ReadBam(out var bamNote);
        result.BamNote = bamNote;

        var (amc, amcNote) = ReadAmCache();
        result.Amc = amc;
        result.AmcNote = amcNote;

        result.Srum = ReadSrumStatus();
        return result;
    }

    /// <summary>
    /// 判断文件是「不存在」还是「存在但没权限」。
    /// File.Exists 在拒绝访问时也返回 false，用它会把权限问题误报成文件缺失。
    /// </summary>
    public static (string State, long Size, DateTime? LastWrite) ProbeFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            var len = fi.Length;
            return ("ok", len, fi.LastWriteTime);
        }
        catch (UnauthorizedAccessException)
        {
            return ("denied", 0, null);
        }
        catch (FileNotFoundException)
        {
            return ("missing", 0, null);
        }
        catch (DirectoryNotFoundException)
        {
            return ("missing", 0, null);
        }
        catch
        {
            return ("unknown", 0, null);
        }
    }

    private static bool IsAdmin
    {
        get
        {
            try { return LoggingService.IsAdmin(); }
            catch { return false; }
        }
    }

    // ---------- 报告 ----------

    public static ReportPayload BuildReport(List<ExecRecord> bam, List<ExecRecord> amc,
        string amcNote, SrumStatus srum)
    {
        AppPaths.Ensure();

        var doc = new HtmlDoc("程序执行记录",
            $"BAM {bam.Count} 条　|　AmCache {amc.Count} 条　|　生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        doc.Kpis(
            (bam.Count.ToString(), "BAM 执行记录", "#1971C2"),
            (amc.Count.ToString(), "AmCache 条目", "#7048E8"),
            (srum.Exists ? (srum.Readable ? "可读" : "受限") : "无库", "SRUM 状态", "#E8590C"));

        doc.Note("<b>这些记录只能说明「系统记录到过」。</b>BAM 可被清理工具删除，AmCache 在精简系统上可能不存在，" +
                 "查不到不代表没运行过。本页不做行为判定。");

        doc.H2($"最近执行（BAM）{bam.Count} 条");
        if (bam.Count == 0)
        {
            doc.Empty("没有读到记录，可能需要管理员权限，或这台机器关闭了活动记录。");
        }
        else
        {
            doc.Table(new[] { "最后执行时间", "程序", "路径" },
                bam.Select(r => new[] { r.TimeText, r.Name, r.Path }),
                new[] { "150px", "26%", "" });
        }

        doc.H2($"安装与版本（AmCache）{amc.Count} 条");
        if (amc.Count == 0)
        {
            doc.Empty(amcNote);
        }
        else
        {
            doc.Table(new[] { "名称", "版本", "发布者", "路径" },
                amc.Select(r => new[]
                {
                    r.Name,
                    r.Version.Length > 0 ? r.Version : "—",
                    r.Publisher.Length > 0 ? r.Publisher : "—",
                    r.Path
                }),
                new[] { "26%", "90px", "22%", "" });
        }

        doc.H2("数据使用库（SRUM）");
        doc.Note($"{HtmlDoc.E(srum.Summary)}" +
                 (string.IsNullOrEmpty(srum.ServiceState) ? "" : $"　DPS 服务：{HtmlDoc.E(srum.ServiceState)}") +
                 "<br><b>本工具不解析 ESE 表</b>，因此不给逐应用流量数字——不同 Windows 版本列顺序不同，" +
                 "解析错了会输出错误数值，比不给更糟。要看每应用用量：设置 → 网络和 Internet → 数据使用量。");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "程序执行记录");

        var sb = new StringBuilder();
        sb.AppendLine($"【程序执行记录】{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"BAM {bam.Count} 条 / AmCache {amc.Count} 条 / SRUM {srum.Summary}");
        sb.AppendLine();
        foreach (var r in bam.Take(40))
            sb.AppendLine($"[执行] {r.TimeText}  {r.Name}  {r.Path}");
        if (bam.Count > 40) sb.AppendLine($"…… 其余 {bam.Count - 40} 条见报告");
        sb.AppendLine();
        foreach (var r in amc.Take(40))
            sb.AppendLine($"[安装] {r.Name}  v{r.Version}  {r.Path}");
        if (amc.Count > 40) sb.AppendLine($"…… 其余 {amc.Count - 40} 条见报告");

        return new ReportPayload
        {
            Title = "程序执行记录",
            Subtitle = $"BAM {bam.Count} 条 · AmCache {amc.Count} 条",
            HtmlPath = path,
            PlainSummary = sb.ToString()
        };
    }

    // ---------- 解析工具（自检会直接验证） ----------

    /// <summary>把 8 字节 FILETIME 转成本地时间；超出合理范围返回 null，避免显示 1601 年。</summary>
    public static DateTime? FromFileTime(byte[] data, int offset = 0)
    {
        if (data == null || data.Length < offset + 8) return null;

        var ft = BitConverter.ToInt64(data, offset);
        if (ft <= 0) return null;

        try
        {
            var utc = DateTime.FromFileTimeUtc(ft);
            if (utc.Year < 2000 || utc.Year > DateTime.Now.Year + 1) return null;
            return utc.ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>UserAssist 的值名用 ROT13 混淆，这里还原。</summary>
    public static string Rot13(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";

        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
            else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static DateTime? ParseLooseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return DateTime.TryParse(s, out var dt) ? dt : null;
    }

    /// <summary>
    /// 注册表值转文本。Amcache 在 Win11 上把字符串存成 REG_BINARY（UTF-16LE 带结尾 \0），
    /// 直接 `as string` 会得到 null —— 这是之前「AmCache 0 条」的根因。
    /// </summary>
    internal static string AsText(object value)
    {
        switch (value)
        {
            case null:
                return "";

            case string s:
                return s.TrimEnd('\0').Trim();

            case string[] arr:
                return arr.Length == 0 ? "" : (arr[0] ?? "").TrimEnd('\0').Trim();

            case byte[] bytes:
                return DecodeBinaryText(bytes);

            case int i:
                return i.ToString();

            case long l:
                return l.ToString();

            default:
                return value.ToString() ?? "";
        }
    }

    private static string DecodeBinaryText(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        if (bytes.Length == 1) return bytes[0] == 0 ? "" : ((char)bytes[0]).ToString();

        var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0').Trim();
        if (text.Length == 0) return "";

        // 解出大量控制字符说明不是 UTF-16，退回单字节
        var bad = 0;
        foreach (var c in text)
            if (c < 0x20 && c != '\t') bad++;

        return bad > text.Length / 4
            ? Encoding.ASCII.GetString(bytes).TrimEnd('\0').Trim()
            : text;
    }

    /// <summary>按候选值名依次取第一个非空文本。</summary>
    private static string ReadText(RegistryKey key, params string[] names)
    {
        foreach (var name in names)
        {
            var text = AsText(key.GetValue(name));
            if (text.Length > 0) return text;
        }

        return "";
    }

    /// <summary>取时间值：既支持 REG_SZ 日期字符串，也支持 8 字节 FILETIME。</summary>
    private static DateTime? ReadTime(RegistryKey key, params string[] names)
    {
        foreach (var name in names)
        {
            var value = key.GetValue(name);

            if (value is byte[] bytes)
            {
                var t = FromFileTime(bytes);
                if (t.HasValue) return t;
                continue;
            }

            var parsed = ParseLooseDate(AsText(value));
            if (parsed.HasValue) return parsed;
        }

        return null;
    }

    private static string FileNameOf(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var i = path.LastIndexOf('\\');
        return i >= 0 && i < path.Length - 1 ? path[(i + 1)..] : path;
    }

    /// <summary>把 \Device\HarddiskVolume3\... 换成盘符路径，读不到映射就原样返回。</summary>
    private static string NormalizeDevicePath(string raw, Dictionary<string, string> devices)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        foreach (var kv in devices)
        {
            if (raw.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                return kv.Value + raw[kv.Key.Length..];
        }

        return raw;
    }

    private static Dictionary<string, string> BuildDeviceMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                var letter = drive.Name.TrimEnd('\\');
                if (letter.Length != 2) continue;

                var buf = new StringBuilder(1024);
                if (QueryDosDevice(letter, buf, (uint)buf.Capacity) == 0) continue;

                var device = buf.ToString();
                if (device.Length > 0) map[device] = letter;
            }
        }
        catch
        {
            // 映射失败就退回原始设备路径
        }

        return map;
    }

    private static string ServiceState(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT State FROM Win32_Service WHERE Name='{name}'");

            foreach (ManagementObject mo in searcher.Get())
                return mo["State"] as string ?? "";
        }
        catch
        {
            // WMI 不可用就不显示
        }

        return "";
    }
}
