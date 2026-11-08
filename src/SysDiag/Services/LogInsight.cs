using System.Text;
using SysDiag.Models;

namespace SysDiag.Services;

public enum InsightLevel
{
    Critical, // 严重：会导致崩溃/卡顿，建议处理
    Warning,  // 注意：异常但不致命
    Info,     // 提示：正常现象，了解即可
    Noise     // 可忽略：开发调试残留、埋点限流等
}

public sealed class Finding
{
    public string Title { get; set; } = "";
    public string Meaning { get; set; } = "";
    public string Advice { get; set; } = "";
    public InsightLevel Level { get; set; }
    public int Count { get; set; }
    /// <summary>未归类的兜底项，始终排在最后。</summary>
    public bool IsResidual { get; set; }
    public List<string> Samples { get; } = new();

    public string LevelText => Level switch
    {
        InsightLevel.Critical => "严重",
        InsightLevel.Warning => "注意",
        InsightLevel.Info => "提示",
        _ => "可忽略"
    };

    public string CssClass => Level switch
    {
        InsightLevel.Critical => "t-bad",
        InsightLevel.Warning => "t-warn",
        InsightLevel.Info => "t-ok",
        _ => "t-bin"
    };
}

/// <summary>
/// 把归一化后的报错翻译成人能看懂的结论：是什么、要不要管、怎么处理。
/// </summary>
public static class LogInsight
{
    private sealed class Rule
    {
        public string[] Keywords { get; init; } = Array.Empty<string>();
        public string Title { get; init; } = "";
        public InsightLevel Level { get; init; }
        public string Meaning { get; init; } = "";
        public string Advice { get; init; } = "";
    }

