using System.Globalization;
using System.Management;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 可靠性监视器：读 <c>Win32_ReliabilityStabilityMetrics</c> 与 <c>Win32_ReliabilityRecords</c>。
///
/// 这两个 WMI 类是 Windows「可靠性和问题历史」自身的数据源（由 RACWMI 提供），
/// 所以拿到的稳定性指数、崩溃记录和 perfmon /rel 界面里看到的是同一份数据，
/// 而不是我们自己按事件日志猜出来的近似值。精简镜像若删了该提供程序，
/// 这里会明确报「不可用」，不会给出编造的数字。
/// </summary>
public static class ReliabilityService
{
    /// <summary>读取结果：数据是否可用 + 记录 + 每日指数。</summary>
    public sealed class Result
    {
        public bool Available { get; set; }

        public string Reason { get; set; } = "";

        public int Days { get; set; } = 30;

        public List<ReliabilityRecord> Records { get; set; } = new();

        public List<StabilityDay> Timeline { get; set; } = new();

        public double AverageIndex => Timeline.Count == 0 ? -1 : Math.Round(Timeline.Average(d => d.Index), 2);

        public double LowestIndex => Timeline.Count == 0 ? -1 : Math.Round(Timeline.Min(d => d.Min), 2);

        public int CrashCount => Records.Count(r => r.Level == "warn");

        public int ChangeCount => Records.Count(r => r.Level == "hint");
    }

    /// <summary>默认看最近 30 天。</summary>
    public static Result Collect(int days = 30)
    {
        var result = new Result { Days = days };

        var since = ToCimDate(DateTime.Now.AddDays(-days));

        if (!TryQuery(
                $"SELECT TimeGenerated, SystemStabilityIndex FROM Win32_ReliabilityStabilityMetrics " +
                $"WHERE TimeGenerated >= '{since}'",
                out var metrics, out var metricError))
        {
            result.Available = false;
            result.Reason = metricError;
            return result;
        }

        if (!TryQuery(
                $"SELECT TimeGenerated, SourceName, EventIdentifier, ProductName, Message FROM Win32_ReliabilityRecords " +
                $"WHERE TimeGenerated >= '{since}'",
                out var records, out var recordError))
        {
            result.Available = false;
            result.Reason = recordError;
            return result;
        }

        result.Available = true;

        foreach (var row in records)
        {
            var time = TimeOf(row);
            if (time == null) continue;

            var source = EvidenceService.Str(row, "SourceName");
            var eventId = int.TryParse(EvidenceService.Str(row, "EventIdentifier"), out var id) ? id : 0;

            var (category, level) = Categorize(source, eventId);

            result.Records.Add(new ReliabilityRecord
            {
                Time = time.Value,
                Source = source,
                EventId = eventId,
                Product = EvidenceService.Str(row, "ProductName"),
                Message = Flatten(EvidenceService.Str(row, "Message")),
                Category = category,
                Level = level
            });
        }

        result.Records = result.Records.OrderByDescending(r => r.Time).ToList();

        // 小时级指数 → 按天聚合
        var byDay = new Dictionary<DateTime, List<double>>();

        foreach (var row in metrics)
        {
            var time = TimeOf(row);
            if (time == null) continue;

            if (!double.TryParse(EvidenceService.Str(row, "SystemStabilityIndex"),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var index))
                continue;

            var day = time.Value.Date;
            if (!byDay.TryGetValue(day, out var list)) byDay[day] = list = new List<double>();
            list.Add(index);
        }

        foreach (var (day, values) in byDay.OrderBy(x => x.Key))
        {
            result.Timeline.Add(new StabilityDay
            {
                Day = day,
                Index = Math.Round(values.Average(), 2),
                Min = Math.Round(values.Min(), 2),
                ProblemCount = result.Records.Count(r => r.Time.Date == day && r.Level == "warn")
            });
        }

        return result;
    }

