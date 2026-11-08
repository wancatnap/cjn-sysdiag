using System.Diagnostics;
using System.IO;
using System.Text;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 外挂痕迹体检：把分散的只读取证能力串成一条流水线，输出一份统一报告。
/// <para>
/// 设计原则（与 pe-sieve / OpenArk 那类工具的区别）：
/// 1. 全程只读，不加载驱动、不注入、不 dump 内存 —— 单文件绿色特性不能破；
/// 2. 单项只算「提示」，多项组合才升「线索」，误报优先于漏报；
/// 3. 每条结论都带原始依据，报告可复核。
/// </para>
/// </summary>
public static class CheatScanService
{
    /// <summary>提示类最多贡献 12 分，避免“提示多就高风险”。</summary>
    private const int HintCap = 12;

    /// <summary>组合加成：DMA 线索 + 模块/驱动异常同时出现时加的分。</summary>
    private const int ComboBonus = 10;

    public static CheatScanResult Scan(IProgress<string> progress = null, string @operator = "")
    {
        var sw = Stopwatch.StartNew();

        var result = new CheatScanResult
        {
            StartedAt = DateTime.Now,
            Machine = Environment.MachineName,
            Operator = (@operator ?? "").Trim(),
            MachineCode = MachineIdentityService.CurrentCode()
        };

        CollectIdentity(result, progress);
        CollectDrivers(result, progress);
        CollectUsb(result, progress);
        CollectDeepTrace(result, progress);
        CollectExecTrace(result, progress);
        CollectModules(result, progress);
        CollectDeletions(result, progress);
        CollectLogging(result, progress);
        CollectSigma(result, progress);
        CollectExecArtifacts(result, progress);
        CollectGameHandles(result, progress);

        // 深度层：服务/驱动来源、无文件持久化、隐藏进程比对。
        // 放在组合判定之前——组合规则要能看到全部发现。
        var deep = CheatDeepProbeService.Run(progress);
        result.Findings.AddRange(deep.Findings);
        result.Stages.AddRange(deep.Stages);

        AddComboFinding(result);
        Score(result);

        result.Timeline = BuildTimeline(result);
        result.ElapsedMs = sw.ElapsedMilliseconds;
        result.FinishedAt = DateTime.Now;
        result.Machine = $"{Environment.MachineName} · {Environment.UserName}";

        return result;
    }

    // ---------------- 1. 硬件指纹（只做标识，不评分） ----------------

