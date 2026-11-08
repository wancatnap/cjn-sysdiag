using System.IO;
using System.Text;
using System.Windows;
using SysDiag.Models;
using SysDiag.Services;

namespace SysDiag;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var options = LaunchOptions.Parse(args);

        // 开发校验：把图标库全部图形渲染成一张 PNG（不开窗口，不弹自检输出）。
        // 改完 IconLibrary 跑一次，肉眼确认 20px 下还认得出是什么。
        if (args.Any(a => a.Equals("--icon-sheet", StringComparison.OrdinalIgnoreCase)))
        {
            AppPaths.Ensure();
            var large = args.Any(a => a.Equals("--big", StringComparison.OrdinalIgnoreCase));
            try
            {
                IconSheet.Render(
                    Path.Combine(AppPaths.OutputDir, large ? "icon-sheet-big.png" : "icon-sheet.png"),
                    "#4C6EF5",
                    large);
                return 0;
            }
            catch
            {
                return 1;
            }
        }

        // --layout-audit：把所有能无参构造的窗口量一遍，列出画到框外面的元素。
        // 改完界面跑一次，比逐个截图快，也不会漏掉滚动区外的控件。
        if (args.Any(a => a.Equals("--layout-audit", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            // 窗口构造要用主题里的画刷/样式；命令行分支不走 App 启动流程，得自己挂一份。
            var auditApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            auditApp.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Theme.xaml", UriKind.Absolute)
            });

            // --screen=1366x728 可以模拟小屏，检查窗口被压扁后的布局
            double screenW = 0, screenH = 0;
            var screenArg = args.FirstOrDefault(a => a.StartsWith("--screen=", StringComparison.OrdinalIgnoreCase));
            if (screenArg != null)
            {
                var parts = screenArg["--screen=".Length..].Split('x');
                if (parts.Length == 2)
                {
                    double.TryParse(parts[0], out screenW);
                    double.TryParse(parts[1], out screenH);
                }
            }

            var results = LayoutAuditRunner.Run(screenW, screenH);
            var text = LayoutAuditRunner.Render(results);

            AppPaths.Ensure();
            File.WriteAllText(Path.Combine(AppPaths.OutputDir, "layout-audit.txt"), text,
                new UTF8Encoding(false));

            Console.WriteLine(text);
            return 0;
        }

        // --mem-probe：把内存读数的每一路打出来（物理/提交/待机/已修改/进程）。
        // 默认只读；加 --purge 才真的清一次待机列表——排查「清完没变化」时先看这里的原始数字。
        if (args.Any(a => a.Equals("--mem-probe", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var text = MemoryOptimizerService.Probe(
                args.Any(a => a.Equals("--purge", StringComparison.OrdinalIgnoreCase)));

            AppPaths.Ensure();
            File.WriteAllText(Path.Combine(AppPaths.OutputDir, "mem-probe.txt"), text,
                new UTF8Encoding(false));

            Console.WriteLine(text);
            return 0;
        }

        // --icc-probe：把 ICC 链路的每一环打出来（关联键 / GDI 报告 / 显卡 LUT 是否可写）。
        // 排查「套了 ICC 没效果」时先跑这个。
        if (args.Any(a => a.Equals("--icc-probe", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            Console.WriteLine(IccService.Probe(args.Any(a => a.Equals("--live", StringComparison.OrdinalIgnoreCase))));
            return 0;
        }

        // --tune-apply-probe：真的写一次调色 LUT、回读确认变化，然后还原。
        if (args.Any(a => a.Equals("--tune-apply-probe", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var monitors = IccService.Monitors();
            if (monitors.Count == 0) { Console.WriteLine("没有找到显示器。"); return 1; }

            var monitor = monitors[0];
            var before = IccService.ReadRamp(monitor.DeviceName);

            var tune = new ManualTune { Gamma = 1.3, Brightness = 3, Contrast = 1.1, GainR = 1.05 };
            var (ok, message) = IccService.ApplyTune(monitor, tune);

            var after = IccService.ReadRamp(monitor.DeviceName);

            Console.WriteLine($"ApplyTune={ok}");
            Console.WriteLine(message);
            Console.WriteLine($"R128：{before?[128]} → {after?[128]}");
            Console.WriteLine($"变化：{Math.Abs((after?[128] ?? 0) - (before?[128] ?? 0))}/65535");

            var (restoreOk, restoreMessage) = IccService.RestoreLut(monitor);
            var back = IccService.ReadRamp(monitor.DeviceName);

            Console.WriteLine($"RestoreLut={restoreOk}　{restoreMessage}");
            Console.WriteLine($"还原后 R128={back?[128]}　线性={IccService.IsLinear(back)}");

            return ok && restoreOk ? 0 : 1;
        }

        // --tune-icc-probe：生成一个调色 ICC，再用「读回 + WPF ColorContext」双重验证它合法。
        if (args.Any(a => a.Equals("--tune-icc-probe", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var path = Path.Combine(Path.GetTempPath(), "sysdiag-tune-probe.icc");
            var tune = new ManualTune { Gamma = 1.30, Brightness = 3, Contrast = 1.10, GainR = 1.05 };

            var (ok, message) = IccService.ExportTuneIcc(tune, "SysDiag Probe", path);
            Console.WriteLine("导出：" + ok);
            Console.WriteLine(message);
            Console.WriteLine();

            var info = IccReader.Read(path);
            Console.WriteLine($"解析：valid={info.IsValid}　desc={info.Description}　space={info.ColorSpace}　v={info.Version}　gamma={info.Gamma:F2}");

            var vcgt = IccReader.ReadVcgt(path);
            Console.WriteLine($"vcgt：{vcgt.Describe()}");

            // 往返：导出的 vcgt 必须和内存里算出来的 LUT 逐项一致
            var expected = tune.BuildRamp();
            var actual = vcgt.IsValid ? vcgt.ToRamp(IccReader.VcgtData.Linear(), 1.0) : null;

            var diff = 0;
            if (actual != null)
                for (var i = 0; i < 768; i++) diff = Math.Max(diff, Math.Abs(expected[i] - actual[i]));

            Console.WriteLine($"往返：最大差 {diff}/65535（应为 0）");

            // 关键验证：WPF 的 ColorContext 走系统色彩管理打开这个文件，
            // 结构不合法会直接抛异常。这比「我们的解析器能读」有说服力得多。
            try
            {
                var context = new System.Windows.Media.ColorContext(new Uri(path));
                GC.KeepAlive(context);
                Console.WriteLine("系统色彩管理：已接受（ColorContext 打开成功）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("系统色彩管理：拒绝 → " + ex.Message);
            }

            return 0;
        }

        // --gen-notice-key：生成公告签名用的密钥对（私钥写盘，公钥打印出来贴进代码）。
        if (args.Any(a => a.Equals("--gen-notice-key", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            AppPaths.Ensure();

            var path = Path.Combine(Environment.CurrentDirectory, "keys", "notice-private.pem");
            var (ok, message) = RemoteNoticeService.GenerateKeyPair(path);

            Console.WriteLine(message);
            return ok ? 0 : 1;
        }

        // --sign-notice <明文.json> [输出.json]：给公告签名，产出的文件直接传 GitHub。
        if (args.Any(a => a.Equals("--sign-notice", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            AppPaths.Ensure();

            var index = Array.FindIndex(args, a => a.Equals("--sign-notice", StringComparison.OrdinalIgnoreCase));
            var input = index >= 0 && index + 1 < args.Length ? args[index + 1] : "";

            if (input.Length == 0 || !File.Exists(input))
            {
                Console.WriteLine("用法: --sign-notice <明文.json> [输出.json]");
                return 2;
            }

            var output = index + 2 < args.Length && !args[index + 2].StartsWith("--", StringComparison.Ordinal)
                ? args[index + 2]
                : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".", "notice.json");

            var key = Path.Combine(Environment.CurrentDirectory, "keys", "notice-private.pem");
            var (ok, message) = RemoteNoticeService.SignFile(input, output, key);

            Console.WriteLine(message);
            return ok ? 0 : 1;
        }

        // --notice-probe [本地文件]：拉一次公告（或验证本地文件）并打印解析/验签结果。
        if (args.Any(a => a.Equals("--notice-probe", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            AppPaths.Ensure();

            var probeIndex = Array.FindIndex(args, a => a.Equals("--notice-probe", StringComparison.OrdinalIgnoreCase));
            var localFile = probeIndex + 1 < args.Length && !args[probeIndex + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[probeIndex + 1]
                : "";

            var note = localFile.Length > 0 && File.Exists(localFile)
                ? RemoteNoticeService.Parse(File.ReadAllText(localFile))
                : RemoteNoticeService.LoadAsync().GetAwaiter().GetResult();

            if (localFile.Length > 0) note.Source = "local";

            Console.WriteLine($"来源={note.Source}　验签={(note.Verified ? "通过" : "失败")}　可显示={note.Showable}");
            Console.WriteLine($"版本={note.Version}　级别={note.Level}　到期={note.Expires?.ToString("yyyy-MM-dd") ?? "无"}");
            Console.WriteLine($"标题={note.Title}");
            Console.WriteLine($"内容={note.Message}");
            Console.WriteLine($"链接={note.Url}　{note.UrlText}");

            if (note.Error.Length > 0) Console.WriteLine("错误=" + note.Error);

            Console.WriteLine("缓存=" + RemoteNoticeService.CacheFile);
            return note.Verified ? 0 : 1;
        }

        // --nvidia-boost：生成「电源管理模式 = 最高性能优先」的 .nip，不开界面。
        if (args.Any(a => a.Equals("--nvidia-boost", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var (ok, message, _) = GameBoostService.GenerateNvidiaBoost();

            Console.WriteLine(message);
            return ok ? 0 : 1;
        }

        // --latency-probe：把延迟优化每一项（含码枪堂那几项）的状态打出来，不开窗口。
        if (args.Any(a => a.Equals("--latency-probe", StringComparison.OrdinalIgnoreCase)))
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            foreach (var check in LatencyService.Scan())
            {
                Console.WriteLine($"[{check.State}] {check.Group} / {check.Title}");
                Console.WriteLine("  " + check.Detail.Replace("\n", "\n  "));

                var actions = check.Options.Count > 0
                    ? string.Join(" | ", check.Options.Select(o => o.Label))
                    : check.ActionLabel;

                Console.WriteLine("  动作：" + (actions.Length == 0 ? "（只读）" : actions));
                Console.WriteLine();
            }

            return 0;
        }

        // --icc-restore：不打开窗口，直接把所有显示器恢复成「本程序改之前」的关联。
        // 给「套了配置之后画面不对、又进不去界面」的情况留一条命令行退路。
        if (options.IccRestore)
        {
            // 输出被重定向时设置编码会抛 IOException，忽略即可
            try { Console.OutputEncoding = Encoding.UTF8; } catch { }

            var monitors = IccService.Monitors();

            if (monitors.Count == 0)
            {
                Console.WriteLine("没有找到显示器。");
                return 1;
            }

            var failed = 0;

            foreach (var monitor in monitors)
            {
                var (ok, message) = IccService.RestoreDefault(monitor);
                Console.WriteLine((ok ? "[OK] " : "[失败] ") + monitor.Display + "：" + message);
                if (!ok) failed++;
            }

            Console.WriteLine("备份中剩余 " + IccService.BackupCount + " 台显示器。");
            return failed == 0 ? 0 : 1;
        }

        // --admin / --open-usn 是「以管理员身份重启」带回来的界面参数，走图形界面；
        // 其余 -- 开头的参数（--selftest / --usn / --pack …）走命令行分支。
        if (!options.HasUiIntent && args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            return SelfTest.Run(args);

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };

        // 兜底：任何未捕获异常都用内部对话框提示并记日志，
        // 不交给系统弹「程序遇到问题需要关闭」那种默认窗口。
        app.DispatcherUnhandledException += (_, e) =>
        {
            e.Handled = true;
            CrashLog.Write(e.Exception);

            try
            {
                UiDialog.Show(e.Exception.Message.Length > 0 ? e.Exception.Message : "未知错误",
                    "程序遇到一个错误（已记录）", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { /* 连对话框都起不来时不再弹别的东西 */ }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) CrashLog.Write(ex);
        };

        // 旧版暗色主题已废弃：现在只有一套浅色主题。
        // --legacy-theme 参数保留兼容（不报错），但同样加载 Theme.xaml——
        // Theme.Legacy.xaml 缺 GridBg 等资源，强行加载会在窗口构造时抛异常。
        var themeFile = "Theme.xaml";

        var themeDict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/{themeFile}", UriKind.Absolute)
        };
        app.Resources.MergedDictionaries.Add(themeDict);

        // 小屏兜底：项目里三分之二的窗口 Height 在 780~900，而 1366x768 笔记本的工作区只有约 728。
        // WPF 不会把超高的窗口自动缩回屏幕，结果是底部按钮被屏幕边缘切掉、鼠标点不到。
        // 在窗口加载时统一压到工作区高度内——一处兜住全部窗口，以后新增窗口也自动生效。
        var workHeight = SystemParameters.WorkArea.Height - 24;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window win && win.MaxHeight > workHeight)
                    win.MaxHeight = workHeight;
            }));

        // 自绘标题栏的样式由 MainWindow 与其 OpenChild 统一套用（见 ThemedWindow 样式）。

        // 标题栏不再走系统深色 API：Theme.xaml 的隐式 Window 样式已改为自绘浅色标题栏，
        // 若仍调用深色 API 会把自绘标题栏重新刷黑（两套机制互相打架）。

        AppPaths.Ensure();
        app.Run(new MainWindow(options));
        return 0;
    }
}
