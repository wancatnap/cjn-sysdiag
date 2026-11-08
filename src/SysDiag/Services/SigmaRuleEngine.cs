using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 轻量 Sigma 规则引擎：把 Windows 事件日志读成结构化字段，再按规则匹配。
/// <para>
/// 与 Chainsaw 的区别：不做全文索引、不解析 evtx 二进制格式，走 Windows 自带的
/// EventLogReader；规则是内置精选集而不是加载 SigmaHQ 全量库——那几千条大多依赖
/// Sysmon，没装 Sysmon 的机器上只会得到一片“未命中”。
/// </para>
/// </summary>
public static class SigmaRuleEngine
{
    /// <summary>每个频道读最近多少条。</summary>
    private const int MaxPerChannel = 3000;

    /// <summary>每条规则最多保留多少条命中，避免刷屏。</summary>
    private const int MaxHitPerRule = 8;

    private const int MaxHits = 200;

    private static readonly Dictionary<string, string> ChannelMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["security"] = "Security",
        ["system"] = "System",
        ["application"] = "Application",
        ["powershell"] = "Microsoft-Windows-PowerShell/Operational",
        ["sysmon"] = "Microsoft-Windows-Sysmon/Operational",
        ["defender"] = "Microsoft-Windows-Windows Defender/Operational",
        ["taskscheduler"] = "Microsoft-Windows-TaskScheduler/Operational"
    };

    public static SigmaScanResult Scan(IProgress<string> progress = null)
    {
        var sw = Stopwatch.StartNew();

        var result = new SigmaScanResult();
        var rules = SigmaRules.All;
        result.Rules = rules;

        // 按 service 分组，一个频道只读一次
        var byService = rules
            .Where(r => r.Service.Length > 0)
            .GroupBy(r => r.Service, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in byService)
        {
            if (!ChannelMap.TryGetValue(group.Key, out var channel))
            {
                result.Channels.Add(new SigmaChannel
                {
                    Name = group.Key,
                    State = "missing",
                    Note = "没有对应的事件通道"
                });

                continue;
            }

            progress?.Report($"读取 {ChannelNameOf(group.Key)} …");

            var events = ReadChannel(channel, out var state, out var note);

            result.Channels.Add(new SigmaChannel
            {
                Name = ChannelNameOf(group.Key),
                State = state,
                Count = events.Count,
                Note = note
            });

            if (events.Count == 0) continue;

            result.EventsScanned += events.Count;

            foreach (var rule in group)
            {
                progress?.Report($"匹配规则：{rule.Title}");

                var perRule = 0;

                foreach (var ev in events)
                {
                    if (perRule >= MaxHitPerRule || result.Hits.Count >= MaxHits) break;
                    if (!Matches(rule, ev)) continue;

                    result.Hits.Add(new SigmaHit
                    {
                        Rule = rule,
                        Event = ev,
                        Why = BuildWhy(rule, ev),
                        Tag = IdentifyAntiCheat(ev)
                    });

                    perRule++;
                }
            }
        }

        result.Hits = result.Hits
            .OrderByDescending(h => LevelRank(h.Rule.Level))
            .ThenByDescending(h => h.Event?.Time)
            .ToList();

        result.ElapsedMs = sw.ElapsedMilliseconds;
        return result;
    }

    private static int LevelRank(string level) => level switch
    {
        "critical" => 5,
        "high" => 4,
        "medium" => 3,
        "low" => 2,
        _ => 1
    };

    /// <summary>把 Sigma 的等级映射成报告里统一的三档。</summary>
    private static string LevelOf(string ruleLevel) => ruleLevel switch
    {
        "critical" or "high" => "warn",
        "medium" => "hint",
        _ => "ok"
    };

    private static string ChannelNameOf(string service) => service switch
    {
        "security" => "安全日志",
        "system" => "系统日志",
        "application" => "应用日志",
        "powershell" => "PowerShell",
        "sysmon" => "Sysmon",
        "defender" => "Defender",
        _ => service
    };

    // ---------------- 读取事件 ----------------

    private static List<SigmaEvent> ReadChannel(string channel, out string state, out string note)
    {
        var list = new List<SigmaEvent>();
        state = "done";
        note = "";

        try
        {
            var query = new EventLogQuery(channel, PathType.LogName)
            {
                ReverseDirection = true,
                TolerateQueryErrors = true
            };

            using var reader = new EventLogReader(query);

            while (list.Count < MaxPerChannel)
            {
                using var rec = reader.ReadEvent();
                if (rec == null) break;

                var ev = ToEvent(rec, channel);
                if (ev != null) list.Add(ev);
            }

            note = $"{list.Count:N0} 条";
        }
        catch (UnauthorizedAccessException)
        {
            state = "limited";
            note = "当前权限读不到这个日志（需要管理员）";
        }
        catch (EventLogNotFoundException)
        {
            state = "missing";
            note = "这个日志在本机不存在（比如没装 Sysmon）";
        }
        catch (Exception ex)
        {
            state = "missing";
            note = Short(ex.Message);
        }

        return list;
    }

    private static SigmaEvent ToEvent(EventRecord rec, string channel)
    {
        try
        {
            var ev = new SigmaEvent
            {
                Channel = channel,
                Provider = rec.ProviderName ?? "",
                EventId = rec.Id,
                Time = rec.TimeCreated,
                RecordId = rec.RecordId ?? 0,
                Level = rec.LevelDisplayName ?? ""
            };

            var xml = rec.ToXml();
            if (!string.IsNullOrEmpty(xml)) ParseXml(xml, ev);

            ev.Summary = BuildSummary(ev);
            return ev;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把事件 XML 拆成字段字典：System 节点 + EventData/Data[@Name] + UserData。</summary>
    internal static void ParseXml(string xml, SigmaEvent ev)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root;
        if (root == null) return;

        var ns = root.Name.Namespace;

        var system = root.Element(ns + "System");
        if (system != null)
        {
            var idText = system.Element(ns + "EventID")?.Value;
            if (int.TryParse(idText, out var id)) ev.EventId = id;

            var provider = system.Element(ns + "Provider")?.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(provider)) ev.Provider = provider;

            var channel = system.Element(ns + "Channel")?.Value;
            if (!string.IsNullOrEmpty(channel)) ev.Channel = channel;

            var recordId = system.Element(ns + "EventRecordID")?.Value;
            if (long.TryParse(recordId, out var rid)) ev.RecordId = rid;

            var time = system.Element(ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
            if (DateTime.TryParse(time, out var dt)) ev.Time = dt;

            var level = system.Element(ns + "Level")?.Value;
            if (!string.IsNullOrEmpty(level)) ev.Level = level;
        }

        var data = root.Element(ns + "EventData");
        if (data != null)
        {
            var index = 0;

            foreach (var item in data.Elements(ns + "Data"))
            {
                var name = (string)item.Attribute("Name");
                var key = string.IsNullOrEmpty(name) ? "Data" + index : name;

                if (!string.IsNullOrEmpty(key)) ev.Fields[key] = item.Value ?? "";

                index++;
            }
        }

        ApplyFieldAliases(ev);

        var userData = root.Element(ns + "UserData");
        if (userData != null)
        {
            foreach (var child in userData.Elements())
            foreach (var leaf in child.Elements())
                if (!string.IsNullOrEmpty(leaf.Name.LocalName))
                    ev.Fields[leaf.Name.LocalName] = leaf.Value ?? "";
        }
    }

    /// <summary>
    /// 字段别名表：同一概念在 Sysmon、安全日志 4688、脚本日志里叫法不同。
    ///
    /// 借鉴 hayabusa 的做法——它给每类事件配一份 field mapping，让一条 Sigma 规则同时吃得下
    /// Sysmon 与内置审计事件。我们的规则子集没有「A 或 B 字段」这种写法，
    /// 所以改成在解析阶段把别名补齐：规则里写 Image，4688 的 NewProcessName 也能被匹配到。
    /// </summary>
    private static readonly (string Canonical, string[] Aliases)[] FieldAliases =
    {
        ("Image", new[] { "NewProcessName", "ProcessName", "Application" }),
        ("ParentImage", new[] { "ParentProcessName", "CreatorProcessName" }),
        ("CommandLine", new[] { "ProcessCommandLine" }),
        ("User", new[] { "TargetUserName", "SubjectUserName", "AccountName", "UserName" }),
        ("TargetFilename", new[] { "FileName", "ObjectName" }),
        ("TargetObject", new[] { "RegKey", "ObjectName" }),
        ("Details", new[] { "NewValue", "EventData" }),
        ("Hashes", new[] { "Hash" }),
        ("TargetImage", new[] { "TargetProcessImage", "ProcessName" })
    };

    /// <summary>把别名补进事件字段（双向：有 canonical 就补别名，有别名就补 canonical）。</summary>
    private static void ApplyFieldAliases(SigmaEvent ev)
    {
        foreach (var (canonical, aliases) in FieldAliases)
        {
            var value = ev.Get(canonical);

            if (value.Length > 0)
            {
                foreach (var alias in aliases)
                    if (ev.Get(alias).Length == 0) ev.Fields[alias] = value;
                continue;
            }

            foreach (var alias in aliases)
            {
                var aliasValue = ev.Get(alias);
                if (aliasValue.Length == 0) continue;
                ev.Fields[canonical] = aliasValue;
                break;
            }
        }
    }

    private static string BuildSummary(SigmaEvent ev)
    {
        string[] keys =
        {
            "ServiceName", "ImagePath", "NewProcessName", "CommandLine",
            "ScriptBlockText", "Threat Name", "ImageLoaded", "TargetImage", "SourceImage"
        };

        foreach (var key in keys)
        {
            var value = ev.Get(key);
            if (value.Length > 0) return Truncate($"{key}: {value}", 120);
        }

        return Truncate(ev.Fields.Values.FirstOrDefault() ?? "", 120);
    }

    // ---------------- 匹配 ----------------

    internal static bool Matches(SigmaRule rule, SigmaEvent ev)
    {
        var results = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, matchers) in rule.Selections)
        {
            results[name] = matchers.Count > 0 && matchers.All(m => MatchField(m, ev));
        }

        return EvalCondition(rule.Condition, results);
    }

    private static bool MatchField(SigmaMatcher matcher, SigmaEvent ev)
    {
        var raw = ev.Get(matcher.Field);
        if (raw.Length == 0 || matcher.Values.Count == 0) return false;

        var useAll = matcher.Modifiers.Any(m => m == "all");

        var op = matcher.Modifiers.FirstOrDefault(m =>
            m is "contains" or "startswith" or "endswith" or "re" or "gt" or "lt");

        bool One(string value) => op switch
        {
            "contains" => raw.Contains(value, StringComparison.OrdinalIgnoreCase),
            "startswith" => raw.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            "endswith" => raw.EndsWith(value, StringComparison.OrdinalIgnoreCase),
            "re" => SafeRegex(raw, value),
            "gt" => Compare(raw, value) > 0,
            "lt" => Compare(raw, value) < 0,
            _ => raw.Equals(value, StringComparison.OrdinalIgnoreCase)
        };

        return useAll ? matcher.Values.All(One) : matcher.Values.Any(One);
    }

    private static bool SafeRegex(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            return false;
        }
    }

    private static int Compare(string raw, string value)
    {
        if (double.TryParse(raw, out var a) && double.TryParse(value, out var b))
            return a.CompareTo(b);

        return string.Compare(raw, value, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>condition 求值：and 优先于 or；支持 not 与 "1 of xxx*" / "all of them"。</summary>
    internal static bool EvalCondition(string condition, Dictionary<string, bool> results)
    {
        if (string.IsNullOrWhiteSpace(condition))
            return results.Values.Any(v => v);

        foreach (var orPart in condition.Split(" or ", StringSplitOptions.RemoveEmptyEntries))
        {
            var all = true;
            var anyTerm = false;

            foreach (var termRaw in orPart.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
            {
                var term = termRaw.Trim();
                if (term.Length == 0) continue;

                anyTerm = true;

                var negate = false;
                if (term.StartsWith("not ", StringComparison.OrdinalIgnoreCase))
                {
                    negate = true;
                    term = term[4..].Trim();
                }

                var value = ResolveTerm(term, results);
                if (negate) value = !value;

                if (!value)
                {
                    all = false;
                    break;
                }
            }

            if (anyTerm && all) return true;
        }

        return false;
    }

    private static bool ResolveTerm(string term, Dictionary<string, bool> results)
    {
        var ofIndex = term.IndexOf(" of ", StringComparison.OrdinalIgnoreCase);

        if (ofIndex > 0)
        {
            var quant = term[..ofIndex].Trim().ToLowerInvariant();
            var pattern = term[(ofIndex + 4)..].Trim();

            List<string> keys;

            if (pattern.Equals("them", StringComparison.OrdinalIgnoreCase))
                keys = results.Keys.ToList();
            else if (pattern.EndsWith("*", StringComparison.Ordinal))
            {
                var prefix = pattern[..^1];
                keys = results.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
                keys = results.Keys.Where(k => k.Equals(pattern, StringComparison.OrdinalIgnoreCase)).ToList();

            if (keys.Count == 0) return false;

            var hit = keys.Count(k => results[k]);
            return quant == "all" ? hit == keys.Count : hit >= 1;
        }

        return results.TryGetValue(term, out var value) && value;
    }

    /// <summary>命中事件是否来自已知反作弊组件（服务名、驱动路径、进程路径都看）。</summary>
    private static string IdentifyAntiCheat(SigmaEvent ev)
    {
        string[] fields =
        {
            "ServiceName", "ImagePath", "ServiceFileName", "NewProcessName",
            "ImageLoaded", "SourceImage", "TargetImage", "Provider"
        };

        foreach (var field in fields)
        {
            var value = ev.Get(field);
            if (value.Length == 0) continue;

            var name = AntiCheatCatalog.Identify(value);
            if (name != null) return name;
        }

        return "";
    }

    private static string BuildWhy(SigmaRule rule, SigmaEvent ev)
    {
        foreach (var matchers in rule.Selections.Values)
        {
            foreach (var matcher in matchers)
            {
                var actual = ev.Get(matcher.Field);
                if (actual.Length == 0) continue;

                return Truncate($"{matcher.Field} = {actual}", 110);
            }
        }

        return "";
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..max] + "…";
    }

    private static string Short(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= 90 ? text : text[..90];
    }

    // ---------------- 报告 ----------------

    public static ReportPayload BuildReport(SigmaScanResult r)
    {
        var doc = new HtmlDoc("日志规则匹配",
            $"Sigma 规则子集　·　{r.Rules.Count} 条规则　·　{r.EventsScanned:N0} 条事件　·　耗时 {r.ElapsedText}");

        doc.Kpis(
            (r.Hits.Count.ToString(), "命中", r.Hits.Count > 0 ? "#E8590C" : "#2F9E44"),
            (r.HighCount.ToString(), "高/严重", "#C92A2A"),
            (r.MediumCount.ToString(), "中", "#E8590C"),
            (r.Rules.Count.ToString(), "加载规则", "#4C6EF5"));

        doc.Note("规则按 Sigma 的 detection/condition 语法匹配 Windows 事件日志的结构化字段。" +
                 "命中不等于作弊：很多正常软件也会触发服务安装、设备识别这类规则，请结合「外挂痕迹体检」的其他维度一起看。");

        doc.H2("命中明细");

        if (r.Hits.Count == 0)
        {
            doc.Empty("没有命中任何规则。可能是机器干净，也可能是相关日志没开启或读不到——看下面的通道状态。");
        }
        else
        {
            doc.Table(
                new[] { "级别", "规则", "时间", "事件", "依据" },
                r.Hits.Select(h => new[]
                {
                    $"<span class=\"tag {(h.Rule.Level is "critical" or "high" ? "t-bad" : h.Rule.Level == "medium" ? "t-warn" : "t-bin")}\">{h.Rule.LevelText}</span>",
                    $"<b>{HtmlDoc.E(h.Rule.Title)}</b><br/><span class=\"muted\">{HtmlDoc.E(h.Rule.Description)}</span>",
                    $"<span class=\"mono\">{h.TimeText}</span>",
                    $"<span class=\"mono\">{HtmlDoc.E(h.EventText)}</span>",
                    $"<span class=\"mono\">{HtmlDoc.E(h.Why)}</span>"
                    + (h.IsKnownGood ? $"<br/><span class=\"tag t-ok\">{HtmlDoc.E(h.TagText)}</span>" : "")
                }),
                new[] { "70", "38%", "110", "150", "" });
        }

        doc.H2("通道覆盖");

        doc.Table(
            new[] { "通道", "状态", "读取条数", "说明" },
            r.Channels.Select(c => new[]
            {
                HtmlDoc.E(c.Name),
                $"<span class=\"tag {(c.State == "done" ? "t-ok" : c.State == "limited" ? "t-warn" : "t-bin")}\">{c.StateText}</span>",
                c.Count.ToString("N0"),
                HtmlDoc.E(c.Note)
            }),
            new[] { "170", "80", "90", "" });

        doc.H2("规则清单");

        doc.Table(
            new[] { "级别", "规则", "通道", "字段" },
            r.Rules.Select(x => new[]
            {
                $"<span class=\"tag {(x.Level is "critical" or "high" ? "t-bad" : x.Level == "medium" ? "t-warn" : "t-bin")}\">{x.LevelText}</span>",
                $"<b>{HtmlDoc.E(x.Title)}</b><br/><span class=\"muted\">{HtmlDoc.E(x.Description)}</span>",
                HtmlDoc.E(x.ServiceText),
                $"<span class=\"mono\">{HtmlDoc.E(x.FieldsText)}</span>"
            }),
            new[] { "70", "", "110", "34%" });

        doc.H2("边界");
        doc.Raw("<div class=\"note\">" +
                "1. 只实现了 Sigma 的子集：字段修饰符、and/or/not、\"N of xxx*\"。<b>不支持时间窗聚合与 correlation</b>（near / count / timeframe）——单次快照里做状态窗口会失真。<br/>" +
                "2. 内置规则是围绕外挂痕迹精选的，<b>不是 SigmaHQ 全量库</b>。全量库大部分规则依赖 Sysmon，没装 Sysmon 的机器上不会命中。<br/>" +
                "3. 安全日志默认只有管理员能读；4688/4697 还需要开启审核策略，否则这些规则永远静默。<br/>" +
                "4. 命中只说明「系统记录到了这个行为」，不说明「这个行为是外挂」。正常装驱动、插 U 盘、装软件都会命中。" +
                "</div>");

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "日志规则匹配");

        var summary = new StringBuilder();
        summary.AppendLine("日志规则匹配（Sigma 子集）");
        summary.AppendLine($"规则 {r.Rules.Count} 条 · 事件 {r.EventsScanned:N0} 条 · 耗时 {r.ElapsedText}");
        summary.AppendLine(r.Verdict);
        summary.AppendLine();

        foreach (var h in r.Hits.Take(30))
            summary.AppendLine($"[{h.Rule.LevelText}] {h.Rule.Title}　{h.TimeText}　{h.Why}");

        var data = new ReportData
        {
            Kind = "sigma",
            Title = "日志规则匹配",
            Subtitle = r.Verdict,
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
                Level = r.HighCount > 0 ? "高" : r.MediumCount > 0 ? "中" : "低",
                Text = r.Verdict,
                Counts = new ReportCounts
                {
                    Warn = r.HighCount,
                    Hint = r.MediumCount,
                    Ok = r.LowCount
                }
            },
            Sections = r.Hits
                .GroupBy(h => h.Rule.Title)
                .Select(g => new ReportSection
                {
                    Group = g.Key,
                    Items = g.Select(h => new ReportItem
                    {
                        Level = h.IsKnownGood ? "ok" : LevelOf(h.Rule.Level),
                        Title = $"{h.Rule.Title}　{h.TimeText}",
                        Detail = h.Rule.Description,
                        Basis = h.Why,
                        Weight = 0,
                        Tags = h.IsKnownGood ? new List<string> { h.Tag } : new List<string>()
                    }).ToList()
                })
                .ToList(),
            Stages = r.Channels.Select(c => new ReportStageInfo
            {
                Name = c.Name,
                State = c.State,
                Items = c.Count,
                Note = c.Note
            }).ToList()
        };

        return new ReportPayload
        {
            Title = "日志规则匹配",
            Subtitle = $"{r.Verdict}　·　{r.Rules.Count} 条规则 / {r.EventsScanned:N0} 条事件　·　{r.ElapsedText}",
            EngineTag = "Sigma 子集",
            HtmlPath = path,
            PlainSummary = summary.ToString(),
            Data = data
        };
    }
}
