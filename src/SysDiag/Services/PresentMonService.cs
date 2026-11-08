using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 帧时间采集：调用 Intel 开源的 <b>PresentMon</b>（GameTechDev/PresentMon，MIT）拿真实 FPS 与帧时间。
///
/// <para><b>为什么不自己写 ETW</b>：PresentMon 已经把 DXGI/D3D9 的 Present 事件、多显卡、
/// UWP、以及「按进程过滤」这些坑填完了，直接调它比自己啃 ETW 稳得多，也不用引第三方包。</para>
///
/// <para><b>为什么要用户自备 exe</b>：PresentMon 是原生 exe，塞进我们的单文件发布里既要签release
/// 又要跟着微软的版本走，不适合打包。放在下面任一位置即可被自动找到：</para>
/// <list type="bullet">
/// <item>本程序同目录（<c>PresentMon.exe</c> / <c>PresentMon-*.exe</c>）</item>
/// <item>同目录下的 <c>tools\</c></item>
/// <item><c>%LocalAppData%\SysDiag\tools\</c></item>
/// </list>
///
/// <para>找不到时本模块只报「未安装」，绝不编造帧率数据。</para>
/// </summary>
public static class PresentMonService
{
    /// <summary>PresentMon 可执行文件路径；找不到返回空串。</summary>
    public static string Locate()
    {
        var candidates = new List<string>();

        void AddFrom(string dir)
        {
            try
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

                candidates.AddRange(Directory
                    .EnumerateFiles(dir, "PresentMon*.exe", SearchOption.TopDirectoryOnly));
            }
            catch { /* 目录不可读就跳过 */ }
        }

        AddFrom(AppPaths.Root);
        AddFrom(Path.Combine(AppPaths.Root, "tools"));
        AddFrom(Path.Combine(AppPaths.DataDir, "tools"));