    /// <summary>顺序有意义：越具体的规则越靠前，命中后不再参与后续匹配。</summary>
    private static readonly Rule[] Rules =
    {
        // ---- 三角洲行动（Lua / DFM）常见 ----
        new()
        {
            Keywords = new[] { "resimageutil", "resource load failed", "failed to load texture", "failed to load asset" },
            Title = "图片 / 资源加载失败",
            Level = InsightLevel.Critical,
            Meaning = "游戏反复读不到美术资源。常见于游戏文件损坏、硬盘读取掉速，或显存/内存吃紧。",
            Advice = "先在启动器里做一次「校验文件完整性」；确认游戏盘剩余空间充足；关闭占用内存的后台程序再试。"
        },
        new()
        {
            Keywords = new[] { "openweapondebugclick", "not debug ---", "bopendebug" },
            Title = "开发调试代码输出",
            Level = InsightLevel.Noise,
            Meaning = "官方留在客户端里的调试开关日志，正常玩也会刷，与你的电脑无关。",
            Advice = "无需处理，可以直接忽略。"
        },
        new()
        {
            Keywords = new[] { "auctionserver", "拍卖行出售", "dobatchfetchsalelistex" },
            Title = "拍卖行业务返回",
            Level = InsightLevel.Noise,
            Meaning = "拍卖行接口的正常业务提示（例如商品已下架），不是程序故障。",
            Advice = "无需处理。"
        },
        new()
        {
            Keywords = new[] { "tlog", "execced tlog rate", "limit total send num" },
            Title = "埋点上报限流",
            Level = InsightLevel.Noise,
            Meaning = "游戏往服务器上报行为数据时被限流，纯粹是统计用途，不影响对局。",
            Advice = "无需处理。"
        },
        new()
        {
            Keywords = new[] { "skinmodifier", "templatematerial == nullptr" },
            Title = "武器皮肤材质缺失",
            Level = InsightLevel.Warning,
            Meaning = "某把武器的皮肤引用了不存在的材质，可能导致模型显示异常甚至闪退。",
            Advice = "换掉该皮肤观察是否复现；若稳定复现，带上日志反馈给官方客服。"
        },
        new()
        {
            Keywords = new[] { "collectionserver", "fetchcamouflagestatusinfo" },
            Title = "收藏 / 外观数据请求异常",
            Level = InsightLevel.Info,
            Meaning = "查询迷彩、收藏状态时服务端返回异常，通常是网络或服务端瞬时问题。",
            Advice = "偶尔出现可忽略；持续出现时检查网络。"
        },

        // ---- Unreal 引擎通用 ----
        new()
        {
            Keywords = new[] { "apperror called", "fatal error", "unhandled exception", "critical error" },
            Title = "游戏发生致命错误",
            Level = InsightLevel.Critical,
            Meaning = "引擎捕获到无法继续的错误，通常表现为闪退。",
            Advice = "更新显卡驱动；关闭超频 / XMP；校验游戏文件；记录崩溃前在做什么。"
        },
        new()
        {
            Keywords = new[] { "access violation" },
            Title = "内存访问违规",
            Level = InsightLevel.Critical,
            Meaning = "程序访问了不属于它的内存，是最典型的闪退原因之一。",
            Advice = "优先校验游戏文件并更新驱动；若装了模组/汉化/加速器，先禁用再试。"
        },
        new()
        {
            Keywords = new[] { "device removed", "dxgi_error", "gpu crash", "gpucrash", "d3d device" },
            Title = "显卡设备丢失",
            Level = InsightLevel.Critical,
            Meaning = "显卡驱动崩溃后被系统重置（TDR），画面会黑屏几秒甚至退出游戏。",
            Advice = "用 DDU 干净卸载后重装显卡驱动；关闭显卡超频；电源设为高性能；降低画质与帧率上限。"
        },
        new()
        {
            Keywords = new[] { "failed to open file", "corrupt file", "corrupted", "pak file", "pakchunk" },
            Title = "游戏文件读取失败",
            Level = InsightLevel.Critical,
            Meaning = "读不到或读到损坏的游戏文件。",
            Advice = "校验游戏完整性；检查硬盘健康（SMART）；确认没有杀毒软件隔离游戏文件。"
        },
        new()
        {
            Keywords = new[] { "out of memory", "内存不足" },
            Title = "内存耗尽",
            Level = InsightLevel.Critical,
            Meaning = "游戏出现了真正的内存耗尽（OOM），容易直接闪退。",
            Advice = "调低纹理/画质；增加虚拟内存；关闭浏览器等多内存占用程序。"
        },
        new()
        {
            Keywords = new[] { "low memory", "lowmemory", "内存占用过高" },
            Title = "内存压力提示",
            Level = InsightLevel.Warning,
            Meaning = "游戏自报内存偏紧，尚未崩，但长时间运行可能卡顿。",
            Advice = "关闭后台占内存的程序；适当降低画质；确保虚拟内存充足。"
        },
        new()
        {
            Keywords = new[] { "json value of type", "logjson" },
            Title = "JSON 数据解析异常",
            Level = InsightLevel.Info,
            Meaning = "配置或服务端下发的 JSON 结构与代码预期不符，多数被代码兜住了，不影响游玩。",
            Advice = "一般可忽略；若伴随界面空白或功能失效，再反馈官方。"
        },
        new()
        {
            Keywords = new[] { "isinreview", "launcherreviewinfo" },
            Title = "版本审核状态检查",
            Level = InsightLevel.Noise,
            Meaning = "客户端读取发布渠道的审核标记，属于常规启动检查。",
            Advice = "无需处理。"
        },
        new()
        {
            Keywords = new[] { "pandora", "gameletlogic", "onsdkmmsg", "onsdkmsg" },
            Title = "内置活动组件（潘多拉 SDK）消息",
            Level = InsightLevel.Noise,
            Meaning = "游戏内的活动/网页组件互相通信的日志，正常运行时也会刷。",
            Advice = "无需处理。"
        },
        new()
        {
            Keywords = new[] { "fpplefthandikstatemask", "left hand ik" },
            Title = "第一人称手部姿态切换",
            Level = InsightLevel.Noise,
            Meaning = "切枪/换姿态时的动画状态切换日志，属正常输出。",
            Advice = "无需处理。"
        },
        new()
        {
            Keywords = new[] { "addactionbinding", "func is nil" },
            Title = "界面动作注册失败",
            Level = InsightLevel.Warning,
            Meaning = "某个 UI 动作没注册成功，可能导致按钮点了没反应。",
            Advice = "记下是哪个界面，反馈官方；一般重开界面可恢复。"
        },
        new()
        {
            Keywords = new[] { "assertion failed", "ensure condition failed" },
            Title = "内部断言失败",
            Level = InsightLevel.Warning,
            Meaning = "引擎内部的自检没通过，属于代码级异常，有时会伴随功能失灵。",
            Advice = "记录出现时的操作步骤反馈官方；频繁出现时校验文件。"
        },
        new()
        {
            Keywords = new[] { "packet loss", "lognet", "connection timeout", "network error", "disconnect" },
            Title = "网络异常",
            Level = InsightLevel.Warning,
            Meaning = "丢包、超时或断线。",
            Advice = "换节点/关加速器对比；优先有线；检查网卡节能设置（EEE）。"
        },
        new()
        {
            Keywords = new[] { "nullptr", "null reference", "nullreferenceexception" },
            Title = "空指针异常",
            Level = InsightLevel.Warning,
            Meaning = "代码访问了未初始化的对象，可能造成功能异常或闪退。",
            Advice = "记住触发场景反馈官方；一般玩家侧无法修复。"
        },
        new()
        {
            Keywords = new[] { "shader", "pipeline state", "pso" },
            Title = "着色器 / 管线编译",
            Level = InsightLevel.Info,
            Meaning = "首次进入场景时编译着色器，会造成短暂卡顿，属正常现象。",
            Advice = "无需处理；新地图第一次进入的卡顿通常就是它。"
        },
        new()
        {
            Keywords = new[] { "ondnsasyncresloved", "dnsasync", "turnserver", "idcspeedlogic" },
            Title = "域名解析 / 节点优选记录",
            Level = InsightLevel.Noise,
            Meaning = "游戏在做域名解析和服务器节点测速，属正常网络流程。",
            Advice = "无需处理。"
        },
        new()
        {
            Keywords = new[] { "dns_config_service", "failed to read dnsconfig", "dns 配置" },
            Title = "DNS 配置读取失败",
            Level = InsightLevel.Info,
            Meaning = "读取系统 DNS 配置失败，可能影响登录或商店加载。",
            Advice = "把 DNS 改成 223.5.5.5 / 119.29.29.29 试试。"
        },
        new()
        {
            Keywords = new[] { "wwise", "xaudio2", "xaudio", "audiodevice", "audio device", "failed to initialize audio", "audio mixer" },
            Title = "音频初始化异常",
            Level = InsightLevel.Info,
            Meaning = "音频设备或音频引擎初始化有问题，可能表现为没声音或爆音。",
            Advice = "检查默认播放设备；关闭音频增强；换个耳机/音箱试试。"
        }
    };

