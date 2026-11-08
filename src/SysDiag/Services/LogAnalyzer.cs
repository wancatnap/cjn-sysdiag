using System.IO;
using System.Diagnostics.Eventing.Reader;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SysDiag.Models;

namespace SysDiag.Services;

public static class LogAnalyzer
{
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".log", ".txt", ".json", ".csv", ".ini", ".cfg", ".xml", ".yml", ".yaml", ".out", ".dump", ".md"
    };

    /// <summary>该文件是否会被当作文本日志扫描（事件日志也算）。供文件选择界面分类用。</summary>
    public static bool IsAnalyzableFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".evtx", StringComparison.OrdinalIgnoreCase) || TextExt.Contains(ext);
    }

    private static readonly string[] CrashKw =
    {
        "fatal", "crash", "unhandled exception", "access violation", "assertion failed",
        "critical error", "watchdog", "out of memory", "stack overflow", "崩溃", "致命", "堆栈溢出"
    };

    private static readonly string[] ErrKw =
    {
        "error", "exception", "failed", "failure", "拒绝访问", "错误", "异常", "失败"
    };

    private static readonly string[] WarnKw = { "warning", "警告" };

    private static readonly Regex TsRe =
        new(@"(\d{4}[-/]\d{1,2}[-/]\d{1,2}[ T]\d{1,2}:\d{2}:\d{2})", RegexOptions.Compiled);

    /// <summary>UE 的时间格式：2026.11.03-22.36.04</summary>
    private static readonly Regex UeTsRe =
        new(@"(\d{4})\.(\d{2})\.(\d{2})-(\d{2})\.(\d{2})\.(\d{2})", RegexOptions.Compiled);

    private const long ByteBudget = 220L * 1024 * 1024;
    private const int MaxCrashRows = 400;
    private const int MaxEvtxEntries = 20000;

    private sealed class ScanItem
    {
        public string Path { get; init; } = "";
        public string Display { get; init; } = "";
        public string Kind { get; init; } = "文本";
    }

    /// <summary>兼容旧调用：直接分析一个 zip。</summary>
    public static LogReport Analyze(string zipPath, IProgress<string> progress = null)
        => Analyze(LogSource.FromZip(zipPath), progress);

    public static LogReport Analyze(LogSource source, IProgress<string> progress = null)
    {
        AppPaths.Ensure();

        var report = new LogReport
        {
            Source = source.Label,
            DisplayTitle = BuildDisplayTitle(source),
            SourceKind = source.Kind switch
            {
                LogSourceKind.Zip => "压缩包",
                LogSourceKind.Folder => "目录",
                _ => "文件"
            },
            Stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss")
        };

        report.WorkDir = Path.Combine(AppPaths.OutputDir, "_work_" + report.Stamp);
        var workUsed = false;

        var items = new List<ScanItem>();

        // ---------- 1. 收集源文件 ----------
        if (source.Kind == LogSourceKind.Zip)
        {
            var zip = source.Paths.FirstOrDefault()
                      ?? throw new ArgumentException("未指定压缩包路径");
            progress?.Report("正在解压…");
            Directory.CreateDirectory(report.WorkDir);
            workUsed = true;
            ExtractZip(zip, report.WorkDir, progress);

            foreach (var f in Directory.EnumerateFiles(report.WorkDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(report.WorkDir, f);
                items.Add(new ScanItem { Path = f, Display = rel });
            }
        }
        else if (source.Kind == LogSourceKind.Folder)
        {
            var root = source.Paths.FirstOrDefault()
                       ?? throw new ArgumentException("未指定目录路径");
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"目录不存在：{root}");

            progress?.Report("正在枚举目录…");
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, f);
                items.Add(new ScanItem { Path = f, Display = rel });
            }
        }
        else
        {
            foreach (var f in source.Paths)
            {
                if (!File.Exists(f)) continue;
                // 从目录里挑的文件，用相对路径，便于在一堆备份里分辨是哪一份
                var display = string.IsNullOrEmpty(source.Root)
                    ? Path.GetFileName(f)
                    : Path.GetRelativePath(source.Root, f);
                items.Add(new ScanItem { Path = f, Display = display });
            }
        }

        // ---------- 2. 按修改时间倒序：预算有限时优先分析最近的对局 ----------
        items = items
            .OrderByDescending(it =>
            {
                try { return new FileInfo(it.Path).LastWriteTime; }
                catch { return DateTime.MinValue; }
            })
            .ToList();

        // ---------- 3. 事件日志转文本 ----------
        var expanded = new List<ScanItem>();
        var evtxIndex = 0;
        foreach (var item in items)
        {
            if (!item.Path.EndsWith(".evtx", StringComparison.OrdinalIgnoreCase))
            {
                expanded.Add(item);
                continue;
            }

            evtxIndex++;
            progress?.Report($"正在解析事件日志 {Path.GetFileName(item.Path)}…");
            Directory.CreateDirectory(report.WorkDir);
            workUsed = true;
            var outPath = Path.Combine(report.WorkDir, $"_evtx_{evtxIndex}.txt");
            var n = ConvertEvtx(item.Path, outPath, out var error);

            if (error != null)
            {
                report.Warnings.Add($"{Path.GetFileName(item.Path)} 解析失败：{error}");
                continue;
            }
            if (n >= MaxEvtxEntries)
                report.Warnings.Add($"{Path.GetFileName(item.Path)} 超过 {MaxEvtxEntries} 条，仅分析前 {MaxEvtxEntries} 条");

            expanded.Add(new ScanItem
            {
                Path = outPath,
                Display = Path.GetFileName(item.Path) + "（事件日志）",
                Kind = "事件日志"
            });
        }
        items = expanded;

        report.FileCount = items.Count;

        long totalBytes = 0;
        foreach (var it in items)
        {
            try { totalBytes += new FileInfo(it.Path).Length; } catch { }
        }
        report.TotalBytes = totalBytes;

        // ---------- 3. 逐文件扫描 ----------
        var crashHits = new List<CrashHit>();
        var errAgg = new Dictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0L;
        var engineUe = 0;
        var engineUnity = 0;
        var index = 0;

        foreach (var item in items)
        {
            index++;
            var info = new FileInfo(item.Path);
            var name = Path.GetFileName(item.Path);

            var isText = item.Kind == "事件日志" || TextExt.Contains(info.Extension);
            if (!isText)
            {
                report.Files.Add(new LogFileRow
                {
                    Name = item.Display,
                    Size = info.Length,
                    Lines = null,
                    Hits = 0,
                    Kind = "二进制"
                });
                continue;
            }

            if (scanned > ByteBudget)
            {
                report.Files.Add(new LogFileRow
                {
                    Name = item.Display,
                    Size = info.Length,
                    Lines = null,
                    Hits = 0,
                    Kind = "超出预算跳过"
                });
                report.Warnings.Add($"总量超预算，已跳过：{item.Display}");
                continue;
            }
            scanned += info.Length;

            if (items.Count <= 30 || index % 5 == 0 || index == items.Count)
                progress?.Report($"正在扫描 {index}/{items.Count}：{name}");

            var content = ReadSafe(item.Path, report);
            if (string.IsNullOrEmpty(content)) continue;
            var lowerAll = content.ToLowerInvariant();
            if (lowerAll.Contains("loginit") || lowerAll.Contains("lognet") || lowerAll.Contains("loggame")) engineUe++;
            if (lowerAll.Contains("nullreferenceexception") || lowerAll.Contains("unityengine") || lowerAll.Contains("il2cpp")) engineUnity++;

            var lines = content.Split('\n');
            report.LineTotal += lines.Length;
            var range = FileRange(lines);
            var hits = 0;

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length < 4) continue;
                var low = line.ToLowerInvariant();

                // 安全检查必须放在最前：崩溃/错误分支会 continue，
                // 而“反作弊拦截”这类关键线索恰恰带 Error 字样，放后面会被跳过。
                if (report.SecurityHits.Count < 200)
                {
                    var secHit = SecurityInspector.Inspect(item.Display, i + 1, line);
                    if (secHit != null) report.SecurityHits.Add(secHit);
                }

                if (ContainsAny(low, CrashKw))
                {
                    hits++;
                    report.ErrorCount++;
                    if (crashHits.Count < MaxCrashRows)
                        crashHits.Add(new CrashHit { File = item.Display, Line = i + 1, Content = line.Trim() });
                    Bump(errAgg, Signature(line));
                    continue;
                }

                if (ContainsAny(low, ErrKw))
                {
                    hits++;
                    report.ErrorCount++;
                    Bump(errAgg, Signature(line));
                    continue;
                }

                if (ContainsAny(low, WarnKw)) report.WarnCount++;
            }

            report.Files.Add(new LogFileRow
            {
                Name = item.Display,
                Size = info.Length,
                Lines = lines.Length,
                Hits = hits,
                Kind = item.Kind,
                StartTime = range.Start,
                EndTime = range.End
            });
        }

        report.CrashCount = crashHits.Count;
        report.Crashes = crashHits;

        if (engineUe > engineUnity && engineUe > 0) report.Engine = "Unreal Engine（虚幻）";
        else if (engineUnity > 0) report.Engine = "Unity（UnityEngine）";

        report.AllErrors = errAgg
            .OrderByDescending(kv => kv.Value)
            .Take(200)
            .Select(kv => new ErrorPattern { Signature = kv.Key, Count = kv.Value })
            .ToList();

        report.TopErrors = report.AllErrors.Take(25).ToList();

        foreach (var hit in crashHits.Take(200))
        {
            var m = TsRe.Match(hit.Content);
            if (!m.Success) continue;
            if (!DateTime.TryParse(m.Value.Replace('/', '-'), out var dt)) continue;
            if (report.TimeMin is null || dt < report.TimeMin) report.TimeMin = dt;
            if (report.TimeMax is null || dt > report.TimeMax) report.TimeMax = dt;
        }

        if (!workUsed) report.WorkDir = "";

        // 会话时间窗：取“结束时间最新”的那个文件，即最近一次对局
        var mainFile = report.Files
            .Where(f => f.StartTime != null && f.EndTime != null && f.EndTime > f.StartTime)
            .OrderByDescending(f => f.EndTime)
            .FirstOrDefault();

        if (mainFile != null)
        {
            report.SessionStart = mainFile.StartTime;
            report.SessionEnd = mainFile.EndTime;
            report.SessionFile = mainFile.Name;
        }
        else if (report.TimeMin != null)
        {
            report.SessionStart = report.TimeMin;
            report.SessionEnd = report.TimeMax ?? report.TimeMin;
        }

        // 同时间段的本机系统事件
        if (report.SessionStart is DateTime s0 && report.SessionEnd is DateTime e0)
        {
            var from = s0.AddMinutes(-5);
            var to = e0.AddMinutes(10);
            if (to - from > TimeSpan.FromHours(24)) from = to.AddHours(-24);

            if (to > from)
            {
                progress?.Report("正在读取同一时段的系统事件…");
                try
                {
                    report.SessionEvents = SessionCorrelator.Correlate(from, to, report.SessionNotes);
                }
                catch (Exception ex)
                {
                    report.SessionNotes.Add("读取本机事件失败：" + ex.Message);
                }
            }
        }
        else
        {
            report.SessionNotes.Add("未能从日志中解析出时间戳，跳过同时间段关联");
        }

        // 本机侧痕迹：与日志内容无关，仅作参考
        var prefetch = SecurityInspector.ScanPrefetch(out var prefetchNote);
        if (!string.IsNullOrEmpty(prefetchNote)) report.SecurityNotes.Add(prefetchNote);
        if (prefetch.Count > 0)
            report.SecurityNotes.Add("Prefetch 记录中出现：" + string.Join("、", prefetch.Take(10)));

        var running = SecurityInspector.ScanRunningProcesses();
        if (running.Count > 0)
            report.SecurityNotes.Add("当前正在运行：" + string.Join("、", running));

        progress?.Report("分析完成");
        return report;
    }

    /// <summary>单个文件读取失败（被独占、权限不足）不应中断整次分析。</summary>
    private static string ReadSafe(string path, LogReport report)
    {
        try
        {
            var text = ReadTextSmartEx(path, out var note);
            if (!string.IsNullOrEmpty(note))
                report.Warnings.Add($"{Path.GetFileName(path)}：{note}");
            return text;
        }
        catch (Exception ex)
        {
            report.Warnings.Add($"{Path.GetFileName(path)} 读取失败：{ex.Message}");
            return null;
        }
    }

    // ---------------- .evtx 解析 ----------------    /// <summary>从首尾若干行里取时间戳，得到该文件的起止时间。</summary>
    private static (DateTime? Start, DateTime? End) FileRange(string[] lines)
    {
        DateTime? start = null, end = null;
        var head = Math.Min(40, lines.Length);
        for (var i = 0; i < head; i++)
        {
            var t = ParseTimestamp(lines[i]);
            if (t != null) { start = t; break; }
        }

        var tail = Math.Max(0, lines.Length - 40);
        for (var i = lines.Length - 1; i >= tail; i--)
        {
            var t = ParseTimestamp(lines[i]);
            if (t != null) { end = t; break; }
        }

        return (start, end);
    }

    private static DateTime? ParseTimestamp(string line)
    {
        if (string.IsNullOrEmpty(line)) return null;

        var m = UeTsRe.Match(line);
        if (m.Success)
        {
            try
            {
                return new DateTime(
                    int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value),
                    int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value), int.Parse(m.Groups[6].Value));
            }
            catch { }
        }

        var m2 = TsRe.Match(line);
        if (m2.Success && DateTime.TryParse(m2.Value.Replace('/', '-'), out var dt)) return dt;

        return null;
    }

    /// <summary>给目录来源生成可读标题：跳过 log/logs/Saved 这类通用层级，并区分游戏日志与启动器日志。</summary>
    private static string BuildDisplayTitle(LogSource source)
    {
        // 从目录里挑了若干文件时，标题仍按那个目录来（例如「三角洲行动 游戏日志」）
        if (source.Kind == LogSourceKind.Files && !string.IsNullOrEmpty(source.Root))
            return BuildTitleForRoot(source.Root);

        if (source.Kind != LogSourceKind.Folder) return source.Label;

        return BuildTitleForRoot(source.Paths[0]);
    }

    private static string BuildTitleForRoot(string rootPath)
    {
        var dir = new DirectoryInfo(rootPath);
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "log", "logs", "saved", "output", "out", "debug"
        };

        var node = dir;
        var guard = 0;
        while (node != null && generic.Contains(node.Name) && guard++ < 4)
            node = node.Parent;

        var name = string.IsNullOrEmpty(node?.Name) ? dir.Name : node.Name;
        var isGameLog = rootPath.Contains(@"Saved\Logs", StringComparison.OrdinalIgnoreCase)
                        || rootPath.Contains("Saved/Logs", StringComparison.OrdinalIgnoreCase);

        if (isGameLog && GameAlias.TryGetValue(name.Replace(" ", ""), out var alias))
            return $"{alias} 游戏日志";

        return isGameLog ? $"{name} 游戏日志" : $"{name} 日志";
    }

    /// <summary>常见游戏目录名到中文名的映射，仅用于报告标题展示。</summary>
    private static readonly Dictionary<string, string> GameAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DeltaForce"] = "三角洲行动",
        ["EscapeTheBackrooms"] = "逃离后室",
        ["ItTakesTwo"] = "双人成行",
        ["Frostrail"] = "霜陨"
    };

    /// <summary>把 Windows 事件日志（.evtx）转成可扫描的文本行，返回条数。</summary>
    private static int ConvertEvtx(string evtxPath, string outPath, out string error)
    {
        error = null;
        var sb = new StringBuilder();
        var count = 0;
        string firstError = null;
        string copy = null;

        // 路径 1：复制副本后按文件读取（适用于导出的 evtx）
        try
        {
            var size = new FileInfo(evtxPath).Length;
            if (size > 256L * 1024 * 1024)
            {
                error = $"文件过大（{size / 1024.0 / 1024:N0} MB），已跳过";
                return 0;
            }

            copy = Path.Combine(Path.GetDirectoryName(outPath) ?? Path.GetTempPath(),
                "_evtxcopy_" + Guid.NewGuid().ToString("N")[..8] + ".evtx");
            File.Copy(evtxPath, copy, true);
            count = ReadRecords(new EventLogQuery(copy, PathType.FilePath), sb);
        }
        catch (Exception ex)
        {
            firstError = ex.Message;
        }
        finally
        {
            if (copy != null)
            {
                try { File.Delete(copy); } catch { }
            }
        }

        // 路径 2：按通道名读取（系统目录下的活动日志常被服务独占）
        if (count == 0)
        {
            sb.Clear();
            var channel = Path.GetFileNameWithoutExtension(evtxPath);
            try
            {
                count = ReadRecords(new EventLogQuery(channel, PathType.LogName), sb);
                if (count > 0) firstError = null;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
            }
        }

        error = count > 0 ? null : firstError ?? "未读取到任何事件";

        if (count > 0 || error == null)
            File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false));

        return count;
    }

    private static int ReadRecords(EventLogQuery query, StringBuilder sb)
    {
        var count = 0;
        using var reader = new EventLogReader(query);

        while (count < MaxEvtxEntries)
        {
            using var rec = reader.ReadEvent();
            if (rec == null) break;

            var time = rec.TimeCreated?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";
            var level = LevelText(rec);
            var provider = rec.ProviderName ?? "";
            var msg = "";
            try { msg = rec.FormatDescription() ?? ""; }
            catch { msg = rec.ToXml() ?? ""; }

            msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            if (msg.Length > 300) msg = msg[..300];

            sb.Append(time).Append(" [").Append(level).Append("] ")
              .Append(provider).Append(" (").Append(rec.Id).Append("): ")
              .Append(msg).Append('\n');
            count++;
        }

        return count;
    }

    private static string LevelText(EventRecord rec)
    {
        try
        {
            if (!string.IsNullOrEmpty(rec.LevelDisplayName)) return rec.LevelDisplayName;
        }
        catch { }

        return rec.Level switch
        {
            1 => "Critical",
            2 => "Error",
            3 => "Warning",
            4 => "Information",
            5 => "Verbose",
            _ => "Info"
        };
    }

    // ---------------- 通用辅助 ----------------

    private static void Bump(Dictionary<string, int> dict, string key)
    {
        dict.TryGetValue(key, out var n);
        dict[key] = n + 1;
    }

    private static bool ContainsAny(string low, string[] keywords)
    {
        foreach (var k in keywords)
            if (low.Contains(k, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>归一化：地址、路径、长数字替换成占位符，让同一类错误能聚合。</summary>
    public static string Signature(string line)
    {
        var t = Regex.Replace(line, "0x[0-9A-Fa-f]+", "<HEX>");
        t = Regex.Replace(t, @"[A-Za-z]:\\[^\s""']+", "<PATH>");
        t = Regex.Replace(t, @"\b\d{4,}\b", "<N>");
        t = Regex.Replace(t, @"\b\d+\b", "<N>");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 150 ? t[..150] : t;
    }

    public static string ReadTextSmart(string path, long maxBytes = 24L * 1024 * 1024)
        => ReadTextSmartEx(path, out _, maxBytes);

    /// <summary>
    /// 共享读写打开（可读正在写入的日志）；大文件读尾部；末尾自动检测单字节异或混淆并解码。
    /// </summary>
    public static string ReadTextSmartEx(string path, out string note, long maxBytes = 24L * 1024 * 1024)
    {
        note = null;
        byte[] buf;
        int read;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            var len = fs.Length;
            if (len <= 0) return "";

            if (len > maxBytes)
            {
                // 大文件读尾部：最近的记录才有诊断价值
                fs.Seek(-maxBytes, SeekOrigin.End);
                buf = new byte[maxBytes];
                read = fs.Read(buf, 0, (int)maxBytes);
                var nl = Array.IndexOf(buf, (byte)'\n', 0, Math.Min(read, 8192));
                if (nl >= 0 && nl + 1 < read)
                {
                    var skip = nl + 1;
                    buf = buf[skip..];
                    read -= skip;
                }
                note = $"文件较大（{len / 1024.0 / 1024:N0} MB），仅分析末尾 {maxBytes / 1024 / 1024} MB";
            }
            else
            {
                buf = new byte[len];
                read = fs.Read(buf, 0, (int)len);
            }
        }

        var offset = BomLength(buf, read);
        if (offset == 2 && buf[0] == 0xFF && buf[1] == 0xFE)
            return Encoding.Unicode.GetString(buf, 2, read - 2);
        if (offset == 2 && buf[0] == 0xFE && buf[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(buf, 2, read - 2);

        var text = DecodeBytes(buf, offset, read - offset);

        // 混淆检测：正常日志的 Log / Error 密度远高于乱码
        if (read - offset < 256) return text;

        var sampleLen = Math.Min(8192, read - offset);
        var baseScore = LogScore(DecodeBytes(buf, offset, sampleLen));
        if (baseScore >= 60) return text;

        var bestKey = 0;
        var bestScore = baseScore;
        var probe = new byte[sampleLen];
        for (var k = 1; k < 256; k++)
        {
            for (var i = 0; i < sampleLen; i++)
                probe[i] = (byte)(buf[offset + i] ^ k);

            var sc = LogScore(DecodeBytes(probe, 0, sampleLen));
            if (sc > bestScore)
            {
                bestScore = sc;
                bestKey = k;
            }
        }

        if (bestKey > 0 && bestScore >= 80 && bestScore > baseScore * 5)
        {
            for (var i = offset; i < read; i++) buf[i] ^= (byte)bestKey;
            var tag = $"检测到单字节混淆，已用 XOR 0x{bestKey:X2} 自动解码";
            note = string.IsNullOrEmpty(note) ? tag : note + "；" + tag;
            return DecodeBytes(buf, offset, read - offset);
        }

        return text;
    }

    private static int BomLength(byte[] b, int read)
    {
        if (read >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return 3;
        if (read >= 2 && b[0] == 0xFF && b[1] == 0xFE) return 2;
        if (read >= 2 && b[0] == 0xFE && b[1] == 0xFF) return 2;
        return 0;
    }

    /// <summary>严格 UTF-8 优先，失败回退 GBK(936)。</summary>
    private static string DecodeBytes(byte[] buf, int offset, int count)
    {
        if (count <= 0) return "";
        try
        {
            return new UTF8Encoding(false, true).GetString(buf, offset, count);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(936).GetString(buf, offset, count);
        }
    }

    /// <summary>日志特征打分：正常日志远高于乱码。</summary>
    private static int LogScore(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var n = 0;
        n += CountOf(s, "Log") * 3;
        n += CountOf(s, "Error") * 5;
        n += CountOf(s, "Warning") * 4;
        n += CountOf(s, "][") * 2;
        n += CountOf(s, "Display:") * 2;
        n += CountOf(s, "Exception") * 5;
        n += CountOf(s, "错误") * 5;
        n += CountOf(s, "警告") * 4;
        return n;
    }

    private static int CountOf(string s, string token)
    {
        var c = 0;
        var i = 0;
        while ((i = s.IndexOf(token, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            c++;
            i += token.Length;
        }
        return c;
    }

    /// <summary>用 GBK 作为条目名编码，兼容中文 Windows 打包的 zip（带 UTF-8 标记的条目仍按 UTF-8 处理）。</summary>
    private static void ExtractZip(string zipPath, string dest, IProgress<string> progress)
    {
        var root = Path.GetFullPath(dest);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Read, Encoding.GetEncoding(936));

        var count = 0;
        foreach (var entry in archive.Entries)
        {
            count++;
            if (count % 200 == 0) progress?.Report($"正在解压 {count}/{archive.Entries.Count}…");

            var rel = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(rel)) continue;

            var full = Path.GetFullPath(Path.Combine(root, rel));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                continue; // 防目录穿越

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(full);
                continue;
            }

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            try { entry.ExtractToFile(full, true); }
            catch (IOException) { /* 同名占用则跳过 */ }
        }
    }
}
