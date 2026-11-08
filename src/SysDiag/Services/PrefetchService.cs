using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 本地执行痕迹：Prefetch（运行过 + 运行次数）与 ShimCache（文件存在过）。
/// <para>
/// Prefetch 覆盖 Win10/11 的 MAM 压缩格式（调 ntdll 的 XPRESS-Huffman 解压），
/// 布局不认识的版本只报「版本 + 名称 + 最后运行」，**不猜运行次数**。
/// ShimCache 只说明「文件在系统里出现过」，Win8 之后不记录执行标志，措辞上不越界。
/// </para>
/// </summary>
public static class PrefetchService
{
    private const ushort FormatXpressHuff = 0x0004;

    [DllImport("ntdll.dll")]
    private static extern int RtlGetCompressionWorkSpaceSize(ushort formatAndEngine, out uint bufferSize, out uint fragmentSize);

    [DllImport("ntdll.dll")]
    private static extern int RtlDecompressBufferEx(ushort formatAndEngine, byte[] uncompressedBuffer, int uncompressedBufferSize,
        byte[] compressedBuffer, int compressedBufferSize, out int finalUncompressedSize, byte[] workSpace);

    // ---------------- 入口 ----------------

    public static ExecArtifactResult Scan(IProgress<string> progress = null, int maxPrefetch = 400)
    {
        var sw = Stopwatch.StartNew();
        var result = new ExecArtifactResult();

        ScanPrefetch(result, progress, maxPrefetch);
        ScanShimCache(result);

        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    // ---------------- Prefetch ----------------

    private static void ScanPrefetch(ExecArtifactResult result, IProgress<string> p, int max)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

        if (!Directory.Exists(dir))
        {
            result.PrefetchState = "missing";
            result.PrefetchNote = "本机没有 Prefetch 目录（可能被优化工具关闭了）。";
            return;
        }

        List<string> files;

        try
        {
            p?.Report("读取 Prefetch 目录…");
            files = Directory.EnumerateFiles(dir, "*.pf").Take(max * 2).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            result.PrefetchState = "limited";
            result.PrefetchNote = "Prefetch 目录需要管理员权限才能列目录。";
            return;
        }
        catch (Exception ex)
        {
            result.PrefetchState = "missing";
            result.PrefetchNote = Short(ex.Message);
            return;
        }

        if (files.Count == 0)
        {
            result.PrefetchState = "missing";
            result.PrefetchNote = "Prefetch 目录是空的（SysMain 服务可能被关闭）。";
            return;
        }

        var ok = 0;

        foreach (var file in files)
        {
            if (result.Prefetch.Count >= max) break;

            p?.Report("解析 " + Path.GetFileName(file));

            try
            {
                var raw = File.ReadAllBytes(file);
                var data = TryDecompress(raw);

                if (data == null)
                {
                    result.SkippedCompressed++;
                    continue;
                }

                var entry = ParsePrefetch(data, file);

                if (entry == null)
                {
                    result.Failed++;
                    continue;
                }

                result.Prefetch.Add(entry);
                ok++;
            }
            catch (UnauthorizedAccessException)
            {
                result.PrefetchState = "limited";
                result.PrefetchNote = "Prefetch 文件需要管理员权限才能读取。";
            }
            catch
            {
                result.Failed++;
            }
        }

        if (result.PrefetchState == "done")
        {
            result.PrefetchNote = ok == 0
                ? "读到了文件，但没有一条能解析（可能全部是未知版本）。"
                : $"{ok} 条可解析" +
                  (result.SkippedCompressed > 0 ? $"，{result.SkippedCompressed} 条压缩格式未解开" : "") +
                  (result.Failed > 0 ? $"，{result.Failed} 条解析失败" : "");
        }
    }

