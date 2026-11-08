using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 把外挂痕迹体检结果画成一张 PNG 长图。
///
/// 为什么不用「渲染 HTML 再截图」：那需要 WebBrowser（IE 内核）或外部浏览器，
/// 前者在精简系统上常被移除，后者窗口抢不到前台就截空。自己用 DrawingVisual 排版，
/// 没有外部依赖，输出尺寸与内容完全可控。
///
/// 输出的是**结论图**，不是整份报告的逐字复制：标题、元信息、风险分、每组条目的标题与级别，
/// 外加底部那段必须写明的效力说明。每条正文（含注册表路径、事件 ID）仍在 HTML 报告里——
/// 全塞进图里会长到没法看，也不方便转发。
/// </summary>
public static class ReportImageExporter
{
    private const double PageWidth = 920;
    private const double Margin = 44;
    private const double ContentWidth = PageWidth - Margin * 2;

    /// <summary>最多画多少条，超出写「另有 N 条」。图太长既不好读也占内存。</summary>
    private const int MaxFindings = 60;

    // 颜色跟主题保持一致，避免图与软件看起来像两个产品
    private static readonly Brush PageBrush = Freeze(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Brush CardBrush = Freeze(Colors.White);
    private static readonly Brush LineBrush = Freeze(Color.FromRgb(0xE4, 0xE4, 0xE7));
    private static readonly Brush InkBrush = Freeze(Color.FromRgb(0x1A, 0x1A, 0x1D));
    private static readonly Brush InkSoftBrush = Freeze(Color.FromRgb(0x6B, 0x6B, 0x74));
    private static readonly Brush InkFaintBrush = Freeze(Color.FromRgb(0x9A, 0x9A, 0xA4));
    private static readonly Brush WarnBrush = Freeze(Color.FromRgb(0xC9, 0x2A, 0x2A));
    private static readonly Brush HintBrush = Freeze(Color.FromRgb(0xE8, 0x59, 0x0C));
    private static readonly Brush OkBrush = Freeze(Color.FromRgb(0x2F, 0x9E, 0x44));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>
    /// 图片底部必须写明的效力说明。
    ///
    /// 这段文字是产品要求的一部分，不是装饰：图会被单独转发出去，离开软件上下文后
    /// 很容易被当成「检测结论」。所以它必须在图上，且写清楚「只能证明大概率没开挂」。
    /// 做成常量是为了自检能断言它存在，也避免以后有人改图时顺手删掉。
    /// </summary>
    public const string DisclaimerText =
        "这份图能说明什么：一次只读扫描没有发现明显线索。\n" +
        "不能说明什么：没有线索不代表一定没开挂——DMA 设备、内核级实现、已被清理的痕迹、" +
        "以及跑在另一台机器上的方案，都可能不在本机留下可读的证据；反过来，部分线索也可能来自正常软件或精简系统。\n" +
        "所以它最多只能作为「大概率没开挂」的参考，不能当作判定依据。\n" +
        "复核请用同时生成的 HTML 报告，里面有每条结论的注册表路径、事件 ID 与文件路径。";

    private static readonly Typeface UiFace = new(
        new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface UiBold = new(
        new FontFamily("Microsoft YaHei UI, Microsoft YaHei, Segoe UI"),
        FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Typeface MonoFace = new(
        new FontFamily("Consolas, Cascadia Mono, Courier New"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>导出的结果。</summary>
    public sealed class ExportResult
    {
        public bool Ok { get; init; }
        public string Path { get; init; } = "";
        public string Message { get; init; } = "";
    }

    /// <summary>
    /// 生成图片。默认写到 outputs 目录，文件名带时间戳。
    /// </summary>
    public static ExportResult Export(CheatScanResult result, string filePath = "")
    {
        if (result == null) return new ExportResult { Ok = false, Message = "还没有体检结果。" };

        try
        {
            AppPaths.Ensure();

            if (string.IsNullOrWhiteSpace(filePath))
                // 名字必须与 HTML 报告同前缀（时间在前），否则资源管理器按拼音排序时
                // 「外挂体检-…」会被排到列表最下面，用户翻半天以为没生成。
                filePath = System.IO.Path.Combine(AppPaths.OutputDir,
                    $"{result.FinishedAt:yyyyMMdd-HHmmss}-外挂痕迹体检.png");

            var visual = BuildVisual(result, out var height, out var truncated);

            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(PageWidth), (int)Math.Ceiling(height), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using (var stream = File.Create(filePath))
                encoder.Save(stream);

            var note = truncated ? "（条目过多，图里只画了前 60 条，完整清单见 HTML 报告）" : "";
            return new ExportResult { Ok = true, Path = filePath, Message = note };
        }
        catch (Exception ex)
        {
            return new ExportResult { Ok = false, Message = "导出失败：" + ex.Message };
        }
    }

    private static DrawingVisual BuildVisual(CheatScanResult r, out double height, out bool truncated)
    {
        var entries = r.Findings
            .OrderBy(f => LevelOrder(f.Level))
            .ThenBy(f => f.Group)
            .ToList();

        truncated = entries.Count > MaxFindings;
        if (truncated) entries = entries.Take(MaxFindings).ToList();

        var groups = entries.GroupBy(f => f.Group).ToList();

        // 先排版再画：高度要提前算出来，RenderTargetBitmap 是一次性分配的。
        var blocks = new List<(FormattedText Text, double Gap)>();

        void Add(string text, double size, Brush brush, Typeface face, double gap = 0, double width = ContentWidth)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                face, size, brush, 1.0)
            {
                MaxTextWidth = width,
                LineHeight = size * 1.55
            };
            blocks.Add((ft, gap));
        }

        // ---------- 头 ----------
        Add("外挂痕迹体检", 26, InkBrush, UiBold, 6);
        Add($"{r.OperatorText}　·　{r.MachineCodeText}", 12, InkSoftBrush, MonoFace, 2);
        Add($"{r.Machine}　·　{r.FinishedAt:yyyy-MM-dd HH:mm:ss}　·　耗时 {r.ElapsedText}", 11, InkFaintBrush, UiFace, 22);

        // ---------- 分数 ----------
        Add($"{r.Score}", 40, ScoreBrush(r.Score), UiBold, 14);
        Add(r.ScoreLevel + "　·　" + r.Verdict, 13, InkSoftBrush, UiFace, 4);
        Add($"线索 {r.WarnCount}　提示 {r.HintCount}　正常 {r.OkCount}　共 {r.Findings.Count} 条", 11, InkFaintBrush, UiFace, 26);

        // ---------- 分组条目 ----------
        foreach (var g in groups)
        {
            Add(g.Key, 13, InkBrush, UiBold, 10);

            foreach (var f in g)
            {
                var mark = f.Level switch
                {
                    "warn" => "● 线索",
                    "hint" => "○ 提示",
                    _ => "· 正常"
                };
                Add($"{mark}　{f.Title}", 11.5, LevelBrush(f.Level), UiFace, 3, ContentWidth - 10);
            }

            Add("", 4, InkFaintBrush, UiFace, 8);
        }

        if (truncated)
            Add($"另有 {r.Findings.Count - MaxFindings} 条未画入，见完整报告。", 11, InkFaintBrush, UiFace, 12);

        // ---------- 效力说明（占位与排版都算出来） ----------
        Add("", 6, InkFaintBrush, UiFace, 18);
        Add("这份图能说明什么，不能说明什么", 12, InkBrush, UiBold, 6);

        Add(DisclaimerText, 10.5, InkSoftBrush, UiFace, 4);

        // 计算总高
        height = Margin * 2 + 24;   // 上下留白 + 卡片内边距
        foreach (var (ft, gap) in blocks)
            height += ft.Height + gap;

        // ---------- 画 ----------
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(PageBrush, null, new Rect(0, 0, PageWidth, height));

            var card = new Rect(Margin - 22, Margin - 22, ContentWidth + 44, height - (Margin - 22) * 2);
            dc.DrawRoundedRectangle(CardBrush, new Pen(LineBrush, 1), card, 14, 14);

            var y = Margin;
            foreach (var (ft, gap) in blocks)
            {
                if (ft.Text.Length > 0)
                    dc.DrawText(ft, new Point(Margin, y));
                y += ft.Height + gap;
            }
        }

        // DrawingVisual 是 Freezable，但只能在空闲状态冻结；这里刚关闭 DrawingContext，
        // 直接返回即可，RenderTargetBitmap 会自己处理线程亲和。
        return visual;
    }

    private static int LevelOrder(string level) => level switch
    {
        "warn" => 0,
        "hint" => 1,
        _ => 2
    };

    private static Brush LevelBrush(string level) => level switch
    {
        "warn" => WarnBrush,
        "hint" => HintBrush,
        _ => OkBrush
    };

    private static Brush ScoreBrush(int score) =>
        score >= 60 ? WarnBrush : score >= 25 ? HintBrush : OkBrush;
}
