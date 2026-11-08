using System.Text;
using System.Windows;

namespace SysDiag.Services;

/// <summary>
/// 给所有「能无参构造」的窗口做布局体检，输出越界与文本截断清单。
///
/// 每个窗口量两组尺寸：它自己声明的尺寸，以及它的 MinWidth/MinHeight。
/// 后者才是真正容易被忽略的——开发者按 1000px 宽调好的界面，
/// 用户拖到 800px 时才会出现挤压，而截图验收通常只截默认尺寸。
/// </summary>
public static class LayoutAuditRunner
{
    public sealed class Finding
    {
        public string Size { get; init; } = "";
        public LayoutAuditor.Issue Issue { get; init; } = new();
    }

    public sealed class Result
    {
        public string Window { get; init; } = "";
        public List<Finding> Findings { get; init; } = new();
        public string Error { get; init; } = "";

        public int OverflowCount => Findings.Count(f => !f.Issue.IsText);
        public int TextCount => Findings.Count(f => f.Issue.IsText);
    }

    /// <summary>
    /// 跑一遍。screenW/screenH 传 0 表示用当前屏幕工作区；
    /// 传具体值可以模拟小屏（例如 1366x728），检查窗口被压到该尺寸后有没有挤坏。
    /// </summary>
    public static List<Result> Run(double screenW = 0, double screenH = 0)
    {
        var limitW = screenW > 0 ? screenW : SystemParameters.WorkArea.Width;
        var limitH = screenH > 0 ? screenH : SystemParameters.WorkArea.Height;

        var results = new List<Result>();

        var types = typeof(LayoutAuditRunner).Assembly.GetTypes()
            .Where(t => typeof(Window).IsAssignableFrom(t) && !t.IsAbstract)
            .Where(t => t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var type in types)
        {
            Window window;
            try
            {
                window = (Window)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                results.Add(new Result { Window = type.Name, Error = "构造失败：" + Root(ex) });
                continue;
            }

            var findings = new List<Finding>();

            try
            {
                var width = window.Width > 0 ? window.Width : 900;
                var height = window.Height > 0 ? window.Height : 700;

                Add(findings, width, height, window);

                // 屏幕放不下时窗口会被压到工作区（见 Program 里的统一 MaxHeight），
                // 这一组尺寸才是小屏用户真实看到的布局
                var fitW = Math.Min(width, limitW);
                var fitH = Math.Min(height, limitH);

                if (fitW < width - 1 || fitH < height - 1)
                    Add(findings, fitW, fitH, window);

                // 最小尺寸：只有比默认小才值得再测一遍
                var minW = Math.Min(window.MinWidth > 0 ? window.MinWidth : width, limitW);
                var minH = Math.Min(window.MinHeight > 0 ? window.MinHeight : height, limitH);

                if (minW < fitW - 1 || minH < fitH - 1)
                    Add(findings, minW, minH, window);

                results.Add(new Result { Window = type.Name, Findings = findings });
            }
            catch (Exception ex)
            {
                results.Add(new Result { Window = type.Name, Error = "测量失败：" + Root(ex) });
            }
        }

        return results;
    }

    private static void Add(List<Finding> findings, double width, double height, Window window)
    {
        var label = $"{width:0}x{height:0}";
        foreach (var issue in LayoutAuditor.Audit(window, width, height))
            findings.Add(new Finding { Size = label, Issue = issue });
    }

    public static string Render(List<Result> results)
    {
        var sb = new StringBuilder();

        var bad = results.Where(r => r.Findings.Count > 0)
            .OrderByDescending(r => r.OverflowCount)
            .ThenByDescending(r => r.TextCount)
            .ToList();
        var errors = results.Where(r => r.Error.Length > 0).ToList();

        var overflowTotal = results.Sum(r => r.OverflowCount);
        var textTotal = results.Sum(r => r.TextCount);

        sb.AppendLine($"时间 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"窗口 {results.Count} 个 · 越界 {overflowTotal} 处 · 文本放不下 {textTotal} 处 · 失败 {errors.Count} 个");
        sb.AppendLine();

        foreach (var r in bad)
        {
            sb.AppendLine($"[{r.Window}]  越界 {r.OverflowCount} · 文本 {r.TextCount}");

            foreach (var f in r.Findings.Where(f => !f.Issue.IsText).Take(8))
                sb.AppendLine($"    [{f.Size}] {f.Issue}");

            foreach (var f in r.Findings.Where(f => f.Issue.IsText).Take(10))
                sb.AppendLine($"    [{f.Size}] {f.Issue}");

            var shown = Math.Min(8, r.OverflowCount) + Math.Min(10, r.TextCount);
            if (r.Findings.Count > shown)
                sb.AppendLine($"    …另有 {r.Findings.Count - shown} 处");

            sb.AppendLine();
        }

        foreach (var r in errors)
            sb.AppendLine($"[跳过] {r.Window}  {r.Error}");

        return sb.ToString();
    }

    private static string Root(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return inner.GetType().Name + "：" + inner.Message;
    }
}