    /// <summary>
    /// 只读一次，失败要能说出「为什么」——WMI 类不存在（精简镜像删了可靠性组件）
    /// 和权限不足的处理方式不一样，用户需要知道是哪种。
    /// </summary>
    private static bool TryQuery(string query, out List<Dictionary<string, object>> rows, out string error)
    {
        rows = new List<Dictionary<string, object>>();
        error = "";

        try
        {
            using var searcher = new ManagementObjectSearcher(query);

            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                foreach (var p in mo.Properties) d[p.Name] = p.Value;

                rows.Add(d);
            }

            return true;
        }
        catch (ManagementException ex)
        {
            error = ex.ErrorCode == ManagementStatus.InvalidClass
                ? "本机没有可靠性数据提供程序（精简镜像常删除「可靠性和性能监视器」组件），改用事件日志查看崩溃记录。"
                : ex.ErrorCode == ManagementStatus.AccessDenied
                    ? "读取可靠性数据被拒绝：请以管理员身份重启后再试。"
                    : "读取可靠性数据失败：" + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            error = "读取可靠性数据失败：" + ex.Message;
            return false;
        }
    }

    private static DateTime? TimeOf(Dictionary<string, object> row)
    {
        if (!row.TryGetValue("TimeGenerated", out var value) || value == null) return null;

        if (value is DateTime dt) return dt;

        try
        {
            return ManagementDateTimeConverter.ToDateTime(value.ToString());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把来源 + 事件号翻译成「这是什么」。数量不多，但决定了列表里能不能一眼看懂。</summary>
    private static (string Category, string Level) Categorize(string source, int eventId)
    {
        var src = source ?? "";

        if (eventId is 1000 or 1001 && src.Contains("Application Error", StringComparison.OrdinalIgnoreCase))
            return ("应用崩溃", "warn");

        if (eventId == 1002)
            return ("应用无响应", "warn");

        if (eventId == 1001 && src.Contains("Windows Error Reporting", StringComparison.OrdinalIgnoreCase))
            return ("错误报告", "hint");

        if (eventId == 1001 && src.Contains("BugCheck", StringComparison.OrdinalIgnoreCase))
            return ("蓝屏", "warn");

        if (eventId == 41 && src.Contains("Kernel-Power", StringComparison.OrdinalIgnoreCase))
            return ("意外断电/死机", "warn");

        if (eventId is 7031 or 7032 or 7033 or 7034)
            return ("服务异常退出", "warn");

        if (eventId is 7000 or 7009 or 7011 or 7023)
            return ("服务启动失败", "warn");

        if (src.Contains("WindowsUpdateClient", StringComparison.OrdinalIgnoreCase))
            return ("系统更新", "hint");

        if (src.Contains("UserPnp", StringComparison.OrdinalIgnoreCase))
            return ("设备驱动安装", "hint");

        if (src.Contains("Servicing", StringComparison.OrdinalIgnoreCase))
            return ("系统组件变更", "hint");

        if (src.Contains("MsiInstaller", StringComparison.OrdinalIgnoreCase))
            return ("软件安装/卸载", "hint");

        if (eventId == 6008 || (eventId == 6005 && src.Contains("EventLog", StringComparison.OrdinalIgnoreCase)))
            return ("开关机记录", "ok");

        return ("其他记录", "ok");
    }

    private static string Flatten(string message)
    {
        if (message.Length == 0) return "";

        var text = message.Replace("\r", " ").Replace("\n", " ").Trim();

        while (text.Contains("  ")) text = text.Replace("  ", " ");

        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>WQL 里的 CIM 时间：yyyyMMddHHmmss.ffffff+zzz。</summary>
    private static string ToCimDate(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var sign = offset.TotalMinutes >= 0 ? "+" : "-";
        var minutes = Math.Abs((int)offset.TotalMinutes);

        return local.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) +
               ".000000" + sign + minutes.ToString("000", CultureInfo.InvariantCulture);
    }
}