    /// <summary>MAM（XPRESS-Huffman）压缩的先解开，未压缩的原样返回；不认识返回 null。</summary>
    internal static byte[] TryDecompress(byte[] raw)
    {
        if (raw == null || raw.Length < 16) return null;

        // 未压缩：offset 4 是 "SCCA"
        if (raw.Length >= 8 && Encoding.ASCII.GetString(raw, 4, 4) == "SCCA") return raw;

        // MAM\x04 头：0-3 魔数，8-11 解压后大小
        if (raw[0] == 'M' && raw[1] == 'A' && raw[2] == 'M')
        {
            var size = BitConverter.ToInt32(raw, 8);

            if (size <= 0 || size > 64 * 1024 * 1024) return null;

            return XpressHuff(raw, 12, raw.Length - 12, size);
        }

        // 老版本 "SCCA" 在 offset 0
        if (raw.Length >= 4 && Encoding.ASCII.GetString(raw, 0, 4) == "SCCA") return raw;

        return null;
    }

    private static byte[] XpressHuff(byte[] input, int offset, int length, int outputSize)
    {
        try
        {
            var rc = RtlGetCompressionWorkSpaceSize(FormatXpressHuff, out var workSize, out _);
            if (rc != 0) return null;

            var workspace = new byte[workSize];
            var source = new byte[length];
            Buffer.BlockCopy(input, offset, source, 0, length);

            var output = new byte[outputSize];

            rc = RtlDecompressBufferEx(FormatXpressHuff, output, outputSize, source, length,
                out var finalSize, workspace);

            if (rc != 0 || finalSize <= 0) return null;

            if (finalSize == outputSize) return output;

            var trimmed = new byte[finalSize];
            Buffer.BlockCopy(output, 0, trimmed, 0, finalSize);
            return trimmed;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析未压缩的 Prefetch 数据；布局不认识时仍返回名称与版本，运行次数留 -1。</summary>
    internal static PrefetchEntry ParsePrefetch(byte[] data, string filePath)
    {
        if (data == null || data.Length < 84) return null;
        if (Encoding.ASCII.GetString(data, 4, 4) != "SCCA") return null;

        var version = BitConverter.ToInt32(data, 0);
        var name = Encoding.Unicode.GetString(data, 16, 60).TrimEnd('\0').Trim();

        if (name.Length == 0) return null;

        DateTime? lastRun = null;
        long fileSize = 0;

        try
        {
            lastRun = File.GetLastWriteTime(filePath);
            fileSize = new FileInfo(filePath).Length;
        }
        catch { }

        var entry = new PrefetchEntry
        {
            Name = name,
            FilePath = filePath,
            Version = version,
            LastRun = lastRun,
            FileSize = fileSize
        };

        var runOffset = version switch
        {
            17 => 0x90,
            23 or 26 => 0x98,
            30 or 31 => 0xD0,
            _ => -1
        };

        if (runOffset > 0 && data.Length >= runOffset + 4)
        {
            var value = BitConverter.ToInt32(data, runOffset);

            // 合理性校验：离谱的数字宁可不报，也不给错数
            if (value is > 0 and < 10_000_000) entry.RunCount = value;
        }

        return entry;
    }

    // ---------------- ShimCache ----------------

    private static void ScanShimCache(ExecArtifactResult result)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatCache");

            var raw = key?.GetValue("AppCompatCache") as byte[];

            if (raw == null || raw.Length < 16)
            {
                result.ShimCacheState = "missing";
                result.ShimCacheNote = "没有读到 AppCompatCache 值（精简系统可能没有）。";
                return;
            }

            result.ShimCache = ParseShimCache(raw);

            result.ShimCacheNote = result.ShimCache.Count == 0
                ? $"读到 {raw.Length:N0} 字节，但没解析出条目。"
                : $"{result.ShimCache.Count:N0} 条（{raw.Length / 1024.0:N0} KB）";
        }
        catch (UnauthorizedAccessException)
        {
            result.ShimCacheState = "limited";
            result.ShimCacheNote = "AppCompatCache 在 HKLM\\SYSTEM 下，需要管理员权限。";
        }
        catch (Exception ex)
        {
            result.ShimCacheState = "missing";
            result.ShimCacheNote = Short(ex.Message);
        }
    }

