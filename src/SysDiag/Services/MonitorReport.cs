using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>挂机监控会话报告：把「他打游戏的这段时间」发生的事整理成可交付的报告。</summary>
public static class MonitorReport
{
    public static ReportPayload Build(MonitorSession s)
    {
        var doc = new HtmlDoc("挂机监控",
            $"{s.TargetName}（{s.TargetPid}）　·　{s.StartedAt:yyyy-MM-dd HH:mm:ss} 起 {s.DurationText}　·　{s.IntervalSec} 秒/次");

        doc.Kpis(
            (s.DurationText, "监控时长", "#22D3EE"),
            (s.Ticks.ToString(), "采样次数", "#4C6EF5"),
            (s.Events.Count.ToString(), "事件", "#8B5CF6"),
            (s.WarnCount.ToString(), "高危迹象", s.WarnCount > 0 ? Palette.WarnHex : Palette.OkHex));

        doc.Note("<b>这是「监控期间的行为记录」，不是作弊判定。</b>" +
                 "监控只看采样时刻的状态，两次采样之间发生的事看不到；本次未见异常不等于全程未开挂。");

        doc.H2("结论");
        doc.Raw($"<div class=\"note\"><b>{Clean(s.Verdict)}</b><br/>" +
                $"目标进程：{Clean(s.TargetName)}（PID {s.TargetPid}）" +
                (s.TargetPath.Length > 0 ? $"<br/>路径：<span class=\"mono\">{Clean(s.TargetPath)}</span>" : "") +
                $"<br/>监控时段：{s.StartedAt:HH:mm:ss} — {(s.EndedAt ?? DateTime.Now):HH:mm:ss}（{Clean(s.DurationText)}）" +
                (s.GameExited ? "<br/>目标进程在监控期间退出过" : "") +
                "</div>");

        doc.H2($"事件时间线（{s.Events.Count} 条）");

        if (s.Events.Count == 0)
            doc.Empty("监控期间没有产生事件。");
        else
            doc.Table(
                new[] { "时间", "来源", "事件", "详情" },
                s.Events.OrderBy(e => e.Time).Select(e => new[]
                {
                    $"<span class=\"mono\">{e.TimeText}</span>",
                    $"<span class=\"tag {(e.Level == "warn" ? "t-warn" : e.Level == "hint" ? "t-hint" : "t-ok")}\">{Clean(e.Source)}</span>",
                    $"<b>{Clean(e.Text)}</b>",
                    e.Detail.Length > 0 ? $"<span class=\"mono\">{Clean(e.Detail)}</span>" : "<span class=\"muted\">—</span>"
                }),
                new[] { "74", "60", "34%", "" });

        doc.H2("怎么用这份记录");
        doc.Raw("<div class=\"note\">" +
                "1. 让被查的人<b>自己开启监控</b>、自己打一局，结束时点「生成报告」——中途无人接触才更有说服力。<br/>" +
                "2. 重点看三类事件：<b>未知进程持有游戏句柄</b>、<b>游戏进程新加载的模块</b>、<b>临时目录里冒出的进程</b>。<br/>" +
                "3. 系统组件与反作弊（ACE / EAC / BattlEye…）的句柄不计入事件，避免正常环境刷屏。<br/>" +
                "4. 报告可以导出成 .sdreport，连同体检报告一起归档或发给别人复核。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "挂机监控");

        var data = new ReportData
        {
            Kind = "monitor-session",
            Title = "挂机监控",
            Subtitle = $"{s.TargetName}（{s.TargetPid}） · {s.DurationText} · 事件 {s.Events.Count} 条",
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
                Level = s.VerdictLevel,
                Text = s.Verdict,
                Counts = new ReportCounts
                {
                    Warn = s.WarnCount,
                    Hint = s.HintCount,
                    Ok = s.Events.Count - s.WarnCount - s.HintCount
                }
            },
            Sections = new List<ReportSection>
            {
                new()
                {
                    Group = "监控事件",
                    Items = s.Events.OrderBy(e => e.Time).Select(e => new ReportItem
                    {
                        Title = e.Text,
                        Detail = e.Detail,
                        Basis = $"{e.TimeText} · {e.Source}",
                        Level = e.Level
                    }).ToList()
                }
            },
            Timeline = s.Events.OrderBy(e => e.Time).Select(e => new ReportEventInfo
            {
                Time = e.Time.ToString("MM-dd HH:mm:ss"),
                Source = e.Source,
                Text = e.Text,
                Level = e.Level
            }).ToList()
        };

        return new ReportPayload
        {
            Title = "挂机监控",
            Subtitle = $"{s.TargetName}（{s.TargetPid}） · {s.DurationText} · 事件 {s.Events.Count} 条 · 高危 {s.WarnCount}",
            EngineTag = "会话记录",
            HtmlPath = path,
            PlainSummary = $"挂机监控\n目标 {s.TargetName}（{s.TargetPid}）\n" +
                           $"时长 {s.DurationText} / 采样 {s.Ticks} 次 / 事件 {s.Events.Count} 条 / 高危 {s.WarnCount}\n" +
                           s.Verdict,
            Data = data
        };
    }

    private static string Clean(string text) => HtmlDoc.E(text);
}
