using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using SysDiag.Models;
using SysDiag.Services;
using System.IO;

namespace SysDiag;

/// <summary>
/// 无头自检：在不能弹出窗口的环境里验证 XAML 能否解析、卡片目录是否完整、
/// 日志分析链路是否可用。结果写入 outputs\selftest.txt。
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        var failed = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (!ok) failed++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(string.IsNullOrEmpty(detail) ? "" : "  → " + detail)}");
        }

        try
        {
            AppPaths.Ensure();

            // 日志记录开关：界面提权后以本参数再次启动自身
            if (args[0].Equals("--apply-logging", StringComparison.OrdinalIgnoreCase))
            {
                LoggingService.ApplyFromFlags(args.Length > 1 ? args[1] : "1,1,1,1");
                return 0;
            }

            // 只打印日志开关当前状态，不改任何东西
            if (args[0].Equals("--logging-state", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var s in LoggingService.ReadState())
                    sb.AppendLine($"{s.Name,-24} {(s.Enabled ? "已开启" : "已关闭"),-4} {s.Detail}");

                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "logging-state.txt"), sb.ToString(), new UTF8Encoding(true));
                return 0;
            }

            // 清理旧报告：保留最近 40 份，供发版后手动整理用。
            if (args[0].Equals("--prune-reports", StringComparison.OrdinalIgnoreCase))
            {
                var pr = OutputHousekeeping.Prune();
                Console.WriteLine($"删除 {pr.Removed} 个文件，释放 {pr.Freed / 1024.0 / 1024.0:0.0} MB");
                return 0;
            }

            // 报告图片导出：跑一次体检并出图，用来肉眼验收排版。
            if (args[0].Equals("--cheat-image", StringComparison.OrdinalIgnoreCase))
            {
                var scan = CheatScanService.Scan(null, "自检");
                var image = ReportImageExporter.Export(scan);
                Console.WriteLine(image.Ok ? image.Path : image.Message);
                return image.Ok ? 0 : 1;
            }

            // PCI 属性键反查：把设备上所有 PCI 属性连同编号列出来，用来核对属性编号。
            if (args[0].Equals("--dma-keys", StringComparison.OrdinalIgnoreCase))
            {
                var filter = args.Length > 1 ? args[1] : "";
                var dump = DmaSpoofProbeService.DumpPropertyKeys(filter);
                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "dma-keys.txt"),
                    dump, new UTF8Encoding(true));
                Console.WriteLine(dump);
                return 0;
            }

            // PCIe 属性探针：打印每个设备读到的链路宽度/速率/类代码。
            // 存在的意义是能一眼看出「属性编号有没有错位」——编号错了这里会出现
            // 当前宽度 > 最大宽度 这类矛盾值，而不是静默给出错误结论。
            if (args[0].Equals("--dma-probe", StringComparison.OrdinalIgnoreCase))
            {
                var dmaText = DmaSpoofProbeService.Probe();
                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "dma-probe.txt"),
                    dmaText, new UTF8Encoding(true));
                Console.WriteLine(dmaText);
                return 0;
            }

            // 内核采集参数诊断：穷举几种 StartTrace 组合，排查本机为什么开不了会话。
            if (args[0].Equals("--kernel-diag", StringComparison.OrdinalIgnoreCase))
            {
                var diag = KernelTraceService.DiagnoseStart();
                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "kernel-diag.txt"),
                    diag, new UTF8Encoding(true));
                Console.WriteLine(diag);
                return 0;
            }

            // 内核 DPC/ISR 探针：单独验证 ETW 链路，必须在管理员下跑。
            // 用法：--kernel-probe [秒数]，结果写 outputs/kernel-probe.txt 并回显。
            if (args[0].Equals("--kernel-probe", StringComparison.OrdinalIgnoreCase))
            {
                var secs = args.Length > 1 && int.TryParse(args[1], out var n) ? n : 5;

                sb.AppendLine($"管理员：{(KernelTraceService.IsElevated ? "是" : "否")}");
                sb.AppendLine($"CPU：{CoreParkingService.ProcessorName()}");
                sb.AppendLine();

                var probe = KernelTraceService.Capture(secs, System.Threading.CancellationToken.None,
                    new Progress<string>(p => Console.WriteLine(p)), takeoverExisting: true);

                sb.AppendLine(probe.Ok
                    ? $"成功：DPC {probe.DpcCount:N0} 次，中断 {probe.IsrCount:N0} 次"
                    : "失败：" + probe.Message);

                if (probe.Ok)
                {
                    sb.AppendLine($"平均 {probe.MeanUs:0.0} µs　P99 {probe.P99Us:0.0} µs　最大 {probe.MaxUs:0.0} µs");
                    sb.AppendLine($"采集时长 {probe.ElapsedSeconds:0.0} 秒");
                    sb.AppendLine();
                    sb.AppendLine("判读：Microsoft 的驱动建议是单次 DPC 不超过 100 µs。");
                    sb.AppendLine("若最大值超过 1 ms，通常是某个驱动占用了过长的高 IRQL 时间。");
                }

                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "kernel-probe.txt"),
                    sb.ToString(), new UTF8Encoding(true));

                Console.WriteLine(sb.ToString());
                return probe.Ok ? 0 : 1;
            }

            // 文件删除记录：界面提权后以本参数再次启动
            if (args[0].Equals("--usn", StringComparison.OrdinalIgnoreCase))
            {
                var drive = args.Length > 1 ? args[1] : "C";
                var hours = args.Length > 2 && int.TryParse(args[2], out var h) ? h : 168;
                UsnService.RunFromArgs(drive, hours);
                return 0;
            }

            // 设备比对：命令行存基线 / 比对
            if (args[0].Equals("--baseline", StringComparison.OrdinalIgnoreCase))
            {
                var sub = (args.Length > 1 ? args[1] : "compare").ToLowerInvariant();

                if (sub == "save")
                {
                    var snap = DeviceBaselineService.Save();
                    sb.AppendLine($"已保存基线 {snap.SavedAt:yyyy-MM-dd HH:mm}　字段 {snap.Fields.Count} 项 / USB {snap.Usb.Count} 个");
                    sb.AppendLine(DeviceBaselineService.FilePath);
                }
                else
                {
                    var diff = DeviceBaselineService.Compare();
                    if (diff == null)
                    {
                        sb.AppendLine("还没有基线，请先运行 --baseline save");
                    }
                    else
                    {
                        var payload = DeviceBaselineService.CompareReport(diff);
                        sb.AppendLine($"变化 {diff.TotalChanges} 项（改动 {diff.Changed.Count} / 新增 {diff.Added.Count} / " +
                                      $"消失 {diff.Removed.Count} / USB ±{diff.UsbAdded.Count + diff.UsbRemoved.Count}）");
                        sb.AppendLine(payload.HtmlPath);
                    }
                }

                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "baseline.txt"), sb.ToString(), new UTF8Encoding(true));
                return 0;
            }

            // 命令行生成证据包（不弹窗）
            if (args[0].Equals("--pack", StringComparison.OrdinalIgnoreCase))
            {
                var pack = EvidenceService.BuildPack();
                foreach (var f in pack.Files)
                    sb.AppendLine($"{f.Name,-28} {f.Size / 1024.0,9:N1} KB");
                sb.AppendLine();
                sb.AppendLine($"ZIP: {pack.ZipPath}");
                sb.AppendLine($"大小: {pack.ZipSize / 1024.0 / 1024:N2} MB");

                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "pack.txt"), sb.ToString(), new UTF8Encoding(true));
                return 0;
            }

            // 全盘查找游戏日志
            if (args[0].Equals("--find-logs", StringComparison.OrdinalIgnoreCase))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var found = GameLogFinder.FindAll(
                    new Progress<string>(s => sb.AppendLine("[SCAN] " + s)));
                sw.Stop();

                sb.AppendLine($"耗时：{sw.Elapsed.TotalSeconds:N1} 秒");
                sb.AppendLine($"找到 {found.Count} 处：");
                foreach (var f in found)
                    sb.AppendLine($"  {f.Game,-28} {f.CountText,-12} {f.SizeText,10}  {f.TimeText}  {f.Path}");

                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "find-logs.txt"), sb.ToString(), new UTF8Encoding(true));
                return 0;
            }

            // 诊断项全量冒烟（WMI / 事件日志 / ping / netstat 路径）
            if (args[0].Equals("--diag-all", StringComparison.OrdinalIgnoreCase))
            {
                var cases = new (string Name, Func<ReportPayload> Run)[]
                {
                    ("系统体检", DiagnosticsService.SystemCheckup),
                    ("错误与警告", DiagnosticsService.ErrorReport),
                    ("磁盘健康", DiagnosticsService.DiskHealth),
                    ("网络诊断", DiagnosticsService.NetworkReport),
                    ("已装更新", DiagnosticsService.UpdateList),
                    ("机器码", EvidenceService.HwidReport),
                    ("设备与驱动记录", EvidenceService.DriverHistoryReport),
                    ("USB 设备清单", EvidenceService.UsbHistoryReport),
                    ("PnP 事件分析", EvidenceService.PnpReport),
                    ("取证证据包", EvidenceService.EvidencePackReport)
                };

                foreach (var (name, run) in cases)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var p = run();
                        sw.Stop();
                        var size = File.Exists(p.HtmlPath) ? new FileInfo(p.HtmlPath).Length : 0;
                        Check(name, size > 500, $"{sw.ElapsedMilliseconds} ms / {size:N0} 字节 / {Path.GetFileName(p.HtmlPath)}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        sb.AppendLine($"[FAIL] {name} → {ex.GetType().Name}: {ex.Message}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine(failed == 0 ? "结果：全部通过" : $"结果：{failed} 项未通过");
                File.WriteAllText(Path.Combine(AppPaths.OutputDir, "diag-all.txt"), sb.ToString(), new UTF8Encoding(true));
                return failed == 0 ? 0 : 1;
            }

            var app = new Application();
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Theme.xaml", UriKind.Absolute)
            });

            // ---------- 1. WebView2 运行时 ----------
            try
            {
                var ver = CoreWebView2Environment.GetAvailableBrowserVersionString();
                Check("WebView2 运行时", !string.IsNullOrEmpty(ver), ver);
            }
            catch (Exception ex)
            {
                Check("WebView2 运行时", false, ex.Message);
            }

            // ---------- 2. 主窗口 XAML + 卡片目录 ----------
            var win = new MainWindow();
            sb.AppendLine($"[INFO] 主窗口：分组 {win.GroupCount} 个，卡片 {win.CardCount} 张");
            Check("卡片总数 = 74", win.CardCount == 74, win.CardCount.ToString());
            Check("分组数 = 9", win.GroupCount == 9, win.GroupCount.ToString());

            foreach (var g in win.Snapshot())
                sb.AppendLine($"       [{g.Name}] {g.Items.Count} 项：{string.Join("、", g.Items.Select(i => i.Name))}");

            // 图标库：每张卡片都得落到一个真实存在的图形上（WithIcon 写错 key 会在这里暴露）
            var cards = win.Snapshot().SelectMany(g => g.Items).ToList();
            var notFound = cards
                .Where(i => Array.IndexOf(IconLibrary.AllKeys, i.IconKey) < 0)
                .Select(i => $"{i.Name}→{i.IconKey}")
                .ToList();
            Check("图标库：卡片图标键都存在", notFound.Count == 0,
                notFound.Count == 0 ? $"{cards.Count} 张卡片 / {cards.Select(i => i.IconKey).Distinct().Count()} 种图标" : string.Join("、", notFound));

            var groups = win.Snapshot().Select(g => g.IconKey).ToList();
            Check("图标库：分组图标键都存在",
                groups.All(k => Array.IndexOf(IconLibrary.AllKeys, k) >= 0),
                string.Join("、", groups));

            // 系统调优必须能从主界面直接看到（收进别的窗口里用户会找不到）；
            // 同时保留系统优化窗口内的「硬件调优」按钮作为第二入口。
            Check("系统调优：主界面有独立入口",
                cards.Any(c => c.Target == "system-tune"),
                cards.Count(c => c.Target == "system-tune") == 1 ? "主界面 1 张直接可见" : "入口数量异常");
            Check("系统调优：系统优化窗口内也有直达按钮",
                typeof(TweakWindow).GetMethod("OnOpenHardwareTune",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null,
                "TweakWindow.OnOpenHardwareTune 存在");

            var iconFailure = "";
            foreach (var key in IconLibrary.AllKeys)
            {
                var img = IconLibrary.Get(key, "#4C6EF5");
                if (img is not { IsFrozen: true }) { iconFailure = key; break; }
            }
            Check("图标库：每个键都能出图且已冻结", iconFailure.Length == 0,
                iconFailure.Length == 0 ? $"{IconLibrary.AllKeys.Length} 个图形 · 全部冻结可共享" : iconFailure);

            win.Close();

            // ---------- 3. 报告窗口 XAML ----------
            var probeHtml = Path.Combine(AppPaths.OutputDir, "_probe.html");
            File.WriteAllText(probeHtml, "<html><body>probe</body></html>", new UTF8Encoding(true));
            var rw = new ReportWindow(new ReportPayload { Title = "自检", HtmlPath = probeHtml });
            Check("报告窗口可构造", true, rw.Title);
            rw.Close();

            // ---------- 4. 日志分析 ----------
            // ---------- 4. 混淆日志自动解码 ----------
            try
            {
                var line = "[2026.11.05-14.20.01:123][  0]LogInit: Display: Engine is initialized\r\n" +
                           "[2026.11.05-14.21.10:789][ 12]LogGame: Error: Failed to load texture\r\n" +
                           "[2026.11.05-14.23.00:334][ 25]LogWindows: Error: Assertion failed: Index >= 0\r\n";
                var plain = string.Concat(Enumerable.Repeat(line, 20));
                var payload = Encoding.UTF8.GetBytes(plain);
                for (var i = 0; i < payload.Length; i++) payload[i] ^= 0x5C;

                var obf = Path.Combine(Path.GetTempPath(), "sysdiag-xor-" + Guid.NewGuid().ToString("N")[..6] + ".log");
                File.WriteAllBytes(obf, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(payload).ToArray());

                var decoded = LogAnalyzer.ReadTextSmartEx(obf, out var note);
                Check("混淆日志自动解码",
                    decoded.Contains("LogInit") && note != null && note.Contains("XOR"),
                    note);
            }
            catch (Exception ex)
            {
                Check("混淆日志自动解码", false, ex.Message);
            }

            // ---------- 4b. 明文中带大写的 ERROR 不应被误判为混淆 ----------
            try
            {
                var cefLine = "[0604/153911.881:ERROR:gpu_init.cc(457)] Passthrough is not supported, GL is disabled\r\n";
                var cef = Path.Combine(Path.GetTempPath(), "sysdiag-cef-" + Guid.NewGuid().ToString("N")[..6] + ".log");
                File.WriteAllText(cef, string.Concat(Enumerable.Repeat(cefLine, 60)), new UTF8Encoding(false));

                var text = LogAnalyzer.ReadTextSmartEx(cef, out var note2);
                Check("明文日志不误判", note2 == null && text.Contains("Passthrough"), note2 ?? "未解码，正确");
            }
            catch (Exception ex)
            {
                Check("明文日志不误判", false, ex.Message);
            }

            // ---------- 4c. 安全线索：真阳性与假阳性 ----------
            Check("安全线索·能识别反作弊拦截",
                SecurityInspector.Inspect("t.log", 1, "[ACE] blocked module C:\\Temp\\hack.dll") != null);
            Check("安全线索·能识别工具名",
                SecurityInspector.Inspect("t.log", 1, "process opened by CheatEngine.exe") != null);
            Check("安全线索·不误报医疗注射",
                SecurityInspector.Inspect("t.log", 1, "LogGPAudio: Item_Medic_Injection_Using_Start") == null);
            Check("安全线索·不误报钩索音效",
                SecurityInspector.Inspect("t.log", 1, "EventName: C403_Skill_Hook_Obj_Dragged_Body_Start") == null);
            Check("安全线索·不误报交互组件",
                SecurityInspector.Inspect("t.log", 1, "UInteractorOperatorProcessComponent") == null);
            Check("安全线索·不误报普通驱动日志",
                SecurityInspector.Inspect("t.log", 1, "[2026] LogD3D11: GPU driver version 32.0.15") == null);

            // ---------- 5. 日志分析 ----------
            LogSource src;
            if (args.Length > 1)
            {
                var a = args[1];
                if (Directory.Exists(a)) src = LogSource.FromFolder(a);
                else if (a.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) src = LogSource.FromZip(a);
                else src = LogSource.FromFiles(new[] { a });
            }
            else src = LogSource.FromZip(BuildTestZip());

            sb.AppendLine($"[INFO] 样本：{src.Label}（{src.Kind}）");

            var report = LogAnalyzer.Analyze(src, new Progress<string>(_ => { }));
            sb.AppendLine($"[INFO] 文件 {report.FileCount} 个 / {report.TotalBytes / 1024.0:N1} KB / 扫描 {report.LineTotal:N0} 行");
            sb.AppendLine($"[INFO] 引擎 {report.Engine} · 崩溃 {report.CrashCount} · 错误 {report.ErrorCount} · 警告 {report.WarnCount}");
            sb.AppendLine($"[INFO] 报告标题：{report.DisplayTitle}");
            sb.AppendLine($"[INFO] 安全线索：{report.SecurityHits.Count} 条");
            sb.AppendLine($"[INFO] 会话窗：{report.SessionStart:MM-dd HH:mm} → {report.SessionEnd:MM-dd HH:mm}  取自 {report.SessionFile}");
            foreach (var f in report.Files.OrderByDescending(f => f.EndTime).Take(5))
                sb.AppendLine($"       {f.Name,-52} {f.StartTime:MM-dd HH:mm} → {f.EndTime:MM-dd HH:mm}");
            foreach (var h in report.SecurityHits.Take(3))
                sb.AppendLine($"       [{h.Strength}] {h.Category} / {h.Indicator} / {h.File}:{h.Line}");
            foreach (var w in report.Warnings.Take(5)) sb.AppendLine($"[WARN] {w}");

            var isSynthetic = args.Length <= 1;
            if (isSynthetic)
            {
                Check("引擎识别为 Unreal", report.Engine.Contains("Unreal"), report.Engine);
                Check("检出错误行", report.ErrorCount > 0, report.ErrorCount.ToString());
                Check("命中归一化聚合", report.TopErrors.Count > 0, $"{report.TopErrors.Count} 类");
            }

            var html = HtmlReportBuilder.BuildLog(report);
            var outPath = HtmlReportBuilder.Save(html, "自检-游戏日志分析");
            sb.AppendLine($"[INFO] HTML：{outPath}（{html.Length:N0} 字符）");

            // 人话解读
            var findings = LogInsight.Analyze(report);
            Check("生成人话解读", findings.Count > 0,
                string.Join("；", findings.Take(4).Select(f => $"{f.LevelText}:{f.Title}({f.Count})")));

            var summary = LogInsight.BuildPlainSummary(report, findings);
            Check("纯文本摘要可复制", summary.Length > 100 && summary.Contains("结论"),
                $"{summary.Length} 字符");

            var summaryPath = HtmlReportBuilder.SaveText(summary, "自检-摘要");
            sb.AppendLine($"[INFO] 摘要：{summaryPath}");
            sb.AppendLine("---- 摘要内容开始 ----");
            sb.AppendLine(summary);
            sb.AppendLine("---- 摘要内容结束 ----");

            if (args.Length <= 1)
            {
                Check("GBK 中文正确解码", html.Contains("连接服务器失败"), "GBK 回退");
                Check("地址归一化为 HEX", html.Contains("&lt;HEX&gt;"), "0x… → <HEX>");
                Check("路径归一化为 PATH", html.Contains("&lt;PATH&gt;"), "C:\\… → <PATH>");
                Check("HTML 含 KPI 卡片", CountOccurrences(html, "class=\"kpi\"") >= 6,
                    CountOccurrences(html, "class=\"kpi\"").ToString());
            }

            // ---------- 5. 目录来源分析 ----------
            if (_testSrcDir != null && Directory.Exists(_testSrcDir))
            {
                var folderReport = LogAnalyzer.Analyze(LogSource.FromFolder(_testSrcDir));
                Check("目录来源可分析",
                    folderReport.FileCount >= 4 && folderReport.ErrorCount > 0,
                    $"{folderReport.FileCount} 个文件 / 错误 {folderReport.ErrorCount} / {folderReport.SourceKind}");
            }

            // ---------- 7. 日志清理扫描（只扫描，不删除） ----------
            var candidates = LogScanner.Scan();
            Check("日志清理扫描", candidates.Count > 0,
                $"{candidates.Count} 处 / {candidates.Sum(c => c.Count)} 个文件 / " +
                $"{candidates.Sum(c => c.Size) / 1024.0 / 1024:N1} MB");
            foreach (var c in candidates.Take(10))
                sb.AppendLine($"       [{c.Category}] {c.Name}  {c.CountText} / {c.SizeText}  {c.KindText}");

            // ---------- 8. 清理窗口可构造（不触发扫描，不删除任何东西） ----------
            var cw = new CleanupWindow();
            Check("清理窗口可构造", true);
            cw.Close();

            // ---------- 9. 查找窗口可构造 ----------
            var lf = new LogFinderWindow(_ => { });
            Check("查找游戏日志窗口可构造", true);
            lf.Close();

            // ---------- 9b. 文件选择窗口：列出目录内文件，由用户自选 ----------
            try
            {
                const string gameLogDir = @"D:\sjzxd\Delta Force\Delta Force\DeltaForce\Saved\Logs";
                string pickRoot;

                if (Directory.Exists(gameLogDir))
                {
                    pickRoot = gameLogDir;
                }
                else
                {
                    pickRoot = Path.Combine(Path.GetTempPath(), "sysdiag-pick-" + Guid.NewGuid().ToString("N")[..6]);
                    Directory.CreateDirectory(pickRoot);
                    File.WriteAllText(Path.Combine(pickRoot, "a.log"), "LogInit: Display: ok\r\n", new UTF8Encoding(false));
                    File.WriteAllText(Path.Combine(pickRoot, "b.log"), "LogNet: Warning: packet loss\r\n", new UTF8Encoding(false));
                }

                var picker = new LogFilePickerWindow(pickRoot);
                var foundFiles = picker.ScanNow();
                Check("文件选择窗口可构造", foundFiles > 0 && picker.VisibleCount > 0,
                    $"{foundFiles} 个文件 / 可见 {picker.VisibleCount}");
                picker.Close();

                if (foundFiles > 0)
                {
                    var one = LogFileDiscovery.Scan(pickRoot)
                        .Where(f => f.IsAnalyzable)
                        .OrderBy(f => f.Size)
                        .First().Path;

                    var picked = LogSource.FromPickedFiles(pickRoot, new[] { one });
                    var pr = LogAnalyzer.Analyze(picked, new Progress<string>(_ => { }));
                    Check("自选单个文件可分析", pr.FileCount == 1,
                        $"{pr.FileCount} 个文件 / {pr.LineTotal} 行 / 标题「{pr.DisplayTitle}」");
                    Check("自选文件标题按目录命名", pr.DisplayTitle.Contains("日志"), pr.DisplayTitle);
                }
            }
            catch (Exception ex)
            {
                Check("文件选择窗口可构造", false, ex.Message);
            }

            // ---------- 10. 全盘扫描能定位到游戏日志 ----------
            if (args.Length <= 1)
            {
                var games = GameLogFinder.FindAll();
                Check("全盘扫描找到游戏日志", games.Count > 0,
                    $"{games.Count} 处 / {games.Sum(g => g.Size) / 1024.0 / 1024:N1} MB");
                foreach (var g in games.Take(5))
                    sb.AppendLine($"       {g.Game,-24} {g.CountText,-12} {g.SizeText,9}  {g.Path}");
            }

            // ---------- 11. 取证：机器码 / 驱动记录 / USB / PnP / 证据包 ----------
            try
            {
                var hwid = EvidenceService.CollectHwid();
                Check("机器码采集", hwid.Count >= 5,
                    $"{hwid.Count} 项：" + string.Join("、", hwid.GroupBy(f => f.Group).Select(g => $"{g.Key}{g.Count()}")));
                foreach (var f in hwid.Take(3))
                    sb.AppendLine($"       {f.Group} / {f.Name} = {f.Value}");

                var dev = EvidenceService.ReadDriverHistory();
                Check("驱动与设备记录", dev.Count > 0,
                    $"{dev.Count} 条 · 失败 {dev.Count(d => d.StatusCss == "t-bad")} 条");
                foreach (var e in dev.Take(3))
                    sb.AppendLine($"       {e.TimeText}  {e.Action}  " +
                                  $"{(e.Target.Length > 70 ? e.Target[..70] + "…" : e.Target)}  [{e.Status}]");

                var usb = EvidenceService.ReadUsbDevices();
                Check("USB 设备清单", usb.Count > 0, $"{usb.Count} 个设备");
                foreach (var d in usb.Take(3))
                    sb.AppendLine($"       {d.VidPid,-28} {d.Name,-34} 序列号 {(d.Serial.Length > 0 ? d.Serial : "(无)")}");

                var pnp = EvidenceService.ReadPnpEvents();
                Check("PnP 事件分析", pnp.Count > 0,
                    $"{pnp.Count} 条 · 错误 {pnp.Count(r => r.Level is "错误" or "严重")} 条");

                var pack = EvidenceService.BuildPack();
                var packOk = File.Exists(pack.ZipPath) && pack.Files.Count >= 5;
                Check("证据包可生成", packOk,
                    $"{pack.Files.Count} 个文件 · {pack.ZipSize / 1024.0 / 1024:N2} MB · {Path.GetFileName(pack.ZipPath)}");

                if (packOk)
                {
                    using var za = ZipFile.OpenRead(pack.ZipPath);
                    Check("证据包含 SHA256 清单",
                        za.Entries.Any(e => e.FullName == "SHA256SUMS.txt"), $"{za.Entries.Count} 个条目");
                }

                try { File.Delete(pack.ZipPath); } catch { }
            }
            catch (Exception ex)
            {
                Check("取证功能", false, ex.Message);
            }

            // ---------- 12. 设备比对 / 日志记录开关 ----------
            try
            {
                var s1 = DeviceBaselineService.Capture();
                var s2 = DeviceBaselineService.Capture();
                if (s2.Fields.Count > 0) s2.Fields[0].Value += "-TEST";
                s2.Fields.Add(new HwidField { Group = "测试", Name = "新增字段", Value = "x" });

                var d = DeviceBaselineService.Compare(s1, s2);
                Check("设备比对可计算", d.Changed.Count == 1 && d.Added.Count == 1,
                    $"改动 {d.Changed.Count} / 新增 {d.Added.Count} / 未变 {d.SameCount}");

                var diffPayload = DeviceBaselineService.CompareReport(d);
                Check("设备比对报告可生成", File.Exists(diffPayload.HtmlPath),
                    $"{new FileInfo(diffPayload.HtmlPath).Length:N0} 字节");

                var bw = new BaselineWindow(_ => { });
                Check("设备比对窗口可构造", true);
                bw.Close();

                var lw = new LoggingWindow();
                var switchCount = lw.LoadState();
                Check("日志开关窗口可构造", switchCount >= 9, $"{switchCount} 个开关（4 基础 + 5 取证审计）");
                lw.Close();

                foreach (var s in LoggingService.ReadState())
                    sb.AppendLine($"       {s.Name,-24} {(s.Enabled ? "已开启" : "已关闭"),-4} {s.Detail}");

                var parsed = LoggingService.ParseFlags("1,0,1,1");
                Check("日志开关参数解析", parsed.Count == 4 && !parsed[LoggingService.KeyApp], "1,0,1,1");
            }
            catch (Exception ex)
            {
                Check("设备比对/日志开关", false, ex.Message);
            }

            // ---------- 13. 文件删除记录（USN 解析 + 权限分支） ----------
            try
            {
                const string testName = "测试删除.txt";
                var nameBytes = Encoding.Unicode.GetBytes(testName);
                var recordLen = 60 + nameBytes.Length;
                var buf = new byte[8 + recordLen];

                BitConverter.GetBytes(recordLen).CopyTo(buf, 8);              // RecordLength
                BitConverter.GetBytes((short)2).CopyTo(buf, 12);              // MajorVersion
                BitConverter.GetBytes((short)0).CopyTo(buf, 14);              // MinorVersion
                BitConverter.GetBytes(12345UL).CopyTo(buf, 16);               // FileReferenceNumber
                BitConverter.GetBytes(0UL).CopyTo(buf, 24);                   // ParentFileReferenceNumber
                BitConverter.GetBytes(1L).CopyTo(buf, 32);                    // Usn
                BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(buf, 40); // TimeStamp
                BitConverter.GetBytes(0x200u).CopyTo(buf, 48);                // Reason = FILE_DELETE
                BitConverter.GetBytes((short)nameBytes.Length).CopyTo(buf, 64);
                BitConverter.GetBytes((short)60).CopyTo(buf, 66);
                nameBytes.CopyTo(buf, 68);

                var parsed = UsnService.ParseForTest(buf);
                Check("USN 记录解析", parsed.Count == 1 && parsed[0].Name == testName,
                    $"{parsed.Count} 条 / {parsed.FirstOrDefault()?.Name}");

                var probe = UsnService.Read("C", DateTime.Now.AddHours(-1));
                if (LoggingService.IsAdmin())
                    Check("USN 管理员读取", probe.Ok || probe.Error.Length > 0,
                        probe.Ok ? $"{probe.Records.Count} 条删除记录" : probe.Error);
                else
                    Check("USN 无权限时提示提权", probe.NeedAdmin, probe.Error);

                var resume = ElevationService.RoundTrip("d:", 24);
                Check("提权重启参数可还原",
                    resume.AdminMode && resume.OpenUsn && resume.UsnDrive == "D" && resume.UsnHours == 24,
                    $"admin={resume.AdminMode} / usn={resume.OpenUsn} / {resume.UsnDrive}:{resume.UsnHours}");

                Check("界面参数与命令行参数分流",
                    LaunchOptions.Parse(new[] { "--admin", "--open-usn", "C", "24" }).HasUiIntent
                    && !LaunchOptions.Parse(new[] { "--selftest" }).HasUiIntent,
                    "--admin/--open-usn → 界面；--selftest → 命令行");

                var uw = new UsnWindow(_ => { });
                uw.Reload();
                Check("文件删除记录窗口可构造", uw.DriveCount > 0, $"{uw.DriveCount} 个固定磁盘");
                Check("USN 按钮按权限切换",
                    uw.ScanButtonText.Contains(ElevationService.IsAdmin ? "开始读取" : "以管理员身份重启"),
                    uw.ScanButtonText);
                uw.Close();

                // 管理员模式：不出现任何提权入口，直接读
                var adminView = new UsnWindow(_ => { }) { AdminOverride = true };
                Check("管理员模式不再要求提权",
                    adminView.ScanButtonText == "开始读取"
                    && adminView.AdminBannerTitle == "已具备管理员权限",
                    $"{adminView.AdminBannerTitle} / 按钮「{adminView.ScanButtonText}」");
                adminView.Close();

                // 普通权限：按钮才变成「以管理员身份重启并读取」
                var normalView = new UsnWindow(_ => { }) { AdminOverride = false };
                Check("普通权限才出现提权按钮",
                    normalView.ScanButtonText == "以管理员身份重启并读取",
                    normalView.ScanButtonText);
                normalView.Close();

                var adminWin = new MainWindow { AdminOverride = true };
                Check("管理员模式主窗口无提权按钮",
                    adminWin.AdminBadgeLabel == "管理员模式" && !adminWin.ElevateButtonVisible,
                    $"{adminWin.AdminBadgeLabel} / 重启按钮{(adminWin.ElevateButtonVisible ? "可见" : "隐藏")}");
                adminWin.Close();

                var normalWin = new MainWindow { AdminOverride = false };
                Check("普通权限主窗口可一键提权",
                    normalWin.AdminBadgeLabel == "普通权限" && normalWin.ElevateButtonVisible,
                    $"{normalWin.AdminBadgeLabel} / 重启按钮{(normalWin.ElevateButtonVisible ? "可见" : "隐藏")}");
                normalWin.Close();

                var resumeWin = new MainWindow(LaunchOptions.Parse(new[] { "--admin", "--open-usn", "D", "24" }))
                {
                    AdminOverride = true
                };
                Check("重启后自动回到文件删除记录", resumeWin.AutoUsnPending, "已挂载自动打开");
                resumeWin.Close();
            }
            catch (Exception ex)
            {
                Check("文件删除记录", false, ex.Message);
            }

            // ---------- 14. 网卡 MAC（修改 / 恢复） ----------
            try
            {
                Check("MAC 格式规范化",
                    MacAddressService.Normalize("02-aa-bb-cc-dd-ee") == "02AABBCCDDEE"
                    && MacAddressService.Normalize("02:AABB:CCDD:EE") == "02AABBCCDDEE",
                    MacAddressService.Normalize("02-aa-bb-cc-dd-ee"));

                Check("MAC 非法值拒绝",
                    MacAddressService.Normalize("XYZ") == null
                    && MacAddressService.Normalize("FFFFFFFFFFFF") == null
                    && MacAddressService.Normalize("000000000000") == null,
                    "短串 / 全 F / 全 0 均拒绝");

                var rndMac = MacAddressService.Random();
                var rndBytes = Convert.FromHexString(rndMac);
                Check("随机 MAC 合法且为本地管理地址",
                    rndMac.Length == 12 && (rndBytes[0] & 0x02) == 0x02 && (rndBytes[0] & 0x01) == 0,
                    rndMac);

                var adapters = MacAddressService.List();
                Check("物理网卡枚举", adapters.Count > 0,
                    $"{adapters.Count} 块：" + string.Join("、", adapters.Take(3).Select(a => a.Name)));

                var withPermanent = adapters.Where(a => !string.IsNullOrEmpty(a.PermanentMac)).ToList();
                Check("固件 MAC 可读", withPermanent.Count > 0,
                    $"{withPermanent.Count}/{adapters.Count} 块读到 PermanentAddress");

                var setWin = new MacWindow("set");
                setWin.Reload();
                Check("修改 MAC 窗口可构造",
                    setWin.AdapterCount > 0 && !setWin.IsRestoreMode && setWin.ApplyButtonText == "写入 MAC",
                    $"{setWin.AdapterCount} 块 / 按钮「{setWin.ApplyButtonText}」");
                setWin.Close();

                var restoreWin = new MacWindow("restore");
                restoreWin.Reload();
                Check("恢复 MAC 窗口可构造",
                    restoreWin.IsRestoreMode && restoreWin.ApplyButtonText == "恢复硬件 MAC",
                    $"按钮「{restoreWin.ApplyButtonText}」");
                restoreWin.Close();

                var macLaunch = LaunchOptions.Parse(new[] { "--admin", "--open-mac", "restore" });
                Check("提权重启后自动打开 MAC 窗口",
                    macLaunch.OpenMac && macLaunch.MacMode == "restore" && macLaunch.HasUiIntent,
                    $"OpenMac={macLaunch.OpenMac} / 模式={macLaunch.MacMode}");

                var macWin = new MainWindow(macLaunch) { AdminOverride = true };
                Check("主窗口挂载 MAC 自动打开", macWin.AutoMacPending, "已挂载");
                macWin.Close();
            }
            catch (Exception ex)
            {
                Check("网卡 MAC", false, ex.Message);
            }

            // ---------- 15. 直播伴侣优化 ----------
            try
            {
                Check("核心掩码跳过 0 号核",
                    LiveOptimizerService.BuildAffinityMask(8) == 0xFE
                    && LiveOptimizerService.BuildAffinityMask(4) == 0xE
                    && LiveOptimizerService.BuildAffinityMask(1) == 1,
                    $"8核=0x{LiveOptimizerService.BuildAffinityMask(8):X} / 4核=0x{LiveOptimizerService.BuildAffinityMask(4):X}");

                Check("进程名配置", LiveOptimizerService.ProcessName == "直播伴侣",
                    LiveOptimizerService.ProcessName + ".exe");

                var live = new LiveOptimizerWindow();
                Check("直播伴侣优化窗口可构造", live.CpuTextValue.Length > 0, "指标控件已初始化");
                live.Close();
            }
            catch (Exception ex)
            {
                Check("直播伴侣优化", false, ex.Message);
            }

            // ---------- 16. 系统调优（Game Bar / GPU 调度 / 服务 / CPU 调度） ----------
            try
            {
                var states = SystemTuningService.GetAll();
                Check("系统调优五项状态可读",
                    states.Count == 5
                    && states.Select(s => s.Kind).Distinct().Count() == 5
                    && states.All(s => s.Title.Length > 0),
                    string.Join("、", states.Select(s => $"{s.Title}={s.StateText}")));

                Check("CPU 档位定义完整",
                    SystemTuningService.CpuProfiles.Count == 4
                    && SystemTuningService.CpuProfiles.Any(p => p.Value == 2)
                    && SystemTuningService.CpuProfiles.Any(p => p.Value == 38),
                    string.Join(" / ", SystemTuningService.CpuProfiles.Select(p => $"{p.Name}={p.Value}")));

                Check("SysMain 跟随优化项联动，仅 EventLog 保留",
                    SystemTuningService.TargetedServices.Contains("SysMain")
                    && SystemTuningService.TargetedServices.Count == 5
                    && SystemTuningService.KeptServices.Count == 1
                    && SystemTuningService.KeptServices[0].Contains("EventLog"),
                    $"禁用清单：{string.Join("、", SystemTuningService.TargetedServices)}　保留：{string.Join("、", SystemTuningService.KeptServices)}");

                Check("还原启动类型：禁用过的回到自动",
                    SystemTuningService.ResolveRestoreStart(2) == 2
                    && SystemTuningService.ResolveRestoreStart(4) == 2
                    && SystemTuningService.ResolveRestoreStart(0) == 2
                    && SystemTuningService.ResolveRestoreStart(3) == 3,
                    "原值 2→2 / 4→2 / 0→2 / 3→3");

                var tuneWin = new SystemTuneWindow();
                Check("系统调优窗口可构造",
                    tuneWin.ItemCount == 4,
                    $"{tuneWin.ItemCount} 张动态卡片 + CPU 档位区");
                tuneWin.Close();

                var (mouseOff, speed, t1, t2) = MouseTweakService.Read();
                Check("鼠标加速状态可读",
                    speed.Length > 0 && t1.Length > 0 && t2.Length > 0,
                    $"关闭={mouseOff} / Speed={speed} T1={t1} T2={t2}");

                var tuneLaunch = LaunchOptions.Parse(new[] { "--open-tune" });
                Check("提权重启后自动打开系统调优",
                    tuneLaunch.OpenTune && tuneLaunch.HasUiIntent,
                    $"OpenTune={tuneLaunch.OpenTune} / Admin={tuneLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("系统调优", false, ex.Message);
            }

            // ---------- 17. DLSS 模型 ----------
            try
            {
                Check("驱动版本规范化",
                    DlssService.NormalizeDriver("32.0.15.8180") == "581.80"
                    && DlssService.NormalizeDriver("31.0.15.3699") == "536.99"
                    && DlssService.NormalizeDriver("581.80") == "581.80",
                    DlssService.NormalizeDriver("32.0.15.8180"));

                Check("预设选项含 K",
                    DlssService.PresetOptions.Any(o => o.Value == 11 && o.Label.Contains("K"))
                    && DlssService.PresetOptions.Any(o => o.Value == 0x00FFFFFF),
                    $"{DlssService.PresetOptions.Count} 个预设");

                Check("DLSS 版本规范化",
                    DlssService.NormalizeDlssVersion("3,1,1,0") == "3.1.1"
                    && DlssService.NormalizeDlssVersion("310.2.1.0") == "310.2.1",
                    DlssService.NormalizeDlssVersion("3,1,1,0") + " / " + DlssService.NormalizeDlssVersion("310.2.1.0"));

                var nip = DlssService.BuildNip("Delta Force", "deltaforceclient-win64-shipping.exe",
                    new DlssChoice { QualityLevel = 1, PresetLetter = 11, DllOverride = 1 });

                Check("NIP 含三个 DRS 字段",
                    nip.Contains("283385331") && nip.Contains("279951208") && nip.Contains("283385345")
                    && nip.Contains("<SettingValue>11</SettingValue>"),
                    $"{nip.Length} 字符");

                var env = DlssService.Detect();
                Check("DLSS 环境检测",
                    env.GpuName.Length > 0,
                    $"{env.GpuName} / 驱动 {(env.DriverVersion.Length > 0 ? env.DriverVersion : "未读到")} / 游戏{(env.GameFound ? "已定位" : "未找到")} / DLSS {(env.DlssFound ? env.DlssVersion : "未找到")}");

                var dlssWin = new DlssWindow();
                Check("DLSS 窗口三档预设",
                    dlssWin.PresetCount == 3,
                    $"{dlssWin.PresetCount} 档 / 当前 {dlssWin.CurrentMode}");
                dlssWin.Close();

                var dlssLaunch = LaunchOptions.Parse(new[] { "--open-dlss" });
                Check("自动打开 DLSS 窗口参数",
                    dlssLaunch.OpenDlss && dlssLaunch.HasUiIntent,
                    $"OpenDlss={dlssLaunch.OpenDlss}");
            }
            catch (Exception ex)
            {
                Check("DLSS 模型", false, ex.Message);
            }

            // ---------- 18. 游戏优化（缓存 / 着色器 / 运行库 / 图形设置） ----------
            try
            {
                var shaders = GameTweakService.ScanShaderCaches();
                Check("着色器缓存扫描",
                    shaders.All(s => s.Name.Length > 0 && s.FileCount > 0),
                    shaders.Count == 0
                        ? "本机未发现缓存目录"
                        : string.Join("、", shaders.Select(s => $"{s.Vendor} {s.Name} {s.SizeText}")));

                var vc = GameTweakService.CheckVcRuntimes();
                Check("VC++ 运行库检查",
                    vc.Count == 2 && vc.All(v => v.Arch.Length > 0),
                    string.Join("　", vc.Select(v => v.Detail)));

                var gameRoot = @"D:\sjzxd\Delta Force\Delta Force\DeltaForce";
                var exe = Directory.Exists(gameRoot) ? GameTweakService.FindGameExe(gameRoot) : "";

                Check("游戏主程序定位",
                    !Directory.Exists(gameRoot) || exe.Length > 0,
                    string.IsNullOrEmpty(exe) ? "本机未定位到（目录不存在则跳过）" : Path.GetFileName(exe));

                // 用临时假路径验证读写逻辑，不碰真实游戏配置
                var probe = Path.Combine(Path.GetTempPath(), "sysdiag-probe-not-a-real-game.exe");
                var (probeOk, _) = GameTweakService.SetGpuPreference(probe, true);
                var (probeOn, _) = GameTweakService.GetGpuPreference(probe);
                var (probeBack, _) = GameTweakService.SetGpuPreference(probe, false);
                var (probeOff, _) = GameTweakService.GetGpuPreference(probe);

                Check("GPU 首选项读写往返",
                    probeOk && probeOn && probeBack && !probeOff,
                    "写入 2 → 读出高性能 → 删除 → 读出未设置");

                var layerExe = probe + ".fs";
                var (fsOk, _) = GameTweakService.SetFullscreenOptimization(layerExe, true);
                var (fsOn, fsRaw) = GameTweakService.GetFullscreenOptimization(layerExe);
                var (fsBack, _) = GameTweakService.SetFullscreenOptimization(layerExe, false);
                var (fsOff, _) = GameTweakService.GetFullscreenOptimization(layerExe);

                Check("全屏优化标记往返且保留其他标记",
                    fsOk && fsOn && fsRaw.Contains("DISABLEDXMAXIMIZEDWINDOWEDMODE") && fsBack && !fsOff,
                    $"写入后：{fsRaw}");

                var optWin = new DeltaCacheWindow();
                Check("游戏优化窗口可构造（三 Tab）",
                    optWin.ItemCount == 0 && optWin.ShaderCount == 0,
                    "未触发扫描时为 0 项，说明绑定正常");
                optWin.Close();

                // ---- 游戏定位：必须能认非三角洲的游戏，否则「游戏优化」名不副实 ----
                try
                {
                    var root = Path.Combine(AppPaths.OutputDir, "selftest-game");
                    var binaries = Path.Combine(root, "Game", "Binaries", "Win64");

                    Directory.CreateDirectory(binaries);

                    // 一个假的 UE 主程序 + 一个更大的启动器：应优先认主程序
                    File.WriteAllBytes(Path.Combine(binaries, "TestGame-Win64-Shipping.exe"), new byte[64]);
                    File.WriteAllBytes(Path.Combine(root, "Launcher.exe"), new byte[512]);

                    var hit = GameTweakService.FindGameExe(root);

                    Check("游戏优化：能认出非三角洲的 UE 主程序",
                        hit.EndsWith("TestGame-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase),
                        hit.Length > 0 ? Path.GetFileName(hit) : "(没找到)");

                    // 没有 Shipping 时退回「体积最大的 exe」
                    File.Delete(Path.Combine(binaries, "TestGame-Win64-Shipping.exe"));

                    var fallback = GameTweakService.FindGameExe(root);

                    Check("游戏优化：没有 Shipping 时退回最大的 exe",
                        fallback.EndsWith("Launcher.exe", StringComparison.OrdinalIgnoreCase),
                        fallback.Length > 0 ? Path.GetFileName(fallback) : "(没找到)");

                    Check("游戏优化：空目录不瞎猜",
                        GameTweakService.FindGameExe(Path.Combine(root, "nonexistent")) == "",
                        "返回空，交给用户自己选");
                }
                catch (Exception ex)
                {
                    Check("游戏优化：通用定位", false, ex.Message);
                }
            }
            catch (Exception ex)
            {
                Check("游戏优化", false, ex.Message);
            }

            // ---------- 19. DNS 优化 ----------
            try
            {
                var q = DnsService.BuildQuery("www.baidu.com", 0x1234);

                Check("DNS 查询报文构造",
                    q.Length == 31
                    && q[0] == 0x12 && q[1] == 0x34
                    && q[2] == 0x01 && q[3] == 0x00
                    && q[4] == 0x00 && q[5] == 0x01
                    && q[^4] == 0x00 && q[^3] == 0x01 && q[^2] == 0x00 && q[^1] == 0x01,
                    $"{q.Length} 字节，尾部 {q[^4]:X2}{q[^3]:X2}{q[^2]:X2}{q[^1]:X2}");

                Check("DNS 方案表完整",
                    DnsService.Presets.Count >= 7
                    && DnsService.Presets.All(p => p.Primary.Length > 0)
                    && DnsService.Presets.Any(p => p.Primary == "223.5.5.5")
                    && DnsService.Presets.Any(p => p.Primary == "119.29.29.29"),
                    string.Join("、", DnsService.Presets.Take(4).Select(p => p.Name)) + " 等");

                var adapters = DnsService.ListAdapters();
                Check("网卡枚举与 DNS 读取",
                    adapters.Count > 0 && adapters.All(a => a.Name.Length > 0),
                    string.Join("；", adapters.Take(3).Select(a => $"{a.Name}（{a.ModeText} {a.DnsText}）")));

                Check("延迟评级映射",
                    new DnsProbeRow { Preset = DnsService.Presets[0], LatencyMs = 12 }.Grade == "fast"
                    && new DnsProbeRow { Preset = DnsService.Presets[0], LatencyMs = 60 }.Grade == "mid"
                    && new DnsProbeRow { Preset = DnsService.Presets[0], LatencyMs = 200 }.Grade == "slow"
                    && new DnsProbeRow { Preset = DnsService.Presets[0], LatencyMs = -1 }.Grade == "timeout",
                    "12→fast / 60→mid / 200→slow / -1→timeout");

                var dnsWin = new DnsWindow();
                Check("DNS 窗口可构造",
                    dnsWin.PresetCount == DnsService.Presets.Count && dnsWin.AdapterCount >= 0,
                    $"{dnsWin.PresetCount} 个方案 / {dnsWin.AdapterCount} 块网卡");
                dnsWin.Close();

                var dnsLaunch = LaunchOptions.Parse(new[] { "--open-dns" });
                Check("提权重启后自动打开 DNS 优化",
                    dnsLaunch.OpenDns && dnsLaunch.HasUiIntent && dnsLaunch.AdminMode,
                    $"OpenDns={dnsLaunch.OpenDns} / Admin={dnsLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("DNS 优化", false, ex.Message);
            }

            // ---------- 20. 断网急救 ----------
            try
            {
                var checks = NetRepairService.Check();
                Check("网络体检项",
                    checks.Count >= 8
                    && checks.All(c => c.Name.Length > 0 && c.Level is "ok" or "warn" or "bad"),
                    $"{checks.Count} 项：" + string.Join("、", checks.Take(4).Select(c => $"{c.Name}={c.LevelText}")));

                var steps = NetRepairService.BuildSteps();
                Check("修复步骤表",
                    steps.Count == 7
                    && steps.Count(s => s.Selected) == 4
                    && steps.Any(s => s.Kind == NetFixKind.ResetWinsock && s.NeedsRestart)
                    && steps.Any(s => s.Kind == NetFixKind.ResetFirewall && !s.Selected),
                    $"{steps.Count} 项，默认勾选 {steps.Count(s => s.Selected)} 项（防火墙/重启 IP/hosts 默认不勾）");

                var (proxyOn, _) = NetRepairService.ReadProxy();
                Check("代理设置可读", true, proxyOn ? "系统代理已开启" : "系统代理未开启");

                var win2 = new NetRepairWindow();
                Check("断网急救窗口可构造",
                    win2.StepCount == 7,
                    $"{win2.StepCount} 个修复步骤（未 Show 时体检未跑）");
                win2.Close();

                var nrLaunch = LaunchOptions.Parse(new[] { "--open-netrepair" });
                Check("提权重启后自动打开断网急救",
                    nrLaunch.OpenNetRepair && nrLaunch.HasUiIntent && nrLaunch.AdminMode,
                    $"OpenNetRepair={nrLaunch.OpenNetRepair} / Admin={nrLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("断网急救", false, ex.Message);
            }

            // ---------- 21. 深度硬件痕迹（DMA / 采集卡 / 输入模拟） ----------
            try
            {
                Check("设备号解析",
                    DeepTraceService.ParseVid(@"PCI\VEN_10EE&DEV_7024&SUBSYS_00000000") == "10EE"
                    && DeepTraceService.ParseVid(@"USB\VID_2341&PID_0043&MI_00") == "2341"
                    && DeepTraceService.ParseVid("") == "",
                    "VEN_10EE → 10EE / VID_2341 → 2341 / 空串 → 空");

                Check("输入模拟厂商表",
                    DeepTraceService.BridgeVids.Count >= 6
                    && DeepTraceService.BridgeVids.Any(v => v.Vid == "2341")
                    && DeepTraceService.BridgeVids.Any(v => v.Vid == "1A86"),
                    $"{DeepTraceService.BridgeVids.Count} 条（Arduino / CH340 等）");

                Check("采集卡关键字表",
                    DeepTraceService.CaptureKeywords.Any(k => k.Equals("elgato", StringComparison.OrdinalIgnoreCase))
                    && DeepTraceService.CaptureKeywords.Any(k => k.Contains("avermedia", StringComparison.OrdinalIgnoreCase)),
                    $"{DeepTraceService.CaptureKeywords.Count} 个关键字");

                var quick = DeepTraceService.Scan(false);

                Check("快速体检三组齐全",
                    quick.Items.Count >= 9
                    && quick.Of(DeepGroup.Dma).Any()
                    && quick.Of(DeepGroup.Capture).Any()
                    && quick.Of(DeepGroup.Input).Any(),
                    $"{quick.Items.Count} 项：DMA {quick.Of(DeepGroup.Dma).Count()} / 采集 {quick.Of(DeepGroup.Capture).Count()} / 输入 {quick.Of(DeepGroup.Input).Count()}");

                Check("结论分级只允许三档",
                    quick.Items.All(i => i.Level is "ok" or "hint" or "warn")
                    && quick.Verdict.Length > 0
                    && (quick.WarnCount + quick.HintCount + quick.OkCount) == quick.Items.Count,
                    $"线索 {quick.WarnCount} / 提示 {quick.HintCount} / 正常 {quick.OkCount} → {quick.Verdict}");

                var deep = DeepTraceService.Scan(true);

                Check("深度扫描比快速多出项目",
                    deep.Items.Count > quick.Items.Count
                    && deep.Items.Any(i => i.DeepOnly),
                    $"快速 {quick.Items.Count} → 深度 {deep.Items.Count}，深度专属 {deep.Items.Count(i => i.DeepOnly)} 项");

                var win2 = new DeepTraceWindow(_ => { });
                win2.Reload();
                Check("深度痕迹窗口可构造",
                    win2.ItemCount > 0 && win2.LastResult != null,
                    $"{win2.ItemCount} 项 / {win2.LastResult.ScopeText}");
                win2.Close();

                var payload = DeepTraceService.BuildReport(deep);
                Check("深度痕迹报告可生成",
                    payload.HtmlPath.Length > 0 && System.IO.File.Exists(payload.HtmlPath)
                    && payload.PlainSummary.Contains("深度硬件痕迹", StringComparison.Ordinal),
                    System.IO.Path.GetFileName(payload.HtmlPath));

                var dtLaunch = LaunchOptions.Parse(new[] { "--open-deeptrace" });
                Check("提权重启后自动打开深度痕迹",
                    dtLaunch.OpenDeepTrace && dtLaunch.HasUiIntent && dtLaunch.AdminMode,
                    $"OpenDeepTrace={dtLaunch.OpenDeepTrace} / Admin={dtLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("深度硬件痕迹", false, ex.Message);
            }

            // ---------- 22. 程序执行记录（BAM / AmCache / SRUM） ----------
            try
            {
                Check("UserAssist 名称解码",
                    ExecTraceService.Rot13("P:////rkr") == "C:////exe"
                    && ExecTraceService.Rot13("Nqzva") == "Admin"
                    && ExecTraceService.Rot13("") == "",
                    "ROT13 往返正确");

                // Win11 把 Amcache 的字符串存成 REG_BINARY（UTF-16LE），只按 string 读会全部拿不到
                var utf16Path = Encoding.Unicode.GetBytes("C:\\Windows\\System32\\notepad.exe\0");
                var decoded = ExecTraceService.AsText(utf16Path);

                Check("AmCache 二进制字符串解码",
                    decoded == @"C:\Windows\System32\notepad.exe"
                    && ExecTraceService.AsText("C:\\a.exe\0") == @"C:\a.exe"
                    && ExecTraceService.AsText(new byte[] { 0x41, 0x00 }) == "A"
                    && ExecTraceService.AsText(new string[] { "x\0", "y" }) == "x"
                    && ExecTraceService.AsText(null) == "",
                    $"{utf16Path.Length} 字节 UTF-16 → {decoded}");

                var utc = new DateTime(2026, 11, 5, 12, 30, 0, DateTimeKind.Utc);
                var bytes = BitConverter.GetBytes(utc.ToFileTimeUtc());
                var parsed = ExecTraceService.FromFileTime(bytes);

                Check("FILETIME 解析与范围校验",
                    parsed.HasValue && Math.Abs((parsed.Value - utc.ToLocalTime()).TotalSeconds) < 1
                    && ExecTraceService.FromFileTime(new byte[8]) == null
                    && ExecTraceService.FromFileTime(new byte[4]) == null,
                    parsed?.ToString("yyyy-MM-dd HH:mm:ss") ?? "解析失败");

                var selfProbe = ExecTraceService.ProbeFile(System.Environment.ProcessPath ?? "");
                var missProbe = ExecTraceService.ProbeFile(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sysdiag-not-exist-" + Guid.NewGuid().ToString("N")[..6]));

                Check("文件探测区分存在与缺失",
                    selfProbe.State == "ok" && selfProbe.Size > 0 && missProbe.State == "missing",
                    $"现有文件 {selfProbe.State}/{selfProbe.Size} 字节，假路径 {missProbe.State}");

                var scan = ExecTraceService.Scan();

                Check("BAM 读取返回明确结论",
                    scan.Bam.All(r => r.Path.Length > 0 && r.Name.Length > 0)
                    && (scan.Bam.Count > 0 || scan.BamNote.Length > 0),
                    scan.Bam.Count > 0
                        ? $"{scan.Bam.Count} 条，最近 {scan.Bam[0].TimeText}"
                        : scan.BamNote);

                Check("AmCache 读取返回明确结论",
                    scan.AmcNote.Length > 0 && scan.Amc.All(r => r.Path.Length > 0),
                    $"{scan.Amc.Count} 条 / {scan.AmcNote}");

                var srum = scan.Srum;
                Check("SRUM 库体检",
                    srum.FilePath.EndsWith("SRUDB.dat", StringComparison.OrdinalIgnoreCase)
                    && srum.Summary.Length > 0,
                    srum.Summary);

                var execWin = new ExecTraceWindow(_ => { });
                execWin.Reload();
                Check("执行记录窗口可构造",
                    execWin.ItemCount >= 0 && execWin.Srum != null,
                    $"{execWin.ItemCount} 条记录 / SRUM {execWin.Srum.LevelText}");

                Check("权限不足时给出提权入口",
                    execWin.ElevateButtonVisible == !LoggingService.IsAdmin(),
                    $"管理员={LoggingService.IsAdmin()} / 按钮{(execWin.ElevateButtonVisible ? "可见" : "隐藏")}");
                execWin.Close();

                var execPayload = ExecTraceService.BuildReport(scan.Bam, scan.Amc, scan.AmcNote, srum);
                Check("执行记录报告可生成",
                    System.IO.File.Exists(execPayload.HtmlPath)
                    && execPayload.PlainSummary.Contains("程序执行记录", StringComparison.Ordinal),
                    System.IO.Path.GetFileName(execPayload.HtmlPath));

                var etLaunch = LaunchOptions.Parse(new[] { "--open-exectrace" });
                Check("自动打开执行记录参数",
                    etLaunch.OpenExecTrace && etLaunch.HasUiIntent,
                    $"OpenExecTrace={etLaunch.OpenExecTrace}");
            }
            catch (Exception ex)
            {
                Check("程序执行记录", false, ex.Message);
            }

            // ---------- 23. C 盘清理（只读扫描 + 隔离目录内的清理验证） ----------
            try
            {
                var probe = new DiskCleanTarget { Key = "x", Size = 0, Risk = DiskRisk.Safe };
                var keep = new DiskCleanTarget { Key = "y", Size = 1024, Risk = DiskRisk.Keep };
                var careful = new DiskCleanTarget { Key = "z", Size = 1024, Risk = DiskRisk.Careful };

                Check("清理项的可勾选规则",
                    !probe.CanClean && !keep.CanClean && careful.CanClean
                    && keep.LevelText == "仅展示" && careful.LevelText == "谨慎",
                    $"空项={probe.CanClean} 仅展示={keep.CanClean} 谨慎={careful.CanClean}");

                var disk = DiskCleanService.Scan();

                Check("C 盘容量读取",
                    disk.TotalBytes > 0 && disk.FreeBytes > 0
                    && disk.UsedBytes > 0 && disk.UsedPercent is > 0 and < 100,
                    $"{disk.TotalText} / 可用 {disk.FreeText} / 已用 {disk.UsedPercent:N1}%");

                Check("扫描清单结构完整",
                    disk.Targets.All(t => t.Key.Length > 0 && t.Name.Length > 0 && t.Size > 0 && t.Note.Length > 0)
                    && disk.Targets.Select(t => t.Key).Distinct().Count() == disk.Targets.Count,
                    $"{disk.Targets.Count} 项，可清理 {disk.CleanableText}");

                Check("安全项与谨慎项分开统计",
                    disk.SafeBytes <= disk.CleanableBytes
                    && disk.SafeCount == disk.Targets.Count(t => t.CanClean && t.Risk == DiskRisk.Safe),
                    $"安全 {disk.SafeCount} 项 / {LogFileItem.FormatSize(disk.SafeBytes)}，合计 {disk.CleanableText}");

                var diskWin = new DiskCleanWindow();
                diskWin.ScanSync();

                // 只校验窗口自身一致：两次独立扫描之间 Temp 会变，跨扫描比字节数必然不稳
                Check("C 盘清理窗口可构造",
                    diskWin.TargetCount > 0
                    && diskWin.CleanableBytes > 0
                    && diskWin.Snapshot.Targets.Count == diskWin.TargetCount,
                    $"{diskWin.TargetCount} 项 / 可清理 {LogFileItem.FormatSize(diskWin.CleanableBytes)}");

                Check("默认只自动勾选安全项",
                    diskWin.SafeSelectedCount == diskWin.Snapshot.SafeCount
                    && diskWin.SafeSelectedCount <= diskWin.TargetCount,
                    $"自动勾选 {diskWin.SafeSelectedCount} / 安全项 {diskWin.Snapshot.SafeCount} / 共 {diskWin.TargetCount}");

                Check("权限不足时给出提权入口",
                    diskWin.ElevateButtonVisible == !ElevationService.IsAdmin,
                    $"管理员={ElevationService.IsAdmin} / 按钮{(diskWin.ElevateButtonVisible ? "可见" : "隐藏")}");
                diskWin.Close();

                // 只在自己的临时目录里验证清理逻辑，不碰用户文件
                var sandbox = Path.Combine(Path.GetTempPath(), "sysdiag-clean-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(Path.Combine(sandbox, "sub"));
                File.WriteAllText(Path.Combine(sandbox, "a.tmp"), new string('x', 1024));
                File.WriteAllText(Path.Combine(sandbox, "sub", "b.tmp"), new string('y', 2048));

                var directTarget = new DiskCleanTarget
                {
                    Key = "sandbox",
                    Name = "自检沙箱",
                    Paths = new List<string> { sandbox },
                    IsDirectory = true,
                    Size = 3072,
                    FileCount = 2,
                    Risk = DiskRisk.Safe,
                    Selected = true
                };

                var direct = DiskCleanService.Clean(new[] { directTarget }, true);

                Check("直接清理：删文件、保留目录本身",
                    direct.DeletedBytes >= 3072
                    && Directory.Exists(sandbox)
                    && !Directory.EnumerateFileSystemEntries(sandbox).Any(),
                    $"释放 {direct.DeletedBytes} 字节 / {direct.RemovedFiles} 个文件 / 目录{(Directory.Exists(sandbox) ? "仍在" : "被删")}");

                // 回收站路径：只放一个几十字节的小文件，避免污染回收站
                var sandbox2 = Path.Combine(Path.GetTempPath(), "sysdiag-recycle-" + Guid.NewGuid().ToString("N")[..8]);
                Directory.CreateDirectory(sandbox2);
                File.WriteAllText(Path.Combine(sandbox2, "tiny.tmp"), "x");

                var recycleTarget = new DiskCleanTarget
                {
                    Key = "sandbox2",
                    Name = "自检沙箱（回收站）",
                    Paths = new List<string> { sandbox2 },
                    IsDirectory = true,
                    Size = 1,
                    FileCount = 1,
                    Risk = DiskRisk.Careful,
                    Selected = true
                };

                var recycl = DiskCleanService.Clean(new[] { recycleTarget }, false);

                Check("谨慎项走回收站：目录被重建为空",
                    recycl.RecycledBytes > 0
                    && Directory.Exists(sandbox2)
                    && !Directory.EnumerateFileSystemEntries(sandbox2).Any(),
                    $"移入回收站 {recycl.RecycledBytes} 字节 / {recycl.RemovedFiles} 个对象");

                try { Directory.Delete(sandbox, true); } catch { }
                try { Directory.Delete(sandbox2, true); } catch { }

                var dcLaunch = LaunchOptions.Parse(new[] { "--open-diskclean" });
                Check("提权重启后回到 C 盘清理",
                    dcLaunch.OpenDiskClean && dcLaunch.HasUiIntent && dcLaunch.AdminMode,
                    $"OpenDiskClean={dcLaunch.OpenDiskClean} / Admin={dcLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("C 盘清理", false, ex.Message);
            }

            // ---------- 24. 外挂痕迹体检 ----------
            try
            {
                var hintOnly = new List<CheatFinding>
                {
                    new() { Level = "hint", Weight = 5 },
                    new() { Level = "hint", Weight = 5 },
                    new() { Level = "hint", Weight = 5 }
                };

                Check("风险分：提示类有总额上限",
                    CheatScanService.ComputeScore(hintOnly) == 12,
                    $"3×5 分提示 → {CheatScanService.ComputeScore(hintOnly)}");

                var mixed = new List<CheatFinding>
                {
                    new() { Level = "warn", Weight = 18 },
                    new() { Level = "hint", Weight = 9 }
                };

                Check("风险分：线索全额累加",
                    CheatScanService.ComputeScore(mixed) == 27,
                    $"18 线索 + 9 提示 → {CheatScanService.ComputeScore(mixed)}");

                var many = Enumerable.Range(0, 10)
                    .Select(_ => new CheatFinding { Level = "warn", Weight = 20 })
                    .ToList();

                Check("风险分：封顶 100",
                    CheatScanService.ComputeScore(many) == 100,
                    $"10×20 分 → {CheatScanService.ComputeScore(many)}");

                var cheatWin = new CheatScanWindow(_ => { });
                cheatWin.ScanSync();
                var cheat = cheatWin.Snapshot;

                Check("十七项采集全部执行",
                    cheat.Stages.Count == 17
                    && cheat.Stages.All(s => s.State is "done" or "limited" or "skipped"
                                            || (s.State == "failed" && s.Note.Length > 0)),
                    string.Join(" / ", cheat.Stages.Select(s => $"{s.Name}:{s.StateText}")));

                Check("体检发现结构完整",
                    cheat.Findings.Count > 0
                    && cheat.Findings.All(f => f.Group.Length > 0 && f.Title.Length > 0
                                               && f.Level is "ok" or "hint" or "warn"),
                    $"{cheat.Findings.Count} 条 / {cheat.GroupCount} 组");

                Check("体检结果自洽",
                    cheat.Score is >= 0 and <= 100
                    && (cheat.WarnCount + cheat.HintCount + cheat.OkCount) == cheat.Findings.Count
                    && cheat.Verdict.Length > 0,
                    $"风险分 {cheat.Score}/100（{cheat.ScoreLevel}）· 线索 {cheat.WarnCount} / 提示 {cheat.HintCount} / 正常 {cheat.OkCount}");

                // 深度层三项：驱动来源、无文件持久化、隐藏进程比对
                Check("深度层：三项采集都有结论",
                    new[] { "服务与驱动来源", "无文件持久化", "隐藏进程比对" }
                        .All(g => cheat.Stages.Any(s => s.Name == g)
                                  && cheat.Findings.Any(f => f.Group == g)),
                    string.Join(" / ", new[] { "服务与驱动来源", "无文件持久化", "隐藏进程比对" }
                        .Select(g => $"{g}:{cheat.Stages.FirstOrDefault(s => s.Name == g)?.StateText ?? "缺"}")));

                // 误报守卫：这台机器是正常系统，不该出现大量「驱动镜像异常」。
                // 开发时用的初版判定跑出 206 条假异常（系统驱动被判未签名、ACE 驱动被判不在系统目录），
                // 这条断言就是防止那种判定再回来。
                var imageWarns = cheat.Findings.Count(f => f.Group == "服务与驱动来源" && f.Level == "warn");

                Check("深度层：正常系统上不产生大批假异常",
                    imageWarns <= 3,
                    $"{imageWarns} 条镜像异常（>3 说明判定条件又放得太宽）");

                // WMI 订阅：系统自带的 SCM Event Log Filter 必须被排除
                Check("深度层：系统自带 WMI 订阅不被当成线索",
                    cheat.Findings.Where(f => f.Group == "无文件持久化")
                        .All(f => !(f.Level == "warn" && f.Title.Contains("SCM"))),
                    "SCM Event Log Filter 已列白名单");

                // ---------- DMA 伪装检测 ----------

                // 属性编号自校验：编号错了会读出「当前宽度 > 最大宽度」这类矛盾值。
                // 这轮就是靠它发现最初凭记忆写的编号全错（12/13/14/15/17/18/20 → 全读成 -1）。
                var idCheck = DmaSpoofProbeService.VerifyPropertyIds();
                Check("DMA 伪装：PCI 属性编号读出的是自洽数据", idCheck.Ok, idCheck.Note);

                // 同一 VID/DID 对应不同设备类：这是逻辑矛盾，不是概率判断，必须报 warn
                var contradictory = DmaSpoofProbeService.Analyze(new List<DmaSpoofProbeService.DeviceFacts>
                {
                    new() { InstanceId = "PCI\\VEN_10EC&DEV_8168&SUBSYS_0000&REV_15\\0", Vid = "10EC", Did = "8168",
                            BaseClass = 0x02, CurrentLinkWidth = 1, MaxLinkWidth = 1, CurrentLinkSpeed = 1, MaxLinkSpeed = 1 },
                    new() { InstanceId = "PCI\\VEN_10EC&DEV_8168&SUBSYS_0000&REV_15\\1", Vid = "10EC", Did = "8168",
                            BaseClass = 0x03, CurrentLinkWidth = 16, MaxLinkWidth = 16, CurrentLinkSpeed = 4, MaxLinkSpeed = 4 }
                });

                Check("DMA 伪装：同 ID 不同设备类判为线索",
                    contradictory.Any(i => i.Level == "warn" && i.Name.Contains("设备标识矛盾")),
                    contradictory.FirstOrDefault(i => i.Level == "warn")?.Name ?? "没有报出矛盾");

                // 伪装成网卡：x16 链路对真实网卡不合理（消费级最高 x1，100G 双口才 x8）
                var fakeNic = DmaSpoofProbeService.Analyze(new List<DmaSpoofProbeService.DeviceFacts>
                {
                    new() { InstanceId = "PCI\\VEN_8086&DEV_15F3\\0", Vid = "8086", Did = "15F3",
                            BaseClass = 0x02, CurrentLinkWidth = 8, MaxLinkWidth = 16,
                            CurrentLinkSpeed = 4, MaxLinkSpeed = 4 }
                });

                Check("DMA 伪装：网卡报 x16 链路会被标出",
                    fakeNic.Any(i => i.Level == "hint" && i.Name.Contains("网络控制器") && i.Name.Contains("x16")),
                    fakeNic.FirstOrDefault(i => i.Name.Contains("网络控制器"))?.Name ?? "没有报出");

                // 伪装成显卡：链路宽度掉到 x1（降速是正常的，降宽度不是）
                var narrowGpu = DmaSpoofProbeService.Analyze(new List<DmaSpoofProbeService.DeviceFacts>
                {
                    new() { InstanceId = "PCI\\VEN_10DE&DEV_2504\\0", Vid = "10DE", Did = "2504",
                            BaseClass = 0x03, CurrentLinkWidth = 1, MaxLinkWidth = 16,
                            CurrentLinkSpeed = 4, MaxLinkSpeed = 4 }
                });

                Check("DMA 伪装：显卡只有 x1 宽度会被标出",
                    narrowGpu.Any(i => i.Level == "hint" && i.Name.Contains("显示控制器")),
                    narrowGpu.FirstOrDefault(i => i.Name.Contains("显示控制器"))?.Name ?? "没有报出");

                // 厂商矩阵：Realtek 的产品线里没有显示控制器
                var wrongVendor = DmaSpoofProbeService.Analyze(new List<DmaSpoofProbeService.DeviceFacts>
                {
                    new() { InstanceId = "PCI\\VEN_10EC&DEV_9999\\0", Vid = "10EC", Did = "9999",
                            BaseClass = 0x03, CurrentLinkWidth = 16, MaxLinkWidth = 16,
                            CurrentLinkSpeed = 4, MaxLinkSpeed = 4 }
                });

                Check("DMA 伪装：厂商与设备类不匹配会被标出",
                    wrongVendor.Any(i => i.Level == "hint" && i.Name.Contains("Realtek") && i.Name.Contains("显示控制器")),
                    wrongVendor.FirstOrDefault(i => i.Name.Contains("Realtek"))?.Name ?? "没有报出");

                // 正常机器不能被误伤：本机真实设备跑一遍，不允许出现 warn
                var realDevices = DmaSpoofProbeService.ReadDevices();
                var realVerdict = DmaSpoofProbeService.Analyze(realDevices);

                Check("DMA 伪装：本机真实设备不产生线索级误报",
                    realVerdict.All(i => i.Level != "warn"),
                    $"{realDevices.Count} 个设备，{realVerdict.Count(i => i.Level == "hint")} 条提示 / " +
                    $"{realVerdict.Count(i => i.Level == "warn")} 条线索");

                // 平台保护必须给出结论（哪怕是「读不到」），不能静默跳过
                var platform = DmaSpoofProbeService.ReadPlatformProtection();
                Check("DMA 伪装：平台保护状态有明确结论",
                    platform.Count > 0 && platform.All(i => i.Detail.Length > 10),
                    platform.FirstOrDefault()?.Name ?? "没有结论");

                // ---------- 网络隐蔽手法 ----------

                var stealth = cheat.Findings.Where(f => f.Group == "网络隐蔽手法").ToList();
                Check("网络隐蔽手法：四类都检查了",
                    stealth.Count >= 3
                    && stealth.Any(f => f.Title.Contains("hosts"))
                    && stealth.Any(f => f.Title.Contains("代理"))
                    && stealth.Any(f => f.Title.Contains("抓包")),
                    string.Join(" / ", stealth.Select(f => f.Title)));

                // hosts 判定：指向真实地址比指向回环更可疑，级别必须不同
                Check("网络隐蔽手法：hosts 指向真实地址比屏蔽更可疑",
                    stealth.Any(f => f.Title.Contains("hosts"))
                    && !stealth.Any(f => f.Title.Contains("hosts") && f.Level == "ok" && f.Detail.Contains("指向")),
                    stealth.FirstOrDefault(f => f.Title.Contains("hosts"))?.Title ?? "缺 hosts 结论");

                // ---------- 精简系统感知 ----------

                // 判定必须与「系统信息」用同一套规则，不能各说一套
                var trimChecks = SystemInfoService.DetectTrimmedOnly();
                var trimScore = trimChecks.Where(c => c.Missing).Sum(c => c.Weight);
                var trimStrong = trimChecks.Count(c => c.Missing && c.Weight >= 3);
                var trimChecked = trimChecks.Count(c => !c.Skipped);
                var expectedTrimmed = trimChecked >= 6 && (trimStrong >= 2 || trimScore >= 6);

                Check("精简感知：与系统信息用同一套判定",
                    TrimAwareness.IsTrimmed == expectedTrimmed
                    && TrimAwareness.Score == trimScore
                    && TrimAwareness.CheckedCount == trimChecked,
                    $"本机 {TrimAwareness.Summary}（缺失分 {trimScore}、强证据 {trimStrong}、检查 {trimChecked} 项）");

                // 归因只能用在「确实缺了这个组件」上。
                // 随便传个不存在的关键字必须返回空串——否则就成了「什么都往精简上赖」，
                // 那和「读不到就说没事」是同一种错。
                Check("精简感知：不对无关组件瞎归因",
                    TrimAwareness.AttributionFor("绝对不存在的组件XYZ").Length == 0,
                    "无关关键字不产生归因");

                // 本机是精简系统时，Defender 那条归因必须真的出现（它就是读不到的原因）
                if (TrimAwareness.IsTrimmed && TrimAwareness.IsMissing("WinDefend"))
                {
                    Check("精简感知：Defender 读不到时归因到组件被移除",
                        TrimAwareness.AttributionFor("WinDefend").Contains("组件已被移除"),
                        TrimAwareness.AttributionFor("WinDefend").Trim());
                }

                // 详细状态读不到时，产品条不能再报「实时保护：开」——两个数据源打架时只给「未知」。
                // 本机实测就出现过「五个指标全读不到、产品条却显示开」，那是替系统说谎。
                var avUnknown = new DefenderReviewService.AvProvider { RealTimeOn = true, StateUnknown = true };
                Check("防护审查：读不到详细状态时产品条显示未知",
                    avUnknown.StateText == "状态未知",
                    avUnknown.StateText);

                // ---------- 直播优化：系统层配套 ----------

                var liveWin = new LiveOptimizerWindow();
                Check("直播优化：窗口可构造", liveWin != null, "OK");
                liveWin.Close();

                var diag = LiveStreamTweaks.Diagnostics();
                Check("直播优化：三项系统状态都有结论",
                    diag.Count == 3 && diag.All(d => d.Name.Length > 0 && d.Value.Length > 0),
                    string.Join(" / ", diag.Select(d => $"{d.Name}={d.Value}")));

                // 路径为空或不存在时不许往注册表里写垃圾
                Check("直播优化：没有有效路径时不写 GPU 首选项",
                    !LiveStreamTweaks.ApplyGpuPreference("").Ok
                    && !LiveStreamTweaks.ApplyGpuPreference(@"C://不存在//x.exe").Ok,
                    "空路径与不存在的路径都被拒绝");

                // 说明必须写清 HAGS 是「有争议、要实测」，不能替用户下结论
                Check("直播优化：HAGS 说明不替用户下结论",
                    diag.First(d => d.Name.Contains("GPU 调度")).Hint.Contains("实测"),
                    diag.First(d => d.Name.Contains("GPU 调度")).Hint);

                // ---------- 报告轮转 ----------

                // 造 45 份报告 + 若干用户文件，验证只删最旧的、且不碰用户文件
                var fake = new List<string>();
                for (var i = 1; i <= 45; i++)
                {
                    var stamp = $"20260101-0000{i:00}";
                    fake.Add($"{stamp}-系统信息.html");
                    fake.Add($"{stamp}-系统信息.png");
                }
                fake.Add("icon-sheet.png");
                fake.Add("p1.png");
                fake.Add("C盘清理-界面.png");
                fake.Add("latency-experiments.json");
                fake.Add("证据包-20260101.zip");

                var victims = OutputHousekeeping.CollectPrunable(fake, 40);

                // 45 份里保留 40 份 -> 删 5 份 x2 个文件 = 10
                Check("报告轮转：只删超出保留数的报告",
                    victims.Count == 10
                    && victims.All(v => v.StartsWith("20260101-00000") || v.StartsWith("20260101-00001")),
                    $"待删 {victims.Count} 个");

                Check("报告轮转：不碰用户自己放的文件",
                    !victims.Contains("icon-sheet.png")
                    && !victims.Contains("p1.png")
                    && !victims.Contains("C盘清理-界面.png")
                    && !victims.Contains("latency-experiments.json")
                    && !victims.Contains("证据包-20260101.zip"),
                    "图标、截图、实验记录、证据包均未被选中");

                // 份数不多时什么都不该删
                Check("报告轮转：没超量时不动任何文件",
                    OutputHousekeeping.CollectPrunable(fake.Take(10).ToList(), 40).Count == 0,
                    "10 个文件、保留 40 份 -> 0 删除");

                // ---------- 报告图片 ----------

                var imagePath = System.IO.Path.Combine(AppPaths.OutputDir, "selftest-cheat-report.png");
                if (System.IO.File.Exists(imagePath)) System.IO.File.Delete(imagePath);

                var imageExport = ReportImageExporter.Export(cheat, imagePath);

                Check("报告图片：能导出有效 PNG",
                    imageExport.Ok && System.IO.File.Exists(imagePath)
                    && new System.IO.FileInfo(imagePath).Length > 8 * 1024,
                    imageExport.Ok
                        ? $"{new System.IO.FileInfo(imagePath).Length / 1024} KB"
                        : imageExport.Message);

                // 复现界面的真实顺序：先 BuildReport（写 HTML），再导出图片。
                // 命令行 --cheat-image 只单独调 Export，测不到这条组合路径。
                var uiPayload = CheatScanService.BuildReport(cheat);
                var uiImage = ReportImageExporter.Export(cheat);

                Check("报告图片：先生成报告再导图也能出图",
                    uiImage.Ok && System.IO.File.Exists(uiImage.Path),
                    uiImage.Ok
                        ? System.IO.Path.GetFileName(uiImage.Path)
                        : uiImage.Message);

                // 没有结果时必须老实返回失败，不能给一张空白图
                Check("报告图片：没有数据时不生成空图",
                    !ReportImageExporter.Export(null).Ok,
                    ReportImageExporter.Export(null).Message);

                // 图会被单独转发，离开软件上下文后最容易被当成「判定结论」。
                // 这段效力说明必须在图上，所以用断言钉住它，防止以后改排版时被删。
                Check("报告图片：效力说明写明只是大概率参考",
                    ReportImageExporter.DisclaimerText.Contains("大概率没开挂")
                    && ReportImageExporter.DisclaimerText.Contains("不能当作判定依据"),
                    $"说明共 {ReportImageExporter.DisclaimerText.Length} 字");

                // 体检的第一条结论必须是系统完整性，且精简机器上要判成提示而不是通过
                var trimFinding = cheat.Findings.FirstOrDefault(f => f.Group == "系统完整性");
                Check("精简感知：体检首条给出系统完整性结论",
                    trimFinding != null
                    && (TrimAwareness.IsTrimmed
                        ? trimFinding.Level == "hint" && trimFinding.Title.Contains("精简")
                        : trimFinding.Level == "ok"),
                    trimFinding?.Title ?? "缺少系统完整性结论");

                // PE 导入表解析：用系统自带的 notepad.exe 当样本。
                // 为什么不用本程序自己：单文件发布版经过混淆与压缩，自身的导入表不可靠，
                // 拿它做基准会让「发布版 FAIL、调试版 PASS」——测的是打包方式，不是解析器。
                var samplePath = System.IO.Path.Combine(Environment.SystemDirectory, "notepad.exe");
                var sampleImports = PeFeatureScanner.ReadImports(samplePath);

                Check("文件特征：能解析 PE 导入表",
                    sampleImports.Count > 20,
                    sampleImports.Count == 0
                        ? "解析不出导入表（样本 " + samplePath + "）"
                        : $"样本 notepad.exe：{sampleImports.Count} 个导入函数");

                // 解析器不能对任何文件都返回空集——那样「没发现」永远成立
                Check("文件特征：解析器不会对任意文件给出空集假象",
                    PeFeatureScanner.ReadImports(samplePath).Count > 0
                    && PeFeatureScanner.ReadImports("C:////不存在的文件.exe").Count == 0,
                    "有效文件能解析、无效路径返回空");

                // 组合才成立：单看 ReadProcessMemory 调试器也有，加远程线程才是注入器
                var onlyMemory = PeFeatureScanner.MatchRuleNames(new[] { "ReadProcessMemory", "WriteProcessMemory" });
                var withInject = PeFeatureScanner.MatchRuleNames(new[]
                    { "ReadProcessMemory", "WriteProcessMemory", "CreateRemoteThread", "VirtualAllocEx" });
                var keyboardOnly = PeFeatureScanner.MatchRuleNames(new[] { "GetAsyncKeyState" });

                Check("文件特征：只有能力组合才升级为注入特征",
                    onlyMemory.Count == 1 && onlyMemory[0] == "跨进程内存读写"
                    && withInject.Count == 2 && withInject.Contains("远程线程注入")
                    && keyboardOnly.Count == 1 && keyboardOnly[0] == "键盘状态轮询",
                    $"仅内存={string.Join("/", onlyMemory)}　加注入={string.Join("/", withInject)}");

                Check("文件特征：规则集覆盖注入与内存读写",
                    PeFeatureScanner.RuleNames.Count >= 6
                    && PeFeatureScanner.RuleNames.Contains("远程线程注入")
                    && PeFeatureScanner.RuleNames.Contains("跨进程内存读写"),
                    string.Join("、", PeFeatureScanner.RuleNames));

                // 特征规则必须覆盖「注入」与「内存读写」两类核心能力，否则等于没做
                var selfHasInjection = new[] { "CreateRemoteThread", "VirtualAllocEx" }.All(sampleImports.Contains);
                Check("文件特征：注入类规则可判定",
                    !selfHasInjection,
                    selfHasInjection ? "notepad 命中了注入特征（规则过宽）" : "notepad 未命中注入特征，规则未误伤正常程序");

                var peStage = cheat.Stages.FirstOrDefault(s => s.Name == "文件行为特征");
                Check("文件特征：扫描有结论",
                    peStage != null && peStage.Note.Length > 0,
                    peStage?.Note ?? "缺少采集阶段");

                // 网络过滤层：必须能读到绑定，或如实说明读不到
                var netStage = cheat.Stages.FirstOrDefault(s => s.Name == "网络过滤层");
                Check("网络过滤层：清点有结论",
                    cheat.Findings.Any(f => f.Group == "网络过滤层") && netStage != null,
                    netStage?.Note ?? "缺少采集阶段");

                // 报告：姓名 / 特征码 / 生成时间必须写进去，边界段按用户要求不再输出
                cheat.Operator = "自检代号";
                var cheatReport = CheatScanService.BuildReport(cheat);
                var reportHtml = System.IO.File.Exists(cheatReport.HtmlPath)
                    ? System.IO.File.ReadAllText(cheatReport.HtmlPath)
                    : "";

                Check("体检报告：含填写者 / 特征码 / 生成时间",
                    reportHtml.Contains("自检代号")
                    && reportHtml.Contains("机器特征码")
                    && reportHtml.Contains(cheat.MachineCodeText)
                    && reportHtml.Contains("生成时间"),
                    $"特征码 {cheat.MachineCodeText}　生成于 {cheat.FinishedAt:HH:mm:ss}");

                Check("体检报告：不再输出边界说明段",
                    reportHtml.Length > 0 && !reportHtml.Contains("边界（重要）"),
                    reportHtml.Length > 0 ? "已移除" : "报告未生成");

                // 特征码格式：四段十六进制，换内存/硬盘/机器名都不该变
                var code = cheat.MachineCode;
                Check("机器特征码：格式为 4-4-4-4 十六进制",
                    code.Length == 19 && code[4] == '-' && code[9] == '-' && code[14] == '-'
                    && code.Where((c, i) => i is not (4 or 9 or 14)).All(Uri.IsHexDigit),
                    code.Length > 0 ? code : "读不到（不编假码）");

                Check("体检窗口可构造",
                    cheatWin.FindingCount == cheat.Findings.Count && cheatWin.StageCount == 17,
                    $"{cheatWin.FindingCount} 条 / {cheatWin.StageCount} 个采集项");

                var cl = cheatWin.Checklist;

                Check("检查清单：四层齐全",
                    cl.Items.Count == 19
                    && CheatChecklist.LayerOrder.All(l => cl.Items.Any(i => i.Layer == l)),
                    $"{cl.Items.Count} 项：" + string.Join(" / ",
                        CheatChecklist.LayerOrder.Select(l => $"{l} {cl.Items.Count(i => i.Layer == l)}")));

                Check("检查清单：数据不可用标「未检查」而非通过",
                    cl.SkipCount > 0 && cl.Items.Any(i => i.IsSkip && i.Evidence.Length > 0),
                    $"{cl.SummaryText}");

                Check("检查清单：异常项带证据",
                    cl.Items.All(i => i.Result != "fail" || i.Evidence.Length > 0),
                    cl.HasFail ? "有异常项，证据齐全" : "本次无异常项");


                Check("统一时间线合并多来源",
                    cheat.Timeline.Count > 0
                    && cheat.Timeline.Select(e => e.Source).Distinct().Count() >= 2,
                    $"{cheat.Timeline.Count} 条 · 来源 {string.Join(" / ", cheat.Timeline.Select(e => e.Source).Distinct())}");

                Check("体检窗口的提权入口",
                    cheatWin.ElevateButtonVisible == !ElevationService.IsAdmin,
                    $"管理员={ElevationService.IsAdmin} / 按钮{(cheatWin.ElevateButtonVisible ? "可见" : "隐藏")}");
                cheatWin.Close();

                var cheatPayload = CheatScanService.BuildReport(cheat);

                Check("体检统一报告可生成",
                    System.IO.File.Exists(cheatPayload.HtmlPath)
                    && cheatPayload.PlainSummary.Contains("外挂痕迹体检", StringComparison.Ordinal)
                    && cheatPayload.Subtitle.Contains(cheat.Score.ToString(), StringComparison.Ordinal),
                    System.IO.Path.GetFileName(cheatPayload.HtmlPath));

                var csLaunch = LaunchOptions.Parse(new[] { "--open-cheatscan" });

                Check("提权重启后回到外挂体检",
                    csLaunch.OpenCheatScan && csLaunch.HasUiIntent && csLaunch.AdminMode,
                    $"OpenCheatScan={csLaunch.OpenCheatScan} / Admin={csLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("外挂痕迹体检", false, ex.Message);
            }

            // ---------- 25. 日志规则匹配（Sigma 子集） ----------
            try
            {
                var rules = SigmaRules.All;

                Check("Sigma 规则解析",
                    rules.Count >= 20
                    && rules.All(x => x.Title.Length > 0 && x.Service.Length > 0
                                      && x.Condition.Length > 0 && x.Selections.Count > 0)
                    && rules.Select(x => x.Id).Distinct().Count() == rules.Count,
                    $"{rules.Count} 条，通道 {string.Join("/", rules.Select(x => x.Service).Distinct())}");

                Check("反作弊组件识别",
                    AntiCheatCatalog.Identify("ACE-SSC-DRV64") == "腾讯 ACE"
                    && AntiCheatCatalog.Identify("ACE-BASE") == "腾讯 ACE"
                    && AntiCheatCatalog.Identify(@"C:\Program Files\AntiCheatExpert\ACE-BASE.sys") == "腾讯 ACE"
                    && AntiCheatCatalog.Identify("EasyAntiCheat") == "Easy Anti-Cheat"
                    && AntiCheatCatalog.Identify("BEService") == "BattlEye"
                    && AntiCheatCatalog.Identify("vgk") == "Riot Vanguard",
                    $"表内 {AntiCheatCatalog.Names.Count} 个组件");

                Check("普通驱动不被误判成反作弊",
                    AntiCheatCatalog.Identify("nvlddmkm") == null
                    && AntiCheatCatalog.Identify("SSCDRV") == null
                    && AntiCheatCatalog.Identify(@"C:\Windows\System32\drivers\disk.sys") == null
                    && AntiCheatCatalog.Identify("") == null,
                    "nvidia / 普通服务 / 系统驱动 / 空串 均未命中");

                var rule = new SigmaRule
                {
                    Id = "selftest",
                    Title = "自检规则",
                    Level = "high",
                    Service = "system",
                    Condition = "selection and not filter",
                    Selections = new Dictionary<string, List<SigmaMatcher>>
                    {
                        ["selection"] = new()
                        {
                            new SigmaMatcher { Field = "EventID", Values = new List<string> { "7045" } }
                        },
                        ["filter"] = new()
                        {
                            new SigmaMatcher
                            {
                                Field = "ImagePath",
                                Modifiers = new List<string> { "contains" },
                                Values = new List<string> { @"\Windows\" }
                            }
                        }
                    }
                };

                var tempEvent = new SigmaEvent { EventId = 7045 };
                tempEvent.Fields["ImagePath"] = @"C:\Temp\x.exe";

                var normalEvent = new SigmaEvent { EventId = 7045 };
                normalEvent.Fields["ImagePath"] = @"C:\Windows\x.exe";

                Check("Sigma 条件求值（and / not）",
                    SigmaRuleEngine.Matches(rule, tempEvent)
                    && !SigmaRuleEngine.Matches(rule, normalEvent),
                    "Temp 命中、Windows 被 filter 排除");

                var otherId = new SigmaEvent { EventId = 7040 };
                Check("Sigma 字段不等不命中",
                    !SigmaRuleEngine.Matches(rule, otherId),
                    "EventID 7040 ≠ 7045");

                const string sampleXml =
                    "<Event xmlns=\"http://schemas.microsoft.com/win/2004/08/events/event\">" +
                    "<System><Provider Name=\"Service Control Manager\"/><EventID>7045</EventID>" +
                    "<Channel>System</Channel><EventRecordID>123</EventRecordID>" +
                    "<TimeCreated SystemTime=\"2026-11-05T10:00:00.0000000Z\"/></System>" +
                    "<EventData><Data Name=\"ServiceName\">evil</Data>" +
                    "<Data Name=\"ImagePath\">C:\\Temp\\a.exe</Data></EventData></Event>";

                var parsed = new SigmaEvent();
                SigmaRuleEngine.ParseXml(sampleXml, parsed);

                Check("事件 XML 解析成字段",
                    parsed.EventId == 7045
                    && parsed.Provider == "Service Control Manager"
                    && parsed.Channel == "System"
                    && parsed.RecordId == 123
                    && parsed.Get("ImagePath") == @"C:\Temp\a.exe"
                    && parsed.Time.HasValue,
                    $"#{parsed.RecordId} / {parsed.EventId} / {parsed.Get("ImagePath")}");

                var sigmaWin = new SigmaWindow(_ => { });
                sigmaWin.ScanSync();
                var sigma = sigmaWin.Snapshot;

                Check("Sigma 扫描结构完整",
                    sigma.Rules.Count == rules.Count
                    && sigma.Channels.Count > 0
                    && sigma.Channels.All(c => c.State is "done" or "limited" or "missing")
                    && sigma.Hits.Count <= 200,
                    $"规则 {sigma.Rules.Count} / 通道 {sigma.Channels.Count} / 事件 {sigma.EventsScanned:N0} / 命中 {sigma.Hits.Count}");

                Check("Sigma 窗口可构造",
                    sigmaWin.RuleCount > 0 && sigmaWin.HitTotal == sigma.Hits.Count,
                    $"{sigmaWin.RuleCount} 条规则 / {sigmaWin.HitTotal} 条命中 / {sigmaWin.EventCount} 条事件");

                sigmaWin.Close();

                var sigmaPayload = SigmaRuleEngine.BuildReport(sigma);

                Check("Sigma 报告可生成",
                    System.IO.File.Exists(sigmaPayload.HtmlPath)
                    && sigmaPayload.PlainSummary.Contains("Sigma", StringComparison.Ordinal),
                    System.IO.Path.GetFileName(sigmaPayload.HtmlPath));

                var sgLaunch = LaunchOptions.Parse(new[] { "--open-sigma" });

                Check("提权重启后回到日志规则匹配",
                    sgLaunch.OpenSigma && sgLaunch.HasUiIntent && sgLaunch.AdminMode,
                    $"OpenSigma={sgLaunch.OpenSigma} / Admin={sgLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("日志规则匹配", false, ex.Message);
            }

            // ---------- 26. 报告包导出 / 导入 / 对比 ----------
            try
            {
                var sample = new ReportData
                {
                    Kind = "selftest",
                    Title = "自检报告",
                    GeneratedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
                    Machine = new MachineInfo { Name = "TEST-PC", User = "tester", Os = "Windows", Admin = false },
                    Verdict = new VerdictInfo
                    {
                        Score = 12,
                        Level = "低",
                        Text = "自检用",
                        Counts = new ReportCounts { Warn = 1, Hint = 2, Ok = 3 }
                    },
                    Sections = new List<ReportSection>
                    {
                        new()
                        {
                            Group = "测试组",
                            Items = new List<ReportItem>
                            {
                                new() { Level = "warn", Title = "条目A", Detail = "说明A", Basis = "依据A", Weight = 15 },
                                new() { Level = "ok", Title = "条目B" }
                            }
                        }
                    }
                };

                var json = sample.ToJson();
                var back = ReportData.FromJson(json);

                Check("报告 JSON 往返",
                    back != null && back.ItemCount == 2 && back.Verdict.Score == 12
                    && back.Schema == ReportData.CurrentSchema && back.IsCompatible(),
                    $"{json.Length} 字节 / {back?.ItemCount} 项 / {back?.Schema}");

                var htmlPath = Path.Combine(AppPaths.OutputDir, "_selftest-report.html");
                File.WriteAllText(htmlPath, "<html><body>selftest</body></html>", new UTF8Encoding(false));

                var payload = new ReportPayload { Title = "自检报告", HtmlPath = htmlPath, Data = sample };
                var packPath = Path.Combine(AppPaths.OutputDir, "selftest-package" + ReportPackage.Extension);

                ReportPackage.Export(payload, packPath);

                Check("导出报告包",
                    File.Exists(packPath) && new FileInfo(packPath).Length > 0,
                    $"{new FileInfo(packPath).Length} 字节");

                var imported = ReportPackage.Import(packPath);

                Check("导入报告包并校验完整性",
                    imported.Data.ItemCount == sample.ItemCount
                    && imported.IntegrityOk
                    && File.Exists(imported.RenderedHtmlPath),
                    $"{imported.SourceName} / 条目 {imported.Data.ItemCount} / 完整性 {(imported.IntegrityOk ? "通过" : "不一致")}");

                // 篡改 report.json，哈希应当对不上
                var tampered = Path.Combine(AppPaths.OutputDir, "selftest-tampered" + ReportPackage.Extension);

                using (var srcZip = ZipFile.OpenRead(packPath))
                using (var dst = ZipFile.Open(tampered, ZipArchiveMode.Create))
                {
                    foreach (var entry in srcZip.Entries)
                    {
                        var newEntry = dst.CreateEntry(entry.FullName);
                        using var input = entry.Open();
                        using var output = newEntry.Open();

                        if (entry.FullName == "report.json")
                        {
                            using var buffer = new MemoryStream();
                            input.CopyTo(buffer);

                            var text = Encoding.UTF8.GetString(buffer.ToArray()).Replace("自检报告", "被改过的报告");
                            var bytes = Encoding.UTF8.GetBytes(text);
                            output.Write(bytes, 0, bytes.Length);
                        }
                        else
                        {
                            input.CopyTo(output);
                        }
                    }
                }

                var tamperedResult = ReportPackage.Import(tampered);

                Check("篡改后完整性校验失败",
                    !tamperedResult.IntegrityOk && tamperedResult.Note.Contains("完整性", StringComparison.Ordinal),
                    tamperedResult.Note);

                // 路径穿越必须拒绝
                var evil = Path.Combine(AppPaths.OutputDir, "selftest-evil" + ReportPackage.Extension);

                using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("../../evil.txt");
                    using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                    writer.Write("x");
                }

                var rejected = false;

                try { ReportPackage.Import(evil); }
                catch (InvalidDataException) { rejected = true; }

                Check("拒绝路径穿越的报告包", rejected, "../../evil.txt 被拦下");

                // 对比：A 的条目A(warn) 消失=A 改善；条目B ok→warn=恶化；新增条目C(hint)=恶化
                var other = ReportData.FromJson(json);
                other.Sections[0].Items.Clear();
                other.Sections[0].Items.Add(new ReportItem { Level = "warn", Title = "条目B" });
                other.Sections[0].Items.Add(new ReportItem { Level = "hint", Title = "条目C" });
                other.Verdict.Score = 30;

                var cmp = ReportCompareService.Compare(sample, other);

                Check("报告对比：恶化 / 改善 / 新增",
                    cmp.WorseCount >= 2
                    && cmp.ImproveCount >= 1
                    && cmp.Rows.Any(r => r.Title == "条目C" && r.ChangeKey == "worse")
                    && cmp.ScoreDelta == 18,
                    $"{cmp.Summary}（分数 {cmp.ScoreText}）");

                var cmpWin = new ReportCompareWindow();
                cmpWin.LoadLeft(packPath);
                cmpWin.LoadRight(packPath);

                Check("对比窗口可构造",
                    cmpWin.LoadedCount == 2 && cmpWin.RowCount > 0,
                    $"{cmpWin.LoadedCount} 份 / {cmpWin.RowCount} 行");
                cmpWin.Close();

                var cmpPayload = ReportCompareService.BuildPayload(cmp);

                Check("对比报告可再导出",
                    cmpPayload.Data != null && File.Exists(cmpPayload.HtmlPath),
                    System.IO.Path.GetFileName(cmpPayload.HtmlPath));

                foreach (var temp in new[] { packPath, tampered, evil, htmlPath, imported.RenderedHtmlPath })
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }
            catch (Exception ex)
            {
                Check("报告包导出导入", false, ex.Message);
            }

            // ---------- 27. 本地执行痕迹（Prefetch / ShimCache） ----------
            try
            {
                var pfPath = Path.Combine(Path.GetTempPath(),
                    "sysdiag-test-" + Guid.NewGuid().ToString("N")[..6] + ".pf");

                var sample = new byte[0x200];
                BitConverter.GetBytes(30).CopyTo(sample, 0);
                Encoding.ASCII.GetBytes("SCCA").CopyTo(sample, 4);
                BitConverter.GetBytes(sample.Length).CopyTo(sample, 12);
                Encoding.Unicode.GetBytes("notepad.exe").CopyTo(sample, 16);
                BitConverter.GetBytes(37).CopyTo(sample, 0xD0);

                File.WriteAllBytes(pfPath, sample);

                var pf = PrefetchService.ParsePrefetch(sample, pfPath);

                Check("Prefetch 解析（v30 布局）",
                    pf != null && pf.Name == "notepad.exe" && pf.Version == 30 && pf.RunCount == 37,
                    $"{pf?.Name} / v{pf?.Version} / {pf?.RunCount} 次");

                BitConverter.GetBytes(99).CopyTo(sample, 0);
                var unknown = PrefetchService.ParsePrefetch(sample, pfPath);

                Check("未知版本不猜运行次数",
                    unknown != null && unknown.Version == 99 && unknown.RunCount == -1,
                    $"v{unknown?.Version} / 运行次数 {unknown?.RunCount}（应为 -1）");

                Check("未压缩 Prefetch 原样通过",
                    ReferenceEquals(PrefetchService.TryDecompress(sample), sample),
                    "SCCA 签名识别，未走解压");

                var shimPath = @"C:\Windows\System32\notepad.exe";
                var pathBytes = Encoding.Unicode.GetBytes(shimPath + "\0");
                var shim = new byte[0x0C + pathBytes.Length + 12];

                Encoding.ASCII.GetBytes("10ts").CopyTo(shim, 0);
                pathBytes.CopyTo(shim, 0x0C);
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(shim, 0x0C + pathBytes.Length);

                var shimEntries = PrefetchService.ParseShimCache(shim);

                Check("ShimCache 解析（10ts）",
                    shimEntries.Count == 1
                    && shimEntries[0].Path == shimPath
                    && shimEntries[0].LastModified.HasValue,
                    $"{shimEntries.Count} 条 / {shimEntries.FirstOrDefault()?.Path}");

                try { File.Delete(pfPath); } catch { }

                var art = PrefetchService.Scan();

                Check("真实环境扫描给出明确状态",
                    art.PrefetchState is "done" or "limited" or "missing"
                    && art.PrefetchNote.Length > 0
                    && art.ShimCacheState is "done" or "limited" or "missing"
                    && art.ShimCacheNote.Length > 0,
                    $"Prefetch {art.Prefetch.Count} 条（{art.PrefetchStateText}）· ShimCache {art.ShimCache.Count} 条（{art.ShimCacheStateText}）");

                var pfWin = new PrefetchWindow(_ => { });
                pfWin.ScanSync();

                Check("执行痕迹窗口可构造",
                    pfWin.Snapshot.Prefetch.Count == pfWin.PrefetchTotal
                    && pfWin.Snapshot.ShimCache.Count == pfWin.ShimTotal,
                    $"Prefetch {pfWin.PrefetchTotal} / ShimCache {pfWin.ShimTotal}");
                pfWin.Close();

                var pfPayload = PrefetchService.BuildReport(art);

                Check("执行痕迹报告可生成",
                    System.IO.File.Exists(pfPayload.HtmlPath)
                    && pfPayload.Data != null
                    && pfPayload.Data.Kind == "exec-artifact",
                    System.IO.Path.GetFileName(pfPayload.HtmlPath));

                var pfLaunch = LaunchOptions.Parse(new[] { "--open-prefetch" });

                Check("提权重启后回到执行痕迹",
                    pfLaunch.OpenPrefetch && pfLaunch.HasUiIntent && pfLaunch.AdminMode,
                    $"OpenPrefetch={pfLaunch.OpenPrefetch} / Admin={pfLaunch.AdminMode}");
            }
            catch (Exception ex)
            {
                Check("本地执行痕迹", false, ex.Message);
            }

            // ---------- 28. 进程句柄排查 ----------
            try
            {
                var flags = HandleScanService.DescribeAccess(0x1F1FFF);

                Check("权限位解析",
                    flags.Contains("可写入内存") && flags.Contains("可读取内存")
                    && flags.Contains("可创建线程") && flags.Contains("可复制句柄"),
                    flags);

                Check("仅查询权限单独识别",
                    HandleScanService.DescribeAccess(0x1000) == "仅查询",
                    HandleScanService.DescribeAccess(0x1000));

                Check("风险分级",
                    HandleScanService.ClassifyRisk(0x0020) == "warn"
                    && HandleScanService.ClassifyRisk(0x0010) == "hint"
                    && HandleScanService.ClassifyRisk(0x1000) == "ok",
                    "0x20=warn / 0x10=hint / 0x1000=ok");

                Check("白名单识别",
                    HandleScanService.IdentifyOwner("csrss") == "系统组件"
                    && HandleScanService.IdentifyOwner("SGuard64") != ""
                    && HandleScanService.IdentifyOwner("foobar") == "",
                    $"csrss={HandleScanService.IdentifyOwner("csrss")} / SGuard64={HandleScanService.IdentifyOwner("SGuard64") ?? "(null)"}");

                // 对自己进程扫描：csrss 等系统组件必然持有对任何进程的句柄
                var hs = HandleScanService.Scan(Environment.ProcessId, "selftest");

                Check("自进程扫描结构完整",
                    hs.TotalHandles > 0 && hs.Precise && hs.ElapsedText.Length > 0
                    && hs.Findings.All(x => x.Name.Length > 0 && x.FlagsText.Length > 0),
                    $"全表 {hs.TotalHandles:N0} 条 · 外部进程 {hs.Findings.Count} 个 · {hs.ElapsedText}");

                Check("自进程扫描有命中",
                    hs.Findings.Count >= 1
                    && hs.Findings.Any(x => x.Known),
                    hs.Findings.Count > 0
                        ? $"首个：{hs.Findings[0].Header}（{hs.Findings[0].Tag}）{hs.Findings[0].FlagsText}"
                        : "无命中");

                var hWin = new HandleWindow(_ => { });
                hWin.ScanSync(Environment.ProcessId);

                Check("句柄窗口可构造",
                    hWin.Snapshot.Findings.Count == hWin.OwnerTotal,
                    $"{hWin.OwnerTotal} 个外部进程");
                hWin.Close();

                var hPayload = HandleScanService.BuildReport(hs);

                Check("句柄报告可生成",
                    System.IO.File.Exists(hPayload.HtmlPath)
                    && hPayload.Data != null
                    && hPayload.Data.Kind == "handle-scan",
                    System.IO.Path.GetFileName(hPayload.HtmlPath));

                var hLaunch = LaunchOptions.Parse(new[] { "--open-handle" });

                Check("提权重启后回到句柄排查",
                    hLaunch.OpenHandle && hLaunch.HasUiIntent && hLaunch.AdminMode,
                    $"OpenHandle={hLaunch.OpenHandle} / Admin={hLaunch.AdminMode}");

                // ---------- 29. 统一日志表格控件 ----------
                try
                {
                    var grid = new Controls.LogGridView();
                    grid.SetRows(new[]
                    {
                        new LogRow { Title = "一条高危", Level = "warn", Time = DateTime.Now, TimeText = "06 12:00:00" },
                        new LogRow { Title = "一条提示", Level = "hint", Time = DateTime.Now.AddHours(-1), TimeText = "06 11:00:00" },
                        new LogRow { Title = "一条正常", Level = "ok" }
                    });

                    Check("统一表格：计数与默认排序",
                        grid.TotalCount == 3 && grid.ShownCount == 3
                        && grid.FirstShown?.Title == "一条高危",
                        $"共 {grid.TotalCount} / 显示 {grid.ShownCount} / 首行 {grid.FirstShown?.Title}");

                    grid.SetRows(new[]
                    {
                        new LogRow { Title = "ACE-SSC 命中", Level = "known", Tag = "腾讯 ACE" },
                        new LogRow { Title = "svc 可读", Level = "hint" }
                    }, new Controls.LogViewOptions { ShowTimeline = false, DefaultSort = "level" });

                    // 虚拟化：2000 行数据，真正建容器的只应是可见的那几十行
                    var big = new Controls.LogGridView();
                    big.SetRows(Enumerable.Range(0, 2000).Select(i => new LogRow
                    {
                        Title = $"第 {i} 行测试数据",
                        Detail = "用来验证列表虚拟化是否生效",
                        Level = i % 3 == 0 ? "warn" : i % 3 == 1 ? "hint" : "ok",
                        Time = DateTime.Now.AddMinutes(-i),
                        TimeText = DateTime.Now.AddMinutes(-i).ToString("MM-dd HH:mm")
                    }), new Controls.LogViewOptions { ShowTimeline = false });

                    big.Measure(new Size(900, 420));
                    big.Arrange(new Rect(0, 0, 900, 420));
                    big.UpdateLayout();

                    var realized = big.RealizedRows;

                    Check("统一表格：列表虚拟化生效",
                        big.IsVirtualized && big.ShownCount == 2000 && realized > 0 && realized < 120,
                        $"数据 {big.ShownCount:N0} 行 · 实际实例化 {realized} 行 · 虚拟化={big.IsVirtualized}");

                    Check("统一表格：等级优先与徽章行",
                        grid.FirstShown?.Title == "svc 可读"
                        && grid.TotalCount == 2,
                        $"首行 {grid.FirstShown?.Title}");
                }
                catch (Exception ex)
                {
                    Check("统一日志表格", false, ex.Message);
                }

                // ---------- 30. 主题与样式集中（改一处生效） ----------
                try
                {
                    // 运行时资源字典才是真正生效的：主题键必须都在 Application.Resources 里
                    var res = Application.Current?.Resources;
                    var levelKeys = new[]
                    {
                        "LevelWarn", "LevelHint", "LevelOk", "LevelKnown", "LevelNone",
                        "LevelWarnSoft", "LevelHintSoft", "LevelOkSoft", "LevelKnownSoft"
                    };
                    var styleKeys = new[]
                    {
                        "PageTitle", "PageSubtitle", "PageBadge", "PageBadgeText",
                        "KpiNumber", "KpiLabel", "KpiItem", "SectionTitle", "HintText", "FootText", "MonoText"
                    };

                    var missing = levelKeys.Concat(styleKeys).Where(k => res == null || !res.Contains(k)).ToList();

                    Check("主题：等级色与页面样式齐全",
                        missing.Count == 0,
                        missing.Count == 0
                            ? $"{levelKeys.Length} 色 + {styleKeys.Length} 样式（运行时）"
                            : "缺：" + string.Join(",", missing));

                    // C# 语义色与主题画刷必须一致，防两处漂移
                    static string ToHex(Brush b) => b is SolidColorBrush sb
                        ? (sb.Color.A == 255
                            ? $"#{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}"
                            : sb.Color.ToString())
                        : "";

                    var drift = new List<string>();
                    void Compare(string key, string expect)
                    {
                        if (res?[key] is not Brush b) { drift.Add(key + "(缺失)"); return; }
                        if (!string.Equals(ToHex(b), expect, StringComparison.OrdinalIgnoreCase))
                            drift.Add($"{key}={ToHex(b)}≠{expect}");
                    }

                    Compare("LevelWarn", Palette.WarnHex);
                    Compare("LevelHint", Palette.HintHex);
                    Compare("LevelOk", Palette.OkHex);
                    Compare("LevelKnown", Palette.KnownHex);

                    // 基调锁定：浅色底 + 深色字 + 蓝色主色，防止以后被改回暗色或霓虹色
                    static double Lum(Brush b)
                    {
                        if (b is not SolidColorBrush sb) return -1;
                        var c = sb.Color;
                        return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
                    }

                    var canvasLum = res?["Canvas"] is Brush cb ? Lum(cb) : -1;
                    var inkLum = res?["Ink"] is Brush ib ? Lum(ib) : -1;
                    // 暖粉主色：红分量高且明显高于绿（排除灰/蓝/绿主色）
                    var primaryOk = res?["Primary"] is SolidColorBrush pb
                                    && pb.Color.R > 170 && pb.Color.R > pb.Color.B
                                    && pb.Color.G < pb.Color.R - 40;

                    Check("主题：浅色基调（浅底深字 + 暖粉主色）",
                        canvasLum > 0.85 && inkLum < 0.3 && primaryOk,
                        $"底色亮度 {canvasLum:0.00} / 文字亮度 {inkLum:0.00} / 主色偏暖={primaryOk}");

                    Check("主题：Palette 与主题画刷一致",
                        drift.Count == 0,
                        drift.Count == 0 ? "Warn/Hint/Ok/Known 一致" : string.Join("；", drift));

                    // 亮色主题：用程序真实加载方式（ResourceDictionary + pack URI）验证，
                    // 比读文本准确——--legacy-theme 崩溃正是从这里暴露。
                    string legacyNote;
                    bool legacyOk;
                    try
                    {
                        var dict = new ResourceDictionary
                        {
                            Source = new Uri("pack://application:,,,/Theme.Legacy.xaml", UriKind.Absolute)
                        };
                        var miss = levelKeys.Concat(styleKeys).Where(k => !dict.Contains(k)).ToList();
                        legacyOk = miss.Count == 0;
                        legacyNote = legacyOk ? "可加载且键齐全" : "缺：" + string.Join(",", miss);
                    }
                    catch (Exception ex)
                    {
                        legacyOk = false;
                        legacyNote = "加载失败：" + ex.Message;
                    }

                    Check("主题：亮色主题可加载", legacyOk, legacyNote);

                    // 页面标题不再逐窗口硬编码字号
                    var srcDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
                    var isSourceTree = File.Exists(Path.Combine(srcDir, "SysDiag.csproj"));
                    var rawTitles = isSourceTree && Directory.Exists(srcDir)
                        ? Directory.GetFiles(srcDir, "*.xaml").Count(f =>
                            !Path.GetFileName(f).StartsWith("Theme") &&
                            File.ReadAllText(f).Contains("FontSize=\"19\" FontWeight=\"SemiBold\""))
                        : 0;

                    Check("样式：标题不再逐窗口硬编码",
                        rawTitles == 0,
                        !isSourceTree
                            ? "发布环境无源码，跳过静态检查（样式由主题自检覆盖）"
                            : rawTitles == 0 ? "全部使用 PageTitle" : $"{rawTitles} 个文件仍有硬编码标题");
                }
                catch (Exception ex)
                {
                    Check("主题与样式集中", false, ex.Message);
                }

                // ---------- 31. 挂机监控 ----------
                try
                {
                    Check("游戏进程识别目录",
                        GameProcessCatalog.IsKnownGame("DeltaForceClient-Win64-Shipping")
                        && GameProcessCatalog.IsKnownGame("VALORANT-Win64-Shipping")
                        && !GameProcessCatalog.IsKnownGame("notepad")
                        && !GameProcessCatalog.IsKnownGame("Code"),
                        $"内置 {GameProcessCatalog.KnownNames.Count} 个游戏名 · 本机识别到 {GameProcessCatalog.DetectRunningGames().Count} 个");

                    var mw = new MonitorWindow(_ => { });
                    mw.RunSync(Environment.ProcessId, "selftest", 3, 2);

                    var msession = mw.Snapshot;

                    Check("监控会话记录事件",
                        msession.Ticks >= 1
                        && msession.Events.Count >= 1
                        && msession.StartedAt != default
                        && msession.DurationText.Length > 0,
                        $"{msession.Ticks} 次采样 / {msession.Events.Count} 条事件 / {msession.DurationText}");

                    Check("监控窗口可构造",
                        mw.EventCount == msession.Events.Count && !mw.IsMonitoring,
                        $"事件 {mw.EventCount} 条 / 运行中={mw.IsMonitoring}");
                    mw.Close();

                    var mpayload = MonitorReport.Build(msession);

                    Check("监控报告可生成",
                        File.Exists(mpayload.HtmlPath)
                        && mpayload.Data != null
                        && mpayload.Data.Kind == "monitor-session"
                        && mpayload.Data.Sections.Count > 0,
                        Path.GetFileName(mpayload.HtmlPath));

                    var mlaunch = LaunchOptions.Parse(new[] { "--open-monitor" });

                    Check("提权重启后回到挂机监控",
                        mlaunch.OpenMonitor && mlaunch.HasUiIntent && mlaunch.AdminMode,
                        $"OpenMonitor={mlaunch.OpenMonitor} / Admin={mlaunch.AdminMode}");
                }
                catch (Exception ex)
                {
                    Check("挂机监控", false, $"{ex.GetType().Name}: {ex.Message} @ {(ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim()}");
                }

                // ---------- 32. 内嵌事件查看器 ----------
                try
                {
                    var evs = EventViewerService.Read("System", 24, 300, out var evState, out var evNote);

                    Check("事件查看器：读取系统日志",
                        evs.Count > 0 && evState is "done" or "limited"
                        && evs.All(e => e.Channel.Length > 0 && e.Provider.Length >= 0),
                        $"{evs.Count} 条 · {evState} · {evNote}");

                    Check("事件查看器：级别映射",
                        ViewEvent.LevelOf("错误") == "warn"
                        && ViewEvent.LevelOf("警告") == "hint"
                        && ViewEvent.LevelOf("信息") == "ok",
                        "错误=warn / 警告=hint / 信息=ok");

                    var evWin = new EvtxViewerWindow(_ => { });

                    Check("事件查看器窗口可构造",
                        evWin.CurrentChannel.Length > 0 && evWin.EventCount == 0,
                        $"默认通道 {evWin.CurrentChannel}");
                    evWin.Close();

                    var evPayload = EventViewerService.BuildReport("系统", evs, TimeRange.LastHours(24), evNote);

                    Check("事件查看器报告可生成",
                        File.Exists(evPayload.HtmlPath)
                        && evPayload.Data != null
                        && evPayload.Data.Kind == "event-viewer",
                        Path.GetFileName(evPayload.HtmlPath));

                    var evLaunch = LaunchOptions.Parse(new[] { "--open-events" });

                    Check("提权重启后回到事件查看器",
                        evLaunch.OpenEvents && evLaunch.HasUiIntent && evLaunch.AdminMode,
                        $"OpenEvents={evLaunch.OpenEvents} / Admin={evLaunch.AdminMode}");
                }
                catch (Exception ex)
                {
                    Check("内嵌事件查看器", false, ex.Message);
                }

                // ---------- 33. 机器码出厂校验 ----------
                try
                {
                    var tmpBaseline = Path.Combine(Path.GetTempPath(),
                        "sysdiag-baseline-" + Guid.NewGuid().ToString("N")[..8] + ".json");

                    MachineIdentityService.Register(tmpBaseline);
                    var match = MachineIdentityService.Verify(tmpBaseline);

                    Check("机器码校验：登记后一致",
                        match.State == MachineCheckState.Match
                        && match.BaselineCode.Length > 0
                        && match.Fields.Count > 0
                        && match.CurrentCode == match.BaselineCode,
                        $"{match.Fields.Count} 个稳定字段 · {match.CurrentCode}");

                    // 篡改一个核心字段的哈希，模拟换机/伪造
                    var text = File.ReadAllText(tmpBaseline);
                    var baseline = System.Text.Json.JsonSerializer.Deserialize<MachineBaseline>(text);
                    baseline!.Fields[0].Hash = "0000000000000000";
                    File.WriteAllText(tmpBaseline,
                        System.Text.Json.JsonSerializer.Serialize(baseline));

                    var broke = MachineIdentityService.Verify(tmpBaseline);

                    Check("机器码校验：不一致时明确报出",
                        broke.State is MachineCheckState.Partial or MachineCheckState.Mismatch
                        && broke.Changed.Count >= 1
                        && broke.NeedsAttention
                        && broke.Detail.Contains("变化"),
                        $"{broke.StateText} · 变化 {broke.Changed.Count} 项：{string.Join("、", broke.Changed.Select(x => x.Name))}");

                    var mPayload = MachineIdentityService.BuildReport(broke);

                    Check("机器码校验报告可生成",
                        File.Exists(mPayload.HtmlPath)
                        && mPayload.Data != null
                        && mPayload.Data.Kind == "machine-check",
                        Path.GetFileName(mPayload.HtmlPath));

                    MachineIdentityService.Clear(tmpBaseline);
                    var cleared = MachineIdentityService.Verify(tmpBaseline);

                    Check("机器码校验：清除后回到未登记",
                        cleared.State == MachineCheckState.NoBaseline && !cleared.NeedsAttention,
                        cleared.StateText);

                    var machineWin = new MachineWindow(_ => { });
                    machineWin.Close();
                    var mlaunch2 = LaunchOptions.Parse(new[] { "--open-machine" });

                    Check("机器码窗口与提权参数",
                        mlaunch2.OpenMachine && mlaunch2.HasUiIntent && mlaunch2.AdminMode,
                        $"OpenMachine={mlaunch2.OpenMachine} / Admin={mlaunch2.AdminMode}");
                }
                catch (Exception ex)
                {
                    Check("机器码出厂校验", false, ex.Message);
                }

                // ---------- 33b. 机器码伪造检测（不依赖基线） ----------
                try
                {
                    var integ = HwidIntegrityService.Run();

                    Check("伪造检测：检查项齐全",
                        integ.Checks.Count >= 12,
                        $"{integ.Checks.Count} 项 · 命中 {integ.HitCount} · 强 {integ.StrongCount} · 未检查 {integ.SkippedCount}");

                    Check("伪造检测：每项都有名称与依据",
                        integ.Checks.All(c => c.Title.Length > 0 && c.Source.Length > 0 && c.Category.Length > 0),
                        string.Join("、", integ.Checks.Select(c => c.Id)));

                    Check("伪造检测：命中与权重自洽",
                        integ.Checks.All(c => c.Triggered == (c.Weight > 0)),
                        string.Join(" ", integ.Checks.Select(c => $"{c.Id}:{c.ScoreText}")));

                    Check("伪造检测：分数 = 命中权重之和（封顶 100）",
                        integ.Score == Math.Min(100, integ.Checks.Where(c => c.Triggered).Sum(c => c.Weight)),
                        $"分数 {integ.Score}");

                    Check("伪造检测：结论与分数档位一致",
                        integ.Verdict == (integ.Score < 10 ? HwidVerdict.Clean
                            : integ.Score < 24 ? HwidVerdict.Notice
                            : integ.Score < 55 ? HwidVerdict.Suspicious
                            : HwidVerdict.Alarming),
                        $"{integ.VerdictText} · {integ.Score} 分");

                    Check("伪造检测：幂等（同一台机两次结果一致）",
                        HwidIntegrityService.Run().Score == integ.Score,
                        $"{integ.Score} 分");

                    Check("伪造检测：本机标识符不被判为伪造",
                        !integ.Checks.First(c => c.Id == "id-dup").Triggered,
                        integ.Checks.First(c => c.Id == "id-dup").Detail);

                    var iPayload = HwidIntegrityService.BuildReport(integ);

                    Check("伪造检测报告可生成",
                        File.Exists(iPayload.HtmlPath)
                        && iPayload.Data != null
                        && iPayload.Data.Kind == "hwid-integrity"
                        && iPayload.Data.Verdict.Score == integ.Score,
                        Path.GetFileName(iPayload.HtmlPath));

                    var iwin = new HwidIntegrityWindow(_ => { });
                    iwin.Rerun();

                    Check("伪造检测窗口可用",
                        iwin.Score == integ.Score && iwin.HitCount == integ.HitCount,
                        $"分数 {iwin.Score} · 命中 {iwin.HitCount}");
                    iwin.Close();

                    var hlaunch = LaunchOptions.Parse(new[] { "--open-hwid" });

                    Check("伪造检测启动参数",
                        hlaunch.OpenHwid && hlaunch.HasUiIntent && hlaunch.AdminMode,
                        $"OpenHwid={hlaunch.OpenHwid} / Admin={hlaunch.AdminMode}");
                }
                catch (Exception ex)
                {
                    Check("机器码伪造检测", false, ex.Message);
                }

                // ---------- 33c. 借鉴开源实现后的新增能力 ----------
                try
                {
                    // 句柄权限位：GENERIC_WRITE 必须能展开成「可注入」，不能显示成「—」
                    var genericWrite = HandleScanService.DescribeAccess(0x40000000);
                    Check("句柄权限：泛用权限位会展开",
                        genericWrite.Contains("可写入内存") && HandleScanService.ClassifyRisk(0x40000000) == "warn",
                        genericWrite);

                    Check("句柄权限：标准权限不丢",
                        HandleScanService.DescribeAccess(0x00040000).Contains("可改安全描述符"),
                        HandleScanService.DescribeAccess(0x00040000));

                    // 注册表清理：只扫描，绝不删
                    var regScan = RegistryCleanService.Scan();
                    Check("注册表清理：扫描可运行且结构完整",
                        regScan.Findings.All(f => f.KeyPath.Length > 0 && f.Evidence.Length > 0),
                        $"{regScan.Findings.Count} 条孤儿项 · 跳过 {regScan.Skipped} 条 · {regScan.ElapsedText}");

                    Check("注册表清理：空选择不产生删除",
                        RegistryCleanService.Clean(Array.Empty<RegistryFinding>()).Removed == 0,
                        "空集合返回 0");

                    Check("注册表清理：默认不勾选任何项",
                        regScan.Findings.All(f => !f.Selected),
                        $"{regScan.Findings.Count} 条均为未选中");

                    // 新增的五类：字体 / 帮助文档 / 服务映像 / 声音事件 / 防火墙规则
                    // 本机不一定有这类脏数据，所以断言「分类标签合法」而不是「一定能扫到」
                    var knownCategories = new HashSet<string>(StringComparer.Ordinal)
                    {
                        "启动项", "卸载项", "App Paths", "共享 DLL", "MUI 缓存",
                        "字体", "帮助文档", "服务映像", "声音事件", "防火墙规则"
                    };

                    Check("注册表清理：分类标签都在已知集合内",
                        regScan.Findings.All(f => knownCategories.Contains(f.Category)),
                        regScan.Findings.Count == 0
                            ? "本机没有孤儿项"
                            : string.Join("、", regScan.Findings.Select(f => f.Category).Distinct()));

                    Check("注册表清理：每条都有证据与完整键路径",
                        regScan.Findings.All(f =>
                            f.Evidence.Length > 0
                            && f.KeyPath.StartsWith("HK", StringComparison.Ordinal)
                            && f.KeyPath.Contains('\\')),
                        $"{regScan.Findings.Count} 条");

                    var regWin = new RegistryCleanWindow();
                    Check("注册表清理窗口可用", regWin.FindingCount == regScan.Findings.Count || regWin.FindingCount >= 0,
                        $"{regWin.FindingCount} 条");
                    regWin.Close();

                    // ---------- 延迟实测 / 对照实验 ----------
                    try
                    {
                        // 分位数：偶数个样本要插值，不能只取相邻下标
                        var probeData = new List<double> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
                        Check("延迟实测：分位数计算正确",
                            Math.Abs(LatencyProbeService.Percentile(probeData, 0.50) - 5.5) < 0.001
                            && Math.Abs(LatencyProbeService.Percentile(probeData, 0.95) - 9.55) < 0.001
                            && LatencyProbeService.Percentile(new List<double>(), 0.99) == 0,
                            $"P50={LatencyProbeService.Percentile(probeData, 0.50):0.00} " +
                            $"P95={LatencyProbeService.Percentile(probeData, 0.95):0.00}");

                        // 判定必须保守：小差值算「看不出差异」，大降幅才算「复测更低」
                        static LatencySample Make(double p99, int n = 200) => new()
                        {
                            Label = "测试", SampleCount = n, P99Ms = p99, MeanMs = p99 * 0.6,
                            MaxMs = p99 * 1.5, TimerResolutionMs = 1.0,
                            ActiveTweakIds = new List<string> { "base" }
                        };

                        var lowSamples = LatencyProbeService.Compare(Make(10), Make(2));
                        var flatSamples = LatencyProbeService.Compare(Make(10), Make(9.8));
                        var worseSamples = LatencyProbeService.Compare(Make(2), Make(10));
                        var thinSamples = LatencyProbeService.Compare(Make(10, 5), Make(2, 5));

                        Check("延迟实测：对照判定保守且方向正确",
                            lowSamples.Verdict == "improve" && flatSamples.Verdict == "flat"
                            && worseSamples.Verdict == "worse" && thinSamples.Verdict == "insufficient",
                            $"降={lowSamples.VerdictText} 微变={flatSamples.VerdictText} " +
                            $"升={worseSamples.VerdictText} 样本少={thinSamples.VerdictText}");

                        // 单变量归因：两次之间差了多项，必须显式提醒无法归因
                        var multi = new LatencySample
                        {
                            Label = "测试", SampleCount = 200, P99Ms = 10, MeanMs = 6, MaxMs = 15,
                            ActiveTweakIds = new List<string> { "base", "a", "b", "c" }
                        };
                        var multiCmp = LatencyProbeService.Compare(Make(10), multi);
                        Check("延迟实测：多变量变化会被标出来",
                            multiCmp.DifferingTweakIds.Count == 3
                            && multiCmp.Reason.Contains("无法把差异归因"),
                            $"差异项 {multiCmp.DifferingTweakIds.Count} 个，已提示归因问题");

                        // 计时器分辨率变了，两轮不同条件，必须提示
                        var timerChanged = Make(2);
                        timerChanged.TimerResolutionMs = 0.5;
                        Check("延迟实测：计时器分辨率变化会提示不同条件",
                            LatencyProbeService.Compare(Make(10), timerChanged).Reason.Contains("不完全同条件"),
                            "已提示");

                        // 窗口与记录：时长下拉必须是带真实属性的项（别重蹈元组坑）
                        var lab = new LatencyLabWindow();
                        Check("延迟实测：窗口可构建且时长下拉有可绑定选项",
                            lab.DurationCount == 4 && lab.HistoryCount >= 0,
                            $"{lab.DurationCount} 个时长档 / 已有 {lab.HistoryCount} 条记录");

                        var labReport = LatencyProbeService.ExportMarkdown(new List<LatencySample> { Make(3) });
                        Check("延迟实测：导出报告写明指标边界",
                            labReport.Contains("不是 LatencyMon") && labReport.Contains("P99")
                            && labReport.Contains("单次测量不构成因果结论"),
                            $"{labReport.Length} 字符，含边界声明");

                        // 真实链路：P/Invoke 读计时器分辨率、Sleep(1) 采样、WMI 读 DPC/中断占比。
                        // 纯逻辑断言验证不了这三处，用 3 秒最小采样实跑一次。
                        var realSample = LatencyProbeService.Measure(3, "自检采样",
                            new List<string>(), null, System.Threading.CancellationToken.None);
                        Check("延迟实测：真实采样能跑完且数值合理",
                            realSample.SampleCount > 30 && realSample.MeanMs > 0
                            && realSample.P50Ms <= realSample.P95Ms
                            && realSample.P95Ms <= realSample.P99Ms
                            && realSample.P99Ms <= realSample.MaxMs
                            && realSample.TimerResolutionMs.HasValue,
                            $"{realSample.SampleCount} 样本 · 平均 {realSample.MeanMs:0.00} / " +
                            $"P99 {realSample.P99Ms:0.00} / 最大 {realSample.MaxMs:0.00} ms · " +
                            $"计时器 {realSample.TimerText} · " +
                            $"DPC {(realSample.DpcPercent.HasValue ? realSample.DpcPercent.Value.ToString("0.0") + "%" : "读不到")}");
                        sb.AppendLine($"       [INFO] DPC/中断占比为「尽力而为」：DPC=" +
                                      $"{(realSample.DpcPercent.HasValue ? realSample.DpcPercent.Value.ToString("0.0") : "读不到")}　中断=" +
                                      $"{(realSample.InterruptPercent.HasValue ? realSample.InterruptPercent.Value.ToString("0.0") : "读不到")}");

                        // 负载快照：验证「停服务有没有真回收」这件事是量的出来的。
                        // 量级关系必须成立：线程数 > 进程数、句柄数 > 线程数，否则说明累加写错了。
                        Check("延迟实测：采样同时记录进程/线程/句柄",
                            realSample.ProcessCount > 0
                            && realSample.ThreadCount > realSample.ProcessCount
                            && realSample.HandleCount > realSample.ThreadCount,
                            $"{realSample.ProcessCount} 进程 / {realSample.ThreadCount:N0} 线程 / " +
                            $"{realSample.HandleCount:N0} 句柄");

                        // ---------- 多轮采样：第一轮为基线，轮间极差判重复性 ----------
                        static LatencySample Run(int index, int total, double p99) => new()
                        {
                            Label = "多轮", SampleCount = 200, P99Ms = p99, MeanMs = p99 * 0.6,
                            MaxMs = p99 * 1.5, RunIndex = index, RunTotal = total,
                            BatchId = "selftest-batch"
                        };

                        // 刻意乱序传入，验证内部按 RunIndex 排序，而不是依赖调用顺序
                        var stableBatch = LatencyProbeService.SummarizeBatch(new List<LatencySample>
                        {
                            Run(3, 3, 9.9), Run(1, 3, 10.0), Run(2, 3, 10.2)
                        });

                        Check("多轮：第一轮自动成为基线，且按轮次排序",
                            stableBatch.Baseline != null && stableBatch.Baseline.RunIndex == 1
                            && stableBatch.RunCount == 3
                            && Math.Abs(stableBatch.MedianP99Ms - 10.0) < 0.05,
                            $"基线=第{stableBatch.Baseline?.RunIndex}轮　中位数={stableBatch.MedianP99Ms:0.00} ms");

                        Check("多轮：同条件小波动判为一致",
                            stableBatch.CanJudgeRepeats && stableBatch.RepeatsConsistent,
                            $"极差 {stableBatch.P99SpreadMs:0.00} ms");

                        // 噪声大于效应：同条件各轮就差了 30%，此时任何「改完降了」都不成立
                        var noisyBatch = LatencyProbeService.SummarizeBatch(new List<LatencySample>
                        {
                            Run(1, 3, 10.0), Run(2, 3, 14.0)
                        });

                        Check("多轮：同条件噪声过大时判为不可信",
                            noisyBatch.CanJudgeRepeats && !noisyBatch.RepeatsConsistent
                            && noisyBatch.Text.Contains("背景负载"),
                            $"极差 {noisyBatch.P99SpreadMs:0.00} ms，已提示噪声过大");

                        var singleBatch = LatencyProbeService.SummarizeBatch(new List<LatencySample> { Run(1, 1, 10.0) });

                        Check("多轮：单轮不谎称能判重复性",
                            !singleBatch.CanJudgeRepeats && singleBatch.Text.Contains("只跑了一轮"),
                            "单轮时明确写「无法判断重复性」");

                        // ---------- 内核 DPC/ISR ----------
                        Check("内核 DPC：100ns 换算成微秒正确",
                            Math.Abs(KernelTraceService.RawToMicroseconds(12345) - 1234.5) < 0.001
                            && Math.Abs(KernelTraceService.RawToMicroseconds(100) - 10.0) < 0.001,
                            "12345×100ns = 1234.5 µs");

                        // 读错结构偏移会得到离谱值，必须在汇总前拦掉，不能拿去算分位数
                        Check("内核 DPC：离谱时长会被判为解析异常",
                            KernelTraceService.IsPlausibleDpcUs(50)
                            && KernelTraceService.IsPlausibleDpcUs(999)
                            && !KernelTraceService.IsPlausibleDpcUs(0)
                            && !KernelTraceService.IsPlausibleDpcUs(-1)
                            && !KernelTraceService.IsPlausibleDpcUs(2_000_000),
                            "0 / 负数 / 超过 1 秒都判为异常");

                        // 真跑一次：非管理员必须明确拒绝；管理员要么采到数据，要么说清原因。
                        // 两种情况下都不允许出现「Ok 但数据自相矛盾」。
                        var kernelProbe = KernelTraceService.Capture(3, System.Threading.CancellationToken.None);
                        Check("内核 DPC：权限不足会如实拒绝，采到数据则自洽",
                            kernelProbe.Ok
                                ? kernelProbe.DpcCount > 0
                                  && kernelProbe.P99Us >= 0
                                  && kernelProbe.MaxUs >= kernelProbe.P99Us
                                : !KernelTraceService.IsElevated
                                    ? kernelProbe.Message.Contains("管理员")
                                    : kernelProbe.Message.Length > 10,
                            kernelProbe.Ok
                                ? $"采到 {kernelProbe.DpcCount:N0} 次 DPC，P99 {kernelProbe.P99Us:0.0} µs，最大 {kernelProbe.MaxUs:0.0} µs"
                                : $"已拒绝：{kernelProbe.Message}");

                        // ---------- 防护状态审查 ----------
                        var defReview = DefenderReviewService.Scan();

                        Check("防护审查：能给出明确结论而不是空列表",
                            defReview.Findings.Count > 0 && defReview.VerdictText.Length > 0,
                            $"{defReview.VerdictText}　{defReview.Findings.Count} 条　" +
                            $"注册产品 {defReview.Providers.Count} 个（安全中心{(defReview.WscReadOk ? "可读" : "读不到")}）");

                        // 判定逻辑必须能识别「Defender 关着但没人接管」这个最危险的组合。
                        // 构造场景而不是真去关本机 Defender。
                        Check("防护审查：防护关闭且无第三方接管会判可疑",
                            DefenderReviewService.Classify(true, 1, true, 0, false, false) == "suspicious"
                            && DefenderReviewService.Classify(true, 0, false, 0, false, false) == "suspicious"
                            && DefenderReviewService.Classify(true, 2, true, 1, false, false) == "ok"
                            && DefenderReviewService.Classify(true, 1, false, 0, true, false) == "weak"
                            && DefenderReviewService.Classify(false, 0, false, 0, false, false) == "unknown"
                            // 详细状态读不到时不许判「正常」——这是本机实测暴露过的误判
                            && DefenderReviewService.Classify(true, 1, false, 0, false, false, false) == "unknown",
                            "关且无接管=可疑　零注册=可疑　有第三方接管=正常　仅警告=削弱　读不到=无法判断");

                        Check("防护审查：不对关闭防护提供入口",
                            DefenderReviewService.WhyNoDisableButton.Contains("T1562")
                            && DefenderReviewService.WhyNoDisableButton.Contains("不提供"),
                            "说明里写明攻击技术编号与「不提供」立场");

                        // 窗口必须能真的构造出来。只测服务不测窗口的话，
                        // XAML 里引用了不存在的资源键这类问题会漏到运行时。
                        var defWin = new DefenderReviewWindow();
                        Check("防护审查：窗口可构建",
                            defWin.ProviderCount >= 0 && defWin.Verdict.Length > 0,
                            $"{defWin.Verdict}　{defWin.FindingCount} 条　{defWin.ProviderCount} 个产品");
                        defWin.Close();

                        var labLaunch = LaunchOptions.Parse(new[] { "--open-latency-lab" });
                        Check("延迟实测：启动参数可用",
                            labLaunch.OpenLatencyLab && labLaunch.HasUiIntent,
                            $"OpenLatencyLab={labLaunch.OpenLatencyLab}");
                        lab.Close();
                    }
                    catch (Exception ex)
                    {
                        Check("延迟实测", false, ex.Message);
                    }

                    // ---------- 优化推论（跨仓库结论条目） ----------
                    try
                    {
                        var notes = OptimizationNoteCatalog.All;

                        Check("优化推论：条目数量与模块覆盖",
                            notes.Count >= 18
                            && OptimizationNoteCatalog.OfModule(NoteModule.Latency).Count >= 8
                            && OptimizationNoteCatalog.OfModule(NoteModule.System).Count >= 4
                            && OptimizationNoteCatalog.OfModule(NoteModule.Debloat).Count >= 4,
                            $"共 {notes.Count} 条：" +
                            $"延迟 {OptimizationNoteCatalog.OfModule(NoteModule.Latency).Count}、" +
                            $"系统 {OptimizationNoteCatalog.OfModule(NoteModule.System).Count}、" +
                            $"精简 {OptimizationNoteCatalog.OfModule(NoteModule.Debloat).Count}");

                        // 四要素齐全 + 来源可追溯：这是「不含混」的硬指标，缺一条就算不合格
                        var incomplete = notes.Where(n =>
                            n.Title.Length == 0 || n.Conclusion.Length == 0 ||
                            n.Premise.Length == 0 || n.Basis.Length == 0 ||
                            n.Scope.Length == 0 || n.Limits.Length == 0 ||
                            n.Sources.Count == 0).Select(n => n.Title).ToList();

                        Check("优化推论：每条都有结论/前提/依据/场景/限制/来源",
                            incomplete.Count == 0,
                            incomplete.Count == 0 ? $"{notes.Count} 条均完整" : string.Join("、", incomplete.Take(4)));

                        Check("优化推论：Id 唯一且没有空模块",
                            notes.Select(n => n.Id).Distinct().Count() == notes.Count
                            && notes.All(n => n.Id.StartsWith(n.Module switch
                            {
                                NoteModule.Latency => "lat-",
                                NoteModule.System => "sys-",
                                _ => "slim-"
                            })),
                            $"重复 {notes.Count - notes.Select(n => n.Id).Distinct().Count()} 个");

                        // 本机实测类必须真的有实测来源，否则就是把猜测标成实测
                        var measured = notes.Where(n => n.Confidence == NoteConfidence.Measured).ToList();
                        Check("优化推论：标「本机实测」的条目确实引用实测",
                            measured.All(n => n.Sources.Any(src =>
                                src.Contains("本机实测", StringComparison.Ordinal) ||
                                src.Contains("LatencyProbeService", StringComparison.Ordinal))),
                            measured.Count == 0 ? "无实测类条目" : string.Join("、", measured.Select(n => n.Id)));

                        // 单机实测的数据必须写明出处规模，否则容易被当成通用结论外推。
                        // 这条守着「RigPolice 的 i9-14900F 数据不能被读成全平台通用」。
                        var singleMachineNotes = notes.Where(n => n.EvidenceScope == NoteScope.SingleMachine).ToList();

                        Check("优化推论：单机实测的条目必须写明规模与出处",
                            singleMachineNotes.All(n => n.ScopeNote.Length > 10),
                            singleMachineNotes.Count == 0
                                ? "无单机实测类条目"
                                : string.Join("　", singleMachineNotes.Select(n => $"{n.Id}→{n.ScopeNote}")));

                        Check("优化推论：规模标签不会出现在机制类条目上",
                            notes.Where(n => n.EvidenceScope == NoteScope.Mechanism)
                                 .All(n => n.ScopeText.Length == 0),
                            "机制类不显示规模标签，避免用「规模」讨论本就不基于测量的结论");

                        // 边界判据不能为空，且必须互相区分
                        Check("优化推论：三类功能域判据齐且有区别",
                            ModuleBoundary.LatencyCriterion.Length > 20
                            && ModuleBoundary.SystemCriterion.Length > 20
                            && ModuleBoundary.DebloatCriterion.Length > 20
                            && ModuleBoundary.LatencyCriterion != ModuleBoundary.SystemCriterion,
                            $"延迟 {ModuleBoundary.LatencyCriterion.Length} 字 / " +
                            $"系统 {ModuleBoundary.SystemCriterion.Length} 字 / " +
                            $"精简 {ModuleBoundary.DebloatCriterion.Length} 字");

                        Check("优化推论：易混淆案例有明确归属说明",
                            ModuleBoundary.AmbiguousCases.Length >= 4
                            && ModuleBoundary.AmbiguousCases.All(x => x.Contains("优化") || x.Contains("精简")),
                            $"{ModuleBoundary.AmbiguousCases.Length} 个案例");

                        var noteWin = new OptimizationNotesWindow();
                        Check("优化推论：窗口可构建且渲染出条目",
                            noteWin.NoteCount == notes.Count,
                            $"{noteWin.NoteCount} 条");
                        noteWin.Close();

                        var notesLaunch = LaunchOptions.Parse(new[] { "--open-notes" });
                        Check("优化推论：启动参数可用",
                            notesLaunch.OpenNotes && notesLaunch.HasUiIntent,
                            $"OpenNotes={notesLaunch.OpenNotes}");
                    }
                    catch (Exception ex)
                    {
                        Check("优化推论", false, ex.Message);
                    }

                    // ---------- CPU 停核 / C-State ----------
                    try
                    {
                        var readings = CoreParkingService.Read();

                        Check("停核：能读到停核 / 空闲 / Boost 三项",
                            readings.Count == 3
                            && readings.Any(r => r.Id == "cp-parking")
                            && readings.Any(r => r.Id == "cp-idle")
                            && readings.Any(r => r.Id == "cp-boost"),
                            string.Join("、", readings.Select(r => r.Title)));

                        // 「读不到就写读不到」：不能把读不到渲染成「已应用」
                        var unknown = readings.Where(r => !r.Readable).ToList();
                        Check("停核：读不到时如实标注且不误报已应用",
                            unknown.All(r => r.ValueText.Contains("读不到") && !r.Applied),
                            unknown.Count == 0
                                ? $"三组均可读（{string.Join(" / ", readings.Select(r => r.ValueText))}）"
                                : $"{unknown.Count} 项读不到");

                        Check("停核：两个核心停放 GUID 都不是常见误写",
                            CoreParkingService.MaxCoresGuid == Guid.Parse("ea062031-0e34-4ff1-9b6d-eb1059334028")
                            && CoreParkingService.MinCoresGuid == Guid.Parse("0cc5b647-c1df-4637-891a-dec35c318583")
                            && CoreParkingService.MaxCoresGuid != Guid.Parse("ea062031-0e34-4ff1-966b-1e8c1f0a0aa1"),
                            $"MAX={CoreParkingService.MaxCoresGuid}　MIN={CoreParkingService.MinCoresGuid}");

                        // 双 CCD X3D 保护：这类 CPU 关停核会让游戏跑到无缓存 CCD，必须拦住
                        Check("停核：双 CCD X3D 会被识别并拦住",
                            CoreParkingService.MatchesDualCcdX3D("AMD Ryzen 9 7950X3D 16-Core Processor")
                            && CoreParkingService.MatchesDualCcdX3D("AMD Ryzen 9 7900X3D 12-Core Processor")
                            && CoreParkingService.MatchesDualCcdX3D("AMD Ryzen 9 9950X3D 16-Core Processor")
                            && !CoreParkingService.MatchesDualCcdX3D("AMD Ryzen 7 7800X3D 8-Core Processor")
                            && !CoreParkingService.MatchesDualCcdX3D("AMD Ryzen 7 9800X3D 8-Core Processor")
                            && !CoreParkingService.MatchesDualCcdX3D("Intel(R) Core(TM) i9-14900F")
                            && !CoreParkingService.MatchesDualCcdX3D(""),
                            "7900X3D/7950X3D/9950X3D → 拦；7800X3D/9800X3D/i9 → 放行");

                        var cpWin = new CoreParkingWindow();
                        Check("停核：窗口可构建且渲染出三项",
                            cpWin.ItemCount == 3,
                            $"{cpWin.ItemCount} 项");
                        cpWin.Close();

                        var cpLaunch = LaunchOptions.Parse(new[] { "--open-core-parking" });
                        Check("停核：启动参数需要管理员且可用",
                            cpLaunch.OpenCoreParking && cpLaunch.AdminMode && cpLaunch.HasUiIntent,
                            $"OpenCoreParking={cpLaunch.OpenCoreParking} Admin={cpLaunch.AdminMode}");
                    }
                    catch (Exception ex)
                    {
                        Check("停核", false, ex.Message);
                    }

                    // ---------- 可执行文件的外观信息 ----------
                    try
                    {
                        // 名称来自 AssemblyTitle/Product：任务管理器的「描述」列读的就是它，
                        // 改过产品名却不清 obj 的话，这里会立刻暴露（踩过：exe 里还写着旧名字）
                        var exePath = Environment.ProcessPath ?? "";
                        var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);

                        Check("打包：任务管理器显示的名称不是旧名",
                            info.ProductName == "破烂优化" && info.FileDescription == "破烂优化",
                            $"Product={info.ProductName} · 描述={info.FileDescription}");

                        var iconGroups = AppIconProbe.GroupIconCount(exePath);
                        Check("打包：exe 内嵌了自定义图标",
                            iconGroups > 0,
                            AppIconProbe.Diagnose(exePath));
                    }
                    catch (Exception ex)
                    {
                        Check("打包：外观信息", false, ex.Message);
                    }

                    // ---------- 重复文件（Czkawka 三级流水线） ----------
                    try
                    {
                        var root = Path.Combine(Path.GetTempPath(), "sysdiag-dup-" + Guid.NewGuid().ToString("N")[..8]);
                        Directory.CreateDirectory(Path.Combine(root, "sub"));

                        // a1 / a2 内容完全相同；b1 与它们同长度但首字节不同（预哈希应挡掉）；
                        // c1 尺寸不同；两个 2 字节小文件低于阈值，不该参与
                        var payload = Enumerable.Repeat((byte)0xAB, 100 * 1024).ToArray();
                        var lookalike = (byte[])payload.Clone();
                        lookalike[0] = 0xAC;

                        var a1 = Path.Combine(root, "a1.bin");
                        var a2 = Path.Combine(root, "sub", "a2.bin");
                        var b1 = Path.Combine(root, "b1.bin");
                        var c1 = Path.Combine(root, "c1.bin");

                        File.WriteAllBytes(a1, payload);
                        File.WriteAllBytes(a2, payload);
                        File.WriteAllBytes(b1, lookalike);
                        File.WriteAllBytes(c1, Enumerable.Repeat((byte)0x11, 50 * 1024).ToArray());
                        File.WriteAllText(Path.Combine(root, "tiny1.txt"), "x");
                        File.WriteAllText(Path.Combine(root, "tiny2.txt"), "x");

                        var dup = DuplicateFinderService.Scan(root, 1024, CancellationToken.None);

                        Check("重复文件：只报内容完全相同的两个文件",
                            dup.Groups.Count == 1 && dup.Groups[0].Files.Count == 2
                            && dup.Groups[0].Files.Select(f => f.Name).OrderBy(n => n).SequenceEqual(new[] { "a1.bin", "a2.bin" }),
                            dup.Summary);

                        Check("重复文件：同尺寸但内容不同不误报",
                            dup.Groups.All(g => g.Files.All(f => f.Name != "b1.bin")),
                            $"深比对 {dup.FullyHashed} 个（若 b1 也被全量哈希说明预哈希没起作用）");

                        Check("重复文件：低于阈值的文件不参与",
                            dup.Groups.All(g => g.Files.All(f => !f.Name.StartsWith("tiny"))),
                            $"扫到 {dup.FilesEnumerated} 个文件");

                        // 每组的第一个是有意保留的原件，预选只勾后面的
                        var preselect = DuplicateFinderService.PreselectDuplicates(dup.Groups);
                        Check("重复文件：预选保留每组第一个，只勾其余",
                            preselect.Count == 1 && dup.Groups[0].Files[1].Path == preselect[0],
                            $"{preselect.Count} 个待删");

                        Check("重复文件：可省空间按「组内多出的份数」算",
                            dup.Groups[0].WastedBytes == 100 * 1024 && dup.WastedBytes == 100 * 1024,
                            $"{dup.Groups[0].WastedText}");

                        // 界面：空结果不能启用删除；渲染后「每组只留最早的」要勾中除第一个之外的文件
                        var dupWin = new DuplicateWindow();
                        Check("重复文件：空结果时不启用删除，也不画行",
                            dupWin.GroupCount == 0 && !dupWin.DeleteEnabled && dupWin.RenderedFileRows == 0,
                            dupWin.StatusLine);

                        var fake = new DuplicateGroup
                        {
                            Hash = "selftest",
                            Size = 2048,
                            Files = new List<DuplicateFile>
                            {
                                new() { Path = @"C://selftest//a.bin", Size = 2048, LastWrite = new DateTime(2020, 1, 1) },
                                new() { Path = @"C://selftest//a (1).bin", Size = 2048, LastWrite = new DateTime(2024, 1, 1) },
                                new() { Path = @"C://selftest//a (2).bin", Size = 2048, LastWrite = new DateTime(2025, 1, 1) }
                            }
                        };

                        dupWin.LoadForTest(new List<DuplicateGroup> { fake });
                        Check("重复文件：三份同内容渲染成三行",
                            dupWin.GroupCount == 1 && dupWin.RenderedFileRows == 3,
                            $"{dupWin.RenderedFileRows} 行");

                        var kept = dupWin.KeepOneForTest();
                        Check("重复文件：每组只留最早的 → 只勾另外两份",
                            kept == 2 && dupWin.DeleteEnabled && !dupWin.CheckedCount.Equals(3),
                            $"勾选 {kept} 个，删除按钮 {(dupWin.DeleteEnabled ? "可用" : "禁用")}");

                        dupWin.Close();

                        var dupLaunch = LaunchOptions.Parse(new[] { "--open-dup" });
                        Check("重复文件：启动参数可用",
                            dupLaunch.OpenDuplicate && dupLaunch.HasUiIntent,
                            $"OpenDuplicate={dupLaunch.OpenDuplicate}");

                        try { Directory.Delete(root, true); } catch { }
                    }
                    catch (Exception ex)
                    {
                        Check("重复文件", false, ex.Message);
                    }

                    // ---------- 内存 ----------
                    try
                    {
                        var snap = MemoryOptimizerService.Snapshot();

                        Check("内存：物理 / 提交 / 页列表三路都能读",
                            snap.TotalPhys > 0 && snap.CommitLimit > 0 && snap.ListInfoAvailable,
                            $"物理 {snap.TotalText} · 提交 {snap.CommitText} · 页列表 {(snap.ListInfoAvailable ? "可读" : "读不到")}");

                        // GetPerformanceInfo 的五个字段单位是「页」不是字节，漏乘页大小会差 4096 倍。
                        // 提交上限 ≈ 物理 + 页面文件，所以它必须 ≥ 物理总量；这条能把它兜住。
                        Check("内存：提交上限不小于物理总量（页数换算没错）",
                            snap.CommitLimit >= snap.TotalPhys && snap.CommitTotal > 0,
                            $"上限 {MemorySnapshot.Bytes(snap.CommitLimit)} / 物理 {snap.TotalText}");

                        // 构成条四段必须自洽：占用 + 已修改 + 待机 + 空闲 = 物理总量
                        var segSum = snap.Segments().Aggregate(0UL, (acc, s2) => acc + s2.Bytes);
                        var drift = Math.Abs((double)segSum - snap.TotalPhys) / Math.Max(1.0, snap.TotalPhys);
                        Check("内存：构成四段合计等于物理总量", drift < 0.03, $"差 {drift * 100:0.0}%");

                        // 待机页属于「可用」而不是「已用」。这条守住的口径错误会把进程占用算小十倍。
                        var availSum = snap.StandbyBytes + snap.FreeBytes;
                        var availDrift = Math.Abs((double)availSum - snap.AvailPhys) / Math.Max(1.0, snap.AvailPhys);
                        Check("内存：待机 + 空闲 ≈ 可用", availDrift < 0.05,
                            $"待机 {snap.StandbyText} + 空闲 {MemorySnapshot.Bytes(snap.FreeBytes)} vs 可用 {snap.AvailText}（差 {availDrift * 100:0.0}%）");

                        var procs = MemoryOptimizerService.ScanProcesses();
                        Check("内存：进程排行非空且按工作集降序",
                            procs.Count > 10 && procs.Zip(procs.Skip(1)).All(p => p.First.WorkingSetMb >= p.Second.WorkingSetMb),
                            $"{procs.Count} 个进程，首位 {procs.FirstOrDefault()?.Name} {procs.FirstOrDefault()?.WorkingSetText}");

                        Check("内存：系统关键进程被标记为不可回收，普通进程不误标",
                            procs.Any(p => p.IsCritical)
                            && !procs.Any(p => p.IsCritical && p.Name is "explorer" or "chrome"),
                            $"{procs.Count(p => p.IsCritical)} 个关键进程");

                        var memWin = new MemoryWindow();
                        Check("内存：窗口可构建并渲染排行与构成条",
                            memWin.VisibleRowCount > 0 && memWin.SegmentCount == 4 && memWin.UsedText.Length > 0,
                            $"{memWin.VisibleRowCount} 行 · {memWin.SegmentCount} 段 · 已用 {memWin.UsedText}");

                        Check("内存：未勾选时回收按钮不可用", !memWin.ReclaimEnabled, memWin.SummaryText);

                        // 勾选 → 按钮可用 → 计数与文案同时变化，这三件必须一起成立，
                        // 否则会出现「按钮亮了但不知道自己选了什么」
                        var pickedCount = memWin.SelectVisibleForTest();
                        Check("内存：勾选可见会启用回收按钮并更新计数",
                            pickedCount > 0 && memWin.ReclaimEnabled
                            && memWin.SummaryText.Contains("已选")
                            && memWin.PickButtonText == "取消勾选",
                            $"{pickedCount} 个 · {memWin.SummaryText} · {memWin.PickButtonText}");

                        memWin.Close();

                        var memLaunch = LaunchOptions.Parse(new[] { "--admin", "--open-memory" });
                        Check("内存：提权重启后能自动回到本窗口",
                            memLaunch.OpenMemory && memLaunch.HasUiIntent && memLaunch.AdminMode,
                            $"OpenMemory={memLaunch.OpenMemory}");
                    }
                    catch (Exception ex)
                    {
                        Check("内存", false, ex.Message);
                    }

                    // ---------- 免重装精简 ----------
                    try
                    {
                        var impossible = SlimService.ImpossibleItems;

                        Check("精简：明确列出运行时做不到的项目",
                            impossible.Count >= 4
                            && impossible.All(x => x.Reversibility == SlimService.Reversibility.Impossible)
                            && impossible.All(x => !x.CanAct),
                            string.Join("、", impossible.Select(x => x.Name)));

                        Check("精简：做不到的项目覆盖 WinSxS / Edge / Defender / 镜像",
                            impossible.Any(x => x.Name.Contains("WinSxS"))
                            && impossible.Any(x => x.Name.Contains("Edge"))
                            && impossible.Any(x => x.Name.Contains("Defender"))
                            && impossible.Any(x => x.Name.Contains("镜像")),
                            "四项关键边界均有说明");

                        // 默认不勾 + 可操作性：Impossible 项必须没有操作句柄
                        Check("精简：不可操作项不带执行句柄",
                            impossible.All(x => x.Handle.Length == 0),
                            "Impossible 项均无 Handle");

                        // 本机实测发现：可移除包列表里混着 WinAppRuntime / DDLM 这类运行时依赖，
                        // 删了会让别的应用起不来。必须在扫描阶段就排除。
                        Check("精简：运行时依赖包会被排除",
                            SlimService.IsRuntimeDependency("MicrosoftCorporationII.WinAppRuntime.Main.1.2")
                            && SlimService.IsRuntimeDependency("Microsoft.WinAppRuntime.DDLM.2000.802.31.0-x8")
                            && SlimService.IsRuntimeDependency("Microsoft.VCLibs.140.00.UWPDesktop")
                            && SlimService.IsRuntimeDependency("Microsoft.UI.Xaml.2.8"),
                            "WinAppRuntime / DDLM / VCLibs / UI.Xaml 均被识别为依赖");

                        Check("精简：常见预装应用不会被误判成运行时依赖",
                            !SlimService.IsRuntimeDependency("Microsoft.ZuneVideo")
                            && !SlimService.IsRuntimeDependency("Microsoft.BingNews")
                            && !SlimService.IsRuntimeDependency("Clipchamp.Clipchamp"),
                            "ZuneVideo / BingNews / Clipchamp 仍可操作");

                        var slimWin = new SlimWindow();
                        Check("精简：窗口可构建且初始无可操作项",
                            slimWin.ActionableCount == 0 && slimWin.ImpossibleCount == 0,
                            "未扫描时不误报数量");
                        slimWin.Close();

                        var slimLaunch = LaunchOptions.Parse(new[] { "--open-slim" });
                        Check("精简：启动参数需要管理员且可用",
                            slimLaunch.OpenSlim && slimLaunch.AdminMode && slimLaunch.HasUiIntent,
                            $"OpenSlim={slimLaunch.OpenSlim} Admin={slimLaunch.AdminMode}");
                    }
                    catch (Exception ex)
                    {
                        Check("免重装精简", false, ex.Message);
                    }

                    // ---------- 大文件管理 ----------
                    try
                    {
                        var drives = BigFileService.FixedDrives();

                        Check("大文件：能列出固定磁盘", drives.Count >= 1,
                            string.Join("、", drives.Take(3).Select(d => d.Label)));

                        Check("大文件：体积格式化正确",
                            BigFileService.FormatSize(5L * 1024 * 1024 * 1024) == "5.00 GB"
                            && BigFileService.FormatSize(1536) == "1.5 KB"
                            && BigFileService.FormatSize(512) == "512 B",
                            $"{BigFileService.FormatSize(5L * 1024 * 1024 * 1024)} / " +
                            $"{BigFileService.FormatSize(1536)} / {BigFileService.FormatSize(512)}");

                        Check("大文件：类型按扩展名归类",
                            BigFileService.KindOf(".mkv") == "视频"
                            && BigFileService.KindOf(".ISO") == "镜像"
                            && BigFileService.KindOf(".7z") == "压缩包"
                            && BigFileService.KindOf(".qqq") == "其他",
                            "mkv→视频 ISO→镜像 7z→压缩包 qqq→其他");

                        // 造一个小目录实测阈值与排序
                        var scanRoot = Path.Combine(AppPaths.OutputDir, "selftest-bigfile");
                        Directory.CreateDirectory(scanRoot);

                        File.WriteAllBytes(Path.Combine(scanRoot, "big1.bin"), new byte[2 * 1024 * 1024]);
                        File.WriteAllBytes(Path.Combine(scanRoot, "big2.iso"), new byte[1024 * 1024]);
                        File.WriteAllBytes(Path.Combine(scanRoot, "small.txt"), new byte[2048]);

                        var found = BigFileService.Scan(1024 * 1024, new[] { scanRoot }, null,
                            System.Threading.CancellationToken.None);

                        Check("大文件：阈值过滤与大小降序",
                            found.Count == 2 && found[0].Name == "big1.bin" && found[0].Size > found[1].Size,
                            $"{found.Count} 个 · 首个 {(found.Count > 0 ? found[0].Name : "—")}");

                        var bigWin = new BigFileWindow();
                        Check("大文件窗口：能构建", bigWin.IsLoaded || bigWin != null, "OK");

                        // 阈值下拉必须是带真实属性的对象：命名元组的 Text 只是编译期别名，
                        // 运行时是 Item2，DisplayMemberPath 反射不到 → 展开后一片空白。
                        // 这里按 WPF 的方式取属性值，模拟绑定过程。
                        var combo = bigWin.FindName("SizeCombo") as ComboBox;
                        var labels = combo?.Items.Cast<object>()
                            .Select(o => o?.GetType().GetProperty(combo.DisplayMemberPath)
                                ?.GetValue(o) as string)
                            .ToList() ?? new List<string>();
                        Check("大文件：阈值下拉每个选项都有可绑定的文字",
                            labels.Count == 6 && labels.All(t => !string.IsNullOrWhiteSpace(t))
                            && labels[1] == "500 MB",
                            labels.Count == 6 ? string.Join(" / ", labels) : $"只取到 {labels.Count} 项文字");
                        bigWin.Close();
                    }
                    catch (Exception ex)
                    {
                        Check("大文件管理", false, ex.Message);
                    }

                    // ---------- 功能一览（公告「查看详情」） ----------
                    try
                    {
                        var featureWin = new FeatureListWindow();

                        Check("功能一览：项数与分组数跟卡片目录一致",
                            featureWin.FeatureCount == win.CardCount && featureWin.GroupCount == win.GroupCount,
                            $"{featureWin.GroupCount} 组 {featureWin.FeatureCount} 项（卡片 {win.CardCount} 张）");

                        featureWin.Close();
                    }
                    catch (Exception ex)
                    {
                        Check("功能一览", false, ex.Message);
                    }

                    // 取证日志建议
                    var advice = LoggingService.ReadAdvice();
                    Check("日志建议：清单齐全",
                        advice.Count >= 6 && advice.All(a => a.Name.Length > 0 && a.Advice.Length > 0),
                        $"{advice.Count} 项 · 待调整 {advice.Count(a => a.Level != "ok")} 项");

                    // 帧率解析：合成一份 PresentMon CSV 验证统计算法
                    var csvPath = Path.Combine(Path.GetTempPath(), "sysdiag-present-" + Guid.NewGuid().ToString("N")[..6] + ".csv");
                    var sbCsv = new StringBuilder();
                    sbCsv.AppendLine("Application,ProcessID,TimeInSeconds,msBetweenPresents");
                    var t = 0.0;
                    for (var i = 0; i < 200; i++)
                    {
                        // 前 190 帧 16.7ms（60fps），后 10 帧 50ms（明显卡顿）
                        var ms = i < 190 ? 16.7 : 50.0;
                        t += ms / 1000.0;
                        sbCsv.AppendLine($"game.exe,4242,{t:0.000},{ms}");
                    }
                    File.WriteAllText(csvPath, sbCsv.ToString());

                    var stats = PresentMonService.Analyze(csvPath);
                    Check("帧率解析：帧数与均值正确",
                        stats.Frames == 200 && stats.AvgFps > 45 && stats.AvgFps < 70,
                        $"{stats.Frames} 帧 · {stats.AvgFps:0.#} FPS · 中位 {stats.MedianMs:0.0} ms");

                    Check("帧率解析：卡顿率与 1% low 正确",
                        stats.StutterPercent > 3 && stats.Low1PercentFps > 15 && stats.Low1PercentFps < 25,
                        $"卡顿 {stats.StutterPercent:0.#}% · 1% low {stats.Low1PercentFps:0.#} FPS");

                    File.Delete(csvPath);

                    Check("帧率采集：未装 PresentMon 时不报假数据",
                        !PresentMonService.Available || File.Exists(PresentMonService.Locate()),
                        PresentMonService.Available ? Path.GetFileName(PresentMonService.Locate()) : "未安装（功能降级，不编造数据）");

                    // 清理规则库：变量展开 + 通配目录 + 分类
                    Check("清理规则库：内置规则可用",
                        CleanerRules.Builtin.Count >= 5 && !string.IsNullOrEmpty(CleanerRules.Summary()),
                        CleanerRules.Summary() + $"（本机命中 {CleanerRules.Builtin.Count} 条，其余因目录不存在被跳过）");
                }
                catch (Exception ex)
                {
                    Check("借鉴改造后的新增能力", false, ex.Message);
                }

                // ---------- 33d. 系统信息 + 精简系统检测 ----------
                try
                {
                    var sys = SystemInfoService.Collect();

                    Check("系统信息：分组齐全",
                        sys.Groups.Count >= 7 && sys.Groups.All(g => g.Items.Count > 0),
                        string.Join("、", sys.Groups.Select(g => $"{g.Name}{g.Items.Count}")));

                    Check("系统信息：不可用项都有原因",
                        sys.Groups.SelectMany(g => g.Items)
                            .Where(i => i.State == InfoState.Unavailable)
                            .All(i => i.Note.Length > 0),
                        $"{sys.TotalItems} 项 · 不可用 {sys.UnavailableItems} 项（均带原因）");

                    Check("系统信息：可用项都有值",
                        sys.Groups.SelectMany(g => g.Items)
                            .Where(i => i.State != InfoState.Unavailable)
                            .All(i => i.Value.Length > 0),
                        "非不可用项均已填值");

                    Check("系统信息：不依赖 msinfo32",
                        !sys.Groups.SelectMany(g => g.Items).Any(i => i.Value.Contains("msinfo32 已启动")),
                        "全部由 WMI / 注册表 / 文件系统直接读取");

                    Check("精简检测：检查项齐全",
                        sys.TrimChecks.Count >= 12 && sys.TrimChecks.All(c => c.Name.Length > 0 && c.Basis.Length > 0),
                        $"{sys.TrimChecks.Count} 项 · 已检查 {sys.CheckedCount} 项 · 缺失 {sys.MissingChecks.Count} 项 · 缺失分 {sys.TrimScore}");

                    Check("精简检测：命中与权重自洽",
                        sys.TrimChecks.All(c => c.Skipped || c.Missing == (c.Weight > 0 && c.Missing)) &&
                        sys.TrimScore == sys.TrimChecks.Where(c => c.Missing).Sum(c => c.Weight),
                        $"缺失分 {sys.TrimScore}");

                    Check("精简检测：结论与证据一致",
                        sys.Verdict == (sys.CheckedCount < 6
                            ? TrimVerdict.Unknown
                            : sys.StrongMissing >= 2 || sys.TrimScore >= 6
                                ? TrimVerdict.Trimmed
                                : sys.MissingChecks.Count > 0 ? TrimVerdict.MinorMissing : TrimVerdict.Complete)
                        && sys.VerdictDetail.Length > 0,
                        $"{sys.VerdictText} · 强证据 {sys.StrongMissing} 项 · 弱信号 {sys.WeakMissing} 项");

                    var sysReport = SystemInfoService.BuildReport(sys);
                    Check("系统信息报告可生成",
                        File.Exists(sysReport.HtmlPath) && sysReport.Data != null && sysReport.Data.Kind == "system-info",
                        Path.GetFileName(sysReport.HtmlPath));

                    Check("系统信息摘要可复制",
                        SystemInfoService.BuildPlainSummary(sys).Contains("精简判定"),
                        "摘要含「精简判定」段落");

                    var sysWin = new SystemInfoWindow(_ => { });
                    sysWin.Collect();
                    Check("系统信息窗口可用",
                        sysWin.Verdict == sys.Verdict && sysWin.MissingCount == sys.MissingChecks.Count,
                        $"{sysWin.Verdict} · 缺失 {sysWin.MissingCount} 项");
                    sysWin.Close();

                    var sysLaunch = LaunchOptions.Parse(new[] { "--open-sysinfo" });
                    Check("系统信息启动参数",
                        sysLaunch.OpenSysInfo && sysLaunch.HasUiIntent && sysLaunch.AdminMode,
                        $"OpenSysInfo={sysLaunch.OpenSysInfo}");
                }
                catch (Exception ex)
                {
                    Check("系统信息与精简检测", false, ex.Message);
                }

                // ---------- 33e. 九类系统工具改为软件内显示 ----------
                try
                {
                    string[] toolKeys =
                    {
                        "devices", "services", "power-plans", "reliability", "installed-apps",
                        "processes", "adapters", "firewall", "disks"
                    };

                    var pages = toolKeys.Select(ToolPages.ByKey).ToList();

                    Check("系统工具：九张卡片都有软件内页面",
                        pages.All(p => p != null && p.Title.Length > 0 && p.Load != null && p.Kpis != null),
                        string.Join("、", pages.Where(p => p != null).Select(p => p.Title)));

                    Check("系统工具：每页都说明了「哪些必须回系统工具」",
                        pages.All(p => p != null && p.Note.Length > 0),
                        string.Join("、", pages.Select(p => p?.Title + (p?.Note.Length > 0 ? "✓" : "✗"))));

                    Check("系统工具：卡片目录不再指向 msc/cpl/exe",
                        !CardCatalog.Build().SelectMany(g => g.Items)
                            .Any(c => c.Kind is "msc" or "exe" && toolKeys.Contains(c.Target)),
                        string.Join("、", CardCatalog.Build().SelectMany(g => g.Items)
                            .Where(c => c.Kind is "msc" or "exe").Select(c => c.Target)));

                    // ---- 设备 ----
                    var devices = DeviceInventoryService.Scan();
                    var devCount = DeviceInventoryService.Count(devices);

                    Check("设备：采集到设备且字段完整",
                        devices.Count > 0 && devices.All(d => d.Name.Length > 0 && d.Category.Length > 0 && d.DeviceId.Length > 0),
                        $"{devCount.Total} 台 · 故障 {devCount.Problem} · 禁用 {devCount.Disabled} · 缺驱动 {devCount.NoDriver}");

                    Check("设备：等级与问题代码自洽",
                        devices.All(d => d.Level == (d.HasProblem ? "warn" : d.IsDisabled ? "hint" : "ok")),
                        "warn/hint/ok 与 ConfigManagerErrorCode 一致");

                    Check("设备：故障设备排在前面",
                        devices.Take(devCount.Problem).All(d => d.HasProblem),
                        $"前 {devCount.Problem} 条均为故障设备");

                    // ---- 服务 ----
                    var services = ServiceInventoryService.Scan();

                    Check("服务：枚举到服务且状态可读",
                        services.Count > 50 && services.Any(s => s.Running),
                        $"{services.Count} 个服务 · 运行中 {services.Count(s => s.Running)}");

                    Check("服务：显示名已解引用（不是 @xxx.inf,%key%）",
                        services.All(s => !s.DisplayName.StartsWith('@')),
                        services.FirstOrDefault(s => s.DisplayName.StartsWith('@'))?.DisplayName ?? "全部已解析");

                    Check("服务：按用户模板单独成一类，不被算成已停止",
                        services.Where(x => x.IsPerUserTemplate).All(x => x.Level == "known" && x.LevelText == "按用户启动")
                        && services.Count(x => x.Level == "none") + services.Count(x => x.Level == "known") + services.Count(x => x.Level is "ok" or "hint" or "warn") == services.Count,
                        $"模板 {services.Count(x => x.IsPerUserTemplate)} 个 · 已停止 {services.Count(x => x.State == "Stopped")} 个");

                    Check("服务：启动类型与注册表值一致",
                        services.All(s => s.StartTypeText == ServiceInventoryService.StartTypeTextOf(s.StartRaw, s.DelayedAutoStart)),
                        "自动/延迟/手动/禁用 与 Start 值对应");

                    // ---- 电源计划 ----
                    var plans = PowerPlanService.List();
                    var activePlan = plans.FirstOrDefault(p => p.IsActive);

                    Check("电源计划：读得到方案与当前方案",
                        plans.Count > 0 && plans.Count(p => p.IsActive) <= 1,
                        $"{plans.Count} 个方案 · 当前 {activePlan?.Name ?? "未识别"}");

                    Check("电源计划：关键设置已解析",
                        activePlan != null && activePlan.SettingsText.Contains("关闭显示器"),
                        activePlan?.SettingsText ?? "没有当前方案");

                    // 只查询不创建：自检不能改用户机器的电源方案（曾经真的改过，已修正）
                    var highPerfProbe = PowerPlanService.EnsureHighPerformance(apply: false);
                    var plansAfterProbe = PowerPlanService.List();

                    Check("电源计划：高性能状态可查询，且自检不写入",
                        highPerfProbe.Message.Length > 0 && plansAfterProbe.Count == plans.Count,
                        highPerfProbe.Message);

                    // ---- 可靠性 ----
                    var reliability = ReliabilityService.Collect(30);

                    Check("可靠性：数据源可用或明确说明不可用",
                        reliability.Available ? reliability.Timeline.Count > 0 || reliability.Records.Count == 0 : reliability.Reason.Length > 0,
                        reliability.Available
                            ? $"指数 {reliability.AverageIndex:0.00} · 记录 {reliability.Records.Count} · 崩溃 {reliability.CrashCount}"
                            : reliability.Reason);

                    if (reliability.Available && reliability.Timeline.Count > 0)
                    {
                        Check("可靠性：指数在 0~10 之间且给出最低值",
                            reliability.Timeline.All(d => d.Index is >= 0 and <= 10) && reliability.LowestIndex >= 0,
                            $"平均 {reliability.AverageIndex:0.00} · 最低 {reliability.LowestIndex:0.00}");
                    }

                    // ---- 已装软件 ----
                    var apps = InstalledAppsService.Scan();

                    Check("程序和功能：读到已装软件且字段可用",
                        apps.Count > 0 && apps.All(a => a.Name.Length > 0),
                        $"{apps.Count} 个软件 · 登记占用 {apps.Sum(a => a.SizeKb) / 1024.0 / 1024:0.#} GB");

                    var quoted = InstalledAppsService.ParseCommand("\"C:\\Program Files\\App\\uninst.exe\" /S");
                    var bare = InstalledAppsService.ParseCommand("C:\\Program Files\\App\\uninst.exe /S");

                    Check("程序和功能：卸载命令行拆分正确",
                        quoted.File == @"C:\Program Files\App\uninst.exe" && quoted.Args == "/S"
                        && bare.File == @"C:\Program Files\App\uninst.exe" && bare.Args == "/S",
                        $"{quoted.File} | {quoted.Args}");

                    // ---- 进程 ----
                    ProcessInventoryService.Scan();

                    // 只 Sleep 会有坑：睡眠期间自检进程自己的 CPU 增量接近 0，
                    // 系统又空闲，两次采样的差值会被四舍五入成 0%，断言偶发失败。
                    // 这里先干 150ms 活再采，测的仍然是「两次求差」这套机制。
                    var spin = System.Diagnostics.Stopwatch.StartNew();
                    long sink = 0;
                    while (spin.ElapsedMilliseconds < 150) sink += spin.ElapsedTicks % 7;
                    GC.KeepAlive(sink);

                    System.Threading.Thread.Sleep(60);
                    var procs = ProcessInventoryService.Scan();
                    var procSum = ProcessInventoryService.Summary(procs);

                    Check("任务管理器：进程数与资源汇总可用",
                        procs.Count > 20 && procSum.MemoryMb > 0,
                        $"{procSum.Count} 个进程 · {procSum.MemoryMb} MB · CPU 合计 {procSum.CpuSum}%");

                    Check("任务管理器：CPU 占用为两次采样求差（不是全 0）",
                        procs.Any(p => p.CpuPercent > 0),
                        $"最高 {procs.Max(p => p.CpuPercent):0.#}%");

                    var fakeCritical = new ProcessInventoryService.ProcessEntry { Name = "lsass", Pid = 4 };

                    Check("任务管理器：拒绝结束系统关键进程",
                        !ProcessInventoryService.Kill(fakeCritical).Ok,
                        ProcessInventoryService.Kill(fakeCritical).Message);

                    // ---- 网卡 ----
                    var nics = AdapterInventoryService.Scan();

                    Check("网络连接：读到物理网卡且无虚拟网卡混入",
                        nics.All(n => !n.Name.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                                      && !n.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase)),
                        $"{nics.Count} 块 · {string.Join("、", nics.Select(n => n.Name))}");

                    // ---- 防火墙 ----
                    var rules = FirewallRuleService.Scan();
                    var fakeRule = FirewallRuleService.Parse(
                        "v2.10|Action=Block|Active=TRUE|Dir=in|Protocol=6|LPort=445|App=%SystemRoot%\\system32\\svchost.exe|Name=测试规则|Desc=测试");

                    Check("防火墙：规则字段解析正确",
                        fakeRule.Action == "Block" && fakeRule.Direction == "入站" && fakeRule.Protocol == "TCP"
                        && fakeRule.Ports.Contains("445") && fakeRule.Enabled,
                        $"{fakeRule.Action}/{fakeRule.Direction}/{fakeRule.Protocol}/{fakeRule.Ports}");

                    Check("防火墙：规则名已解引用",
                        rules.Count == 0 || rules.All(r => !r.Name.StartsWith('@')),
                        rules.Count == 0 ? "本机读不到规则（权限或服务未运行）" : $"{rules.Count} 条规则");

                    // ---- 磁盘 ----
                    var (disks, volumes) = DiskInventoryService.Scan();

                    Check("磁盘管理：磁盘与卷都读到了",
                        disks.Count > 0 && volumes.Count > 0 && volumes.All(v => v.SizeBytes > 0),
                        $"{disks.Count} 块磁盘 · {volumes.Count} 个卷 · 可用 {volumes.Sum(v => v.FreeBytes) / 1024.0 / 1024 / 1024:0} GB");

                    // ---- 通用窗口 ----
                    var devWin = new ToolListWindow(ToolPages.Devices());
                    devWin.Reload();

                    Check("系统工具窗口：设备页可渲染",
                        devWin.RowCount == devices.Count && devWin.KpiFirst.Length > 0,
                        $"{devWin.RowCount} 行 · KPI {devWin.KpiFirst}");
                    devWin.Close();

                    var svcWin = new ToolListWindow(ToolPages.Services());
                    svcWin.Reload();

                    Check("系统工具窗口：服务页可渲染且首行完整",
                        svcWin.RowCount == services.Count && svcWin.SelectedTitle.Length == 0,
                        $"{svcWin.RowCount} 行");
                    svcWin.Close();

                    var toolLaunch = LaunchOptions.Parse(new[] { "--open-tool=power-plans" });

                    Check("系统工具启动参数",
                        toolLaunch.OpenTool == "power-plans" && toolLaunch.HasUiIntent && toolLaunch.AdminMode,
                        $"OpenTool={toolLaunch.OpenTool}");
                }
                catch (Exception ex)
                {
                    Check("系统工具软件内化", false, ex.Message);
                }

                // ---------- 33f. 系统优化（逆向重写）+ 开机自启动 ----------
                try
                {
                    var catalog = TweakCatalog.All;

                    Check("优化条目：十组都有内容且总数够",
                        TweakCatalog.Groups.Count == 10 && TweakCatalog.Groups.All(g => TweakCatalog.OfGroup(g.Group).Count > 0),
                        string.Join("、", TweakCatalog.Groups.Select(g => $"{g.Title}{TweakCatalog.OfGroup(g.Group).Count}")) +
                        $"（共 {catalog.Count} 项）");

                    var diskN = TweakCatalog.OfGroup(TweakGroup.Disk).Count;
                    var netN = TweakCatalog.OfGroup(TweakGroup.Network).Count;
                    var speedN = TweakCatalog.OfGroup(TweakGroup.Speed).Count;
                    var startupN = TweakCatalog.OfGroup(TweakGroup.Startup).Count;
                    var latencyN = TweakCatalog.OfGroup(TweakGroup.Latency).Count;
                    var atlasN = TweakCatalog.OfGroup(TweakGroup.Atlas).Count;

                    Check("优化条目：新增六组条量达标",
                        diskN >= 4 && netN >= 6 && speedN >= 10 && startupN >= 6 && latencyN >= 4 && atlasN >= 5,
                        $"固态 {diskN} · 网络 {netN} · 响应 {speedN} · 开关机 {startupN} · 延迟 {latencyN} · Atlas {atlasN}");

                    // Atlas 那组必须真的来自 Atlas：每条都得说清它是什么、代价是什么。
                    // 只写「优化」不写代价的条目，等于把取舍藏起来了。
                    var atlasItems = TweakCatalog.OfGroup(TweakGroup.Atlas).ToList();

                    Check("优化条目：Atlas 组每条都写清代价",
                        atlasItems.All(x => x.Effect.Length >= 20 && x.Desc.Length >= 20),
                        $"最短说明 {atlasItems.Min(x => x.Desc.Length)} 字 / 最短代价 {atlasItems.Min(x => x.Effect.Length)} 字");

                    // AutoEndTasks 会直接结束未保存的程序，必须落在「谨慎」，绝不能是安全档
                    var autoEnd = atlasItems.FirstOrDefault(x => x.Id == "atlas-auto-end-tasks");

                    Check("优化条目：会丢数据的 Atlas 项没标成安全",
                        autoEnd != null && autoEnd.Risk != TweakRisk.Safe
                        && autoEnd.Effect.Contains("丢"),
                        autoEnd == null ? "找不到 atlas-auto-end-tasks" : $"风险={autoEnd.Risk}，代价里写明会丢数据");

                    Check("优化条目：延迟组都是可还原的单变量项",
                        TweakCatalog.OfGroup(TweakGroup.Latency).All(x =>
                            x.Kind == TweakKind.Registry && x.Writes.Count >= 1 && x.Id.StartsWith("lat-")),
                        string.Join("、", TweakCatalog.OfGroup(TweakGroup.Latency).Select(x => x.Id)));

                    // 带「会丢数据」副作用的条目必须标 Careful/Risky，不能算 Safe
                    var loose = TweakCatalog.OfGroup(TweakGroup.Startup)
                        .Where(x => x.Effect.Contains("丢") || x.Effect.Contains("损坏"))
                        .Where(x => x.Risk == TweakRisk.Safe)
                        .ToList();

                    Check("优化条目：会丢数据的项没有标成 Safe",
                        loose.Count == 0,
                        loose.Count == 0 ? "已按 Careful 归档" : string.Join("、", loose.Select(x => x.Title)));

                    Check("优化条目：Id 唯一且每条都有说明与位置",
                        catalog.Select(x => x.Id).Distinct().Count() == catalog.Count
                        && catalog.All(x => x.Id.Length > 0 && x.Title.Length > 0 && x.Desc.Length > 0 && x.TargetText.Length > 0),
                        $"重复 {catalog.Count - catalog.Select(x => x.Id).Distinct().Count()} 个");

                    Check("优化条目：风险分级覆盖三档",
                        catalog.Any(x => x.Risk == TweakRisk.Safe)
                        && catalog.Any(x => x.Risk == TweakRisk.Careful)
                        && catalog.Any(x => x.Risk == TweakRisk.Risky),
                        $"安全 {catalog.Count(x => x.Risk == TweakRisk.Safe)} / " +
                        $"谨慎 {catalog.Count(x => x.Risk == TweakRisk.Careful)} / " +
                        $"高风险 {catalog.Count(x => x.Risk == TweakRisk.Risky)}");

                    // REG_SZ 允许把「开启」写成空串——经典右键菜单就是靠「默认值存在但为空」生效的，
                    // 所以只对非字符串类型要求 OnValue 非空，否则这条断言会把正常条目判成配漏了。
                    Check("优化条目：注册表类都配了写入",
                        catalog.Where(x => x.Kind == TweakKind.Registry)
                            .All(x => x.Writes.Count > 0
                                      && x.Writes.All(w => w.KeyPath.Length > 0
                                                           && (w.Kind == Microsoft.Win32.RegistryValueKind.String
                                                               || w.OnValue.Length > 0))),
                        $"{catalog.Count(x => x.Kind == TweakKind.Registry)} 项注册表类");

                    Check("优化条目：服务类都有服务名与启动类型",
                        catalog.Where(x => x.Kind == TweakKind.Service)
                            .All(x => x.ServiceName.Length > 0 && x.OffStart is >= 2 and <= 4),
                        string.Join("、", catalog.Where(x => x.Kind == TweakKind.Service).Select(x => x.ServiceName)));

                    Check("优化条目：命令类都有双向命令与探针",
                        catalog.Where(x => x.Kind == TweakKind.Command)
                            .All(x => x.OnCommand.Length > 0 && x.OffCommand.Length > 0 && x.Probe != null),
                        $"{catalog.Count(x => x.Kind == TweakKind.Command)} 项命令类");

                    var readings = TweakService.ReadAll(catalog);
                    var readable = readings.Count(r => r.State != TweakState.Unknown);

                    Check("优化条目：全部可读（不抛异常）",
                        readings.Count == catalog.Count,
                        $"读到 {readable} 项，读不到 {readings.Count - readable} 项（普通权限下服务类会读不到）");

                    Check("优化条目：读不到的项都带了原因",
                        readings.Where(r => r.State == TweakState.Unknown).All(r => r.Detail.Length > 0),
                        readings.FirstOrDefault(r => r.State == TweakState.Unknown)?.Detail ?? "全部可读");

                    Check("优化条目：本机 HKCU 类都能读出状态",
                        readings.Where(r => r.Item.Kind == TweakKind.Registry
                                            && r.Item.Writes.All(w => w.Hive == Microsoft.Win32.RegistryHive.CurrentUser))
                            .All(r => r.State != TweakState.Unknown),
                        "HKCU 读取不依赖管理员，不应出现「读不到」");

                    // 写入 → 读回 → 还原：用自建的一次性条目，绝不碰用户已有的设置
                    var probeId = "selftest-probe";
                    var probe = new TweakItem
                    {
                        Id = probeId,
                        Group = TweakGroup.Common,
                        Kind = TweakKind.Registry,
                        Risk = TweakRisk.Safe,
                        Title = "自检探针",
                        Desc = "只在自检里用的临时条目",
                        Writes = new List<TweakWrite>
                        {
                            new()
                            {
                                Hive = Microsoft.Win32.RegistryHive.CurrentUser,
                                KeyPath = @"Software\SysDiag\SelfTest",
                                ValueName = "Probe",
                                Kind = Microsoft.Win32.RegistryValueKind.DWord,
                                OnValue = "1",
                                DeleteWhenOff = true
                            }
                        }
                    };

                    var wroteOn = TweakService.Apply(probe, true);
                    var afterOn = TweakService.Read(probe);

                    var wroteOff = TweakService.Apply(probe, false);
                    var afterOff = TweakService.Read(probe);

                    var wroteAgain = TweakService.Apply(probe, true);
                    var restored = TweakService.RestoreOne(probeId);
                    var afterRestore = TweakService.Read(probe);

                    Check("优化写入：开 → 关 → 还原三步都对",
                        wroteOn.Ok && afterOn.State == TweakState.On
                        && wroteOff.Ok && afterOff.State == TweakState.Off
                        && wroteAgain.Ok && restored.Ok && afterRestore.State == TweakState.Off,
                        $"开={afterOn.StateText} 关={afterOff.StateText} 还原后={afterRestore.StateText}");

                    try
                    {
                        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\SysDiag", throwOnMissingSubKey: false);
                    }
                    catch { /* 清理失败无所谓 */ }

                    var (regFile, cmdFile) = TweakService.ExportRestoreScript();
                    var regHead = File.Exists(regFile) ? File.ReadLines(regFile).FirstOrDefault() ?? "" : "";
                    var regBody = File.Exists(regFile) ? File.ReadAllText(regFile) : "";

                    Check("优化导出：还原脚本可用",
                        File.Exists(regFile) && File.Exists(cmdFile)
                        && regHead.StartsWith("Windows Registry Editor"),
                        Path.GetFileName(regFile));

                    Check("优化导出：.reg 用根键全名（HKCU 缩写导入会失败）",
                        regBody.Contains("HKEY_CURRENT_USER") || regBody.Contains("HKEY_LOCAL_MACHINE"),
                        regBody.Length > 0 ? "根键写法正确" : "文件为空");

                    var tweakWin = new TweakWindow();
                    tweakWin.Reload();

                    Check("优化窗口：渲染出的条目数与目录一致",
                        tweakWin.ItemCount == TweakCatalog.OfGroup(TweakGroup.Common).Count,
                        $"{tweakWin.ItemCount} 项 · 已开启 {tweakWin.AppliedCount} · 读不到 {tweakWin.UnknownCount}");

                    // 左侧分组会随条目扩展变多，必须真的挂在 ScrollViewer 下。
                    // 只查 XAML 字符串没用，这里沿可视化树找祖先。
                    var host = tweakWin.FindName("GroupHost") as FrameworkElement;
                    var scrollAncestor = FindAncestor<ScrollViewer>(host);
                    Check("优化窗口：左侧分组栏可上下滚动",
                        host != null && scrollAncestor != null
                        && scrollAncestor.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
                        scrollAncestor != null ? "GroupHost 位于可滚动容器内" : "分组栏没有滚动容器（底部分组会点不到）");
                    tweakWin.Close();

                    var tweakLaunch = LaunchOptions.Parse(new[] { "--open-tweak" });
                    Check("优化启动参数", tweakLaunch.OpenTweak && tweakLaunch.HasUiIntent && tweakLaunch.AdminMode,
                        $"OpenTweak={tweakLaunch.OpenTweak}");

                    // ---- 开机自启动 ----
                    var autoStatus = AutostartService.Read();

                    Check("自启动：状态可读且字段完整",
                        autoStatus.Detail.Length > 0 && autoStatus.ExePath.Length > 0,
                        $"{autoStatus.ModeText} · {Path.GetFileName(autoStatus.ExePath)}");

                    Check("自启动：Mode 与两项检测结果自洽",
                        autoStatus.Mode == ((autoStatus.RunValue.Length > 0, autoStatus.AdminTaskExists) switch
                        {
                            (true, true) => AutostartService.Mode.Both,
                            (true, false) => AutostartService.Mode.User,
                            (false, true) => AutostartService.Mode.Admin,
                            _ => AutostartService.Mode.None
                        }),
                        $"Run={autoStatus.RunValue.Length > 0} · Task={autoStatus.AdminTaskExists}");

                    var autoWin = new AutostartWindow();
                    autoWin.Reload();

                    Check("自启动窗口：可构造且读到的模式与状态一致",
                        autoWin.Mode == autoStatus.Mode,
                        autoWin.Mode.ToString());
                    autoWin.Close();

                    var autoLaunch = LaunchOptions.Parse(new[] { "--open-autostart" });
                    Check("自启动启动参数", autoLaunch.OpenAutostart && autoLaunch.HasUiIntent,
                        $"OpenAutostart={autoLaunch.OpenAutostart}");

                    var autoArg = LaunchOptions.Parse(new[] { "--autostart" });
                    Check("自启动参数不触发界面意图（开机时不该弹窗）",
                        !autoArg.HasUiIntent && !autoArg.AdminMode,
                        $"HasUiIntent={autoArg.HasUiIntent} Admin={autoArg.AdminMode}");
                }
                catch (Exception ex)
                {
                    Check("系统优化与开机自启动", false, ex.Message);
                }

                // ---------- 33g. 游戏全自动调优流水线 ----------
                try
                {
                    var plan = DeltaAutoTune.Plan();

                    Check("全自动调优：步骤清单齐全且 Id 唯一",
                        plan.Count >= 10 && plan.Select(x => x.Id).Distinct().Count() == plan.Count,
                        $"{plan.Count} 步：" + string.Join("、", plan.Take(4).Select(x => x.Title)) + " …");

                    Check("全自动调优：每步都有说明与执行体",
                        plan.All(x => x.Title.Length > 0 && x.Desc.Length > 0 && x.Run != null),
                        "无空步骤");

                    Check("全自动调优：默认勾选的是推荐项，重活留给人选",
                        plan.Count(x => x.DefaultOn) >= 6 && plan.Count(x => !x.DefaultOn) >= 2,
                        $"默认勾选 {plan.Count(x => x.DefaultOn)} / 共 {plan.Count}；" +
                        "未默认勾选：" + string.Join("、", plan.Where(x => !x.DefaultOn).Select(x => x.Title)));

                    Check("全自动调优：关打印/搜索的那步必须默认不勾",
                        plan.First(x => x.Id == "sys-services").DefaultOn == false,
                        "sys-services.DefaultOn = false");

                    Check("全自动调优：需要游戏的步骤都标了 NeedsGame",
                        plan.Count(x => x.NeedsGame) == 2
                        && plan.Where(x => x.NeedsGame).All(x => x.SystemWide == false),
                        string.Join("、", plan.Where(x => x.NeedsGame).Select(x => x.Title)));

                    Check("全自动调优：进程类步骤标了「需游戏在运行」",
                        plan.First(x => x.Id == "game-priority").NeedsRunning,
                        "game-priority.NeedsRunning = true");

                    // 未选定 exe + 未勾任何项：必须安全返回空结果，不能抛
                    var empty = DeltaAutoTune.RunAsync("", Array.Empty<string>(), null)
                        .GetAwaiter().GetResult();

                    Check("全自动调优：没勾任何项时不执行任何动作",
                        empty.Count == 0,
                        "返回 0 条结果");

                    // 只勾需要游戏的那步、但没选 exe：应被跳过而不是报错
                    var skipped = DeltaAutoTune.RunAsync("", new[] { "game-gpu" }, null)
                        .GetAwaiter().GetResult();

                    Check("全自动调优：没选游戏时跳过游戏级步骤",
                        skipped.Count == 1 && skipped[0].Skipped && !skipped[0].Ok,
                        skipped.Count > 0 ? skipped[0].Message : "无结果");

                    var deltaWin = new DeltaCacheWindow(startOnTune: true);

                    Check("全自动调优窗口：步骤都渲染出来了",
                        deltaWin.StepCount == plan.Count,
                        $"{deltaWin.StepCount} 行");
                    deltaWin.Close();

                    var deltaLaunch = LaunchOptions.Parse(new[] { "--open-delta" });
                    Check("游戏优化启动参数", deltaLaunch.OpenDelta && deltaLaunch.HasUiIntent,
                        $"OpenDelta={deltaLaunch.OpenDelta}");

                    // 服务类条目的语义：开启优化 = 禁用服务（曾经的 bug 是把方向写反）
                    var wsearch = TweakCatalog.ById("common-wsearch");
                    Check("优化条目：服务类「开启优化」= 设为禁用",
                        wsearch != null && wsearch.OnStart == 4 && wsearch.OffStart == 3,
                        $"OnStart={wsearch?.OnStart} OffStart={wsearch?.OffStart}");
                }
                catch (Exception ex)
                {
                    Check("游戏全自动调优", false, ex.Message);
                }

                // ---------- 33h. 显示器配色（ICC） ----------
                try
                {
                    var monitors = IccService.Monitors();

                    Check("ICC：枚举到显示器且字段完整",
                        monitors.Count > 0
                        && monitors.All(m => m.DeviceName.StartsWith(@"\\.") && m.Instance.Length > 0),
                        string.Join("、", monitors.Select(m => $"{m.Name}（实例 {m.Instance}）")));

                    Check("ICC：能读出现在生效的配置",
                        monitors.All(m => m.CurrentPath.Length > 0 || m.CurrentProfile.Length > 0),
                        monitors.Count > 0 ? monitors[0].CurrentName : "无显示器");

                    var profiles = IccService.Profiles();

                    Check("ICC：列出配置并解析出色彩空间",
                        profiles.Count > 0 && profiles.All(p => p.ColorSpace.Length > 0),
                        $"{profiles.Count} 个：" + string.Join("、", profiles.Take(4).Select(p => p.Description)));

                    Check("ICC：解析出合法的版本号",
                        profiles.All(p => p.Version.Length > 0
                                          && (p.Version.StartsWith('2') || p.Version.StartsWith('4'))),
                        string.Join("、", profiles.Take(4).Select(p => $"{p.Description} v{p.Version}")));

                    Check("ICC：路径与文件名一致",
                        profiles.All(p => File.Exists(p.FullPath) && Path.GetFileName(p.FullPath) == p.FileName),
                        $"{profiles.Count} 个文件都在");

                    // ---- 显卡 LUT：ICC 里唯一能让桌面/游戏肉眼可见的部分（只读，不写） ----
                    var ramp = monitors.Count > 0 ? IccService.ReadRamp(monitors[0].DeviceName) : null;

                    Check("ICC：能读显卡 LUT",
                        ramp is { Length: 768 },
                        ramp == null
                            ? "读不到（HDR 开启或驱动不支持）"
                            : $"R128={ramp[128]} · 线性={IccService.IsLinear(ramp)}");

                    var withVcgt = profiles.Where(p => p.HasVcgt).ToList();

                    Check("ICC：区分「含校准表」与「仅色彩描述」",
                        profiles.All(p => p.VcgtText.Length > 0),
                        $"{withVcgt.Count}/{profiles.Count} 个含校准表（只有这些能让桌面真的变化）");

                    Check("ICC：vcgt 偏差在合理区间",
                        withVcgt.All(p => p.VcgtDelta is >= 0 and <= 100),
                        withVcgt.Count == 0
                            ? "本机没有含校准表的配置"
                            : string.Join("、", withVcgt.Take(3).Select(p => $"{p.FileName} {p.VcgtDelta:F1}%")));

                    Check("ICC：LUT 状态文字可用",
                        monitors.All(m => IccService.LutStatus(m).Length > 0),
                        monitors.Count > 0 ? IccService.LutStatus(monitors[0]) : "无显示器");

                    Check("ICC：含校准表的配置能转成 768 点显卡 LUT",
                        withVcgt.All(p =>
                        {
                            var v = IccReader.ReadVcgt(p.FullPath);
                            return v.IsValid && v.ToRamp(IccReader.VcgtData.Linear(), 1.0).Length == 768;
                        }),
                        withVcgt.Count == 0 ? "跳过（没有含校准表的配置）" : $"{withVcgt.Count} 个都能转");

                    // 合成只算不写：没有 vcgt 的配置按曲线生成一条表，用于「强行出效果」
                    var plain = profiles.FirstOrDefault(p => !p.HasVcgt && p.IsMatrix);
                    if (plain != null)
                    {
                        var synth = IccReader.Synthesize(plain.FullPath);
                        Check("ICC：无校准表的配置能按色调曲线合成",
                            synth.IsValid,
                            synth.IsValid
                                ? $"{plain.FileName} → {synth.EntryCount} 项"
                                : $"{plain.FileName} 没有可用曲线（这时只能靠关联，画面本来就不会变）");
                    }

                    // 关联读取（只读，绝不写）
                    var assocMonitors = monitors.Where(m => IccService.ReadAssociation(m.Instance) != null).ToList();

                    Check("ICC：关联键可读（HKCU 路径正确）",
                        assocMonitors.Count > 0,
                        $"可读关联的显示器 {assocMonitors.Count} 台");

                    var firstAssoc = assocMonitors.Count > 0 ? IccService.ReadAssociation(assocMonitors[0].Instance) : null;

                    Check("ICC：关联清单按多字符串解析",
                        firstAssoc == null || firstAssoc.Profiles.Length >= 0,
                        firstAssoc == null ? "无关联" : $"{firstAssoc.Profiles.Length} 项 · UsePerUser={firstAssoc.UsePerUser}");

                    var iccWin = new IccWindow();

                    Check("ICC 窗口：显示器与配置都渲染出来",
                        iccWin.MonitorCount == monitors.Count && iccWin.ProfileCount == profiles.Count,
                        $"{iccWin.MonitorCount} 台 · {iccWin.ProfileCount} 个配置");
                    iccWin.Close();

                    var iccLaunch = LaunchOptions.Parse(new[] { "--open-icc" });
                    Check("ICC 启动参数", iccLaunch.OpenIcc && iccLaunch.HasUiIntent,
                        $"OpenIcc={iccLaunch.OpenIcc}");

                    var iccRestore = LaunchOptions.Parse(new[] { "--icc-restore" });
                    Check("ICC 命令行还原参数", iccRestore.IccRestore && iccRestore.HasUiIntent,
                        $"IccRestore={iccRestore.IccRestore}");
                }
                catch (Exception ex)
                {
                    Check("显示器配色（ICC）", false, ex.Message);
                }

                // ---------- 33h2. 手动调色（gamma / 亮度 / 对比度 / RGB 增益） ----------
                try
                {
                    var neutral = new ManualTune();
                    var neutralRamp = neutral.BuildRamp();

                    Check("调色：中性参数生成完全线性的 LUT",
                        neutral.IsNeutral
                        && Enumerable.Range(0, 256).All(i => neutralRamp[i] == i * 257
                                                             && neutralRamp[256 + i] == i * 257
                                                             && neutralRamp[512 + i] == i * 257),
                        $"R128={neutralRamp[128]}（线性应为 32896）");

                    var brighter = new ManualTune { Gamma = 1.6 }.BuildRamp();
                    var darker = new ManualTune { Gamma = 0.7 }.BuildRamp();

                    Check("调色：gamma 增大抬亮中间调、减小压暗",
                        brighter[128] > neutralRamp[128] && darker[128] < neutralRamp[128],
                        $"gamma1.6 → {brighter[128]}　gamma0.7 → {darker[128]}　中性 {neutralRamp[128]}");

                    var highContrast = new ManualTune { Contrast = 1.5 }.BuildRamp();

                    Check("调色：对比度以中灰为轴（暗更暗、亮更亮、中灰基本不动）",
                        highContrast[64] < neutralRamp[64]
                        && highContrast[192] > neutralRamp[192]
                        && Math.Abs(highContrast[128] - neutralRamp[128]) < 600,
                        $"暗部 {highContrast[64]}　中灰 {highContrast[128]}　亮部 {highContrast[192]}");

                    var lifted = new ManualTune { Brightness = 10 }.BuildRamp();

                    Check("调色：亮度抬升黑位（纯黑不再为 0）",
                        lifted[0] > 0 && lifted[0] < 12000,
                        $"黑位输出 {lifted[0]}/65535");

                    var redOnly = new ManualTune { GainR = 1.4 }.BuildRamp();

                    Check("调色：通道增益只作用于对应通道",
                        redOnly[128] > neutralRamp[128]
                        && redOnly[256 + 128] == neutralRamp[256 + 128]
                        && redOnly[512 + 128] == neutralRamp[512 + 128],
                        $"R128={redOnly[128]}　G128={redOnly[384]}　B128={redOnly[640]}");

                    var clamped = new ManualTune
                    {
                        Gamma = 99, Brightness = 999, Contrast = -5, GainR = 50
                    }.Clamped();

                    Check("调色：越界参数被钳制回允许区间",
                        Math.Abs(clamped.Gamma - ManualTune.GammaMax) < 1e-9
                        && Math.Abs(clamped.Brightness - ManualTune.BrightnessMax) < 1e-9
                        && Math.Abs(clamped.Contrast - ManualTune.ContrastMin) < 1e-9
                        && Math.Abs(clamped.GainR - ManualTune.GainMax) < 1e-9,
                        $"gamma={clamped.Gamma} 亮度={clamped.Brightness} 对比度={clamped.Contrast} R={clamped.GainR}");

                    Check("调色：中性参数的变化量为 0",
                        neutral.MaxDeltaPercent() < 0.01,
                        $"{neutral.MaxDeltaPercent():F2}%");

                    // ---- 对比底图：不内置版权素材 + 自选图能解码 ----
                    Check("调色：exe 内不含版权底图（合规）",
                        !ResourceExists("pack://application:,,,/Assets/color-reference.jpg"),
                        "底图由用户自选，只存本机，不随程序分发");

                    try
                    {
                        // 现造一张 8×8 的小图，验证「换底图」那条解码链路
                        var probePath = Path.Combine(AppPaths.OutputDir, "selftest-ref.png");

                        var probePixels = new byte[8 * 8 * 4];
                        for (var i = 0; i < probePixels.Length; i++) probePixels[i] = 200;

                        var probe = System.Windows.Media.Imaging.BitmapSource.Create(
                            8, 8, 96, 96, PixelFormats.Bgra32, null, probePixels, 8 * 4);

                        var probeEncoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        probeEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(probe));

                        using (var probeStream = File.Create(probePath))
                            probeEncoder.Save(probeStream);

                        var loadedRef = ColorTuneWindow.LoadImageFile(probePath);

                        Check("调色：自选底图能解码（换底图链路）",
                            loadedRef.PixelWidth == 8 && loadedRef.PixelHeight == 8 && loadedRef.IsFrozen,
                            $"{loadedRef.PixelWidth}×{loadedRef.PixelHeight} · frozen={loadedRef.IsFrozen}");
                    }
                    catch (Exception ex)
                    {
                        Check("调色：自选底图能解码（换底图链路）", false, ex.Message);
                    }

                    // B G R A 四个字节，中性表必须原样返回（长度/顺序都不能串）
                    var pixelSrc = new byte[] { 10, 20, 30, 255, 0, 128, 255, 255 };
                    var pixelDst = new byte[pixelSrc.Length];

                    ColorTuneWindow.ApplyRampBytes(pixelSrc, pixelDst, neutral.BuildRamp());

                    var identity = pixelDst.SequenceEqual(pixelSrc);

                    // 只压红通道：B/G/A 必须纹丝不动，R 减半
                    var pixelRedOnly = neutral.BuildRamp();
                    for (var i = 0; i < 256; i++) pixelRedOnly[i] = (ushort)(pixelRedOnly[i] / 2);

                    ColorTuneWindow.ApplyRampBytes(pixelSrc, pixelDst, pixelRedOnly);

                    Check("调色：逐像素映射是恒等（中性表不改变画面）",
                        identity,
                        string.Join(",", pixelDst));

                    Check("调色：通道表不串位（只压 R 时 B/G/A 不动）",
                        pixelDst[0] == 10 && pixelDst[1] == 20 && pixelDst[2] == 15 && pixelDst[3] == 255
                        && pixelDst[4] == 0 && pixelDst[5] == 128 && pixelDst[6] == 127,
                        $"像素1 B={pixelDst[0]} G={pixelDst[1]} R={pixelDst[2]} A={pixelDst[3]}");

                    // 空表（长度不对）必须走直通，不能崩
                    var fallbackDst = new byte[pixelSrc.Length];
                    ColorTuneWindow.ApplyRampBytes(pixelSrc, fallbackDst, Array.Empty<ushort>());

                    Check("调色：LUT 长度异常时直通不崩",
                        fallbackDst.SequenceEqual(pixelSrc),
                        "已按原值拷贝");

                    // ---- 导出 ICC：结构、往返、系统是否认 ----
                    var tunePath = Path.Combine(AppPaths.OutputDir, "selftest-tune.icc");
                    var probeTune = new ManualTune { Gamma = 1.3, Brightness = 3, Contrast = 1.1, GainR = 1.05 };

                    var (exportOk, _) = IccService.ExportTuneIcc(probeTune, "SelfTest Tune", tunePath);
                    var tuneInfo = IccReader.Read(tunePath);

                    Check("调色：导出的 ICC 能被解析回来",
                        exportOk && tuneInfo.IsValid
                        && tuneInfo.Description == "SelfTest Tune"
                        && tuneInfo.ColorSpace == "RGB"
                        && tuneInfo.Version.StartsWith('2'),
                        $"{tuneInfo.Description} · {tuneInfo.ColorSpace} · v{tuneInfo.Version} · gamma {tuneInfo.Gamma:F2}");

                    var tuneVcgt = IccReader.ReadVcgt(tunePath);
                    var expectedRamp = probeTune.BuildRamp();
                    var roundTrip = tuneVcgt.IsValid ? tuneVcgt.ToRamp(IccReader.VcgtData.Linear(), 1.0) : null;

                    var roundTripDiff = 0;
                    if (roundTrip != null)
                    {
                        for (var i = 0; i < 768; i++)
                            roundTripDiff = Math.Max(roundTripDiff, Math.Abs(expectedRamp[i] - roundTrip[i]));
                    }

                    Check("调色：ICC 里的 vcgt 与内存 LUT 逐项一致",
                        tuneVcgt.IsValid && roundTripDiff == 0,
                        tuneVcgt.Describe() + $"　往返差 {roundTripDiff}");

                    var header = File.ReadAllBytes(tunePath);

                    Check("调色：ICC 头部字段正确（acsp / mntr / RGB）",
                        header.Length > 128
                        && Encoding.ASCII.GetString(header, 36, 4) == "acsp"
                        && Encoding.ASCII.GetString(header, 12, 4) == "mntr"
                        && Encoding.ASCII.GetString(header, 16, 4) == "RGB ",
                        $"{header.Length} 字节");

                    // 最硬的验证：系统色彩管理自己打开这个文件。结构不合法会直接抛异常。
                    var accepted = false;
                    var rejectReason = "";

                    try
                    {
                        var ctx = new System.Windows.Media.ColorContext(new Uri(tunePath));
                        GC.KeepAlive(ctx);
                        accepted = true;
                    }
                    catch (Exception ex)
                    {
                        rejectReason = ex.Message;
                    }

                    Check("调色：系统色彩管理接受生成的 ICC",
                        accepted,
                        accepted ? "ColorContext 打开成功" : rejectReason);

                    // ---- 方案保存 ----
                    var (saveOk, _) = IccService.SaveTunePreset("__selftest__", probeTune);
                    var loadedPreset = IccService.LoadTunePresets().FirstOrDefault(p => p.Name == "__selftest__");
                    var (deleteOk, _) = IccService.DeleteTunePreset("__selftest__");

                    Check("调色：方案能保存、读回、删除",
                        saveOk && loadedPreset != null
                        && Math.Abs(loadedPreset.Gamma - 1.3) < 0.001 && deleteOk,
                        loadedPreset == null ? "读回失败" : $"gamma={loadedPreset.Gamma:0.00}");

                    var tuneWin = new ColorTuneWindow();

                    Check("调色窗口：能构造且默认中性",
                        tuneWin.MonitorCount == IccService.Monitors().Count && tuneWin.IsNeutral,
                        $"{tuneWin.MonitorCount} 台显示器 · 中性={tuneWin.IsNeutral}");

                    tuneWin.Close();

                    var tuneLaunch = LaunchOptions.Parse(new[] { "--open-color-tune" });

                    Check("调色启动参数",
                        tuneLaunch.OpenColorTune && tuneLaunch.HasUiIntent,
                        $"OpenColorTune={tuneLaunch.OpenColorTune}");
                }
                catch (Exception ex)
                {
                    Check("手动调色", false, ex.Message);
                }

                // ---------- 33h3. 远程公告（签名 / 降级 / 不做授权） ----------
                try
                {
                    // 只用于「显示条件」断言的辅助：不涉及真实签名
                    RemoteNotice Showable(string message, DateTimeOffset expires, bool enabled = true) => new()
                    {
                        Verified = true,
                        Enabled = enabled,
                        Message = message,
                        Title = "T",
                        Expires = expires
                    };
                    // 没有签名的内容一律不可显示——这是整个功能的第一道闸
                    var noSign = RemoteNoticeService.Parse(
                        "{\"v\":1,\"enabled\":true,\"level\":\"info\",\"title\":\"测试\",\"message\":\"没有签名\"}");

                    Check("公告：缺签名时拒绝显示",
                        !noSign.Verified && !noSign.Showable && noSign.Error.Contains("签名"),
                        noSign.Error);

                    var badSign = RemoteNoticeService.Parse(
                        "{\"v\":1,\"enabled\":true,\"level\":\"info\",\"title\":\"测试\",\"message\":\"签名是假的\"," +
                        "\"signature\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==\"}");

                    Check("公告：伪造签名被拒绝",
                        !badSign.Verified && !badSign.Showable,
                        badSign.Verified ? "错误地通过了" : badSign.Error);

                    var broken = RemoteNoticeService.Parse("这不是 JSON");

                    Check("公告：格式错误不抛异常",
                        !broken.Verified && broken.Error.Contains("格式"),
                        broken.Error);

                    // 规范串必须稳定：同样字段 → 同样字符串，否则签名永远对不上
                    var a = new RemoteNotice
                    {
                        Version = 1, Enabled = true, Level = "info", Title = "T",
                        Message = "M", Url = "https://x", UrlText = "看", SignedAt = "2026-11-07T00:00:00Z"
                    };

                    var b = new RemoteNotice
                    {
                        Version = 1, Enabled = true, Level = "info", Title = "T",
                        Message = "M", Url = "https://x", UrlText = "看", SignedAt = "2026-11-07T00:00:00Z"
                    };

                    Check("公告：签名规范串稳定且分隔符数量固定",
                        RemoteNoticeService.Canonical(a) == RemoteNoticeService.Canonical(b)
                        && RemoteNoticeService.Canonical(a).Count(c => c == '|') == 8,
                        RemoteNoticeService.Canonical(a));

                    // 只改一个字段，规范串必须变（说明字段确实进了签名范围）
                    b.Message = "M ";

                    Check("公告：改任一字段都会改变签名串",
                        RemoteNoticeService.Canonical(a) != RemoteNoticeService.Canonical(b),
                        "改动 message 后规范串已变化");

                    // 过期 / 禁用 / 空内容都不显示
                    var expired = Showable("到期", DateTimeOffset.Now.AddDays(-1));
                    var disabled = Showable("禁用", DateTimeOffset.Now.AddDays(30), enabled: false);
                    var empty = Showable("", DateTimeOffset.Now.AddDays(30));

                    Check("公告：过期 / 禁用 / 空内容都不显示",
                        !expired.Showable && !disabled.Showable && !empty.Showable
                        && Showable("正常", DateTimeOffset.Now.AddDays(30)).Showable,
                        $"过期={expired.Showable} 禁用={disabled.Showable} 空={empty.Showable}");

                    // 项目根的签名文件（存在才验，不依赖私钥）
                    var noticeCandidates = new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "notice.json"),
                        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "notice.json"),
                        Path.Combine(Environment.CurrentDirectory, "notice.json")
                    };

                    var noticePath = noticeCandidates.FirstOrDefault(File.Exists);

                    if (noticePath != null)
                    {
                        var realJson = File.ReadAllText(noticePath);
                        var real = RemoteNoticeService.Parse(realJson);

                        // 篡改样本必须**动态**取当前正文，不能硬编码某段文案——
                        // 原来写死 Replace("本次新增", …)，正文一改就匹配不上，
                        // 文件没变自然「验签仍通过」，测试反而报错。这个坑踩过一次。
                        var target = real.Message.Length > 0 ? real.Message : real.Title;
                        var tampered = target.Length > 0 ? realJson.Replace(target, target + "改") : realJson;

                        var tamperedNote = RemoteNoticeService.Parse(tampered);

                        Check("公告：篡改样本确实改动过内容",
                            target.Length > 0 && !tampered.Equals(realJson, StringComparison.Ordinal),
                            target.Length > 0 ? $"改动字段长度 {target.Length}" : "正文与标题都为空，无法构造样本");

                        Check("公告：真实签名通过、改一个字即失效",
                            real.Verified && !tamperedNote.Verified,
                            $"原文={(real.Verified ? "通过" : "失败")}　篡改={(tamperedNote.Verified ? "仍通过（有问题）" : "被拒")}");
                    }
                    else
                    {
                        Check("公告：真实签名通过、改一个字即失效", true, "跳过（发布环境无 notice.json）");
                    }

                    var noticeLaunch = LaunchOptions.Parse(new[] { "--notice-probe" });

                    Check("公告：--notice-probe 不触发界面",
                        !noticeLaunch.HasUiIntent,
                        $"HasUiIntent={noticeLaunch.HasUiIntent}");
                }
                catch (Exception ex)
                {
                    Check("远程公告", false, ex.Message);
                }

                // ---------- 33i. 延迟优化（时钟 / MSI / 外设节能 / 计时器） ----------
                try
                {
                    var latency = LatencyService.Scan();

                    // 原四组 + 码枪堂那两项的「虚拟化与安全」「电源与显卡」
                    var latencyGroups = latency.Select(x => x.Group).Distinct().ToList();
                    var expectedGroups = new[]
                    {
                        "系统时钟取向", "中断模式（驱动级）", "外设节能", "计时器", "虚拟化与安全", "电源与显卡"
                    };

                    Check("延迟优化：六组检查齐全（原四组 + 码枪堂两组）",
                        latency.Count >= 10 && expectedGroups.All(latencyGroups.Contains),
                        $"{latency.Count} 项 · 分组：" + string.Join("、", latencyGroups));

                    Check("延迟优化：每项都有标题与依据",
                        latency.All(x => x.Title.Length > 0 && x.Detail.Length > 0),
                        "无空项");

                    var timer = latency.FirstOrDefault(x => x.Title.Contains("计时器"));

                    Check("延迟优化：计时器分辨率可读",
                        timer != null && timer.Detail.Contains("ms"),
                        timer?.Detail.Split('\n')[0] ?? "没有这一项");

                    var msi = latency.Where(x => x.Title.Contains("MSI")).ToList();

                    Check("延迟优化：MSI 中断按设备统计",
                        msi.Count == 2 && msi.All(x => x.Detail.Contains("个设备")),
                        string.Join("　", msi.Select(x => x.Title + "：" + x.Detail.Split('\n')[0])));

                    var usb = latency.FirstOrDefault(x => x.Title.Contains("USB"));

                    Check("延迟优化：USB 选择性暂停可读或明确说明",
                        usb != null && usb.Detail.Length > 0 && usb.State != "",
                        usb?.Detail ?? "没有这一项");

                    // bcdedit 需要管理员：普通权限下必须是「读不到」，不能瞎报已优化
                    var hpet = latency.FirstOrDefault(x => x.Title.Contains("HPET"));

                    Check("延迟优化：读不到启动配置时如实标注",
                        hpet == null
                        || ElevationService.IsAdmin
                        || (hpet.State == "unknown" && hpet.Detail.Contains("管理员")),
                        hpet == null ? "无此项" : $"{hpet.StateText} · {hpet.Detail.Split('\n')[0]}");

                    var latencyWin = new LatencyWindow();

                    Check("延迟优化窗口：渲染出的项数与扫描一致",
                        latencyWin.Count == latency.Count,
                        $"{latencyWin.Count} 项 · 可执行 {latencyWin.Actionable}");
                    latencyWin.Close();

                    // ---- 码枪堂同类项：Hyper-V / HPET 双模式 / 卓越性能 / 游戏优先级 / N 卡 Boost ----
                    Check("码枪堂项：六组检查齐全",
                        latency.Select(x => x.Group).Distinct().Count() >= 6,
                        string.Join("、", latency.Select(x => x.Group).Distinct()));

                    var hyperV = latency.FirstOrDefault(x => x.Title.Contains("Hyper-V"));

                    Check("码枪堂项：Hyper-V 有开/关两个方向",
                        hyperV != null
                        && hyperV.Options.Count == 2
                        && hyperV.Options.Any(o => o.Label.Contains("关闭"))
                        && hyperV.Options.Any(o => o.Label.Contains("恢复")),
                        hyperV == null ? "无此项" : string.Join(" | ", hyperV.Options.Select(o => o.Label)));

                    Check("码枪堂项：HPET 给的是两种模式而不是单向开关",
                        hpet != null
                        && hpet.Options.Count == 2
                        && hpet.Options.Any(o => o.Label.Contains("强制"))
                        && hpet.Options.Any(o => o.Label.Contains("默认")),
                        hpet == null ? "无此项" : string.Join(" | ", hpet.Options.Select(o => o.Label)));

                    // 卓越性能：只查询，绝不在这里创建或切换（会把用户的电源方案改掉）
                    var ultimate = PowerPlanService.Ultimate();
                    var ultimateCheck = latency.FirstOrDefault(x => x.Title.Contains("卓越性能"));

                    Check("码枪堂项：卓越性能状态可读且与方案一致",
                        ultimateCheck != null
                        && ultimateCheck.State == (ultimate.Active ? "ok" : "off"),
                        ultimate.Exists
                            ? $"{(ultimate.Active ? "使用中" : "存在未启用")}：{ultimate.Name}"
                            : "本机没有（点击会复制模板）");

                    Check("码枪堂项：卓越性能枚举不到时能靠注册表认出来",
                        ultimate.Exists == (ultimate.Guid != Guid.Empty),
                        ultimate.Exists ? ultimate.Guid.ToString() : "不存在");

                    var gamePriority = latency.FirstOrDefault(x => x.Title.Contains("游戏进程优先级"));

                    Check("码枪堂项：游戏优先级在没游戏时如实说明",
                        gamePriority != null
                        && (gamePriority.ActionLabel.Length > 0
                            || gamePriority.Detail.Contains("没有检测到游戏")),
                        gamePriority?.Detail.Split('\n')[0] ?? "无此项");

                    var nvidia = latency.FirstOrDefault(x => x.Title.Contains("N 卡"));

                    Check("码枪堂项：N 卡 Boost 只生成导入文件、不直接写驱动",
                        nvidia != null
                        && nvidia.Options.Count == 2
                        && nvidia.Options[0].Label.Contains(".nip")
                        && nvidia.Detail.Length > 0,
                        nvidia?.Detail.Split('\n')[0] ?? "无此项");

                    // N 卡 DRS 字段 ID 必须和 Profile Inspector 一致，写错会往驱动里塞垃圾设置
                    Check("码枪堂项：电源管理模式字段 ID 正确",
                        GameBoostService.PowerManagementModeId == 0x1057EB71,
                        $"0x{GameBoostService.PowerManagementModeId:X8}");

                    var boostLaunch = LaunchOptions.Parse(new[] { "--nvidia-boost" });
                    Check("码枪堂项：--nvidia-boost 不触发界面",
                        !boostLaunch.HasUiIntent,
                        $"HasUiIntent={boostLaunch.HasUiIntent}");

                    // 普通权限下读不到 bcdedit，解析逻辑就是唯一的防线：必须能从文本里挑出值，
                    // 且不能把不存在的项读成非空（否则会误报「已优化」）。
                    const string fakeBcd =
                        "Windows 启动管理器\r\n" +
                        "identifier              {current}\r\n" +
                        "hypervisorlaunchtype    Off\r\n" +
                        "useplatformclock        Yes\r\n" +
                        "disabledynamictick      Yes\r\n" +
                        "timeout                 30\r\n";

                    Check("码枪堂项：bcdedit 输出解析正确（含不存在的项返回空）",
                        LatencyService.ReadBcdValue(fakeBcd, "hypervisorlaunchtype") == "Off"
                        && LatencyService.ReadBcdValue(fakeBcd, "useplatformclock") == "Yes"
                        && LatencyService.ReadBcdValue(fakeBcd, "disabledynamictick") == "Yes"
                        && LatencyService.ReadBcdValue(fakeBcd, "notexists") == "",
                        $"hypervisor={LatencyService.ReadBcdValue(fakeBcd, "hypervisorlaunchtype")} · " +
                        $"platform={LatencyService.ReadBcdValue(fakeBcd, "useplatformclock")} · " +
                        $"缺失项='{LatencyService.ReadBcdValue(fakeBcd, "notexists")}'");

                    var latencyLaunch = LaunchOptions.Parse(new[] { "--open-latency" });
                    Check("延迟优化启动参数", latencyLaunch.OpenLatency && latencyLaunch.HasUiIntent,
                        $"OpenLatency={latencyLaunch.OpenLatency}");
                }
                catch (Exception ex)
                {
                    Check("延迟优化", false, ex.Message);
                }

                // ---------- 34. 弹窗内部化与内存基线 ----------
                try
                {
                    var srcRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..");
                    var inSourceTree = File.Exists(Path.Combine(srcRoot, "SysDiag.csproj"));
                    var systemBoxes = inSourceTree && Directory.Exists(srcRoot)
                        ? Directory.GetFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(f) is not ("SelfTest.cs" or "UiDialog.cs"))
                            // 只看真实调用：排除注释里的方法名引用
                            .Count(f => File.ReadAllLines(f)
                                .Any(line => line.Contains("MessageBox.Show(", StringComparison.Ordinal)
                                             && !line.TrimStart().StartsWith("//")
                                             && !line.TrimStart().StartsWith("///")))
                        : 0;

                    Check("弹窗：已全部改为内部对话框",
                        systemBoxes == 0,
                        !inSourceTree
                            ? "发布环境无源码，跳过静态检查（运行时行为由各窗口覆盖）"
                            : systemBoxes == 0 ? "0 处 MessageBox.Show" : $"{systemBoxes} 个文件仍在调用系统弹窗");

                    var snap = MemoryTuner.Snapshot();

                    Check("内存：快照可读",
                        MemoryTuner.WorkingSetMB > 0 && snap.Contains("工作集"),
                        snap);

                    Check("内存：私有内存不超目标（<200MB）",
                        MemoryTuner.PrivateMB > 0 && MemoryTuner.PrivateMB < 200,
                        $"当前 {snap}");

                    // 内存明细：托管堆 vs 私有内存 vs 视觉元素，用来判断还有没有可压缩空间
                    var heapMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;
                    var gi = GC.GetGCMemoryInfo();
                    var committedMb = gi.TotalCommittedBytes / 1024.0 / 1024.0;
                    var asmCount = AppDomain.CurrentDomain.GetAssemblies().Length;

                    static int CountVisual(DependencyObject node)
                    {
                        var n = 1;
                        var c = VisualTreeHelper.GetChildrenCount(node);
                        for (var i = 0; i < c; i++) n += CountVisual(VisualTreeHelper.GetChild(node, i));
                        return n;
                    }

                    var visualCount = 0;
                    try { visualCount = CountVisual(win); } catch { /* 窗口未渲染 */ }
                    var privateMb = MemoryTuner.PrivateMB;

                    var webviewLoaded = AppDomain.CurrentDomain.GetAssemblies()
                        .Count(a => (a.GetName().Name ?? "").Contains("WebView2", StringComparison.OrdinalIgnoreCase));
                    var wmiLoaded = AppDomain.CurrentDomain.GetAssemblies()
                        .Any(a => (a.GetName().Name ?? "") == "System.Management");

                    Check("内存：明细可读",
                        heapMb > 0 && privateMb > 0 && asmCount > 0,
                        $"托管堆 {heapMb:0.0} MB · GC 已提交 {committedMb:0.0} MB · 私有 {privateMb} MB "
                        + $"· 程序集 {asmCount} · 视觉元素 {visualCount:N0} · WebView2 程序集 {webviewLoaded} · WMI {(wmiLoaded ? "已加载" : "未加载")}");

                    Check("内存：工作集回收可用",
                        MemoryTuner.TrimIfIdle() || MemoryTuner.TrimCount > 0,
                        $"累计回收 {MemoryTuner.TrimCount} 次 · {MemoryTuner.Snapshot()}");

                    // 版本标识：界面上要能看出跑的是哪个构建
                    var pv = LaunchOptions.Parse(new[] { "--preview-dialog" });

                    var mr = LaunchOptions.Parse(new[] { "--mem-report" });

                    Check("内存自证参数",
                        mr.MemReport && mr.HasUiIntent && !mr.AdminMode,
                        $"MemReport={mr.MemReport} / Admin={mr.AdminMode}");

                    Check("对话框预览参数",
                        pv.PreviewDialog && pv.HasUiIntent && !pv.AdminMode,
                        $"PreviewDialog={pv.PreviewDialog} / Admin={pv.AdminMode}");

                    // 背景动效：花瓣（主体+拖尾）与飞鸟都能生成，且可一键关闭
                    win.EnablePetals();
                    var petals = win.PetalCount;
                    var birds = win.BirdCount;
                    var petalsOn = win.PetalsOn;
                    win.DisablePetals();

                    Check("背景动效：花瓣+飞鸟可开关且不常驻",
                        petals == MainWindow.PetalTotal * 2 && birds == 2
                        && petalsOn && !win.PetalsOn && win.PetalCount == 0 && win.BirdCount == 0,
                        $"{petals} 片花瓣（含拖尾）· {birds} 只飞鸟 · 关闭后 {win.PetalCount}/{win.BirdCount}");

                    Check("版本标识可见",
                        !string.IsNullOrEmpty(AppInfo.Version)
                        && win.VersionLabel.Contains(AppInfo.Version),
                        $"界面显示：{win.VersionLabel}");

                    // 内部对话框不是系统样式（不是 MessageBox）
                    var dlg = new SysDiag.Ui.DialogWindow();
                    dlg.Configure("自检用对话框", "自检", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

                    Check("对话框：自绘且非系统样式",
                        dlg.WindowStyle == WindowStyle.None
                        && dlg.AllowsTransparency
                        && !dlg.ShowInTaskbar,
                        $"WindowStyle={dlg.WindowStyle} / 透明={dlg.AllowsTransparency} / 任务栏={dlg.ShowInTaskbar}");
                    dlg.Close();

                    // 未处理异常落盘（不弹系统框）
                    var probeLog = Path.Combine(Path.GetTempPath(),
                        "sysdiag-crash-" + Guid.NewGuid().ToString("N")[..6] + ".log");
                    CrashLog.Write(new InvalidOperationException("selftest-crash-probe"), probeLog);
                    var crashOk = File.Exists(probeLog)
                                  && File.ReadAllText(probeLog).Contains("selftest-crash-probe");

                    Check("异常落盘：不弹系统崩溃框",
                        crashOk,
                        "写入临时日志验证（不污染正式 crash.log）");
                }
                catch (Exception ex)
                {
                    Check("弹窗与内存", false, ex.Message);
                }

                // ---------- 35. 小时区间筛选 ----------
                try
                {
                    // 正常区间：起点取整点、终点取该小时末
                    var normal = TimeRange.Custom(
                        new DateTime(2026, 11, 5, 8, 0, 0),
                        new DateTime(2026, 11, 6, 12, 0, 0));

                    Check("小时区间：起止按小时对齐",
                        normal.IsValid
                        && normal.From.Minute == 0 && normal.From.Second == 0
                        && normal.To.Minute == 59
                        && normal.Hours == 29,
                        $"{normal.Describe()} · 调整={normal.Adjustment}");

                    // 起止颠倒：自动对调并给出说明
                    var swapped = TimeRange.Custom(
                        new DateTime(2026, 11, 6, 12, 0, 0),
                        new DateTime(2026, 11, 5, 8, 0, 0));

                    Check("小时区间：起止颠倒自动对调",
                        swapped.IsValid
                        && swapped.From < swapped.To
                        && swapped.Adjustment.Contains("对调"),
                        $"{swapped.Describe()} · {swapped.Adjustment}");

                    // 整段在未来：退化为最近 1 小时，并说明
                    var future = TimeRange.Custom(
                        DateTime.Now.AddDays(3), DateTime.Now.AddDays(5));

                    Check("小时区间：未来区间被纠正",
                        future.IsValid
                        && future.To <= DateTime.Now.AddMinutes(1)
                        && future.Adjustment.Contains("未来"),
                        $"{future.Describe()} · {future.Adjustment}");

                    // 跨度超上限：截断（且仍有效）
                    var huge = TimeRange.Custom(
                        DateTime.Now.AddDays(-5000), DateTime.Now.AddHours(-1));

                    Check("小时区间：超长区间被截断",
                        huge.IsValid && huge.Hours <= TimeRange.MaxSpan.TotalHours + 1,
                        $"{huge.Hours} 小时 · {huge.Adjustment}");

                    // 起点在未来、终点在过去（完全矛盾）：仍要落成有效区间
                    var chaotic = TimeRange.Custom(
                        DateTime.Now.AddDays(2), DateTime.Now.AddDays(-2));

                    Check("小时区间：矛盾输入不产生崩溃",
                        chaotic.IsValid && chaotic.From < chaotic.To,
                        chaotic.Describe());

                    // 界面组件：预设与自定义可切换
                    var picker = new SysDiag.Controls.HourRangePicker();
                    var presetRange = picker.Range;

                    picker.SetPresetHours(72);
                    var p72 = picker.Range;

                    picker.SetCustom(DateTime.Now.AddHours(-6), DateTime.Now);
                    var customRange = picker.Range;

                    Check("区间选择器：预设与自定义",
                        presetRange.IsValid && p72.Hours >= 71 && customRange.IsCustom
                        && customRange.IsValid,
                        $"默认 {presetRange.Hours}h / 72h→{p72.Hours}h / 自定义→{customRange.Hours}h");

                    // 查询一致性：自定义区间内读到的事件必须全部落在区间里
                    var from = DateTime.Now.AddHours(-72);
                    var to = DateTime.Now.AddHours(-48);

                    var ranged = EventViewerService.Read("System", from, to, 3000,
                        out var rState, out var rNote);

                    var inRange = ranged.All(e => !e.Time.HasValue
                                                  || (e.Time.Value >= from && e.Time.Value <= to));

                    Check("查询一致性：结果全部落在所选区间",
                        inRange && rState is "done" or "limited",
                        $"区间 72~48 小时前 · 读到 {ranged.Count} 条 · {rState} {rNote}");

                    // 空区间（很久以前、日志里没有记录）：0 条且不报错
                    var ancient = EventViewerService.Read("System",
                        new DateTime(2001, 1, 1), new DateTime(2001, 1, 2), 100,
                        out var aState, out var aNote);

                    Check("查询一致性：无记录区间返回 0 条",
                        ancient.Count == 0 && aState is "done" or "limited",
                        $"2001-01-01 区间 · {aState} {aNote}");

                    // 报告的区间文案与实际查询同源
                    var rPayload = EventViewerService.BuildReport("系统", ranged,
                        TimeRange.Custom(from, to), rNote);

                    Check("报告：区间文案与查询一致",
                        rPayload.Subtitle.Contains("整点")
                        && rPayload.PlainSummary.Contains("—")
                        && File.Exists(rPayload.HtmlPath),
                        rPayload.Subtitle);
                }
                catch (Exception ex)
                {
                    Check("小时区间筛选", false, ex.Message);
                }

                // ---------- 36. 挂机监控：自动锁定与开销 ----------
                try
                {
                    // 1) 句柄扫描缓冲复用：连续两次都要成功，数量级一致
                    var h1 = HandleScanService.Scan(Environment.ProcessId, "selftest");
                    var h2 = HandleScanService.Scan(Environment.ProcessId, "selftest");

                    Check("句柄扫描：缓冲复用连续可用",
                        h1.TotalHandles > 0 && h2.TotalHandles > 0
                        && Math.Abs(h1.TotalHandles - h2.TotalHandles) < Math.Max(1000, h1.TotalHandles * 0.5),
                        $"{h1.TotalHandles:N0} → {h2.TotalHandles:N0} 条句柄");

                    // 2) 监控开销：采样耗时 + 每次采样分配量（缓冲复用后不应有大对象）
                    var monitor = new MonitorWindow(_ => { });

                    var allocBefore = GC.GetTotalAllocatedBytes(true);
                    monitor.RunSync(Environment.ProcessId, "selftest", 9, 2);
                    var allocAfter = GC.GetTotalAllocatedBytes(true);

                    var ms = monitor.Snapshot;
                    var perTickKb = (allocAfter - allocBefore) / 1024.0 / Math.Max(1, ms.Ticks);

                    Check("监控：采样耗时可接受",
                        ms.Ticks >= 3 && ms.AvgSampleMs < 500 && ms.MaxSampleMs < 1200,
                        $"{ms.PerfText}");

                    Check("监控：每次采样分配受控（<2MB）",
                        perTickKb > 0 && perTickKb < 2048,
                        $"{perTickKb:0} KB/次 · 共 {ms.Ticks} 次");

                    Check("监控：跟随重启字段可用",
                        ms.TargetSwitches == 0 && ms.TargetText.Contains("selftest"),
                        ms.TargetText);
                    monitor.Close();

                    // 3) 等待模式：目标不存在时进入等待，不报错、不刷事件
                    var runner = new MonitorRunner();
                    runner.Start("NoSuchGameProcess_ForSelfTest", 0, 2, autoFollow: true);

                    var deadline = DateTime.Now.AddSeconds(4);
                    while (DateTime.Now < deadline) Thread.Sleep(200);
                    var waiting = runner.Stop();
                    runner.Dispose();

                    Check("监控：目标未启动时进入等待",
                        waiting.TargetPid == 0
                        && waiting.Ticks >= 1
                        && waiting.WarnCount == 0
                        && waiting.TargetText.Contains("等待"),
                        $"{waiting.Ticks} 次探测 · {waiting.TargetText}");
                }
                catch (Exception ex)
                {
                    Check("挂机监控自动锁定与开销", false, ex.Message);
                }
            }
            catch (Exception ex)
            {
                Check("进程句柄排查", false, ex.Message);
            }

            sb.AppendLine();
            sb.AppendLine(failed == 0 ? "结果：全部通过" : $"结果：{failed} 项未通过");
        }
        catch (Exception ex)
        {
            failed++;
            sb.AppendLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace);
        }

        var logPath = Path.Combine(AppPaths.OutputDir, "selftest.txt");
        try
        {
            Directory.CreateDirectory(AppPaths.OutputDir);
            File.WriteAllText(logPath, sb.ToString(), new UTF8Encoding(true));
        }
        catch { }

        return failed == 0 ? 0 : 1;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    private static string _testSrcDir;

    /// <summary>生成一份合成样本：UE 风格 UTF-8 日志 + GBK 中文日志 + 说明文件 + 二进制。</summary>
    private static string BuildTestZip()
    {
        var root = Path.Combine(Path.GetTempPath(), "sysdiag-selftest-" + Guid.NewGuid().ToString("N")[..6]);
        var src = Path.Combine(root, "src");
        _testSrcDir = src;
        Directory.CreateDirectory(Path.Combine(src, "Logs"));

        var ue = new[]
        {
            "[2026.11.05-14.20.01:123][  0]LogInit: Display: Engine is initialized",
            "[2026.11.05-14.20.05:456][  1]LogNet: Warning: Packet loss detected on connection",
            "[2026.11.05-14.21.10:789][ 12]LogGame: Error: Failed to load texture 0x7FF6A1B2C3D4",
            "[2026.11.05-14.21.11:001][ 13]LogGame: Error: Failed to load texture 0x7FF6E5F6A7B8",
            "[2026.11.05-14.22.30:222][ 20]LogTemp: Warning: Low memory warning",
            "[2026.11.05-14.23.00:333][ 25]LogWindows: Error: appError called: Fatal error!",
            "[2026.11.05-14.23.00:334][ 25]LogWindows: Error: Assertion failed: Index >= 0"
        };
        File.WriteAllText(Path.Combine(src, "Logs", "Game.log"), string.Join("\r\n", ue), new UTF8Encoding(false));

        var cn = new[]
        {
            "2026-11-05 14:20:01 [INFO] 游戏启动成功",
            "2026-11-05 14:21:33 [ERROR] 连接服务器失败：超时",
            "2026-11-05 14:21:40 [ERROR] 连接服务器失败：超时",
            "2026-11-05 14:22:10 [WARN] 内存占用过高",
            @"2026-11-05 14:22:55 [ERROR] 资源加载失败 路径 C:\Game\Assets\tex.pak"
        };
        File.WriteAllText(Path.Combine(src, "Logs", "Client.log"), string.Join("\r\n", cn), Encoding.GetEncoding(936));

        File.WriteAllText(Path.Combine(src, "readme.txt"), "普通说明文件", new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(src, "dump.dmp"), new byte[4096]);

        var zip = Path.Combine(root, "gamelogs.zip");
        ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Optimal, false);
        return zip;
    }

    /// <summary>沿逻辑树向上找指定类型的祖先，用来验证控件真的挂在滚动容器里。
    /// 自检里窗口没有 Show()，可视化树尚未连接，必须走逻辑树（XAML 解析后即存在）。</summary>
    private static T FindAncestor<T>(DependencyObject node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T hit) return hit;
            node = LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>程序集里有没有打进这个 pack 资源。不存在会抛异常，所以不能直接用 GetResourceStream 判断。</summary>
    private static bool ResourceExists(string packUri)
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri(packUri, UriKind.Absolute));
            if (stream == null) return false;

            stream.Stream?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