    /// <summary>
    /// 解析 AppCompatCache。Win10/11 的 "10ts" 是「路径 + FILETIME + 长度 + 数据」的序列。
    /// 本机实测 Win11 24H2 会在标准结构前额外加一段 0x24 字节的前置头，
    /// 所以先在头部窗口里找签名，找不到再退回纯字符串扫描（只给路径、不给时间）。
    /// </summary>
    internal static List<ShimCacheEntry> ParseShimCache(byte[] raw)
    {
        var list = new List<ShimCacheEntry>();
        if (raw == null || raw.Length < 32) return list;

        var offset = FindSignature(raw);

        if (offset >= 0)
        {
            var parsed = ParseWin10(raw, offset);
            if (parsed.Count > 0) return parsed;
        }

        return FallbackScan(raw);
    }

    /// <summary>在头部 0x400 字节内找 "10ts" / "11ts" / "00ts"。</summary>
    private static int FindSignature(byte[] raw)
    {
        var limit = Math.Min(raw.Length - 4, 0x400);

        for (var i = 0; i <= limit; i++)
        {
            if (raw[i] != '1' && raw[i] != '0') continue;

            var sig = Encoding.ASCII.GetString(raw, i, 4);
            if (sig is "10ts" or "11ts" or "00ts") return i;
        }

        return -1;
    }

    /// <summary>
    /// 按「路径 + 紧跟 8 字节 FILETIME」解析。
    /// 不假设 header 长度、不依赖 dataSize 推进——本机 Win11 24H2 的前置头长度和
    /// 常见资料不一致，写死偏移会一条都读不出来。改成逐字节找路径起点，容错换版本。
    /// </summary>
    private static List<ShimCacheEntry> ParseWin10(byte[] raw, int headerOffset = 0)
    {
        var list = new List<ShimCacheEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var pos = Math.Max(0, headerOffset + 8);

        while (pos + 6 < raw.Length && list.Count < 3000)
        {
            if (!IsDriveStart(raw, pos))
            {
                pos++;
                continue;
            }

            var text = ReadUtf16Z(raw, pos, out var used);

            if (text == null || !LooksLikePath(text))
            {
                pos++;
                continue;
            }

            var after = pos + used;
            DateTime? modified = null;

            if (after + 8 <= raw.Length)
                modified = FileTimeToLocal(BitConverter.ToInt64(raw, after));

            if (seen.Add(text))
                list.Add(new ShimCacheEntry { Path = text, LastModified = modified });

            pos = after;
        }

        return list;
    }

    /// <summary>判断该位置是不是 UTF-16 编码的「X:\」开头。</summary>
    private static bool IsDriveStart(byte[] raw, int pos)
    {
        if (pos < 0 || pos + 6 > raw.Length) return false;

        if (raw[pos + 1] != 0 || raw[pos + 2] != ':' || raw[pos + 3] != 0) return false;
        if (raw[pos + 4] != '\\' || raw[pos + 5] != 0) return false;

        return char.IsLetter((char)raw[pos]);
    }

    /// <summary>
    /// 结构不认识时的兜底：只扫 UTF-16 里的路径字符串，不给时间。
    /// 逐字节滑动并只对「盘符 + :\」的位置尝试读串——不能按「读到一个串就跳过它的长度」走，
    /// 否则一段二进制会被当成超长字符串，把后面真正的路径整段吞掉。
    /// </summary>
    private static List<ShimCacheEntry> FallbackScan(byte[] raw)
    {
        var list = new List<ShimCacheEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var pos = 0; pos + 6 < raw.Length && list.Count < 3000; pos += 2)
        {
            if (raw[pos + 1] != 0) continue;                 // UTF-16 低字节必须为 0

            var ch = (char)raw[pos];
            if (!char.IsLetter(ch)) continue;

            if (raw[pos + 2] != ':' || raw[pos + 3] != 0) continue;
            if (raw[pos + 4] != '\\' || raw[pos + 5] != 0) continue;

            var text = ReadUtf16Z(raw, pos, out _);
            if (text == null || !LooksLikePath(text)) continue;
            if (!seen.Add(text)) continue;

            list.Add(new ShimCacheEntry { Path = text });
        }

