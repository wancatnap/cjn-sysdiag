using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 两份报告对比：以「分组 + 标题」为键，比较等级变化。
/// 只做可解释的判定——恶化 / 改善 / 新增 / 消失 / 相同，不做加权打分。
/// </summary>
public static class ReportCompareService
{
    private static int Rank(string level) => level switch
    {
        "warn" => 2,
        "hint" => 1,
        _ => 0
    };

    private static string LevelText(string level) => level switch
    {
        "warn" => "线索",
        "hint" => "提示",
        _ => "正常"
    };

    public static CompareResult Compare(ReportData left, ReportData right)
    {
        var result = new CompareResult { Left = left, Right = right };
        var rows = new List<CompareRow>();

        var rightItems = right.Sections
            .SelectMany(s => s.Items.Select(i => (s.Group, Item: i)))
            .GroupBy(x => x.Group + "||" + x.Item.Title, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var section in left.Sections)
        {
            foreach (var item in section.Items)
            {
                var key = section.Group + "||" + item.Title;
                used.Add(key);

                if (rightItems.TryGetValue(key, out var hit))
                {
                    var a = Rank(item.Level);
                    var b = Rank(hit.Item.Level);

                    rows.Add(new CompareRow
                    {
                        Group = section.Group,
                        Title = item.Title,
                        Detail = item.Detail,
                        LevelA = LevelText(item.Level),
                        LevelB = LevelText(hit.Item.Level),
                        ChangeKey = b > a ? "worse" : b < a ? "improve" : "same"
                    });

                    continue;
                }

                // A 有、B 没有：原本是线索的消失属于改善，其余算消失
                rows.Add(new CompareRow
                {
                    Group = section.Group,
                    Title = item.Title,
                    Detail = item.Detail,
                    LevelA = LevelText(item.Level),
                    LevelB = "—",
                    ChangeKey = item.Level == "warn" ? "improve" : "removed"
                });
            }
        }

        foreach (var section in right.Sections)
        {
            foreach (var item in section.Items)
            {
                var key = section.Group + "||" + item.Title;
                if (used.Contains(key)) continue;

                rows.Add(new CompareRow
                {
                    Group = section.Group,
                    Title = item.Title,
                    Detail = item.Detail,
                    LevelA = "—",
                    LevelB = LevelText(item.Level),
                    ChangeKey = item.Level is "warn" or "hint" ? "worse" : "added"
                });
            }
        }

        result.Rows = rows
            .OrderBy(r => r.ChangeKey switch
            {
                "worse" => 0,
                "improve" => 1,
                "added" => 2,
                "removed" => 3,
                _ => 4
            })
            .ThenBy(r => r.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return result;
    }

    /// <summary>把对比结果打包成可再次导出的报告。</summary>
    public static ReportPayload BuildPayload(CompareResult r)
    {
        var data = new ReportData
        {
            Kind = "compare",
            Title = "报告对比",
            Subtitle = r.Summary,
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
                Score = r.HasScore ? r.Right.Verdict.Score : -1,
                Level = r.Right.Verdict.Level,
                Text = r.Summary,
                Counts = new ReportCounts
                {
                    Warn = r.WorseCount,
                    Hint = r.AddedCount,
                    Ok = r.SameCount
                }
            },
            Sections = r.Rows
                .GroupBy(x => x.ChangeText)
                .Select(g => new ReportSection
                {
                    Group = g.Key,
                    Items = g.Select(x => new ReportItem
                    {
                        Level = x.ChangeKey == "worse" ? "warn" : x.ChangeKey == "improve" ? "hint" : "ok",
                        Title = $"[{x.Group}] {x.Title}",
                        Detail = $"基准（A）：{x.LevelA}　→　对比（B）：{x.LevelB}",
                        Basis = x.Detail
                    }).ToList()
                })
                .ToList()
        };

        var doc = new HtmlDoc("报告对比", r.Summary);
        doc.Kpis(
            (r.WorseCount.ToString(), "恶化", "#C92A2A"),
            (r.AddedCount.ToString(), "新增", "#E8590C"),
            (r.ImproveCount.ToString(), "改善", "#2F9E44"),
            (r.SameCount.ToString(), "相同", "#868E96"));

        doc.Note($"基准：{HtmlDoc.E(r.Left.Machine.Name)} / {HtmlDoc.E(r.Left.GeneratedText)}<br/>" +
                 $"对比：{HtmlDoc.E(r.Right.Machine.Name)} / {HtmlDoc.E(r.Right.GeneratedText)}<br/>" +
                 $"风险分：{HtmlDoc.E(r.ScoreText)}");

        doc.H2("逐项变化");
        doc.Table(
            new[] { "变化", "分组", "条目", "基准 A", "对比 B" },
            r.Rows.Select(x => new[]
            {
                $"<span class=\"tag {(x.ChangeKey == "worse" ? "t-bad" : x.ChangeKey == "improve" ? "t-ok" : x.ChangeKey == "added" ? "t-warn" : "t-bin")}\">{x.ChangeText}</span>",
                HtmlDoc.E(x.Group),
                $"<b>{HtmlDoc.E(x.Title)}</b>",
                HtmlDoc.E(x.LevelA),
                HtmlDoc.E(x.LevelB)
            }),
            new[] { "70", "24%", "", "86", "86" });

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "报告对比");

        return new ReportPayload
        {
            Title = "报告对比",
            Subtitle = r.Summary,
            EngineTag = "对比",
            HtmlPath = path,
            PlainSummary = $"报告对比\n基准：{r.Left.Machine.Name} {r.Left.GeneratedText}\n" +
                           $"对比：{r.Right.Machine.Name} {r.Right.GeneratedText}\n{r.Summary}",
            Data = data
        };
    }
}
