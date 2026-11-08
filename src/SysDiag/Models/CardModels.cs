using SysDiag.Services;
using System.Windows.Media;

namespace SysDiag.Models;

/// <summary>
/// 统一取色与语义色表。**改颜色只需要改这里 + Theme.xaml 的 Level* 段**，
/// 窗口与界面代码一律引用语义名，不再写裸十六进制。
/// </summary>
public static class Palette
{
    // ---------------- 语义色（与 Theme.xaml 保持一致） ----------------

    /// <summary>等级：高危 / 线索。</summary>
    public const string WarnHex = "#E03131";

    /// <summary>等级：提示。</summary>
    public const string HintHex = "#F08C00";

    /// <summary>等级：正常。</summary>
    public const string OkHex = "#2F9E44";

    /// <summary>等级：可信（系统组件 / 反作弊）。</summary>
    public const string KnownHex = "#7048E8";

    /// <summary>无等级。</summary>
    public const string NoneHex = "#868E96";

    /// <summary>危险强调（比等级红更亮一档）。</summary>
    public const string DangerHex = "#D32F2F";

    public const string DangerSoftHex = "#FDECEC";
    public const string WarnSoftHex = "#FEF6E7";
    public const string OkSoftHex = "#E8F8F0";

    /// <summary>高饱和标签色。</summary>
    public const string HintChipHex = "#F08C00";

    // ---------------- 文本与表面 ----------------

    public const string PrimaryHex = "#DB4A7B";
    public const string PrimarySoftHex = "#FFE8F0";
    public const string PrimaryLineHex = "#FFC2D6";
    public const string InkHex = "#2E2330";
    public const string InkSecondaryHex = "#6A5860";
    public const string InkTertiaryHex = "#8A7580";
    public const string InkFaintHex = "#A6919B";
    public const string SurfaceHex = "#FFFFFF";
    public const string SurfaceRaiseHex = "#FCEFF4";
    public const string LineHex = "#F1E3E8";
    public const string LineBrightHex = "#E0C9D2";

    // ---------------- 常用画刷 ----------------

    public static Brush Warn => Get(WarnHex);
    public static Brush Hint => Get(HintHex);
    public static Brush Ok => Get(OkHex);
    public static Brush Known => Get(KnownHex);
    public static Brush None => Get(NoneHex);
    public static Brush Danger => Get(DangerHex);
    public static Brush DangerSoft => Get(DangerSoftHex);
    public static Brush WarnSoft => Get(WarnSoftHex);
    public static Brush OkSoft => Get(OkSoftHex);
    public static Brush HintChip => Get(HintChipHex);
    public static Brush Primary => Get(PrimaryHex);
    public static Brush PrimarySoft => Get(PrimarySoftHex);
    public static Brush PrimaryLine => Get(PrimaryLineHex);
    public static Brush Ink => Get(InkHex);
    public static Brush InkSecondary => Get(InkSecondaryHex);
    public static Brush InkTertiary => Get(InkTertiaryHex);
    public static Brush InkFaint => Get(InkFaintHex);
    public static Brush Surface => Get(SurfaceHex);
    public static Brush SurfaceRaise => Get(SurfaceRaiseHex);
    public static Brush Line => Get(LineHex);
    public static Brush LineBright => Get(LineBrightHex);

    /// <summary>等级 → 文本色 hex（warn / hint / ok / known / none）。</summary>
    public static string LevelHex(string level) => level switch
    {
        "warn" => WarnHex,
        "hint" => HintHex,
        "ok" => OkHex,
        "known" => KnownHex,
        _ => NoneHex
    };

    /// <summary>等级 → 高饱和标签色 hex。</summary>
    public static string LevelChipHex(string level) => level switch
    {
        "warn" => "#C92A2A",
        "hint" => HintChipHex,
        "ok" => "#2F9E44",
        "known" => KnownHex,
        _ => NoneHex
    };

    public static Brush LevelBrush(string level) => Get(LevelHex(level));

    // ---------------- 缓存 ----------------

    private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush Get(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) hex = "#4C6EF5";
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var cached)) return cached;
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }
    }
}

/// <summary>一张功能卡片。</summary>
public sealed class CardItem
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public string Kind { get; set; } = "exe";
    public string Target { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
    public string AccentHex { get; set; } = "#4C6EF5";
    public string TintHex { get; set; } = "#EDF2FF";

    /// <summary>显式指定的图标键；为空时按执行目标推断。同属「目录」的卡片靠它区分语义。</summary>
    public string IconOverride { get; private set; }

    /// <summary>卡片图标：按执行目标推断（见 IconLibrary），颜色跟着分组走。</summary>
    public string IconKey => IconOverride ?? IconLibrary.Resolve(Target, Kind);

    public ImageSource Icon => IconLibrary.Get(IconKey, AccentHex);

    /// <summary>卡片定义处链式指定图标，例如 <c>Card(...).WithIcon("clock")</c>。</summary>
    public CardItem WithIcon(string key)
    {
        IconOverride = key;
        return this;
    }

    public Brush AccentBrush => Palette.Get(AccentHex);
    public Brush TintBrush => Palette.Get(TintHex);
    public string Tip => $"{Name} — {Desc}\n{Kind}: {Target} {string.Join(" ", Args)}".Trim();
}

/// <summary>一个分组。</summary>
public sealed class CardGroup
{
    public string Name { get; set; } = "";
    public string AccentHex { get; set; } = "#4C6EF5";
    public string TintHex { get; set; } = "#EDF2FF";
    public List<CardItem> Items { get; set; } = new();

    public Brush AccentBrush => Palette.Get(AccentHex);
    public Brush TintBrush => Palette.Get(TintHex);
    public string CountText => $"{Items.Count} 项";

    /// <summary>分组图标：按分组名取，颜色同样是分组色。</summary>
    public string IconKey => IconLibrary.ResolveGroup(Name);

    public ImageSource Icon => IconLibrary.Get(IconKey, AccentHex);
}

/// <summary>一份待展示的报告。</summary>
public sealed class ReportPayload
{
    public string Title { get; set; } = "报告";
    public string Subtitle { get; set; } = "";
    public string EngineTag { get; set; } = "";
    public string HtmlPath { get; set; } = "";
    /// <summary>纯文本摘要，供「复制摘要」按钮使用；为空则不显示该按钮。</summary>
    public string PlainSummary { get; set; } = "";

    /// <summary>结构化数据：导出 .sdreport 与导入对比都靠它，为空时只能导出 HTML。</summary>
    public ReportData Data { get; set; }
}