        return list;
    }

    private static bool LooksLikePath(string text)
    {
        if (text.Length < 4 || text.Length > 520) return false;
        if (text[1] != ':' || text[2] != '\\') return false;

        foreach (var c in text)
            if (c < 0x20) return false;

        return true;
    }

    /// <summary>读一个 UTF-16LE 的 null 结尾字符串；不是合法字符串返回 null。</summary>
    private static string ReadUtf16Z(byte[] raw, int offset, out int used)
    {
        used = 0;

        if (offset < 0 || offset + 2 > raw.Length) return null;

        var sb = new StringBuilder();
        var i = offset;

        while (i + 1 < raw.Length)
        {
            var ch = (char)(raw[i] | (raw[i + 1] << 8));

            if (ch == '\0')
            {
                used = i + 2 - offset;
                return sb.Length >= 3 ? sb.ToString() : null;
            }

            // 控制字符说明这里不是字符串
            if (ch < 0x20 && ch != '\t') return null;
            if (sb.Length > 600) return null;

            sb.Append(ch);
            i += 2;
        }

        return null;
    }

    private static DateTime? FileTimeToLocal(long fileTime)
    {
        if (fileTime <= 0) return null;

        try
        {
            var utc = DateTime.FromFileTimeUtc(fileTime);
            if (utc.Year < 1990 || utc.Year > DateTime.Now.Year + 1) return null;

            return utc.ToLocalTime();
        }
        catch
        {
            return null;
        }
    }

    // ---------------- 报告 ----------------

    public static ReportPayload BuildReport(ExecArtifactResult r)
    {
        var suspicious = r.Prefetch.Where(x => x.IsSuspiciousPath).ToList();

        var doc = new HtmlDoc("本地执行痕迹",
            $"Prefetch {r.Prefetch.Count} 条 · ShimCache {r.ShimCache.Count} 条 · 耗时 {r.ElapsedText}");

        doc.Kpis(
            (r.Prefetch.Count.ToString(), "Prefetch 记录", "#4C6EF5"),
            (r.ShimCache.Count.ToString(), "ShimCache 条目", "#8B5CF6"),
            (suspicious.Count.ToString(), "临时目录里的程序", suspicious.Count > 0 ? "#C92A2A" : "#2F9E44"),
            (r.SkippedCompressed.ToString(), "压缩未解开", "#868E96"));

        doc.Note("<b>Prefetch 说明「运行过几次」，ShimCache 只说明「文件出现过」。</b>" +
                 "Win8 之后 ShimCache 不再记录执行标志，所以它不能用来证明某程序跑过。");

        doc.H2($"Prefetch（按运行次数排序，共 {r.Prefetch.Count} 条）");

        if (r.Prefetch.Count == 0)
            doc.Empty($"没有可用的 Prefetch 数据。{r.PrefetchNote}");
        else
        {
            doc.Table(
                new[] { "程序", "运行次数", "最后运行", "版本", "文件" },
                r.Top.Take(200).Select(x => new[]
                {
                    $"<b>{HtmlDoc.E(x.Name)}</b>" + (x.IsSuspiciousPath ? " <span class=\"tag t-bad\">临时目录</span>" : ""),
                    x.RunCountText,
                    $"<span class=\"mono\">{x.LastRunText}</span>",
                    x.VersionText,
                    $"<span class=\"mono\">{HtmlDoc.E(Path.GetFileName(x.FilePath))}</span>"
                }),
                new[] { "", "86", "160", "60", "" });
        }

        doc.H2($"ShimCache（共 {r.ShimCache.Count} 条）");

        if (r.ShimCache.Count == 0)
            doc.Empty($"没有可用的 ShimCache 数据。{r.ShimCacheNote}");
        else
        {
            doc.Table(
                new[] { "路径", "最后修改" },
                r.ShimCache.OrderByDescending(x => x.LastModified).Take(300).Select(x => new[]
                {
                    $"<span class=\"mono\">{HtmlDoc.E(x.Path)}</span>" +
                    (x.IsSuspiciousPath ? " <span class=\"tag t-bad\">临时目录</span>" : ""),
                    $"<span class=\"mono\">{x.LastModifiedText}</span>"
                }),
                new[] { "", "170" });
        }

        doc.H2("边界");
        doc.Raw("<div class=\"note\">" +
                "1. Prefetch 需要管理员权限；SysMain 被关闭时目录为空，这里也就查不到。<br/>" +
                "2. <b>最后运行时间取的是 .pf 写入时间</b>，不是文件内的时间戳字段——后者偏移随版本变化，宁可给近似值也不给错值。<br/>" +
                "3. 运行次数按版本布局读取（v17 / v23 / v26 / v30 / v31），读出来离谱就直接显示「—」。<br/>" +
                "4. 部分 .pf 是 MAM 压缩格式，本工具会调系统接口解压；解不开的计入「压缩未解开」，不会导出错误数据。<br/>" +
                "5. 这里只证明「运行过 / 出现过」，不代表当前还在，也不代表行为可疑。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "本地执行痕迹");

        var data = new ReportData
        {
            Kind = "exec-artifact",
            Title = "本地执行痕迹",
            Subtitle = $"Prefetch {r.Prefetch.Count} 条 · ShimCache {r.ShimCache.Count} 条",
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            Tool = new ToolInfo(),
            Machine = new MachineInfo
            {
                Name = Environment.MachineName,
                User = Environment.UserName,
                Os = Environment.OSVersion.VersionString,
                Admin = ElevationService.IsAdmin
            },
            Verdict = new VerdictInfo
            {
                Score = -1,
                Level = suspicious.Count > 0 ? "中" : "低",
                Text = suspicious.Count > 0
                    ? $"{suspicious.Count} 个程序从临时目录运行过"
                    : "没有发现从临时目录运行的程序",
                Counts = new ReportCounts
                {
                    Warn = suspicious.Count,
                    Hint = 0,
                    Ok = r.Prefetch.Count - suspicious.Count
                }
            },
            Sections = new List<ReportSection>
            {
                new()
                {
                    Group = "Prefetch",
                    Items = r.Prefetch.Take(200).Select(x => new ReportItem
                    {
                        Level = x.IsSuspiciousPath ? "hint" : "ok",
                        Title = x.Name,
                        Detail = $"运行 {x.RunCountText} 次",
                        Basis = $"最后运行 {x.LastRunText} · {x.VersionText}"
                    }).ToList()
                }
            },
            Stages = new List<ReportStageInfo>
            {
                new() { Name = "Prefetch", State = r.PrefetchState, Items = r.Prefetch.Count, Note = r.PrefetchNote },
                new() { Name = "ShimCache", State = r.ShimCacheState, Items = r.ShimCache.Count, Note = r.ShimCacheNote }
            }
        };

        return new ReportPayload
        {
            Title = "本地执行痕迹",
            Subtitle = $"Prefetch {r.Prefetch.Count} 条 · ShimCache {r.ShimCache.Count} 条 · {r.ElapsedText}",
            EngineTag = "执行痕迹",
            HtmlPath = path,
            PlainSummary = $"本地执行痕迹\nPrefetch {r.Prefetch.Count} 条 / ShimCache {r.ShimCache.Count} 条\n" +
                           $"临时目录程序 {suspicious.Count} 个",
            Data = data
        };
    }

    private static string Short(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= 100 ? text : text[..100];
    }
}
