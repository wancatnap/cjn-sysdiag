using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace SysDiag.Services;

/// <summary>
/// 布局体检：把窗口内容摘下来放进固定尺寸的宿主里量一遍，找出「画到框外面」的元素。
///
/// 为什么不用 Show()：项目里已经有教训——自检里 Show 窗口会触发 OnLastWindowClose，
/// 把后续检查一并带崩。这里只做 Measure/Arrange，不建 hwnd，也就没有副作用。
///
/// 只查一件事：元素相对根元素的矩形是否越界。文本被省略号截断不在这里判定
/// （WPF 不会告诉你"省略号后面还有字"），那类问题靠截图看。
/// </summary>
public static class LayoutAuditor
{
    /// <summary>一处越界。</summary>
    public sealed class Issue
    {
        public string Element { get; init; } = "";
        public double Right { get; init; }
        public double Bottom { get; init; }
        public string Detail { get; init; } = "";

        /// <summary>文本类问题：硬裁剪（没有省略号，直接被切掉）或加了省略号。</summary>
        public bool IsText { get; init; }

        public override string ToString() => IsText
            ? $"{Element} {Detail}"
            : $"{Element} 右越界 {Right:0.0}px / 下越界 {Bottom:0.0}px（{Detail}）";
    }

    /// <summary>越界超过这个像素数才算问题，亚像素误差不算。</summary>
    private const double Tolerance = 1.5;

    /// <summary>
    /// 量一个窗口。width/height 用窗口声明尺寸；返回空列表表示这块没越界。
    /// 注意：调用后原 Window 的 Content 会被摘掉，窗口只能丢弃，不能再用。
    /// </summary>
    public static List<Issue> Audit(Window window, double? width = null, double? height = null)
    {
        var issues = new List<Issue>();

        if (window.Content is not FrameworkElement content) return issues;

        var w = width ?? (window.Width > 0 ? window.Width : 900);
        var h = height ?? (window.Height > 0 ? window.Height : 700);

        // 摘下来独立测量：Window 本身没有 hwnd 时不会走真实布局
        window.Content = null;

        var host = new Border
        {
            Width = w,
            Height = h,
            Child = content
        };

        host.Measure(new Size(w, h));
        host.Arrange(new Rect(0, 0, w, h));
        host.UpdateLayout();

        Walk(content, content, issues, insideScroll: false, depth: 0);

        // 摘下来的内容放回去：同一个窗口可能要按默认尺寸和最小尺寸各测一遍
        host.Child = null;
        window.Content = content;

        return issues;
    }

    private static void Walk(DependencyObject node, FrameworkElement root, List<Issue> issues,
        bool insideScroll, int depth)
    {
        if (depth > 60) return;

        if (node is FrameworkElement fe)
        {
            // 滚动容器里的内容允许超出，这是它的职责
            var nowInsideScroll = insideScroll || fe is ScrollViewer or ScrollContentPresenter;

            if (!nowInsideScroll && fe.Visibility == Visibility.Visible && fe.ActualWidth > 0)
                CheckText(fe, issues);

            if (!nowInsideScroll && fe != root && fe.Visibility == Visibility.Visible
                && fe.ActualWidth > 0 && fe.ActualHeight > 0 && ShouldCheck(fe))
            {
                var rect = Bounds(fe, root);
                var overRight = rect.Right - root.ActualWidth;
                var overBottom = rect.Bottom - root.ActualHeight;
                var offLeft = -rect.Left;

                if (overRight > Tolerance || overBottom > Tolerance || offLeft > Tolerance)
                {
                    issues.Add(new Issue
                    {
                        Element = Describe(fe),
                        Right = Math.Max(0, overRight),
                        Bottom = Math.Max(0, overBottom),
                        Detail = $"根 {root.ActualWidth:0}x{root.ActualHeight:0}，元素右={rect.Right:0.0} 下={rect.Bottom:0.0}"
                    });
                }
            }

            insideScroll = nowInsideScroll;
        }

        var count = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            Walk(child, root, issues, insideScroll, depth + 1);
        }
    }

    /// <summary>
    /// 文本是否放不下。用 FormattedText 量自然宽度，和实际渲染宽度比：
    /// 差 1px 以上就说明放不下——没有 TextTrimming 的是硬切（严重），
    /// 有 TextTrimming 的会显示省略号（仍然丢信息，但属于设计选择，单独标注）。
    /// </summary>
    private static void CheckText(FrameworkElement fe, List<Issue> issues)
    {
        if (fe is not TextBlock tb) return;
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        if (tb.TextWrapping != TextWrapping.NoWrap) return;
        if (tb.ActualWidth <= 0) return;

        try
        {
            var dpi = VisualTreeHelper.GetDpi(tb);
            var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
            var formatted = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture,
                tb.FlowDirection, typeface, tb.FontSize, Brushes.Black, dpi.PixelsPerDip);

            var needed = formatted.WidthIncludingTrailingWhitespace;
            var available = tb.ActualWidth - tb.Padding.Left - tb.Padding.Right;

            if (needed <= available + 1) return;

            var trimmed = tb.TextTrimming != TextTrimming.None;
            issues.Add(new Issue
            {
                Element = Describe(tb),
                Detail = $"{(trimmed ? "省略号截断" : "硬裁剪")}：需要 {needed:0}px，可用 {available:0}px"
                         + $"（差 {needed - available:0}px）",
                IsText = true
            });
        }
        catch { }
    }

    /// <summary>
    /// 跳过那些「本来就该超出」的元素：
    /// 菜单/提示/装饰性图形，以及被显式裁掉的部分（ClipToBounds 的元素自己会兜住）。
    /// </summary>
    private static bool ShouldCheck(FrameworkElement fe)
    {
        if (fe is System.Windows.Shapes.Shape) return false;
        if (fe is System.Windows.Controls.Primitives.Popup) return false;

        // 面板类只作为容器参与，越界判定交给它的子元素
        return fe is not Panel || fe is Grid;
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement root)
    {
        try
        {
            var transform = element.TransformToAncestor(root);
            return transform.TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch
        {
            return Rect.Empty;
        }
    }

    private static string Describe(FrameworkElement fe)
    {
        var type = fe.GetType().Name;
        var name = string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name;

        var text = fe switch
        {
            TextBlock tb => "「" + Shorten(tb.Text) + "」",
            Button b => "[" + Shorten(b.Content?.ToString() ?? "") + "]",
            _ => ""
        };

        // 带上两层祖先：只报「Border 越界」等于没说，得知道它在哪个容器里
        var parent = fe.Parent as FrameworkElement;
        var grand = parent?.Parent as FrameworkElement;
        var chain = ParentLabel(parent) + (grand != null ? " < " + ParentLabel(grand) : "");

        return type + name + text + (chain.Length > 0 ? "  ← " + chain : "");
    }

    private static string ParentLabel(FrameworkElement? fe)
    {
        if (fe == null) return "";
        var name = string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name;
        return fe.GetType().Name + name;
    }

    private static string Shorten(string text) =>
        text.Length <= 18 ? text : text[..18] + "…";
}
