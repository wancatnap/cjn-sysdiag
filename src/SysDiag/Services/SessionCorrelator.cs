using System.Diagnostics.Eventing.Reader;
using System.Text;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 把游戏日志的时间窗和本机事件日志对齐，看同一时段系统里还发生了什么
/// （驱动加载、显卡 TDR、磁盘错误、应用崩溃、程序启动等）。
/// 只做时间相关性整理，不代表因果。
/// </summary>
public static class SessionCorrelator
{
    private const int MaxRawEvents = 500;
    private const int MaxRows = 40;

    public static List<SessionEvent> Correlate(DateTime from, DateTime to, List<string> notes)
    {
        var raw = new List<SessionEvent>();

        ReadLog("System", from, to, raw, notes);
        ReadLog("Application", from, to, raw, notes);
        ReadUserAssist(from, to, raw, notes);

        return raw
            .GroupBy(e => (e.Category, e.Title, e.Source))
            .Select(g =>
            {
                var first = g.OrderBy(x => x.Time).First();
                first.Count = g.Count();
                first.Detail = g.OrderBy(x => x.Time).First().Detail;
                return first;
            })
            .OrderByDescending(e => e.Relevant)
            .ThenByDescending(e => e.Count)
            .ThenBy(e => e.Time)
            .Take(MaxRows)
            .ToList();
    }

    private static void ReadLog(string logName, DateTime from, DateTime to,
        List<SessionEvent> raw, List<string> notes)
    {
        try
        {
            var q = string.Format(
                "*[System[(Level=1 or Level=2 or Level=3) and " +
                "TimeCreated[@SystemTime>='{0}' and @SystemTime<='{1}']]]",
                from.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                to.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));

            var query = new EventLogQuery(logName, PathType.LogName, q);
            using var reader = new EventLogReader(query);

            var n = 0;
            while (n < MaxRawEvents)
            {
                using var rec = reader.ReadEvent();
                if (rec == null) break;
                n++;

                var level = rec.Level switch
                {
                    1 => "错误",
                    2 => "错误",
                    3 => "警告",
                    _ => "信息"
                };

                var (category, title) = Classify(rec);
                raw.Add(new SessionEvent
                {
                    Time = rec.TimeCreated?.ToLocalTime() ?? DateTime.MinValue,
                    Category = category,
                    Source = logName,
                    Title = title,
                    Detail = Short(rec),
                    Level = level,
                    Relevant = category is "显卡" or "磁盘" or "反作弊/驱动"
                });
            }
        }
        catch (Exception ex)
        {
            notes?.Add($"读取「{logName}」事件日志失败：{ex.Message}");
        }
    }

    private static (string Category, string Title) Classify(EventRecord rec)
    {
        var provider = rec.ProviderName ?? "";
        var id = rec.Id;

        if (provider.Contains("FilterManager", StringComparison.OrdinalIgnoreCase))
            return ("反作弊/驱动", "文件系统筛选器加载（如 ACE 反作弊驱动）");

        if (provider.Contains("ACE", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("AntiCheat", StringComparison.OrdinalIgnoreCase))
            return ("反作弊/驱动", "反作弊组件事件");

        if (provider.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("amdkmdag", StringComparison.OrdinalIgnoreCase))
            return ("显卡", id == 4101 ? "显卡驱动停止响应并被重置（TDR）" : "显卡驱动事件");

        if (provider.Contains("VSS", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("ntbackup", StringComparison.OrdinalIgnoreCase))
            return ("磁盘", "卷影复制（VSS）错误，常与磁盘压力或备份冲突有关");

        if (provider.Contains("disk", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("StorPort", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Ntfs", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("volmgr", StringComparison.OrdinalIgnoreCase))
            return ("磁盘", "磁盘 / 存储错误");

        if (provider.Contains("Application Error", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Windows Error Reporting", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase))
            return ("应用崩溃", "应用程序崩溃或错误报告");

        if (provider.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase))
            return ("电源", "非正常关机 / 电源事件");

        if (provider.Contains("EventLog", StringComparison.OrdinalIgnoreCase) && id == 6008)
            return ("电源", "上次关机是意外的");

        if (provider.Contains("Service Control Manager", StringComparison.OrdinalIgnoreCase))
            return ("服务", id == 7045 ? "安装了新服务（可能是驱动/外挂）" : "服务状态变化");

        if (provider.Contains("WLAN", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Tcpip", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Dhcp", StringComparison.OrdinalIgnoreCase))
            return ("网络", "网络连接事件");

        return ("其他", $"{provider}（事件 {id}）");
    }

    private static string Short(EventRecord rec)
    {
        try
        {
            var msg = rec.FormatDescription() ?? "";
            msg = msg.Replace("\r", " ").Replace("\n", " ").Trim();
            return msg.Length > 140 ? msg[..140] + "…" : msg;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>UserAssist：资源管理器记录的程序启动时间（无需管理员权限）。</summary>
    private static void ReadUserAssist(DateTime from, DateTime to,
        List<SessionEvent> raw, List<string> notes)
    {
        var guids = new[]
        {
            "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}",
            "{F4E57C4B-2036-45F0-A9AB-443BCFE33D9F}"
        };

        var found = 0;
        foreach (var g in guids)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{g}\Count");
                if (key == null) continue;

                foreach (var name in key.GetValueNames())
                {
                    if (key.GetValue(name) is not byte[] data || data.Length < 68) continue;

                    var ft = BitConverter.ToInt64(data, 60);
                    if (ft <= 0) continue;

                    DateTime last;
                    try { last = DateTime.FromFileTimeUtc(ft).ToLocalTime(); }
                    catch { continue; }

                    if (last < from || last > to) continue;

                    raw.Add(new SessionEvent
                    {
                        Time = last,
                        Category = "程序启动",
                        Source = "UserAssist",
                        Title = Rot13(name),
                        Detail = "该程序在此时间窗内运行过",
                        Level = "信息"
                    });
                    found++;
                }
            }
            catch (Exception ex)
            {
                notes?.Add("读取 UserAssist 失败：" + ex.Message);
            }
        }

        if (found == 0)
            notes?.Add("UserAssist 中没有落在该时间窗内的程序启动记录（Windows 只保留部分记录）");
    }

    private static string Rot13(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c >= 'a' && c <= 'z') sb.Append((char)('a' + (c - 'a' + 13) % 26));
            else if (c >= 'A' && c <= 'Z') sb.Append((char)('A' + (c - 'A' + 13) % 26));
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