        // PATH 里的
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                AddFrom(dir.Trim());
        }
        catch { /* 忽略 */ }

        return candidates
            .Where(File.Exists)
            .OrderByDescending(p => Path.GetFileName(p).Equals("PresentMon.exe", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(p => p)
            .FirstOrDefault() ?? "";
    }

    public static bool Available => Locate().Length > 0;

    /// <summary>没装时给用户看的说明。</summary>
    public static string MissingHint =>
        "没有找到 PresentMon.exe。\n\n" +
        "帧率采集依赖 Intel 开源的 PresentMon（MIT）：\n" +
        "  1. 从 GitHub 的 GameTechDev/PresentMon Releases 下载；\n" +
        "  2. 把 PresentMon.exe 放到本程序同目录（或同目录下的 tools\\）；\n" +
        "  3. 回来点「测 20 秒帧率」。\n\n" +
        "不装也不影响挂机监控的其它功能——只是没有真实帧率数据可用。";

    // =========================================================
    // 采集
    // =========================================================

    /// <summary>
    /// 采集指定进程的帧数据到 CSV。返回 false 时 <paramref name="message"/> 里有原因。
    /// 不同版本 PresentMon 参数不同，这里按新→旧依次尝试。
    /// </summary>
    public static bool Capture(string processName, int seconds, string csvPath, out string message)
    {
        message = "";

        var exe = Locate();
        if (exe.Length == 0)
        {
            message = MissingHint;
            return false;
        }

        if (string.IsNullOrWhiteSpace(processName))
        {
            message = "没有指定要采集的进程名。";
            return false;
        }

        try
        {
            if (File.Exists(csvPath)) File.Delete(csvPath);
        }
        catch { /* 删不掉就让 PresentMon 自己覆盖 */ }

        // 参数兼容：2.x 用 -timed，1.x 用 -capture_time；-terminate_after_timed 只有新版认。
        var variants = new[]
        {
            $"-process_name {processName} -output_file \"{csvPath}\" -timed {seconds} -terminate_after_timed -stop_existing_session -no_console_stats",
            $"-process_name {processName} -output_file \"{csvPath}\" -timed {seconds} -terminate_after_timed -no_console_stats",
            $"-process_name {processName} -output_file \"{csvPath}\" -capture_time {seconds} -terminate_on_proc_exit",
            $"-process_name {processName} -output_file \"{csvPath}\" -capture_time {seconds}"
        };

        var errors = new List<string>();

        foreach (var args in variants)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) { errors.Add("无法启动 PresentMon"); continue; }

                var output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();

                if (!proc.WaitForExit((seconds + 25) * 1000))
                {
                    try { proc.Kill(); } catch { }
                }

                if (File.Exists(csvPath) && new FileInfo(csvPath).Length > 200)
                {
                    message = $"采集完成：{Path.GetFileName(csvPath)}";
                    return true;
                }

                errors.Add($"参数「{args}」失败：{(output.Length > 0 ? FirstLine(output) : $"退出码 {proc.ExitCode}")}");
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        message = "PresentMon 采集失败，试过的参数都不行：\n\n" + string.Join("\n", errors.Take(3)) +
                  "\n\n如果 PresentMon 版本差异较大，可以用它的 GUI 手动录一份 CSV，再把文件拖进本窗口。";
        return false;
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "";
        return line.Length > 160 ? line[..160] + "…" : line.Trim();
    }

    // =========================================================
    // 解析
    // =========================================================

    /// <summary>
    /// 解析 PresentMon 的 CSV。列名在不同版本间有差异（<c>msBetweenPresents</c> /
    /// <c>MsBetweenPresents</c> / <c>msBetweenDisplayChange</c>），这里按不区分大小写匹配。
    /// </summary>
    public static PresentStats Analyze(string csvPath)
    {
        if (!File.Exists(csvPath)) return new PresentStats();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(csvPath, Encoding.UTF8);
        }
        catch
        {
            return new PresentStats();
        }

        if (lines.Length < 2) return new PresentStats();

        var header = SplitCsv(lines[0]);
        var frameCol = IndexOf(header, "msBetweenPresents", "MsBetweenPresents", "msBetweenDisplayChange");
        var timeCol = IndexOf(header, "TimeInSeconds", "CPUStartTime", "TimeInMs");
        var procCol = IndexOf(header, "Application", "ProcessName");

        if (frameCol < 0) return new PresentStats();

        var times = new List<double>(1024);
        var frames = new List<double>(1024);

        // 有的导出会在末尾追加汇总行，遇到解析不了的行直接跳过
        for (var i = 1; i < lines.Length; i++)
        {
            var cells = SplitCsv(lines[i]);
            if (cells.Length <= frameCol) continue;

            if (!TryNum(cells[frameCol], out var ms) || ms <= 0 || ms > 5000) continue;

            var t = 0.0;
            if (timeCol >= 0 && timeCol < cells.Length) TryNum(cells[timeCol], out t);

            frames.Add(ms);
            times.Add(t);
        }

        if (frames.Count == 0) return new PresentStats();

        // 时间列可能是「秒」也可能是「毫秒」，用量级判断一下
        var span = 0.0;
        if (times.Count > 1)
        {
            var min = times.Min();
            var max = times.Max();
            span = max - min;
            if (span > 10000) span /= 1000.0;          // 毫秒 → 秒
        }

        if (span <= 0)
        {
            // 没有时间列就按帧时间累加
            span = frames.Sum() / 1000.0;
        }

        frames.Sort();

        return new PresentStats
        {
            Frames = frames.Count,
            Seconds = span,
            AvgFps = span > 0 ? frames.Count / span : 0,
            MedianMs = Percentile(frames, 0.50),
            P99Ms = Percentile(frames, 0.99),
            WorstMs = frames[^1],
            StutterPercent = frames.Count(f => f > Percentile(frames, 0.50) * 2) * 100.0 / frames.Count
        };
    }

    private static int IndexOf(string[] header, params string[] names)
    {
        for (var i = 0; i < header.Length; i++)
        {
            foreach (var name in names)
            {
                if (header[i].Trim().Trim('"').Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
            }
        }

        return -1;
    }

    private static string[] SplitCsv(string line)
        => line.Split(',');

    private static bool TryNum(string text, out double value)
        => double.TryParse(text.Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>已排序列表上的百分位（最近秩法，够用且不会插值出假数据）。</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;

        var index = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
