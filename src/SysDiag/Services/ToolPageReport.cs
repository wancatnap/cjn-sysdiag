using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 通用工具页报告：把表格里的行按 Tag 分组写成 HTML，附一份结构化数据。
///
/// 九个系统工具页共用它——每页只是「一类系统对象的清单」，
/// 没必要各写一套报告排版；页面有特殊结论时（如服务失败数）用 ToolPage.Report 覆盖。
/// </summary>
public static class ToolPageReport
{
    public static ReportPayload BuildGeneric(ToolPage page)
    {
        var rows = page.Rows ?? new List<LogRow>();

        var doc = new HtmlDoc(page.Title,
            $"{Environment.MachineName}　·　{DateTime.Now:yyyy-MM-dd HH:mm:ss}　·　{rows.Count} 条记录");

        var warn = rows.Count(r => r.Level == "warn");
        var hint = rows.Count(r => r.Level == "hint");
        var ok = rows.Count - warn - hint;

        doc.Kpis(
            (rows.Count.ToString(), "记录数", Palette.KnownHex),
            (warn.ToString(), "异常", warn > 0 ? Palette.WarnHex : Palette.OkHex),
            (hint.ToString(), "提示", hint > 0 ? Palette.HintHex : Palette.OkHex),
            (ok.ToString(), "正常", Palette.OkHex));

        if (page.Note.Length > 0)
            doc.Note(HtmlDoc.E(page.Note));

        // 按 Tag 分组（设备的类别、服务的启动类型、事件的来源…），没有 Tag 的归到「全部」
        var groups = rows
            .GroupBy(r => r.Tag.Length > 0 ? r.Tag : "全部")
            .OrderByDescending(g => g.Count())
            .ToList();

        foreach (var group in groups)
        {
            doc.H2($"{group.Key}（{group.Count()}）");
            doc.Table(
                new[] { "名称", "详情", "依据" },
                group.Select(r => new[]
                {
                    $"<b>{HtmlDoc.E(r.Title)}</b>" +
                    (r.LevelText.Length > 0
                        ? $" <span class=\"tag {(r.Level == "warn" ? "t-warn" : r.Level == "hint" ? "t-hint" : "t-ok")}\">" +
                          $"{HtmlDoc.E(r.LevelText)}</span>"
                        : ""),
                    HtmlDoc.E(r.Detail),
                    $"<span class=\"mono\">{HtmlDoc.E(r.Basis)}</span>"
                }),
                new[] { "240", "", "220" });
        }

        if (rows.Count == 0)
            doc.Empty(page.EmptyText);

        doc.H2("说明");
        doc.Raw("<div class=\"note\">" +
                "1. <b>本页内容由程序自己读取</b>（WMI / 注册表 / 系统 API），不依赖对应的 Windows 管理工具，" +
                "因此在精简系统里同样可用。<br/>" +
                "2. 标注「需系统工具」的操作（例如安装驱动、改防火墙规则、分区），" +
                "涉及驱动签名与内核交互，仍然只能用 Windows 原生工具完成。<br/>" +
                "3. 记录为采集瞬间的快照，系统状态变化后请重新刷新。" +
                "</div>");

        var path = HtmlReportBuilder.Save(doc.Build(), page.Title);

        var data = new ReportData
        {
            Kind = "tool-" + (page.Key.Length > 0 ? page.Key : "generic"),
            Title = page.Title,
            Subtitle = $"{rows.Count} 条记录 · 异常 {warn} 条",
            GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
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
                Level = warn > 0 ? "中" : "低",
                Text = warn > 0 ? $"有 {warn} 条异常" : "未发现异常",
                Counts = new ReportCounts { Warn = warn, Hint = hint, Ok = ok }
            },
            Sections = groups.Select(g => new ReportSection
            {
                Group = g.Key,
                Items = g.Select(r => new ReportItem
                {
                    Level = r.Level == "none" ? "ok" : r.Level,
                    Title = r.Title,
                    Detail = r.Detail,
                    Basis = r.Basis
                }).ToList()
            }).ToList()
        };

        return new ReportPayload
        {
            Title = page.Title,
            Subtitle = $"{rows.Count} 条记录 · 异常 {warn} 条",
            EngineTag = page.Title,
            HtmlPath = path,
            PlainSummary = BuildPlain(page, rows, warn),
            Data = data
        };
    }

    private static string BuildPlain(ToolPage page, List<LogRow> rows, int warn)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{page.Title}　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"共 {rows.Count} 条，异常 {warn} 条");

        foreach (var r in rows.Where(r => r.Level == "warn").Take(20))
            sb.AppendLine($"  [异常] {r.Title}　{r.Detail}");

        return sb.ToString();
    }
}
