using System.Diagnostics.Eventing.Reader;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>事件查看器里的一条事件。</summary>
public sealed class ViewEvent
{
    public DateTime? Time { get; init; }
    public string Level { get; init; } = "";
    public int EventId { get; init; }
    public string Provider { get; init; } = "";
    public string Channel { get; init; } = "";
    public long RecordId { get; init; }
    public string Message { get; init; } = "";

    public string TimeText => Time?.ToString("MM-dd HH:mm:ss") ?? "—";
    public string ProviderText => Provider.Length > 0 ? Provider : "(未知来源)";
    public string HeaderText => $"{ProviderText} · 事件 {EventId}";
    public string ShortMessage => Message.Length > 260 ? Message[..260].TrimEnd() + "…" : Message;

    /// <summary>Windows 事件级别 → 统一等级。</summary>
    public static string LevelOf(string display) => display switch
    {
        "错误" or "Critical" or "Error" or "严重" => "warn",
        "警告" or "Warning" => "hint",
        "信息" or "Information" or "详细" or "Verbose" => "ok",
        _ => "none"
    };

    public string LevelText => LevelDisplay switch
    {
        "warn" => "错误",
        "hint" => "警告",
        "ok" => "信息",
        _ => "其他"
    };

    public string LevelDisplay => LevelOf(Level);
}

/// <summary>
/// 内嵌事件查看器：读本机事件通道，替代 eventvwr.msc 系统窗口。
/// 默认只读最近 N 条，不把几十万条事件全塞进内存。
/// </summary>
public static class EventViewerService
{
    /// <summary>可读通道（与系统「事件查看器」常用分类对应）。</summary>
    public static readonly (string Channel, string Name)[] Channels =
    {
        ("System", "系统"),
        ("Application", "应用程序"),
        ("Security", "安全（需管理员）"),
        ("Setup", "安装"),
        ("Microsoft-Windows-Windows Defender/Operational", "Defender"),
        ("Microsoft-Windows-TaskScheduler/Operational", "计划任务"),
        ("Microsoft-Windows-Kernel-PnP/Configuration", "设备安装"),
        ("Microsoft-Windows-Sysmon/Operational", "Sysmon（若已安装）")
    };

    /// <summary>默认与上限条数：控制内存，同时保证够查。</summary>
    public const int DefaultMax = 1500;
    public const int HardMax = 20000;

    /// <summary>按「最近 N 小时」读取（预设简写，内部走 from/to 同一套逻辑）。</summary>
    public static List<ViewEvent> Read(string channel, int hours, int max, out string state, out string note)
    {
        var to = DateTime.Now;
        var from = hours > 0 ? to.AddHours(-hours) : DateTime.MinValue;
        return Read(channel, from, to, max, out state, out note);
    }

    /// <summary>
    /// 读一个通道在 [from, to] 内的事件（倒序读：晚于 to 的跳过、早于 from 的停）。
    /// from/to 与界面显示、报告文案用同一对边界。
    /// </summary>
    public static List<ViewEvent> Read(string channel, DateTime from, DateTime to, int max,
        out string state, out string note)
    {
        var list = new List<ViewEvent>();
        state = "done";
        note = "";
        max = Math.Clamp(max, 1, HardMax);

        try
        {
            var query = new EventLogQuery(channel, PathType.LogName)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);

            var hasFrom = from > DateTime.MinValue;

            while (list.Count < max)
            {
                using var rec = reader.ReadEvent();
                if (rec == null) break;

                var time = rec.TimeCreated;

                if (hasFrom && time.HasValue && time.Value < from)
                    break;   // 倒序读，越过起点就可以停

                if (time.HasValue && time.Value > to)
                    continue;   // 晚于终点的跳过（自定义区间可能落在过去）

                list.Add(new ViewEvent
                {
                    Time = rec.TimeCreated,
                    Level = rec.LevelDisplayName ?? "",
                    EventId = rec.Id,
                    Provider = rec.ProviderName ?? "",
                    Channel = channel,
                    RecordId = rec.RecordId ?? 0,
                    Message = SafeMessage(rec)
                });
            }

            note = $"{list.Count:N0} 条";
        }
        catch (UnauthorizedAccessException)
        {
            state = "limited";
            note = "当前权限读不到这个日志（安全日志需要管理员）";
        }
        catch (EventLogNotFoundException)
        {
            state = "missing";
            note = "这个日志在本机不存在";
        }
        catch (Exception ex)
        {
            state = "failed";
            note = ex.Message.Length > 120 ? ex.Message[..120] : ex.Message;
        }

