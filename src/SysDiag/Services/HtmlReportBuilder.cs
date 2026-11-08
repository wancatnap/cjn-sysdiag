using System.Text;
using SysDiag.Models;
using System.IO;

namespace SysDiag.Services;

public static class HtmlReportBuilder
{
    internal static readonly UTF8Encoding Utf8Bom = new(true);

    public static string Save(string html, string suffix) => Save(html, suffix, "");

    /// <summary>
    /// 保存报告。targetPath 非空时写到用户指定的位置（「另存为」），空则写默认 outputs。
    /// 指定路径时会把目录一并建出来——用户可能选一个还不存在的文件夹。
    /// </summary>
    public static string Save(string html, string suffix, string targetPath)
    {
        if (!string.IsNullOrWhiteSpace(targetPath))
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(targetPath, html, Utf8Bom);
            return targetPath;
        }

        AppPaths.Ensure();
        var path = Path.Combine(AppPaths.OutputDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}.html");
        File.WriteAllText(path, html, Utf8Bom);
        return path;
    }

    /// <summary>保存纯文本摘要，便于发给客服或贴到群里。</summary>
    public static string SaveText(string text, string suffix)
    {
        AppPaths.Ensure();
        var path = Path.Combine(AppPaths.OutputDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-{suffix}.txt");
        File.WriteAllText(path, text, Utf8Bom);
        return path;
    }

    public static string BuildLog(LogReport r, List<Finding> findings = null)
    {
        findings ??= LogInsight.Analyze(r);

        var doc = new HtmlDoc("游戏日志分析报告",
            $"来源：{HtmlDoc.E(r.Source)}（{HtmlDoc.E(r.SourceKind)}）<br>分析时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　识别引擎：<b>{HtmlDoc.E(r.Engine)}</b>");

        // ---- 人话结论放最前面 ----
        doc.Raw($"<div class=\"verdict\">{LogInsight.Verdict(r, findings)}</div>");

        doc.H2("问题清单（按影响排序）");
        if (findings.Count == 0)
        {
            doc.Empty("没有识别到值得报告的问题。");
        }
        else
        {
            foreach (var f in findings) doc.Raw(RenderFinding(f));
        }

        doc.H2("同时间段的其他活动");

        if (r.SessionStart is not DateTime ss || r.SessionEnd is not DateTime se)
        {
            doc.Empty("未能从日志中解析出时间戳，无法做同时间段关联。");
        }
        else
        {
            var minutes = Math.Max(1, (int)(se - ss).TotalMinutes);
            var span = minutes >= 60 ? $"{minutes / 60.0:N1} 小时" : $"{minutes} 分钟";

            doc.Note($"游戏日志覆盖：<b>{ss:MM-dd HH:mm}</b> → <b>{se:MM-dd HH:mm}</b>（约 {span}）" +
                     (string.IsNullOrEmpty(r.SessionFile) ? "" : $"，取自 <code>{HtmlDoc.E(r.SessionFile)}</code>") +
                     "。下面是同一时段本机记录的系统事件。");

            if (r.SessionEvents.Count == 0)
            {
                doc.Note("该时段本机没有记录错误或警告事件。");
            }
            else
            {
                doc.Table(new[] { "时间", "类别", "级别", "事件", "次数" },
                    r.SessionEvents.Select(e => new[]
                    {
                        e.TimeText,
                        HtmlDoc.E(e.Category),
                        $"<span class=\"tag {e.CssClass}\">{e.Level}</span>",
                        HtmlDoc.E(e.Title),
                        e.Count > 1 ? $"×{e.Count}" : ""
                    }),
                    new[] { "110px", "100px", "70px", "", "60px" });
            }

            // 相关性提示（只说时间重合，不说因果）
            var hints = new List<string>();
            if (r.SessionEvents.Any(e => e.Category == "显卡"))
                hints.Add("该时段有<b>显卡驱动</b>事件，可能与画面异常 / 资源加载失败有关");
            if (r.SessionEvents.Any(e => e.Category == "磁盘"))
                hints.Add("该时段有<b>磁盘/存储</b>错误，可能解释资源读取失败");
            if (r.SessionEvents.Any(e => e.Category == "反作弊/驱动"))
                hints.Add("该时段有<b>反作弊驱动</b>加载记录（正常现象，说明 ACE 已随游戏启动）");
            if (r.SessionEvents.Any(e => e.Category == "应用崩溃"))
                hints.Add("该时段有其他<b>程序崩溃</b>记录，可对照时间点排查");

            if (hints.Count > 0)
            {
                var sb2 = new StringBuilder();
                foreach (var h in hints) sb2.Append("· ").Append(h).Append("<br>");
                doc.Note(sb2.ToString().TrimEnd());
            }

            if (r.SessionNotes.Count > 0)
            {
                var sb3 = new StringBuilder();
                foreach (var n in r.SessionNotes) sb3.Append("· ").Append(HtmlDoc.E(n)).Append("<br>");
                doc.Note(sb3.ToString().TrimEnd());
            }

            doc.Note("<b>怎么看这一段：</b>只是时间上的重合，不代表因果。" +
                     "另外这些事件来自<b>本机</b>——如果分析的是别人发来的日志包，这一段没有参考意义。");
        }

        doc.H2("外部软件痕迹（安全检查）");

        if (r.SecurityHits.Count == 0)
        {
            doc.Raw("<div class=\"verdict\" style=\"border-left-color:#2F9E44\">" +
                    "在日志里<b>没有发现</b>已知外挂 / 调试器 / 注入器的痕迹。" +
                    "</div>");
        }
        else
        {
            doc.Note($"发现 <b>{r.SecurityHits.Count}</b> 条线索，按可信度从高到低列出（最多 20 条）：");
            doc.Table(new[] { "类别", "强度", "指示", "位置" },
                r.SecurityHits.Take(20).Select(h => new[]
                {
                    HtmlDoc.E(h.Category),
                    $"<span class=\"tag {h.CssClass}\">{h.Strength}</span>",
                    $"<code>{HtmlDoc.E(h.Indicator)}</code>",
                    HtmlDoc.E($"{h.File}:{h.Line}")
                }),
                new[] { "130px", "70px", "", "180px" });
        }

        if (r.SecurityNotes.Count > 0)
        {
            var notes = new StringBuilder();
            foreach (var n in r.SecurityNotes) notes.Append("· ").Append(HtmlDoc.E(n)).Append("<br>");
            doc.Note(notes.ToString().TrimEnd());
        }

        doc.Note("<b>务必注意：</b>游戏日志只记录游戏自身选择记录的内容。" +
                 "绝大多数外部工具根本不写游戏日志，<b>所以“没有痕迹”不等于“没运行过”</b>；" +
                 "反过来，命中也可能是同名文件或误报，<b>不等同于作弊事实</b>。" +
                 "是否违规以反作弊系统的判定为准，本工具只做线索整理。");

        doc.H2("技术概览");
        doc.Kpis(
            (r.FileCount.ToString("N0"), "文件总数", ""),
            ($"{r.TotalBytes / 1024.0 / 1024.0:N1} MB", "扫描总大小", ""),
            (r.LineTotal.ToString("N0"), "扫描行数", ""),
            (r.CrashCount.ToString("N0"), "崩溃类命中", "#C92A2A"),
            (r.ErrorCount.ToString("N0"), "错误类行数", "#E8590C"),
            (r.WarnCount.ToString("N0"), "警告类行数", "#F08C00"));

        if (r.TimeMin is not null)
        {
            doc.H2("日志时间范围");
            doc.Table(new[] { "项", "时间" }, new[]
            {
                new[] { "最早", r.TimeMin.Value.ToString("yyyy-MM-dd HH:mm:ss") },
                new[] { "最晚", (r.TimeMax ?? r.TimeMin).Value.ToString("yyyy-MM-dd HH:mm:ss") }
            }, new[] { "130px", "" });
        }

        doc.H2("高频错误原文（技术细节）");
        if (r.TopErrors.Count > 0)
        {
            doc.Table(new[] { "次数", "归一化内容" },
                r.TopErrors.Select(e => new[]
                {
                    $"<b>{e.Count}</b>",
                    $"<code>{HtmlDoc.E(e.Signature)}</code>"
                }),
                new[] { "70px", "" });
        }
        else doc.Empty("未发现错误行。");

        doc.H2($"崩溃类明细（最多 100 条，共 {r.CrashCount} 条）");
        if (r.Crashes.Count > 0)
        {
            doc.Table(new[] { "文件", "行号", "内容" },
                r.Crashes.Take(100).Select(c => new[]
                {
                    HtmlDoc.E(c.File),
                    c.Line.ToString(),
                    $"<code>{HtmlDoc.E(c.Content)}</code>"
                }),
                new[] { "200px", "60px", "" });
        }
        else doc.Empty("未发现崩溃类关键字。");

        doc.H2("文件清单");
        var ordered = r.Files.OrderByDescending(f => f.Size).ToList();
        var shown = ordered.Take(300).ToList();
        doc.Table(new[] { "文件名", "大小", "行数", "命中", "类型" },
            shown.Select(f => new[]
            {
                HtmlDoc.E(f.Name),
                $"{f.Size / 1024.0:N1} KB",
                f.Lines?.ToString("N0") ?? "",
                f.Hits.ToString(),
                Tag(f.Kind)
            }),
            new[] { "", "100px", "80px", "80px", "110px" });

        if (ordered.Count > shown.Count)
            doc.Note($"文件较多，此处仅按体积列出前 {shown.Count} 个（共 {ordered.Count} 个）。完整清单见工作目录。");

        var tail = new StringBuilder();
        if (r.Warnings.Count > 0)
        {
            tail.Append("<h2>处理提示</h2><div class=\"note\">");
            foreach (var w in r.Warnings.Take(20))
                tail.Append("· ").Append(HtmlDoc.E(w)).Append("<br>");
            tail.Append("</div>");
        }
        if (!string.IsNullOrEmpty(r.WorkDir))
            tail.Append($"<div class=\"meta\" style=\"margin-top:28px\">工作目录：{HtmlDoc.E(r.WorkDir)}<br>报告由 破烂优化 生成</div>");
        else
            tail.Append("<div class=\"meta\" style=\"margin-top:28px\">报告由 破烂优化 生成</div>");
        return doc.Build().Replace("</div></body></html>", tail + "</div></body></html>");
    }

    private static string RenderFinding(Finding f)
    {
        var sb = new StringBuilder();
        sb.Append("<div class=\"finding\">");
        sb.Append("<div class=\"fh\">");
        sb.Append($"<span class=\"tag {f.CssClass}\">{f.LevelText}</span> ");
        sb.Append(HtmlDoc.E(f.Title));
        sb.Append($"<span class=\"fc\">出现 {f.Count:N0} 次</span>");
        sb.Append("</div>");
        sb.Append($"<div class=\"fm\">含义：{HtmlDoc.E(f.Meaning)}</div>");
        sb.Append($"<div class=\"fa\">建议：{HtmlDoc.E(f.Advice)}</div>");
        if (f.Samples.Count > 0)
            sb.Append($"<div class=\"fs\">示例：{HtmlDoc.E(f.Samples[0])}</div>");
        sb.Append("</div>");
        return sb.ToString();
    }

    private static string Tag(string kind) => kind switch
    {
        "文本" => "<span class=\"tag t-ok\">文本</span>",
        "事件日志" => "<span class=\"tag t-warn\">事件日志</span>",
        "二进制" => "<span class=\"tag t-bin\">二进制</span>",
        _ => "<span class=\"tag t-bin\">跳过</span>"
    };

    // ==================== 取证类报告 ====================

    public static string BuildHwid(List<HwidField> fields)
    {
        var doc = new HtmlDoc("机器码（HWID）",
            $"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　共 {fields.Count} 项");

        doc.Note("本页是<b>本机原始硬件标识</b>。游戏厂商的「机器码」通常是这些字段（或其中一部分）的组合哈希，" +
                 "各家算法不同，因此这里的值与他们系统里的编号不一定相同。<br>" +
                 "这些信息可唯一识别一台机器，转发前请确认接收方可信。");

        foreach (var g in fields.GroupBy(f => f.Group))
        {
            doc.H2(g.Key);
            doc.Table(new[] { "字段", "值" },
                g.Select(f => new[] { HtmlDoc.E(f.Name), Code(f.Value) }),
                new[] { "200px", "" });
        }

        if (fields.Count == 0) doc.Empty("没有采集到可用的硬件标识（可能是权限限制）。");
        return doc.Build();
    }

    public static string BuildDriverHistory(List<DeviceEvent> events)
    {
        var fail = events.Count(e => e.StatusCss == "t-bad");

        var doc = new HtmlDoc("设备与驱动记录",
            $"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　来源：<code>C:\\Windows\\INF\\setupapi.dev.log</code>");

        doc.Kpis(
            (events.Count.ToString("N0"), "记录条数", "#4C6EF5"),
            (fail.ToString("N0"), "失败", fail > 0 ? "#C92A2A" : "#2F9E44"),
            (events.Count(e => e.Action.Contains("卸载") || e.Action.Contains("移除")).ToString("N0"), "卸载 / 移除", "#E8590C"));

        doc.Note("这份日志只保留最近一段记录，会被 Windows 滚动覆盖：<b>更早的事件查不到，不代表没发生过</b>。" +
                 "「执行者」是当时调用安装程序的可执行文件。");

        doc.H2("记录明细（按时间倒序）");
        doc.Table(new[] { "时间", "操作", "对象", "执行者", "状态" },
            events.Select(e => new[]
            {
                HtmlDoc.E(e.TimeText),
                $"<span class=\"tag t-bin\">{HtmlDoc.E(e.Action)}</span>",
                Code(Trim(e.Target, 110)),
                Code(Trim(e.Caller, 80)),
                $"<span class=\"tag {e.StatusCss}\">{HtmlDoc.E(e.Status)}</span>"
            }),
            new[] { "150px", "86px", "", "260px", "150px" });

        return doc.Build();
    }

    public static string BuildUsbDevices(List<UsbDeviceInfo> devices)
    {
        var abnormal = devices.Count(d => d.Note.Length > 0);

        var doc = new HtmlDoc("USB 设备清单",
            $"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　来源：注册表 <code>Enum\\USB</code>");

        doc.Kpis(
            (devices.Count.ToString("N0"), "设备数", "#4C6EF5"),
            (devices.Count(d => d.Serial.Length > 0).ToString("N0"), "带序列号", "#2F9E44"),
            (abnormal.ToString("N0"), "需注意", abnormal > 0 ? "#E8590C" : "#2F9E44"));

        doc.Note("包含本机记录过、当前未必接着的 USB 设备。同一型号（同 VID/PID 且同序列号）已合并为一条，" +
                 "端口位置并列显示。<br>" +
                 "<b>首次 / 最后接入时间不在注册表可直接读取的位置</b>，读取需要管理员权限，因此本表不含时间列。");

        doc.H2("设备列表");
        doc.Table(new[] { "名称", "VID_PID", "序列号", "服务 / 驱动", "端口", "备注" },
            devices.Select(d => new[]
            {
                HtmlDoc.E(d.Name),
                Code(d.VidPid),
                Code(d.Serial.Length > 0 ? d.Serial : "（无）"),
                HtmlDoc.E(d.Service),
                HtmlDoc.E(d.Location),
                d.Note.Length > 0 ? $"<span class=\"tag {d.NoteCss}\">{HtmlDoc.E(d.Note)}</span>" : ""
            }),
            new[] { "", "170px", "190px", "110px", "150px", "110px" });

        return doc.Build();
    }

    public static string BuildPnp(List<PnpEventRow> rows)
    {
        var err = rows.Count(r => r.Level is "错误" or "严重");
        var warn = rows.Count(r => r.Level == "警告");

        var doc = new HtmlDoc("PnP 事件分析",
            $"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　来源：事件日志 " +
            "<code>Microsoft-Windows-Kernel-PnP/Configuration</code>　|　近 14 天");

        doc.Kpis(
            (rows.Count.ToString("N0"), "事件数", "#4C6EF5"),
            (err.ToString("N0"), "错误", err > 0 ? "#C92A2A" : "#2F9E44"),
            (warn.ToString("N0"), "警告", warn > 0 ? "#E8590C" : "#2F9E44"));

        doc.Note("常见事件 ID：<b>400</b> 设备已配置、<b>410</b> 设备已启动、<b>430</b> 设备需重启才能启动、" +
                 "<b>441 / 442</b> 驱动加载失败。设备频繁被重新配置，通常意味着接触不良或驱动异常。");

        doc.H2("按事件 ID 统计");
        doc.Table(new[] { "事件 ID", "次数", "级别" },
            rows.GroupBy(r => r.Id)
                .OrderByDescending(g => g.Count())
                .Select(g => new[]
                {
                    g.Key.ToString(),
                    g.Count().ToString("N0"),
                    $"<span class=\"tag {g.First().LevelCss}\">{HtmlDoc.E(g.First().Level)}</span>"
                }),
            new[] { "100px", "100px", "100px" });

        doc.H2("明细（按时间正序）");
        doc.Table(new[] { "时间", "ID", "级别", "设备", "说明" },
            rows.Select(r => new[]
            {
                HtmlDoc.E(r.TimeText),
                r.Id.ToString(),
                $"<span class=\"tag {r.LevelCss}\">{HtmlDoc.E(r.Level)}</span>",
                Code(r.Device),
                HtmlDoc.E(r.Message)
            }),
            new[] { "150px", "50px", "66px", "260px", "" });

        return doc.Build();
    }

    private static string Code(string text)
        => string.IsNullOrEmpty(text) ? "" : $"<code>{HtmlDoc.E(text)}</code>";

    private static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    // ==================== 文件删除记录（USN） ====================

    public static string BuildUsn(UsnResult r)
    {
        var doc = new HtmlDoc("文件删除记录",
            $"磁盘 <b>{HtmlDoc.E(r.Drive)}:</b>　|　时间范围：{r.Since:yyyy-MM-dd HH:mm} 之后　|　" +
            $"采集：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        if (r.NeedAdmin)
        {
            doc.Note("<b>需要管理员权限。</b>读取 NTFS USN 日志要打开磁盘卷句柄，普通权限拿不到。<br>" +
                     "在软件里点「以管理员身份重启并读取」：弹一次 UAC，整个程序以管理员身份重启，" +
                     "重启后自动回到「文件删除记录」并按原选择开始读取。");
            return doc.Build();
        }

        if (r.Error.Length > 0 && r.Records.Count == 0)
        {
            doc.Note("<b>读取失败：</b>" + HtmlDoc.E(r.Error) + "<br>" +
                     "常见原因：该卷不是 NTFS、USN 日志被关闭、或权限不足。");
            return doc.Build();
        }

        if (r.Error.Length > 0)
            doc.Note("读取过程中出现问题（已拿到部分记录）：" + HtmlDoc.E(r.Error));

        doc.Kpis(
            (r.Records.Count.ToString("N0"), "删除记录", r.Records.Count > 0 ? "#C92A2A" : "#2F9E44"),
            (r.ScannedRecords.ToString("N0"), "扫描的 USN 记录", "#4C6EF5"),
            (r.Records.Count > 0 ? r.Records.Max(x => x.Time).ToString("MM-dd HH:mm") : "-", "最近一次删除", "#868E96"));

        doc.Note("USN 日志是 NTFS 的变更流水，<b>按容量滚动覆盖</b>（本机默认 32 MB，通常只覆盖最近几天到几周）。" +
                 "查不到更早的记录不代表没删过。<br>" +
                 "路径由 MFT 索引还原；如果上级目录也已被删除，路径会退化成「文件名」，这是正常的。");

        doc.H2($"删除记录（最多显示 500 条，共 {r.Records.Count:N0} 条）");
        doc.Table(new[] { "时间", "文件名", "路径" },
            r.Records.Take(500).Select(x => new[]
            {
                HtmlDoc.E(x.TimeText),
                HtmlDoc.E(x.Name),
                Code(x.Path)
            }),
            new[] { "150px", "240px", "" });

        doc.H2("说明");
        doc.Note("· 这里的「删除」包含永久删除和移入回收站——移入回收站同样会产生删除记录。<br>" +
                 "· 只反映 NTFS 卷；exFAT / FAT32 没有 USN。<br>" +
                 "· 本功能只读日志，不恢复文件、不改动任何数据。");

        return doc.Build();
    }

    // ==================== 设备比对 ====================

    public static string BuildBaselineDiff(BaselineDiff diff)
    {
        var b = diff.Baseline;

        var doc = new HtmlDoc("设备比对",
            $"基线：{b.SavedAt:yyyy-MM-dd HH:mm}（{HtmlDoc.E(b.Computer)}，{HtmlDoc.E(b.Os)}）　|　" +
            $"当前：{DateTime.Now:yyyy-MM-dd HH:mm:ss}　|　变化 <b>{diff.TotalChanges}</b> 项");

        doc.Kpis(
            (diff.Changed.Count.ToString("N0"), "改动", diff.Changed.Count > 0 ? "#E8590C" : "#2F9E44"),
            (diff.Added.Count.ToString("N0"), "新增", diff.Added.Count > 0 ? "#4C6EF5" : "#2F9E44"),
            (diff.Removed.Count.ToString("N0"), "消失", diff.Removed.Count > 0 ? "#C92A2A" : "#2F9E44"),
            (diff.SameCount.ToString("N0"), "未变", "#868E96"));

        if (diff.TotalChanges == 0)
        {
            doc.Note("<b>结论：与基线完全一致。</b>机器码各字段与 USB 设备清单都没有变化，" +
                     "可用于说明这台机器在这段时间内没有更换硬件、也没有接入新设备。");
        }
        else
        {
            doc.Note($"<b>发现 {diff.TotalChanges} 处变化。</b>更换硬件、更新 BIOS、安装或卸载驱动、插入新的 USB 设备都会造成变化；" +
                     "如果这些操作不是你做的，值得逐条核对。");
        }

        if (diff.Changed.Count > 0)
        {
            doc.H2("改动的字段");
            doc.Table(new[] { "字段", "基线值", "当前值" },
                diff.Changed.Select(c => new[] { HtmlDoc.E(c.Key), Code(c.Old), Code(c.New) }),
                new[] { "240px", "", "" });
        }

        if (diff.Added.Count > 0)
        {
            doc.H2("新增的字段");
            doc.Table(new[] { "字段", "当前值" },
                diff.Added.Select(c => new[] { HtmlDoc.E(c.Key), Code(c.Value) }),
                new[] { "240px", "" });
        }

        if (diff.Removed.Count > 0)
        {
            doc.H2("消失的字段");
            doc.Table(new[] { "字段", "基线值" },
                diff.Removed.Select(c => new[] { HtmlDoc.E(c.Key), Code(c.Value) }),
                new[] { "240px", "" });
        }

        if (diff.UsbAdded.Count > 0 || diff.UsbRemoved.Count > 0)
        {
            doc.H2("USB 设备变化");
            var rows = diff.UsbAdded.Select(u => new[] { "<span class=\"tag t-bin\">新增</span>", HtmlDoc.E(u), "" })
                .Concat(diff.UsbRemoved.Select(u => new[] { "<span class=\"tag t-bad\">消失</span>", HtmlDoc.E(u), "" }));
            doc.Table(new[] { "变化", "设备", "说明" }, rows, new[] { "70px", "", "" });
        }

        doc.H2("说明");
        doc.Note("比对范围：机器码清单（UUID、主板 / BIOS 序列号、ProcessorId、磁盘序列号、MachineGuid、物理网卡 MAC、卷序列号）" +
                 "与 USB 设备清单。<br>" +
                 "以下情况会造成正常变化：换硬盘 / 加内存 / 插拔设备 / 更新 BIOS / 重装系统（MachineGuid 与卷序列号会变）。<br>" +
                 "本比对<b>不判断「是不是同一台机器」</b>，只列出变化，结论由你判断。");

        return doc.Build();
    }
}
