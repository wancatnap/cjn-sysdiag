using System.Windows;
using System.Windows.Media;

namespace SysDiag.Services;

/// <summary>
/// 二次元风格矢量图标库。
///
/// 设计约定（改图标只改这一个文件）：
/// <list type="bullet">
/// <item>画布统一 24×24，图形尽量落在 2.5~21.5 内，四周留白一致。</item>
/// <item>只用两种调子：<c>strong</c>（满色）与 <c>soft</c>（同色 22% 透明），
///       靠深浅区分结构，而不是靠描边粗细——20px 下才立得住。</item>
/// <item>所有转角、线头都是圆的（PenLineCap.Round / LineJoin.Round），
///       这是「可爱不硬朗」的关键，别改成 Miter。</item>
/// <item>颜色由调用方给（分组色），图标本身不含具体色值，所以同一套图形能配 7 种色。</item>
/// </list>
/// 生成结果按 (key, 颜色) 缓存并 Freeze，卡片列表滚动时零分配。
/// </summary>
public static class IconLibrary
{
    /// <summary>无对应图形时的兜底。</summary>
    private const string Fallback = "sparkle";

    private static readonly Dictionary<string, DrawingImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>图标出图缓存：key|颜色。</summary>
    public static DrawingImage Get(string key, string accentHex)
    {
        if (string.IsNullOrWhiteSpace(key)) key = Fallback;
        if (string.IsNullOrWhiteSpace(accentHex)) accentHex = "#4C6EF5";

        var id = key + "|" + accentHex;
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var hit)) return hit;
            var img = Build(key, accentHex);
            Cache[id] = img;
            return img;
        }
    }

    // =========================================================
    // 卡片 / 分组 → 图标
    // =========================================================

    /// <summary>按卡片的执行目标推断图标。目标比显示名稳定，改文案不会丢图标。</summary>
    public static string Resolve(string target, string kind)
    {
        var t = (target ?? "").Trim();

        // 目录类统一用文件夹；其余按目标名精确匹配
        var key = t.ToLowerInvariant() switch
        {
            "msinfo32.exe" => "cpu",
            "devmgmt.msc" => "devices",
            "gpu-info" => "gpu",
            "diskmgmt.msc" => "disk",
            "powercfg.cpl" => "power",
            "appwiz.cpl" => "apps",
            "services.msc" => "gear",
            "eventviewer" => "scroll",
            "/rel" => "chart",
            "error-report" => "alert",
            "cleanup-logs" => "broom",
            "disk-clean" => "broom",
            "find-logs" => "search",
            "analyze-zip" => "archive",
            "analyze-folder" => "folder",
            "analyze-files" => "archive",
            "delta-cache" => "crosshair",
            "taskmgr.exe" => "activity",
            "resmon.exe" => "line-chart",
            "perfmon.msc" => "chart",
            "shell:startup" => "rocket",
            "live-optimizer" => "sliders",
            "system-tune" => "gear",
            "dlss" => "sparkle",
            "ncpa.cpl" => "wifi",
            "network-report" => "pulse",
            "net-repair" => "bolt",
            "dns" => "globe",
            "mac-set" => "link",
            "mac-restore" => "toggle",
            "firewall.cpl" => "shield",
            "system-checkup" => "pulse",
            "disk-health" => "heart-disk",
            "update-list" => "cloud-down",
            "cheat-scan" => "detective",
            "monitor" => "eye",
            "sigma" => "book",
            "prefetch" => "clock",
            "handle" => "link",
            "machine" => "barcode",
            "driver-history" => "cpu",
            "usb-history" => "usb",
            "pnp-report" => "devices",
            "deep-trace" => "radar",
            "exec-trace" => "activity",
            "usn" => "trash",
            "baseline" => "bookmark",
            "logging-switch" => "toggle",
            "trace-cleanup" => "broom",
            "evidence-pack" => "box",
            "report-compare" => "compare",
            "shell:recent" => "clock",
            "shell:recyclebinfolder" => "trash",
            _ => ""
        };

        if (key.Length > 0) return key;

        // 没匹配上时按类型兜底：目录 / 快捷方式 / 其它
        return (kind ?? "").ToLowerInvariant() switch
        {
            "folder" => "folder",
            "msc" => "gear",
            "exe" => "apps",
            "shell" => "folder",
            _ => Fallback
        };
    }

    /// <summary>分组标题的小图标，按分组名取。</summary>
    public static string ResolveGroup(string groupName) => (groupName ?? "") switch
    {
        "系统与硬件" => "cpu",
        "日志与事件" => "scroll",
        "游戏日志" => "crosshair",
        "性能与进程" => "activity",
        "网络" => "wifi",
        "一键诊断" => "pulse",
        "取证" => "detective",
        "常用目录" => "folder",
        _ => Fallback
    };

    // =========================================================
    // 出图
    // =========================================================

    /// <summary>供图标总览（--icon-sheet）遍历用，顺序即展示顺序。</summary>
    public static readonly string[] AllKeys =
    {
        "cpu", "devices", "gpu", "disk", "power", "apps", "gear", "scroll",
        "chart", "alert", "broom", "folder", "search", "archive", "crosshair",
        "activity", "line-chart", "rocket", "sliders", "sparkle", "wifi", "globe",
        "shield", "bolt", "pulse", "heart-disk", "cloud-down", "detective", "eye",
        "book", "clock", "link", "barcode", "usb", "radar", "trash", "key",
        "bookmark", "toggle", "box", "compare"
    };

    private static DrawingImage Build(string key, string accentHex)
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(accentHex); }
        catch { accent = Color.FromRgb(0x4C, 0x6E, 0xF5); }

        var strong = Frozen(accent);
        var soft = Frozen(Color.FromArgb(56, accent.R, accent.G, accent.B));
        var mid = Frozen(Color.FromArgb(132, accent.R, accent.G, accent.B));
        var glass = Frozen(Color.FromArgb(232, 255, 255, 255));

        var g = new DrawingGroup();

        // 填充 / 描边 / 填充+同色描边（后者专治尖角，用来把多边形磨圆）
        void F(Geometry geo, Brush brush) => g.Children.Add(new GeometryDrawing(brush, null, geo));

        void S(Geometry geo, Brush brush, double width = 2.0)
            => g.Children.Add(new GeometryDrawing(null, Pen(brush, width), geo));

        void FS(Geometry geo, Brush brush, double width = 2.4)
            => g.Children.Add(new GeometryDrawing(brush, Pen(brush, width), geo));

        switch (key)
        {
            // ---------- 系统与硬件 ----------
            case "cpu":
                F(R(6.2, 6.2, 11.6, 11.6, 3.2), soft);
                S(R(7.4, 7.4, 9.2, 9.2, 2.4), strong, 1.8);
                F(R(10.1, 10.1, 3.8, 3.8, 1.2), strong);
                S(D("M12,3 V5.4 M12,18.6 V21 M3,12 H5.4 M18.6,12 H21"), strong, 1.7);
                break;

            case "devices":
                F(R(2.8, 5.2, 18.4, 11.6, 2.6), strong);
                F(R(5.6, 8, 12.8, 6, 1.4), glass);
                F(R(2, 18.2, 20, 2.8, 1.4), strong);
                break;

            case "gpu":
                F(R(2.4, 5.6, 19.2, 12.8, 2.6), strong);
                F(E(9.6, 12, 5.4, 5.4), glass);
                for (var i = 0; i < 3; i++) F(RotAbout(E(9.6, 8.7, 1.75, 2.7), i * 120, 9.6, 12), strong);
                F(E(9.6, 12, 1.5, 1.5), glass);
                F(R(15.6, 9.4, 4, 1.7, 0.85), glass);
                F(R(15.6, 12.9, 4, 1.7, 0.85), glass);
                break;

            case "disk":
                F(D("M4,7.2 V16.8 A8,3.4 0 0 0 20,16.8 V7.2 Z"), strong);
                F(E(12, 7.2, 8, 3.4), strong);
                S(D("M4,12 A8,3.4 0 0 0 20,12"), glass, 1.3);
                F(E(12, 7.2, 1.7, 0.75), glass);
                break;

            case "power":
                S(D("M15.7,6.6 A7.4,7.4 0 1 1 8.3,6.6"), strong, 2.3);
                S(D("M12,3.2 V11.8"), strong, 2.3);
                break;

            case "apps":
                F(R(3.4, 3.4, 7.4, 7.4, 2.2), strong);
                F(R(13.2, 3.4, 7.4, 7.4, 2.2), strong);
                F(R(3.4, 13.2, 7.4, 7.4, 2.2), strong);
                F(R(13.2, 13.2, 7.4, 7.4, 2.2), mid);
                break;

            case "gear":
                for (var i = 0; i < 6; i++) F(Rot(R(10.2, 2.2, 3.6, 3.6, 1.3), i * 60), strong);
                F(E(12, 12, 6.2, 6.2), strong);
                F(E(12, 12, 2.6, 2.6), glass);
                break;

            // ---------- 日志 ----------
            case "scroll":
                F(R(4.8, 2.6, 14.4, 18.8, 2.8), strong);
                F(R(8, 7, 8, 1.8, 0.9), glass);
                F(R(8, 11, 8, 1.8, 0.9), glass);
                F(R(8, 15, 5.2, 1.8, 0.9), glass);
                break;

            case "chart":
                F(R(2.8, 2.8, 18.4, 18.4, 3.6), soft);
                F(R(6.4, 13.8, 3.2, 5.6, 1.5), strong);
                F(R(10.7, 10.2, 3.2, 9.2, 1.5), strong);
                F(R(15, 12.2, 3.2, 7.2, 1.5), mid);
                break;

            case "alert":
                FS(D("M12,3.4 L21.2,19.8 L2.8,19.8 Z"), strong, 3.2);
                F(R(10.9, 9.4, 2.2, 5.4, 1.1), glass);
                F(E(12, 17.4, 1.25, 1.25), glass);
                break;

            case "broom":
                S(D("M16.4,3 L12.2,9.6"), strong, 2.2);
                FS(D("M6.6,13.2 L13.4,10.6 L17.6,17.8 L8.2,20.4 Z"), strong, 2.2);
                S(D("M10.4,13.2 L12.2,19"), glass, 1.3);
                S(D("M13.2,12.2 L15,18"), glass, 1.3);
                break;

            // ---------- 目录 / 文件 ----------
            case "folder":
                FS(D("M3.2,8.4 A2.2,2.2 0 0 1 5.4,6.2 H9.2 L11.6,8.6 H18.6 A2.2,2.2 0 0 1 20.8,10.8 V17.4 A2.2,2.2 0 0 1 18.6,19.6 H5.4 A2.2,2.2 0 0 1 3.2,17.4 Z"), strong, 1.6);
                F(R(5.6, 10.8, 12.8, 6.8, 1.8), glass);
                break;

            case "search":
                F(E(10.4, 10.4, 6.4, 6.4), soft);
                S(E(10.4, 10.4, 6.4, 6.4), strong, 2.3);
                S(D("M15.4,15.4 L20.6,20.6"), strong, 2.6);
                F(E(13.2, 7.6, 1.1, 1.1), strong);
                break;

            case "archive":
                F(R(3, 5.8, 18, 14.4, 2.8), strong);
                F(R(3, 5.8, 18, 3.4, 1.7), mid);
                F(R(9.9, 11.6, 4.2, 6, 1.6), glass);
                break;

            // ---------- 性能 ----------
            case "crosshair":
                S(E(12, 12, 7.8, 7.8), strong, 2.1);
                S(E(12, 12, 2.9, 2.9), strong, 1.7);
                S(D("M12,1.6 V4.8 M12,19.2 V22.4 M1.6,12 H4.8 M19.2,12 H22.4"), strong, 1.9);
                F(E(12, 12, 1.2, 1.2), strong);
                break;

            case "activity":
                S(D("M2.4,13.4 H6.9 L9.3,7.2 L12.8,18.6 L15.1,11.4 L17.3,13.4 H21.6"), strong, 2.3);
                break;

            case "line-chart":
                F(R(2.8, 2.8, 18.4, 18.4, 3.6), soft);
                S(D("M6,16.6 L9.8,12 L12.8,14.4 L17.9,7.6"), strong, 2.2);
                F(E(17.9, 7.6, 1.7, 1.7), strong);
                break;

            case "rocket":
                F(D("M12,2.4 C15.5,5.2 16.9,9 16.9,12.4 V14.8 H7.1 V12.4 C7.1,9 8.5,5.2 12,2.4 Z"), strong);
                F(D("M7.1,11.6 L4.2,15.8 V18.8 L7.1,15.4 Z"), mid);
                F(D("M16.9,11.6 L19.8,15.8 V18.8 L16.9,15.4 Z"), mid);
                F(D("M10.2,15.2 H13.8 L12,21.2 Z"), mid);
                F(E(12, 9.2, 1.9, 1.9), glass);
                break;

            case "sliders":
                S(D("M3.4,6.6 H20.6 M3.4,12 H20.6 M3.4,17.4 H20.6"), strong, 1.8);
                foreach (var (x, y) in new[] { (8.6, 6.6), (15.4, 12.0), (10.4, 17.4) })
                {
                    F(E(x, y, 2.4, 2.4), glass);
                    S(E(x, y, 2.4, 2.4), strong, 1.8);
                }
                break;

            case "sparkle":
                F(D("M11.2,3.2 C11.8,8 14.2,10.4 19,11 C14.2,11.6 11.8,14 11.2,18.8 C10.6,14 8.2,11.6 3.4,11 C8.2,10.4 10.6,8 11.2,3.2 Z"), strong);
                F(D("M19.4,2.6 C19.7,4.6 20.4,5.3 22.4,5.6 C20.4,5.9 19.7,6.6 19.4,8.6 C19.1,6.6 18.4,5.9 16.4,5.6 C18.4,5.3 19.1,4.6 19.4,2.6 Z"), mid);
                break;

            // ---------- 网络 ----------
            case "wifi":
                S(D("M4,9.2 A11.3,11.3 0 0 1 20,9.2"), strong, 2.2);
                S(D("M7,13.2 A7,7 0 0 1 17,13.2"), strong, 2.2);
                S(D("M9.9,17.1 A3.1,3.1 0 0 1 14.1,17.1"), strong, 2.2);
                F(E(12, 20.2, 1.4, 1.4), strong);
                break;

            case "globe":
                S(E(12, 12, 8.6, 8.6), strong, 2.1);
                S(E(12, 12, 4.0, 8.6), strong, 1.6);
                S(D("M3.6,12 H20.4"), strong, 1.6);
                break;

            case "shield":
                FS(D("M12,2.6 L20.2,5.6 V11.6 C20.2,16.2 16.8,19.8 12,21.4 C7.2,19.8 3.8,16.2 3.8,11.6 V5.6 Z"), strong, 1.6);
                S(D("M8.2,11.8 L11.1,14.7 L16,9.2"), glass, 2.2);
                break;

            case "bolt":
                F(D("M13.6,2.6 L6.2,13.6 H11.2 L10.2,21.4 L17.8,9.6 H12.6 Z"), strong);
                break;

            // ---------- 诊断 ----------
            case "pulse":
                F(E(12, 12, 9.0, 9.0), soft);
                S(E(12, 12, 9.0, 9.0), strong, 1.8);
                S(D("M6.4,12.2 H9.2 L10.7,8.8 L13,15.4 L14.4,12.2 H17.8"), strong, 1.8);
                break;

            case "heart-disk":
                F(D("M12,20.6 C6.4,16.8 3.4,13.6 3.4,9.9 C3.4,6.9 5.8,4.6 8.7,4.6 C10.4,4.6 11.5,5.4 12,6.4 C12.5,5.4 13.6,4.6 15.3,4.6 C18.2,4.6 20.6,6.9 20.6,9.9 C20.6,13.6 17.6,16.8 12,20.6 Z"), strong);
                S(D("M7.6,11.6 H9.8 L10.9,9.6 L12.4,14 L13.6,11.6 H16.4"), glass, 1.7);
                break;

            case "cloud-down":
                F(D("M7.6,18.4 A4.5,4.5 0 0 1 7.4,9.6 A5.9,5.9 0 0 1 18.6,10.6 A4.2,4.2 0 0 1 17.8,18.4 Z"), soft);
                S(D("M7.6,18.4 A4.5,4.5 0 0 1 7.4,9.6 A5.9,5.9 0 0 1 18.6,10.6 A4.2,4.2 0 0 1 17.8,18.4 Z"), strong, 1.8);
                S(D("M12,9.8 V14.6 M9.8,12.4 L12,14.8 L14.2,12.4"), strong, 1.9);
                break;

            // ---------- 取证 ----------
            case "detective":
                F(E(10.4, 10.4, 6.6, 6.6), glass);
                S(E(10.4, 10.4, 6.6, 6.6), strong, 2.2);
                S(D("M15.4,15.4 L20.8,20.8"), strong, 2.6);
                F(D("M7.3,10.4 C8.7,8.4 12.1,8.4 13.5,10.4 C12.1,12.4 8.7,12.4 7.3,10.4 Z"), strong);
                F(E(10.4, 10.4, 1.0, 1.0), glass);
                break;

            case "eye":
                F(D("M2.6,12 C5.5,7.4 8.8,5.6 12,5.6 C15.2,5.6 18.5,7.4 21.4,12 C18.5,16.6 15.2,18.4 12,18.4 C8.8,18.4 5.5,16.6 2.6,12 Z"), soft);
                S(D("M2.6,12 C5.5,7.4 8.8,5.6 12,5.6 C15.2,5.6 18.5,7.4 21.4,12 C18.5,16.6 15.2,18.4 12,18.4 C8.8,18.4 5.5,16.6 2.6,12 Z"), strong, 1.8);
                F(E(12, 12, 3.4, 3.4), strong);
                F(E(10.9, 10.9, 1.15, 1.15), glass);
                break;

            case "book":
                F(R(3.2, 3.4, 17.6, 17.2, 2.8), strong);
                F(R(6.2, 6.4, 11.8, 11.2, 1.6), glass);
                S(D("M9.2,9.4 H15.2 M9.2,12 H15.2 M9.2,14.6 H12.6"), strong, 1.5);
                break;

            case "clock":
                S(E(12, 12, 8.4, 8.4), strong, 2.2);
                S(D("M12,6.8 V12 L15.6,14"), strong, 2.2);
                break;

            case "link":
                F(E(9.2, 12, 5.4, 5.4), soft);
                F(E(14.8, 12, 5.4, 5.4), soft);
                S(E(9.2, 12, 5.4, 5.4), strong, 1.8);
                S(E(14.8, 12, 5.4, 5.4), strong, 1.8);
                break;

            case "usb":
                F(R(7.4, 8.6, 13.2, 6.8, 2.4), strong);
                F(R(3, 10.2, 5, 3.6, 1.2), glass);
                S(R(3, 10.2, 5, 3.6, 1.2), strong, 1.4);
                S(D("M7.8,9.6 V14.4"), glass, 1.3);
                F(E(17, 12, 1.0, 1.0), glass);
                break;

            case "key":
                F(E(8.4, 8.4, 4.6, 4.6), soft);
                S(E(8.4, 8.4, 4.6, 4.6), strong, 2.2);
                S(D("M11.7,11.7 L20.4,20.4"), strong, 2.4);
                S(D("M16.6,16.6 L18.9,14.3"), strong, 2.0);
                S(D("M18.4,18.4 L20.7,16.1"), strong, 2.0);
                break;

            case "barcode":
                F(R(2.6, 6.2, 18.8, 11.6, 2.4), soft);
                F(R(5.2, 9, 1.9, 6, 0.7), strong);
                F(R(8.4, 9, 1.2, 6, 0.5), strong);
                F(R(11, 9, 2.3, 6, 0.7), strong);
                F(R(14.8, 9, 1.2, 6, 0.5), strong);
                F(R(17.2, 9, 1.6, 6, 0.6), strong);
                break;

            case "radar":
                F(E(12, 12, 8.8, 8.8), soft);
                S(E(12, 12, 8.8, 8.8), strong, 1.8);
                S(E(12, 12, 5.2, 5.2), strong, 1.5);
                S(D("M12,12 L19,6.4"), strong, 2.0);
                F(E(15.8, 8.6, 1.4, 1.4), strong);
                break;

            case "trash":
                F(R(6.8, 8.2, 10.4, 11.6, 2.2), strong);
                S(D("M5,6.2 H19"), strong, 2.2);
                S(D("M9.8,3.4 H14.2"), strong, 2.2);
                S(D("M10.3,11.2 V16.8 M13.7,11.2 V16.8"), glass, 1.5);
                break;

            case "bookmark":
                FS(D("M7,3.2 H17 A1.8,1.8 0 0 1 18.8,5 V20.8 L12,16.4 L5.2,20.8 V5 A1.8,1.8 0 0 1 7,3.2 Z"), strong, 1.6);
                F(E(12, 9.2, 1.9, 1.9), glass);
                break;

            case "toggle":
                F(R(2.6, 7.4, 18.8, 9.2, 4.6), soft);
                S(R(3.2, 8, 17.6, 8, 4), strong, 2.0);
                F(E(16.4, 12, 3.3, 3.3), strong);
                break;

            case "box":
                F(D("M12,3 L20.4,7.6 L12,12.2 L3.6,7.6 Z"), soft);
                F(D("M3.6,7.6 L12,12.2 V21 L3.6,16.4 Z"), strong);
                F(D("M20.4,7.6 L12,12.2 V21 L20.4,16.4 Z"), mid);
                S(D("M12,3 L20.4,7.6 L12,12.2 L3.6,7.6 Z M3.6,7.6 V16.4 L12,21 L20.4,16.4 V7.6"), strong, 1.4);
                break;

            case "compare":
                F(R(2.8, 4.4, 10.6, 13.6, 2.4), soft);
                S(R(2.8, 4.4, 10.6, 13.6, 2.4), strong, 1.7);
                F(R(9.6, 5.8, 12, 14.8, 2.8), glass);
                F(R(10.6, 6.4, 10.6, 13.6, 2.4), strong);
                break;

            default:
                F(D("M11.2,3.2 C11.8,8 14.2,10.4 19,11 C14.2,11.6 11.8,14 11.2,18.8 C10.6,14 8.2,11.6 3.4,11 C8.2,10.4 10.6,8 11.2,3.2 Z"), strong);
                break;
        }

        g.Freeze();
        var image = new DrawingImage(g);
        image.Freeze();
        return image;
    }

    // =========================================================
    // 小工具
    // =========================================================

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen Pen(Brush brush, double width)
    {
        var pen = new Pen(brush, width)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    /// <summary>圆角矩形。</summary>
    private static Geometry R(double x, double y, double w, double h, double r)
        => new RectangleGeometry(new Rect(x, y, w, h), r, r);

    /// <summary>圆/椭圆（传等半径即正圆）。</summary>
    private static Geometry E(double cx, double cy, double rx, double ry)
        => new EllipseGeometry(new Point(cx, cy), rx, ry);

    /// <summary>路径：24×24 网格上的 SVG 风格小语言。</summary>
    private static Geometry D(string path) => Geometry.Parse(path);

    /// <summary>绕画布中心旋转，用于齿轮这类环形重复。</summary>
    private static Geometry Rot(Geometry geo, double angle)
    {
        geo.Transform = new RotateTransform(angle, 12, 12);
        return geo;
    }

    /// <summary>绕指定点旋转，用于风扇叶片这类偏心的环形重复。</summary>
    private static Geometry RotAbout(Geometry geo, double angle, double cx, double cy)
    {
        geo.Transform = new RotateTransform(angle, cx, cy);
        return geo;
    }
}