        return list;
    }

    /// <summary>读一条事件的完整消息（点开详情时用，不在列表阶段批量格式化）。</summary>
    public static string ReadFullMessage(string channel, long recordId)
    {
        try
        {
            var query = new EventLogQuery(channel, PathType.LogName,
                $"*[System[EventRecordID={recordId}]]")
            {
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);
            using var rec = reader.ReadEvent();
            return rec == null ? "" : SafeMessage(rec);
        }
        catch
        {
            return "";
        }
    }

    private static string SafeMessage(EventRecord rec)
    {
        try
        {
            var text = rec.FormatDescription();
            return string.IsNullOrWhiteSpace(text) ? "" : text.Trim();
        }
        catch
        {
            return "";
        }
    }

    public static ReportPayload BuildReport(string channelName, List<ViewEvent> events, TimeRange range,
        string stateNote)
    {
        var doc = new HtmlDoc("事件查看器",
            $"{channelName}　·　{events.Count:N0} 条　·　{range.Label}");

        var err = events.Count(e => e.LevelDisplay == "warn");
        var warn = events.Count(e => e.LevelDisplay == "hint");

        doc.Kpis(
            (events.Count.ToString("N0"), "事件", "#4C6EF5"),
            (err.ToString(), "错误", err > 0 ? Palette.WarnHex : Palette.OkHex),
            (warn.ToString(), "警告", warn > 0 ? Palette.HintHex : Palette.OkHex),
            ((events.Count - err - warn).ToString(), "信息 / 其他", "#8B5CF6"));

        doc.Note("这是本机事件通道的只读快照，用于替代系统「事件查看器」。" +
                 $"查询区间与界面显示一致：<b>{range.Describe()}</b>。" +
                 "单次读取有上限，避免把整本日志读进内存。");

        doc.H2($"事件列表（{events.Count:N0} 条）");
        if (events.Count == 0)
            doc.Empty(stateNote.Length > 0 ? stateNote : "没有读到事件。");
        else
            doc.Table(
                new[] { "时间", "级别", "来源 / 事件号", "内容" },
                events.Take(800).Select(e => new[]
                {
                    $"<span class=\"mono\">{e.TimeText}</span>",
                    $"<span class=\"tag {(e.LevelDisplay == "warn" ? "t-warn" : e.LevelDisplay == "hint" ? "t-hint" : "t-ok")}\">{e.LevelText}</span>",
                    $"<span class=\"mono\">{HtmlDoc.E(e.ProviderText)} · {e.EventId}</span>",
                    $"<span class=\"muted\">{HtmlDoc.E(e.ShortMessage)}</span>"
                }),
                new[] { "96", "56", "200", "" });

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "事件查看器");

        var data = new ReportData
        {
            Kind = "event-viewer",
            Title = "事件查看器",
            Subtitle = $"{channelName} · {range.Label} · {events.Count:N0} 条",
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
                Level = err > 0 ? "中" : "低",
                Text = err > 0 ? $"{err} 条错误事件" : "没有错误级事件",
                Counts = new ReportCounts { Warn = err, Hint = warn, Ok = events.Count - err - warn }
            },
            Sections = new List<ReportSection>
            {
                new()
                {
                    Group = channelName,
                    Items = events.Take(300).Select(e => new ReportItem
                    {
                        Title = $"{e.HeaderText}",
                        Detail = e.ShortMessage,
                        Basis = $"{e.TimeText} · {e.Channel} · 记录 {e.RecordId}",
                        Level = e.LevelDisplay
                    }).ToList()
                }
            },
            Timeline = events.Take(300).Where(e => e.Time.HasValue).Select(e => new ReportEventInfo
            {
                Time = e.TimeText,
                Source = e.ProviderText,
                Text = $"事件 {e.EventId}",
                Level = e.LevelDisplay
            }).ToList()
        };

        return new ReportPayload
        {
            Title = "事件查看器",
            Subtitle = $"{channelName} · {range.Label} · {events.Count:N0} 条 · 错误 {err} / 警告 {warn}",
            EngineTag = "事件日志",
            HtmlPath = path,
            PlainSummary = $"事件查看器 · {channelName}\n区间 {range.Describe()}\n" +
                           $"事件 {events.Count:N0} 条 / 错误 {err} / 警告 {warn}",
            Data = data
        };
    }
}