    public static List<Finding> Analyze(LogReport report)
    {
        var findings = new List<Finding>();
        var byRule = new Dictionary<int, Finding>();
        var unmatchedCount = 0;
        var unmatchedSamples = new List<string>();

        foreach (var err in report.AllErrors)
        {
            var sig = err.Signature ?? "";
            var hit = -1;

            for (var i = 0; i < Rules.Length; i++)
            {
                if (!Rules[i].Keywords.Any(k => sig.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                hit = i;
                break;
            }

            if (hit < 0)
            {
                unmatchedCount += err.Count;
                if (unmatchedSamples.Count < 3) unmatchedSamples.Add(sig);
                continue;
            }

            if (!byRule.TryGetValue(hit, out var f))
            {
                f = new Finding
                {
                    Title = Rules[hit].Title,
                    Level = Rules[hit].Level,
                    Meaning = Rules[hit].Meaning,
                    Advice = Rules[hit].Advice
                };
                byRule[hit] = f;
                findings.Add(f);
            }

            f.Count += err.Count;
            if (f.Samples.Count < 2) f.Samples.Add(sig);
        }

        if (unmatchedCount > 0)
        {
            var other = new Finding
            {
                Title = "其他未归类报错",
                Level = InsightLevel.Info,
                Count = unmatchedCount,
                IsResidual = true,
                Meaning = "规则库未覆盖的杂项日志。数量大不代表有问题——同类提示会被合并计数，游戏正常运行也会持续输出这类信息。",
                Advice = "先看上面几条；这条只在你想深挖时参考。"
            };
            other.Samples.AddRange(unmatchedSamples);
            findings.Add(other);
        }

        // 崩溃命中但没有任何规则覆盖时，单独提示
        if (report.CrashCount > 0 && findings.All(f => f.Level != InsightLevel.Critical))
        {
            var crash = new Finding
            {
                Title = "检测到崩溃关键字",
                Level = InsightLevel.Critical,
                Count = report.CrashCount,
                Meaning = "日志里出现了崩溃相关字样，但未能归入已知类型。",
                Advice = "带上这份日志与复现步骤联系官方客服。"
            };
            crash.Samples.AddRange(report.Crashes.Take(2).Select(c => c.Content));
            findings.Add(crash);
        }

        return findings
            .OrderBy(f => f.IsResidual ? 1 : 0)
            .ThenBy(f => (int)f.Level)
            .ThenByDescending(f => f.Count)
            .ToList();
    }

    /// <summary>一句话结论，放在报告最上方。</summary>
    public static string Verdict(LogReport report, List<Finding> findings)
    {
        var critical = findings.Where(f => f.Level == InsightLevel.Critical).ToList();
        var warning = findings.Where(f => f.Level == InsightLevel.Warning).ToList();
        var noise = findings.Where(f => f.Level == InsightLevel.Noise).Sum(f => f.Count);

        var sb = new StringBuilder();

        if (critical.Count > 0)
        {
            var top = critical.OrderByDescending(f => f.Count).First();
            sb.Append($"发现 <b>{critical.Count}</b> 类可能导致崩溃或卡顿的问题，最突出的是「<b>{HtmlDoc.E(top.Title)}</b>」，出现 {top.Count:N0} 次。");
        }
        else if (warning.Count > 0)
        {
            sb.Append($"没有发现致命错误，但有 <b>{warning.Count}</b> 类异常值得留意。");
        }
        else
        {
            sb.Append("没有发现影响游戏的问题，运行状态基本正常。");
        }

        if (noise > 0)
            sb.Append($" 另外有 {noise:N0} 条是官方调试输出或埋点限流等<b>无害噪音</b>，可以忽略。");

        if (report.TimeMin is not null)
            sb.Append($" 日志覆盖 {report.TimeMin:MM-dd HH:mm} 至 {(report.TimeMax ?? report.TimeMin).Value:MM-dd HH:mm}。");

        return sb.ToString();
    }

    /// <summary>纯文本摘要，便于复制粘贴给客服或发群里。</summary>
    public static string BuildPlainSummary(LogReport report, List<Finding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"【游戏日志分析】{report.DisplayTitle}");
        sb.AppendLine($"时间：{DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"范围：{report.FileCount} 个文件 / {report.LineTotal:N0} 行 / {report.TotalBytes / 1024.0 / 1024:N1} MB");
        sb.AppendLine();

        var critical = findings.Where(f => f.Level == InsightLevel.Critical).ToList();
        var warning = findings.Where(f => f.Level == InsightLevel.Warning).ToList();
        var noise = findings.Where(f => f.Level == InsightLevel.Noise).Sum(f => f.Count);

        if (critical.Count > 0)
        {
            var top = critical.OrderByDescending(f => f.Count).First();
            sb.AppendLine($"结论：发现 {critical.Count} 类可能导致崩溃/卡顿的问题，最突出的是「{top.Title}」（{top.Count:N0} 次）。");
        }
        else if (warning.Count > 0)
            sb.AppendLine($"结论：没有致命错误，有 {warning.Count} 类异常值得留意。");
        else
            sb.AppendLine("结论：没有发现影响游戏的问题。");

        if (noise > 0) sb.AppendLine($"另有 {noise:N0} 条无害噪音（调试输出 / 埋点限流），可忽略。");
        sb.AppendLine();

        sb.AppendLine(report.SecurityHits.Count == 0
            ? "安全检查：日志中未发现已知外挂 / 调试器 / 注入器痕迹（不代表一定没运行过）。"
            : $"安全检查：发现 {report.SecurityHits.Count} 条线索，需人工确认（不等同于作弊）。");

        if (report.SessionStart is DateTime ss && report.SessionEnd is DateTime se)
        {
            if (report.SessionEvents.Count == 0)
                sb.AppendLine($"同时间段：{ss:MM-dd HH:mm}–{se:HH:mm}，本机没有记录错误或警告事件。");
            else
                sb.AppendLine($"同时间段：{ss:MM-dd HH:mm}–{se:HH:mm}，本机记录 {report.SessionEvents.Count} 类系统事件（" +
                              string.Join("、", report.SessionEvents.Take(3).Select(e => e.Title)) + "）。");
        }
        sb.AppendLine();

        foreach (var f in findings.Where(x => x.Level != InsightLevel.Noise))
        {
            sb.AppendLine($"[{f.LevelText}] {f.Title}（{f.Count:N0} 次）");
            sb.AppendLine($"  含义：{f.Meaning}");
            sb.AppendLine($"  建议：{f.Advice}");
            if (f.Samples.Count > 0) sb.AppendLine($"  示例：{Trim(f.Samples[0], 120)}");
            sb.AppendLine();
        }

        var ignored = findings.Where(x => x.Level == InsightLevel.Noise).ToList();
        if (ignored.Count > 0)
        {
            sb.AppendLine("可忽略：" + string.Join("、", ignored.Select(x => $"{x.Title}（{x.Count:N0} 次）")));
            sb.AppendLine();
        }

        sb.Append("由 破烂优化 生成");
        return sb.ToString();
    }

    private static string Trim(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);
}