    private static void CollectIdentity(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("读取硬件指纹…");
        var stage = new CheatStage { Name = "硬件指纹" };

        try
        {
            var fields = EvidenceService.CollectHwid();
            stage.Items = fields.Count;
            stage.Note = $"共 {fields.Count} 项，用于标识本次报告";
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 2. 驱动与设备 ----------------

    private static void CollectDrivers(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("读驱动与设备记录…");
        var stage = new CheatStage { Name = "驱动与设备" };

        try
        {
            var since = DateTime.Now.AddDays(-30);
            var events = EvidenceService.ReadDriverHistory(300)
                .Where(e => e.Time.HasValue && e.Time.Value >= since)
                .ToList();

            var installs = events
                .Where(e => e.Action.Contains("安装", StringComparison.Ordinal))
                .Where(e => !LooksMicrosoft(e.Target + " " + e.Caller))
                .ToList();

            stage.Items = events.Count;

            if (installs.Count == 0)
            {
                stage.Note = $"近 30 天 {events.Count} 条设备事件，没有非微软驱动安装";
                r.Findings.Add(new CheatFinding
                {
                    Group = "驱动与设备",
                    Title = "近 30 天没有非微软驱动安装",
                    Detail = "驱动是外挂最常用的落地方式之一（内存读写、键鼠模拟）。这段时间的驱动安装都来自微软或系统自带组件。",
                    Level = "ok",
                    Weight = 0
                });
            }
            else
            {
                stage.Note = $"近 30 天 {installs.Count} 次第三方驱动安装";

                // 装外设驱动是再正常不过的事，这里只清点、不计分——
                // 真正的异常信号是「签名无效/测试签名」，交给深度痕迹与安全项覆盖
                r.Findings.Add(new CheatFinding
                {
                    Group = "驱动与设备",
                    Title = $"近 30 天安装了 {installs.Count} 次第三方驱动",
                    Detail = "显卡、外设、虚拟网卡都会装驱动，属于正常操作，因此不计入分数。要看的是这些设备你认不认识。",
                    Basis = string.Join("；", installs
                        .GroupBy(e => Shorten(e.Target))
                        .Select(g => g.Key)
                        .Take(4)),
                    Level = "ok",
                    Weight = 0
                });
            }

            // 设备启动失败：可能是驱动被拦截或设备被拔
            var failed = events.Count(e => e.Status.Contains("FAIL", StringComparison.OrdinalIgnoreCase));
            if (failed > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "驱动与设备",
                    Title = $"{failed} 次设备安装/启动失败",
                    Detail = "失败可能是驱动签名不合法、被系统策略拦截，也可能只是设备没插好。数量多时值得看一眼。",
                    Level = "hint",
                    Weight = 2
                });
            }
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 3. USB 与输入设备 ----------------

    private static void CollectUsb(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("清点 USB 设备…");
        var stage = new CheatStage { Name = "USB 与输入设备" };

        try
        {
            var list = EvidenceService.ReadUsbDevices();
            stage.Items = list.Count;

            var hits = list
                .Where(d => DeepTraceService.BridgeVids.Any(v =>
                    d.VidPid.Contains("VID_" + v.Vid, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // 输入模拟的判定交给深度痕迹组，这里只做清点，避免同一件事扣两次分
            stage.Note = hits.Count == 0
                ? $"{list.Count} 个 USB 设备，未见常见模拟器芯片"
                : $"{list.Count} 个 USB 设备，其中 {hits.Count} 个使用常见模拟器芯片";

            if (hits.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "USB 与输入设备",
                    Title = $"{hits.Count} 个设备使用模拟器芯片",
                    Detail = "Arduino / CH340 / CP210x 这类芯片常被做成键鼠模拟器（KMBox 一类）。它们也广泛用于单片机开发和串口设备，单独出现不算异常。",
                    Basis = string.Join("；", hits.Take(4).Select(d => $"{d.Name} {d.VidPid}")),
                    Level = "hint",
                    Weight = 3
                });
            }
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 4. 深度硬件痕迹（DMA / 采集卡 / 输入模拟） ----------------

    private static void CollectDeepTrace(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("检查 DMA / 采集卡 / 输入模拟…");
        var stage = new CheatStage { Name = "深度硬件痕迹" };

        try
        {
            var deep = DeepTraceService.Scan(false, p);
            stage.Items = deep.Items.Count;

            foreach (var item in deep.Items.Where(i => i.Level != "ok"))
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = item.GroupText,
                    Title = item.Name,
                    Detail = item.Detail,
                    Basis = item.Basis,
                    Level = item.Level,
                    // 组合线索是这套工具最有价值的信号，权重给足
                    Weight = item.Level == "warn" ? 18 : 1
                });
            }

            if (deep.OkCount > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "深度硬件痕迹",
                    Title = $"其余 {deep.OkCount} 项检查正常",
                    Detail = "三个维度里没有同时命中的特征组合：没有可被 DMA 直接读写的设备、没有正在运行的采集链路、没有输入模拟设备。",
                    Level = "ok",
                    Weight = 0
                });
            }

            stage.Note = $"线索 {deep.WarnCount} / 提示 {deep.HintCount} / 正常 {deep.OkCount}";
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 5. 程序执行记录 ----------------

    private static void CollectExecTrace(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("读程序执行记录…");
        var stage = new CheatStage { Name = "程序执行记录" };

        try
        {
            var scan = ExecTraceService.Scan();

            var bamTemp = scan.Bam.Where(x => LooksTemporary(x.Path)).ToList();
            var amcTemp = scan.Amc.Where(x => LooksTemporary(x.Path)).ToList();

            stage.Items = scan.Bam.Count + scan.Amc.Count;

            if (!ElevationService.IsAdmin)
                stage.State = "limited";

            var samples = bamTemp.Concat(amcTemp)
                .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(6)
                .ToList();

            if (samples.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "程序执行记录",
                    Title = $"{samples.Count} 个程序从临时/下载目录运行过",
                    Detail = "正规软件一般装在 Program Files，从临时目录直接跑的多是解压即用的工具、安装器，也可能是外挂。看着眼熟就可以忽略。",
                    Basis = string.Join("；", samples.Select(s => $"{s.Name}（{s.TimeText}）")),
                    Level = "hint",
                    Weight = 2
                });
            }
            else
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "程序执行记录",
                    Title = "没有程序从临时/下载目录运行过",
                    Detail = "BAM 与 AmCache 里都没有出现临时目录、下载目录下的可执行记录。",
                    Level = "ok",
                    Weight = 0
                });
            }

            stage.Note = $"BAM {scan.Bam.Count} 条 / AmCache {scan.Amc.Count} 条"
                         + (stage.State == "limited" ? "（普通权限只读到一部分）" : "");
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 6. 模块完整性 ----------------

    private static void CollectModules(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("检查进程已加载模块…");
        var stage = new CheatStage { Name = "模块完整性" };

        try
        {
            var tempLoaded = new List<string>();
            var vanished = new List<string>();
            var scanned = 0;
            var denied = 0;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    foreach (ProcessModule m in proc.Modules)
                    {
                        var file = m.FileName;
                        if (string.IsNullOrEmpty(file)) continue;

                        scanned++;

                        if (LooksTemporaryDll(file))
                        {
                            AddUnique(tempLoaded, $"{proc.ProcessName} → {file}");
                            continue;
                        }

                        // 只对非系统目录做存在性检查，否则几万次 IO 拖慢体检
                        if (file.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)) continue;

                        if (!File.Exists(file))
                            AddUnique(vanished, $"{proc.ProcessName} → {file}");
                    }
                }
                catch
                {
                    denied++;
                }
                finally
                {
                    proc.Dispose();
                }
            }

            stage.Items = scanned;

            if (tempLoaded.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "模块完整性",
                    Title = $"{tempLoaded.Count} 个模块从临时目录加载",
                    Detail = "正常软件不会把 DLL 放在临时目录再加载。这一条最接近“注入”的行为特征，但也可能是某些安装器、便携软件的做法。",
                    Basis = string.Join("；", tempLoaded.Take(4)),
                    Level = "warn",
                    Weight = 10
                });
            }

            if (vanished.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "模块完整性",
                    Title = $"{vanished.Count} 个模块的文件已不存在",
                    Detail = "文件被删掉了但模块还挂在进程里，常见于“先加载再删文件”的隐藏手法。也可能是软件升级时替换了同名文件。",
                    Basis = string.Join("；", vanished.Take(4)),
                    Level = "warn",
                    Weight = 14
                });
            }

            if (tempLoaded.Count == 0 && vanished.Count == 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "模块完整性",
                    Title = $"扫描 {scanned:N0} 个已加载模块，未发现异常",
                    Detail = "没有模块来自临时目录，也没有出现“文件已删除但仍在内存”的情况。",
                    Level = "ok",
                    Weight = 0
                });
            }

            stage.Note = scanned == 0
                ? "读不到进程模块列表"
                : $"{scanned:N0} 个模块，{denied} 个进程无权限";

            if (scanned == 0) stage.State = "limited";
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 7. 文件删除痕迹（USN） ----------------

    private static void CollectDeletions(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("读文件删除记录（USN）…");
        var stage = new CheatStage { Name = "文件删除痕迹" };

        try
        {
            var since = DateTime.Now.AddDays(-7);
            var usn = UsnService.Read("C", since, 3000);

            if (usn.NeedAdmin)
            {
                stage.State = "limited";
                stage.Note = "需要管理员权限才能读 USN 日志";
                r.Stages.Add(stage);
                return;
            }

            if (!usn.Ok)
            {
                stage.State = "failed";
                stage.Note = usn.Error.Length > 0 ? usn.Error : "读取失败";
                r.Stages.Add(stage);
                return;
            }

            var deletes = usn.Records
                .Where(x => x.Reason.Contains("delete", StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            || x.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                .ToList();

            stage.Items = usn.Records.Count;

            var suspicious = deletes.Where(x => LooksTemporary(x.Path) || LooksCheatName(x.Name)).ToList();

            if (suspicious.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "文件删除痕迹",
                    Title = $"近 7 天删除了 {suspicious.Count} 个可执行文件/模块",
                    Detail = "用完就删是外挂的常见收尾动作。安装器清理临时文件也会留下类似记录，所以只算提示。",
                    Basis = string.Join("；", suspicious.Take(4).Select(x => $"{x.Name}（{x.TimeText}）")),
                    Level = "hint",
                    Weight = 3
                });
            }
            else
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "文件删除痕迹",
                    Title = $"近 7 天 {deletes.Count} 条可执行文件删除记录，位置正常",
                    Detail = "删除都发生在常规目录（更新缓存、安装临时文件），没有出现“临时目录里的 exe 用完即删”这种模式。",
                    Level = "ok",
                    Weight = 0
                });
            }

            stage.Note = $"{usn.Records.Count:N0} 条记录，其中删除 {deletes.Count} 条";

            // 时间线复用这次读取结果，避免再扫一遍 USN
            foreach (var x in deletes.Take(40))
            {
                r.Timeline.Add(new CheatEvent
                {
                    Time = x.Time,
                    Source = "删除",
                    Text = x.Name,
                    Level = LooksTemporary(x.Path) || LooksCheatName(x.Name) ? "hint" : "ok"
                });
            }
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 8. 审计日志状态 ----------------

    private static void CollectLogging(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("检查审计日志开关…");
        var stage = new CheatStage { Name = "审计日志" };

        try
        {
            var states = LoggingService.ReadState();

            // 「最近使用记录」默认就是关的（隐私默认值），拿它当异常会把所有人都标成可疑
            var watched = new[] { LoggingService.KeySystem, LoggingService.KeyApp, LoggingService.KeyPrefetch };
            var important = states.Where(s => watched.Contains(s.Key)).ToList();

            var off = important.Where(s => !s.Enabled).ToList();

            stage.Items = important.Count;

            if (off.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "审计日志",
                    Title = $"{off.Count} 项系统记录处于关闭状态",
                    Detail = "关掉记录能减少痕迹，但正常优化系统、精简版系统也会关。要看这些开关是不是你亲手关的。",
                    Basis = string.Join("；", off.Take(4).Select(s => s.Name)),
                    Level = "hint",
                    Weight = 2
                });
            }
            else
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "审计日志",
                    Title = "系统记录保持开启",
                    Detail = "程序执行、设备安装、审计策略几项都处于开启状态，后续还有据可查。",
                    Level = "ok",
                    Weight = 0
                });
            }

            stage.Note = $"{important.Count - off.Count}/{important.Count} 项开启（不含隐私默认关闭的「最近使用」）";
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 9. 日志规则匹配（Sigma 子集） ----------------

    private static void CollectSigma(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("按 Sigma 规则匹配事件日志…");
        var stage = new CheatStage { Name = "日志规则匹配" };

        try
        {
            var sigma = SigmaRuleEngine.Scan(p);
            stage.Items = sigma.Hits.Count;

            // 反作弊组件（ACE / EAC / BattlEye…）也会触发「注册内核驱动」这类规则，
            // 但它们恰恰是环境可信的正面信号，单独成组、不计分。
            var antiCheat = sigma.Hits.Where(h => h.IsKnownGood).ToList();
            var scored = sigma.Hits.Where(h => !h.IsKnownGood).ToList();

            var high = scored.Where(h => h.Rule.Level is "critical" or "high").ToList();
            var medium = scored.Where(h => h.Rule.Level == "medium").ToList();
            var low = scored.Where(h => h.Rule.Level is "low" or "informational").ToList();

            if (antiCheat.Count > 0)
            {
                var names = antiCheat.Select(h => h.Tag).Distinct().ToList();

                r.Findings.Add(new CheatFinding
                {
                    Group = "反作弊组件",
                    Title = $"检测到 {string.Join("、", names)}",
                    Detail = "反作弊驱动会主动拦截跨进程内存读写和输入模拟，是环境可信的正面信号。" +
                             "它们同样会触发「服务注册内核驱动」这类规则，属于正常，不计入分数。",
                    Basis = $"{antiCheat.Count} 条相关事件 · 最近 {antiCheat.First().TimeText}",
                    Level = "ok",
                    Weight = 0
                });
            }

            foreach (var g in high.GroupBy(h => h.Rule.Title).Take(4))
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "日志规则匹配",
                    Title = g.Key,
                    Detail = g.First().Rule.Description,
                    Basis = $"{g.Count()} 次 · 最近 {g.First().TimeText} · {g.First().Why}",
                    Level = "warn",
                    Weight = 15
                });
            }

            foreach (var g in medium.GroupBy(h => h.Rule.Title).Take(4))
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "日志规则匹配",
                    Title = g.Key,
                    Detail = g.First().Rule.Description,
                    Basis = $"{g.Count()} 次 · 最近 {g.First().TimeText} · {g.First().Why}",
                    Level = "hint",
                    Weight = 3
                });
            }

            if (low.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "日志规则匹配",
                    Title = $"{low.Count} 条低等级规则命中",
                    Detail = "服务安装、设备识别这类记录本身不说明问题，只作时间线参考。",
                    Basis = string.Join("；", low.GroupBy(h => h.Rule.Title).Select(g => g.Key).Take(4)),
                    Level = "ok",
                    Weight = 0
                });
            }

            if (sigma.Hits.Count == 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "日志规则匹配",
                    Title = $"已用 {sigma.Rules.Count} 条规则扫过 {sigma.EventsScanned:N0} 条事件，无命中",
                    Detail = "事件日志里没有出现规则覆盖的行为。注意：相关日志没开启时，这里同样会是空的。",
                    Level = "ok",
                    Weight = 0
                });
            }

            // 规则命中并入统一时间线（反作弊组件不计入，避免刷屏）
            foreach (var h in scored.Take(20))
            {
                r.Timeline.Add(new CheatEvent
                {
                    Time = h.Event?.Time,
                    Source = "规则",
                    Text = h.Rule.Title + (h.Why.Length > 0 ? "　" + h.Why : ""),
                    Level = h.Rule.Level is "critical" or "high" ? "warn" : "hint"
                });
            }

            stage.Note = $"{sigma.Rules.Count} 条规则 / {sigma.EventsScanned:N0} 条事件 / 命中 {sigma.Hits.Count}";

            if (sigma.EventsScanned == 0 && sigma.Channels.All(c => c.State != "done"))
                stage.State = "limited";
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 10. 本地执行痕迹（Prefetch / ShimCache） ----------------

    private static void CollectExecArtifacts(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("读取 Prefetch 与 ShimCache…");
        var stage = new CheatStage { Name = "本地执行痕迹" };

        try
        {
            var art = PrefetchService.Scan(p, 200);
            stage.Items = art.Prefetch.Count + art.ShimCache.Count;

            var tempPrefetch = art.Prefetch.Where(x => x.IsSuspiciousPath).ToList();
            var tempShim = art.ShimCache.Where(x => x.IsSuspiciousPath).Take(50).ToList();

            if (tempPrefetch.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "本地执行痕迹",
                    Title = $"{tempPrefetch.Count} 个程序从临时/下载目录运行过",
                    Detail = "Prefetch 记录了运行次数，是「确实跑过」的硬证据。临时目录里的程序多是解压即用的工具或安装器，也可能是外挂。",
                    Basis = string.Join("；", tempPrefetch.Take(4).Select(x => $"{x.Name}（{x.RunCountText} 次）")),
                    Level = "hint",
                    Weight = 3
                });
            }

            if (tempShim.Count > 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "本地执行痕迹",
                    Title = $"{tempShim.Count} 个临时目录文件出现在 ShimCache",
                    Detail = "ShimCache 只说明文件在系统里出现过，不说明运行过——Win8 之后它不再记录执行标志。",
                    Basis = string.Join("；", tempShim.Take(3).Select(x => x.Path)),
                    Level = "hint",
                    Weight = 1
                });
            }

            if (tempPrefetch.Count == 0 && tempShim.Count == 0)
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "本地执行痕迹",
                    Title = art.HasData
                        ? "Prefetch 与 ShimCache 里没有临时目录程序"
                        : "没有读到执行痕迹数据",
                    Detail = art.HasData
                        ? "运行记录都来自常规安装路径。"
                        : "Prefetch 需要管理员权限，SysMain 被关闭时目录也会是空的。",
                    Level = "ok",
                    Weight = 0
                });
            }

            stage.Note = $"Prefetch {art.Prefetch.Count} / ShimCache {art.ShimCache.Count}";

            if (art.PrefetchState != "done" || art.ShimCacheState != "done")
            {
                stage.State = "limited";
                stage.Note += "　·　" + (art.PrefetchState != "done" ? art.PrefetchNote : art.ShimCacheNote);
            }
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 11. 游戏进程句柄（谁在读游戏） ----------------

    /// <summary>
    /// 句柄维度：检测到正在运行的游戏时，扫一遍「谁打开了它、拿的什么权限」。
    /// 没游戏就明确标「未检查」——不当作没问题。
    /// </summary>
    private static void CollectGameHandles(CheatScanResult r, IProgress<string> p)
    {
        p?.Report("检查游戏进程句柄…");
        var stage = new CheatStage { Name = "游戏进程句柄" };

        try
        {
            var game = GameProcessCatalog.Best();

            if (game == null)
            {
                stage.State = "skipped";
                stage.Note = "当前没有正在运行的游戏进程";
                r.Stages.Add(stage);
                return;
            }

            stage.Note = $"目标：{game.Name}（{game.Pid}）";
            var hs = HandleScanService.Scan(game.Pid, game.Name);
            stage.Items = hs.Findings.Count;
            stage.State = hs.Precise ? "done" : "limited";

            foreach (var f in hs.Findings.Where(x => x.Risk == "warn" && !x.Known).Take(6))
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "游戏进程句柄",
                    Title = $"{f.Name} 可写入 {game.Name} 的内存",
                    Detail = $"句柄权限：{f.FlagsText}。可写内存 / 可建线程是注入类外挂的必要条件。",
                    Basis = $"{f.Header} · 全表 {hs.TotalHandles:N0} 条句柄",
                    Level = "warn",
                    Weight = 12
                });

                r.Timeline.Add(new CheatEvent
                {
                    Time = DateTime.Now,
                    Source = "句柄",
                    Text = $"{f.Name} → {game.Name}：{f.FlagsText}",
                    Level = "warn"
                });
            }

            foreach (var f in hs.Findings.Where(x => x.Risk == "hint" && !x.Known).Take(3))
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "游戏进程句柄",
                    Title = $"{f.Name} 可读取 {game.Name} 的内存",
                    Detail = "只读权限很常见（监控、调试、任务管理器级工具都会要），结合进程名判断。",
                    Basis = f.Header,
                    Level = "hint",
                    Weight = 1
                });
            }

            var known = hs.Findings.Count(x => x.Known);
            if (known > 0 && !r.Findings.Any(f => f.Group == "反作弊组件"))
            {
                r.Findings.Add(new CheatFinding
                {
                    Group = "反作弊组件",
                    Title = $"持有游戏句柄的已知组件 {known} 个",
                    Detail = "系统组件与反作弊打开游戏进程是正常工作，不计分。",
                    Basis = string.Join("、", hs.Findings.Where(x => x.Known).Select(x => x.Name).Take(4)),
                    Level = "ok",
                    Weight = 0
                });
            }
        }
        catch (Exception ex)
        {
            stage.State = "failed";
            stage.Note = ex.Message;
        }

        r.Stages.Add(stage);
    }

    // ---------------- 组合判定与评分 ----------------

    /// <summary>DMA/采集链路出现线索，同时模块完整性也异常，才升为一条独立线索。</summary>
    private static void AddComboFinding(CheatScanResult r)
    {
        var dmaWarn = r.Findings.Any(f => f.Group.Contains("DMA", StringComparison.Ordinal) && f.Level == "warn");
        var moduleWarn = r.Findings.Any(f => f.Group == "模块完整性" && f.Level == "warn");

        if (!dmaWarn || !moduleWarn) return;

        r.Findings.Insert(0, new CheatFinding
        {
            Group = "组合判定",
            Title = "硬件通路与软件痕迹同时命中",
            Detail = "DMA/采集链路出现了可疑特征，同时游戏侧还有异常模块。单独看每一条都能解释，凑在一起解释成本就高了——建议人工核对。",
            Basis = "深度硬件痕迹 + 模块完整性",
            Level = "warn",
            Weight = ComboBonus
        });
    }

    private static void Score(CheatScanResult r)
    {
        r.Score = ComputeScore(r.Findings);
    }

    /// <summary>
    /// 风险分：线索全额累加，提示类合计封顶 12 分，总分封顶 100。
    /// 这样「提示很多但都是正常配置」不会把分数推成高风险。
    /// </summary>
    internal static int ComputeScore(IEnumerable<CheatFinding> findings)
    {
        var list = findings as IList<CheatFinding> ?? findings.ToList();

        var warnSum = list.Where(f => f.Level == "warn").Sum(f => f.Weight);
        var hintSum = Math.Min(HintCap, list.Where(f => f.Level == "hint").Sum(f => f.Weight));

        return Math.Clamp(warnSum + hintSum, 0, 100);
    }

    // ---------------- 时间线 ----------------

    private static List<CheatEvent> BuildTimeline(CheatScanResult r)
    {
        // 删除事件在 CollectDeletions 里已经读进来了
        var list = new List<CheatEvent>(r.Timeline);

        try
        {
            foreach (var e in EvidenceService.ReadDriverHistory(60))
            {
                if (!e.Time.HasValue) continue;

                list.Add(new CheatEvent
                {
                    Time = e.Time,
                    Source = "设备",
                    Text = $"{e.Action} {Shorten(e.Target)}",
                    Level = e.Status.Contains("FAIL", StringComparison.OrdinalIgnoreCase) ? "hint" : "ok"
                });
            }
        }
        catch { }

        return list
            .Where(x => x.Time.HasValue)
            .OrderByDescending(x => x.Time)
            .Take(60)
            .ToList();
    }

    // ---------------- 报告 ----------------

    public static ReportPayload BuildReport(CheatScanResult r) => BuildReport(r, "");

    /// <summary>targetPath 非空时写到用户选的位置；图片由调用方放在同目录同名。</summary>
    public static ReportPayload BuildReport(CheatScanResult r, string targetPath)
    {
        var doc = new HtmlDoc("外挂痕迹体检",
            $"{r.OperatorText}　·　{r.MachineCodeText}　·　{r.FinishedAt:yyyy-MM-dd HH:mm:ss}");

        doc.Kpis(
            (r.Score.ToString(), $"风险分（{r.ScoreLevel}，仅供参考）", r.ScoreColor),
            (r.WarnCount.ToString(), "线索", "#FB7185"),
            (r.HintCount.ToString(), "提示", "#FBBF24"),
            (r.OkCount.ToString(), "正常", "#34D399"));

        doc.H2("报告信息");
        doc.Table(
            new[] { "字段", "值" },
            new[]
            {
                new[] { "填写者", $"<b>{Clean(r.OperatorText)}</b>" },
                new[] { "机器特征码", $"<span class=\"mono\">{Clean(r.MachineCodeText)}</span>" },
                new[] { "机器 / 用户", Clean(r.Machine) },
                new[] { "开始时间", $"<span class=\"mono\">{r.StartedAt:yyyy-MM-dd HH:mm:ss}</span>" },
                new[] { "生成时间", $"<span class=\"mono\">{r.FinishedAt:yyyy-MM-dd HH:mm:ss}</span>" },
                new[] { "耗时", $"{r.ElapsedText}" }
            },
            new[] { "120", "" });

        doc.H2("结论");
        doc.Raw($"<div class=\"note\"><b>{Clean(r.Verdict)}</b><br/>{Clean(r.VerdictDetail)}</div>");

        // ---------------- 检查清单（四层证据链） ----------------
        var checklist = CheatChecklistBuilder.Build(r);

        doc.H2("检查清单：查一个人有没有开挂，要过这 " + checklist.Items.Count + " 项");
        doc.Note("按证据链分层：<b>硬件</b>（有没有作弊硬件）→ <b>系统</b>（系统有没有被改）→ " +
                 "<b>运行</b>（现在在做什么）→ <b>历史</b>（做过什么）。" +
                 "<b>「未检查」不等于通过</b>——数据源读不到时会明确标出来。");

        doc.Raw($"<div class=\"note\"><b>{Clean(checklist.SummaryText)}</b></div>");

        doc.Table(
            new[] { "层级", "检查项", "结论", "证据" },
            checklist.Items.Select(c => new[]
            {
                Clean(c.Layer),
                $"<b>{Clean(c.Name)}</b><br/><span class=\"muted\">{Clean(c.Why)}</span>",
                $"<span class=\"tag {(c.Result == "fail" ? "t-warn" : c.Result == "watch" ? "t-hint" : c.Result == "skip" ? "t-bin" : "t-ok")}\">{c.ResultText}</span>",
                Clean(c.Evidence)
            }),
            new[] { "58", "40%", "64", "" });

        doc.H2("采集覆盖");

        foreach (var group in r.Findings.GroupBy(f => f.Group))
        {
            var items = group.OrderByDescending(f => f.Level == "warn")
                .ThenByDescending(f => f.Level == "hint")
                .ToList();

            doc.H2($"{group.Key}（{items.Count} 项）");

            doc.Table(
                new[] { "级别", "结论", "依据" },
                items.Select(f => new[]
                {
                    $"<span class=\"tag {(f.Level == "warn" ? "t-warn" : f.Level == "hint" ? "t-hint" : "t-ok")}\">{f.LevelText}</span>" +
                    (f.Weight > 0 ? $" <span class=\"muted\">+{f.Weight}</span>" : ""),
                    $"<b>{Clean(f.Title)}</b>" + (f.HasDetail ? $"<br/><span class=\"muted\">{Clean(f.Detail)}</span>" : ""),
                    f.HasBasis ? $"<span class=\"mono\">{Clean(f.Basis)}</span>" : "<span class=\"muted\">—</span>"
                }),
                new[] { "86", "44%", "" });
        }

        doc.H2("采集覆盖");
        doc.Table(
            new[] { "采集项", "状态", "条数", "说明" },
            r.Stages.Select(s => new[]
            {
                Clean(s.Name),
                $"<span class=\"tag {(s.State == "done" ? "t-ok" : s.State == "limited" ? "t-hint" : "t-warn")}\">{s.StateText}</span>",
                s.Items.ToString(),
                Clean(s.Note)
            }),
            new[] { "150", "78", "66", "" });

        if (r.Timeline.Count > 0)
        {
            doc.H2($"时间线（最近 {r.Timeline.Count} 条）");
            doc.Table(
                new[] { "时间", "来源", "事件" },
                r.Timeline.Select(e => new[]
                {
                    $"<span class=\"mono\">{e.TimeText}</span>",
                    HtmlDoc.E(e.Source),
                    $"<span class=\"{(e.Level == "hint" ? "t-hint" : "")}\">{Clean(e.Text)}</span>"
                }),
                new[] { "130", "70", "" });
        }

        var html = doc.Build();
        var path = HtmlReportBuilder.Save(html, "外挂痕迹体检", targetPath);

        var summary = new StringBuilder();
        summary.AppendLine("外挂痕迹体检");
        summary.AppendLine($"填写者：{r.OperatorText}");
        summary.AppendLine($"特征码：{r.MachineCodeText}");
        summary.AppendLine($"机器：{r.Machine}");
        summary.AppendLine($"生成时间：{r.FinishedAt:yyyy-MM-dd HH:mm:ss}　耗时 {r.ElapsedText}");
        summary.AppendLine($"风险分：{r.Score}/100（{r.ScoreLevel}，仅供参考）");
        summary.AppendLine($"线索 {r.WarnCount} · 提示 {r.HintCount} · 正常 {r.OkCount}");
        summary.AppendLine(r.Verdict);
        summary.AppendLine();

        foreach (var f in r.Findings.Where(f => f.Level != "ok"))
        {
            summary.AppendLine($"[{f.LevelText}] {f.Group} / {f.Title}");
            if (f.HasBasis) summary.AppendLine("        " + f.Basis);
        }

        var data = new ReportData
        {
            Kind = "cheat-scan",
            Title = "外挂痕迹体检",
            Subtitle = r.Verdict,
            GeneratedAt = r.StartedAt.ToString("yyyy-MM-ddTHH:mm:sszzz"),
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
                Score = r.Score,
                Level = r.ScoreLevel,
                Text = r.Verdict,
                Counts = new ReportCounts
                {
                    Warn = r.WarnCount,
                    Hint = r.HintCount,
                    Ok = r.OkCount
                }
            },
            Sections = r.Findings
                .GroupBy(f => f.Group)
                .Select(g => new ReportSection
                {
                    Group = g.Key,
                    Items = g.Select(f => new ReportItem
                    {
                        Level = f.Level,
                        Title = f.Title,
                        Detail = f.Detail,
                        Basis = f.Basis,
                        Weight = f.Weight
                    }).ToList()
                })
                .ToList(),
            Stages = r.Stages.Select(s => new ReportStageInfo
            {
                Name = s.Name,
                State = s.State,
                Items = s.Items,
                Note = s.Note
            }).ToList(),
            Timeline = r.Timeline.Select(e => new ReportEventInfo
            {
                Time = e.Time?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                Source = e.Source,
                Text = e.Text,
                Level = e.Level
            }).ToList()
        };

        return new ReportPayload
        {
            Title = "外挂痕迹体检",
            Subtitle = $"{r.Verdict}　·　风险分 {r.Score}（{r.ScoreLevel}）　·　{r.ElapsedText}",
            EngineTag = "只读采集",
            HtmlPath = path,
            PlainSummary = summary.ToString(),
            Data = data
        };
    }

    // ---------------- 辅助 ----------------

    /// <summary>数据源里可能已经带 HTML 实体（如 &amp;quot;），先解再转义，避免二次转义。</summary>
    private static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return HtmlDoc.E(System.Net.WebUtility.HtmlDecode(text));
    }

    private static void AddUnique(List<string> list, string value)
    {
        if (list.Count >= 20) return;
        if (list.Contains(value, StringComparer.OrdinalIgnoreCase)) return;

        list.Add(value);
    }

    private static bool LooksMicrosoft(string text)
        => text.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Windows", StringComparison.OrdinalIgnoreCase)
           || text.Contains("INTEL", StringComparison.OrdinalIgnoreCase)
           || text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
           || text.Contains("AMD", StringComparison.OrdinalIgnoreCase)
           || text.Contains("Realtek", StringComparison.OrdinalIgnoreCase);

    private static bool LooksTemporary(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        return path.Contains(@"\Temp\", StringComparison.OrdinalIgnoreCase)
               || path.Contains(@"\Windows\Temp", StringComparison.OrdinalIgnoreCase)
               || path.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase)
               || path.Contains(@"\$Recycle.Bin\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 只把「真正的 DLL 从临时目录加载」当作注入特征。
    /// Node 原生模块（.tmp.node）、.NET 单文件解压（Temp\.net）、Inno/NSIS 解压产物、
    /// Roslyn 编译服务器的分析器目录（Temp\VBCSCompiler）都出现在 Temp 里且完全正常——
    /// 自检时 WorkBuddy、SteelSeries、dotnet build 分别撞在这三条上。
    /// </summary>
    private static bool LooksTemporaryDll(string file)
    {
        if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return false;
        if (!LooksTemporary(file)) return false;

        if (file.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\.net\", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\is-", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\MSI", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\Roslyn\", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\VBCSCompiler\", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\.dotnet\", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\NuGetScratch\", StringComparison.OrdinalIgnoreCase)) return false;
        if (file.Contains(@"\Temp\MSBuildTemp\", StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }

    private static bool LooksCheatName(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;

        string[] keys = { "cheat", "hack", "aim", "esp", "inject", "kmbox", "宏", "外挂", "辅助" };
        return keys.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length > 70 ? text[..70] + "…" : text;
    }
}
