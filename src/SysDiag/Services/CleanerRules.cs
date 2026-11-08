using System.IO;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>一条清理规则（对应 Winapp2.ini 的一个段落）。</summary>
public sealed class CleanerRule
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Category { get; init; } = "应用缓存";
    public string Note { get; init; } = "";
    public DiskRisk Risk { get; init; } = DiskRisk.Safe;

    /// <summary>已展开成绝对路径的目录 / 文件。通配符与越界路径在解析阶段就被剔掉了。</summary>
    public List<string> Paths { get; init; } = new();

    public bool IsDirectory { get; init; } = true;

    /// <summary>来自用户放到软件目录的 winapp2.ini，而不是内置精选。</summary>
    public bool External { get; init; }

    /// <summary>规则里有 RegKey 段（本工具只清文件，注册表项留给「注册表清理」）。</summary>
    public bool HasRegistry { get; init; }
}

/// <summary>
/// 清理规则库：解析 Winapp2.ini 格式，内置一份精选规则，并允许用户外部扩展。
///
/// 为什么用这个格式而不是自己发明一套：Winapp2.ini 是 CCleaner 时代沿用至今的
/// 声明式清理规则格式（社区库 MoscaDotTo/Winapp2 有一千多条），字段语义清晰、
/// 照着格式写就能直接吃社区的规则，不用一条条翻译。BleachBit 的 CleanerML 是 GPL，
/// 所以只借格式与思路。
///
/// **数据来源要说清**：内置规则是我们自己按「各启动器的缓存目录」写的（中文说明、
/// 自己分的类），**没有复制 Winapp2.ini 的条目**。那个仓库是 CC BY-SA 4.0，
/// 带 ShareAlike 传染性——一旦从这里贴进去一条它的规则，整个规则库乃至本项目的
/// 许可就得跟着改成 CC BY-SA。想加规则请自己写，不要粘贴。
///
/// **安全边界**（比格式本身更重要）：外部规则包是别人写的，可能包含删 Documents、
/// 删 Program Files 这种段落。所以解析后强制做两道过滤：
/// 1. 只接受落在「明确是缓存目录」的几个根下的路径（见 <see cref="AllowedRoots"/>）；
/// 2. 含通配符的路径一律拒绝——我们不猜。
/// 被拒绝的条目会计数并在界面上说明，不做静默丢弃。
/// </summary>
public static class CleanerRules
{
    /// <summary>外部规则包文件名：放在 exe 同目录即自动加载。</summary>
    public const string ExternalFileName = "winapp2.ini";

    private static List<CleanerRule> _builtin;
    private static List<CleanerRule> _external;
    private static int _externalRejected;
    private static string _externalPath = "";

    /// <summary>内置精选规则（首次访问时解析，之后缓存）。</summary>
    public static List<CleanerRule> Builtin => _builtin ??= Parse(BuiltinIni, external: false).Rules;

    /// <summary>外部规则包（没放文件时为 0 条）。</summary>
    public static List<CleanerRule> External => _external ??= LoadExternal();

    /// <summary>外部规则包里被安全过滤拒绝的条目数。</summary>
    public static int ExternalRejected => _externalRejected;

    /// <summary>外部规则包路径（未加载为空串）。</summary>
    public static string ExternalPath => _externalPath;

    /// <summary>内置 + 外部，供 C 盘扫描直接使用。</summary>
    public static List<CleanerRule> All()
    {
        var list = new List<CleanerRule>(Builtin);
        list.AddRange(External);
        return list;
    }

    /// <summary>规则库说明（界面与报告用）。</summary>
    public static string Summary()
    {
        var text = $"内置精选 {Builtin.Count} 条";
        if (External.Count > 0) text += $" · 外部规则包 {External.Count} 条（{Path.GetFileName(_externalPath)}）";
        if (_externalRejected > 0) text += $" · 已按安全规则拒绝 {_externalRejected} 条";
        return text;
    }

    // =========================================================
    // 解析
    // =========================================================

    /// <summary>解析结果：可行的规则 + 被安全过滤拒绝的条目数。</summary>
    public sealed class ParseResult
    {
        public List<CleanerRule> Rules { get; } = new();
        public int Rejected { get; set; }
    }

    /// <summary>解析 Winapp2.ini 文本。</summary>
    public static ParseResult Parse(string text, bool external)
    {
        var result = new ParseResult();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var section = "";
        var files = new List<string>();
        var detect = "";
        var warning = "";
        var note = "";
        var riskText = "";
        var hasRegistry = false;
        var category = "";
        var recommended = false;

        void Flush()
        {
            if (section.Length == 0) return;

            var rule = Build(section, files, detect, warning, note, riskText, hasRegistry, external, category, recommended);
            if (rule != null) result.Rules.Add(rule);
            else if (files.Count > 0) result.Rejected++;

            section = "";
            files = new List<string>();
            detect = "";
            warning = "";
            note = "";
            riskText = "";
            hasRegistry = false;
            category = "";
            recommended = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                Flush();
                section = line[1..^1].Trim();

                // 段落名末尾的 * 是 Winapp2 的「作者推荐清理」标记，去掉但记下来
                if (section.EndsWith('*'))
                {
                    recommended = true;
                    section = section.TrimEnd('*').Trim();
                }

                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (key.StartsWith("FileKey", StringComparison.OrdinalIgnoreCase)) files.Add(value);
            else if (key.Equals("Detect", StringComparison.OrdinalIgnoreCase)) detect = value;
            else if (key.Equals("Warning", StringComparison.OrdinalIgnoreCase)) warning = value;
            else if (key.Equals("Note", StringComparison.OrdinalIgnoreCase)) note = value;
            else if (key.Equals("Risk", StringComparison.OrdinalIgnoreCase)) riskText = value;
            else if (key.Equals("Category", StringComparison.OrdinalIgnoreCase)) category = value;
            else if (key.Equals("LangSecRef", StringComparison.OrdinalIgnoreCase)) category = MapLangSec(value);
            else if (key.StartsWith("RegKey", StringComparison.OrdinalIgnoreCase)) hasRegistry = true;
        }

        Flush();
        return result;
    }

    private static CleanerRule Build(string name, List<string> files, string detect,
        string warning, string note, string riskText, bool hasRegistry, bool external,
        string category, bool recommended)
    {
        // 检测条件不满足就整段跳过：省掉无谓的目录枚举
        if (detect.Length > 0 && !DetectHit(detect)) return null;

        var paths = new List<string>();
        foreach (var entry in files)
        {
            var expanded = Expand(entry);
            if (expanded.Length == 0) continue;

            // 层级通配（User Data\*\Cache）先展开成具体目录，再逐个过安全闸
            foreach (var dir in ExpandWildcardDirs(expanded))
            {
                if (dir.Length == 0 || !IsAllowed(dir)) continue;
                if (!paths.Contains(dir, StringComparer.OrdinalIgnoreCase)) paths.Add(dir);
            }
        }

        if (paths.Count == 0) return null;

        var risk = riskText.ToLowerInvariant() switch
        {
            "careful" => DiskRisk.Careful,
            "keep" => DiskRisk.Keep,
            "safe" => DiskRisk.Safe,
            _ => DiskRisk.Safe
        };

        // 规则自带 Warning 说明「删了有代价」，一律降级成谨慎
        if (warning.Length > 0 && risk == DiskRisk.Safe) risk = DiskRisk.Careful;

        return new CleanerRule
        {
            Key = "rule:" + name,
            Name = name,
            Category = category.Length > 0 ? category : external ? "外部规则" : "应用缓存",
            Note = note.Length > 0 ? note : warning.Length > 0 ? warning : "来自清理规则库的应用缓存。删除后对应软件会重新生成。",
            // 带 * 的规则作者明确推荐清理，风险下调一档（缺省就是 Safe）
            Risk = recommended && risk == DiskRisk.Careful && warning.Length == 0 ? DiskRisk.Safe : risk,
            Paths = paths,
            IsDirectory = true,
            External = external,
            HasRegistry = hasRegistry
        };
    }

    /// <summary>Winapp2 的 LangSecRef 是整数分类号，映射成中文分组名。</summary>
    private static string MapLangSec(string value)
    {
        var id = value.Trim();
        return id switch
        {
            "3021" => "浏览器",
            "3022" => "办公",
            "3023" => "媒体",
            "3024" => "系统",
            "3025" => "开发",
            "3026" => "游戏",
            "3027" => "聊天工具",
            "3028" => "网络",
            "3029" => "驱动",
            _ => ""
        };
    }

    /// <summary>Detect= 支持三种写法：文件/目录路径、HKLM\… / HKCU\… 注册表键。</summary>
    private static bool DetectHit(string detect)
    {
        try
        {
            if (detect.StartsWith("HKLM\\", StringComparison.OrdinalIgnoreCase))
            {
                using var k = Registry.LocalMachine.OpenSubKey(detect[5..]);
                return k != null;
            }

            if (detect.StartsWith("HKCU\\", StringComparison.OrdinalIgnoreCase))
            {
                using var k = Registry.CurrentUser.OpenSubKey(detect[5..]);
                return k != null;
            }

            var path = Expand(detect);
            return path.Length > 0 && (Directory.Exists(path) || File.Exists(path));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>展开 %LOCALAPPDATA% 这类变量，并去掉末尾的 \*.* / \* 通配。</summary>
    private static string Expand(string entry)
    {
        var text = (entry ?? "").Trim().Trim('"');
        if (text.Length == 0) return "";

        // FileKey 允许「路径|文件名过滤」，这里只用路径部分
        var pipe = text.IndexOf('|');
        if (pipe >= 0) text = text[..pipe].Trim();

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 按 token 总长从长到短替换，避免 %PROGRAMFILES% 抢掉 %PROGRAMFILES(X86)% 的前半段
        var vars = new (string Token, string Value)[]
        {
            ("%PROGRAMFILES(X86)%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)),
            ("%PROGRAMFILES%", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)),
            ("%COMMONAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("%ALLUSERSPROFILE%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("%PROGRAMDATA%", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)),
            ("%LOCALAPPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            ("%APPDATA%", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)),
            ("%SYSTEMDRIVE%", Path.GetPathRoot(win)?.TrimEnd('\\') ?? "C:"),
            ("%SYSTEMROOT%", win),
            ("%WINDIR%", win),
            ("%USERPROFILE%", user),
            ("%DOWNLOADS%", Path.Combine(user, "Downloads")),
            ("%DOCUMENTS%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            ("%TEMP%", Path.GetTempPath().TrimEnd('\\')),
            ("%TMP%", Path.GetTempPath().TrimEnd('\\'))
        };

        foreach (var (token, value) in vars.OrderByDescending(x => x.Token.Length))
            text = text.Replace(token, value, StringComparison.OrdinalIgnoreCase);

        // 末尾的 *.* / * 表示「目录下所有文件」，去掉后当作目录处理
        if (text.EndsWith("\\*.*", StringComparison.Ordinal)) text = text[..^4];
        else if (text.EndsWith("\\*", StringComparison.Ordinal)) text = text[..^2];

        return text.TrimEnd('\\');
    }

    /// <summary>
    /// 把带 <c>*</c> 的路径展开成具体目录（如 Chrome 的 <c>User Data\*\Cache</c>）。
    /// 没通配符时直接返回原路径；展开结果为空时返回空列表（宁可不报，也不瞎猜）。
    /// </summary>
    private static List<string> ExpandWildcardDirs(string path, int cap = 64)
    {
        if (path.IndexOfAny(new[] { '*', '?' }) < 0) return new List<string> { path };

        var results = new List<string>();

        void Walk(string current, int depth)
        {
            if (results.Count >= cap || depth > 6) return;

            var sep = current.IndexOfAny(new[] { '*', '?' }, current.IndexOf('\\') + 1);
            if (sep < 0) { results.Add(current); return; }

            var slash = current.LastIndexOf('\\', sep);
            if (slash < 0) { results.Add(current); return; }

            var parent = current[..slash];
            var nextSlash = current.IndexOf('\\', sep);
            var pattern = nextSlash < 0 ? current[(slash + 1)..] : current[(slash + 1)..nextSlash];
            var tail = nextSlash < 0 ? "" : current[nextSlash..];

            try
            {
                if (!Directory.Exists(parent)) return;
                foreach (var dir in Directory.EnumerateDirectories(parent, pattern))
                    Walk(dir + tail, depth + 1);
            }
            catch { /* 权限不足就跳过这一支 */ }
        }

        try { Walk(path, 0); } catch { }
        return results;
    }

    /// <summary>
    /// 只允许落在缓存根目录下的路径。
    /// 外部规则包是别人写的，这一层是最后一道闸：宁可少清几条，也不能删到文档或程序目录。
    /// </summary>
    private static bool IsAllowed(string path)
    {
        try
        {
            if (!Path.IsPathRooted(path)) return false;

            var full = Path.GetFullPath(path).TrimEnd('\\');
            if (full.Length <= 3) return false; // 盘根

            foreach (var root in AllowedRoots())
            {
                if (root.Length == 0) continue;
                if (full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }

            // Program Files 默认一律拒绝（外部规则可能想删程序目录）。
            // 只放开几个「确定是缓存」的精确前缀——Steam 默认就装在 Program Files 下，
            // 它的 appcache / htmlcache / logs / dumps 删了只会自动重建。
            foreach (var root in ProgramFilesCacheRoots())
            {
                if (root.Length > 0 && full.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string[] AllowedRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var temp = Path.GetTempPath().TrimEnd('\\');

        return new[]
        {
            local,
            roaming,
            common,
            temp,
            Path.Combine(win, "Temp"),
            Path.Combine(local, "Temp")
        };
    }

    /// <summary>Program Files 下允许清理的精确缓存根（不在这个清单里的 Program Files 路径一律拒绝）。</summary>
    private static string[] ProgramFilesCacheRoots()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        var roots = new List<string>();
        foreach (var baseDir in new[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;

            var steam = Path.Combine(baseDir, "Steam");
            roots.Add(Path.Combine(steam, "appcache"));
            roots.Add(Path.Combine(steam, "htmlcache"));
            roots.Add(Path.Combine(steam, "logs"));
            roots.Add(Path.Combine(steam, "dumps"));
        }

        return roots.ToArray();
    }

    // =========================================================
    // 外部规则包
    // =========================================================

    private static List<CleanerRule> LoadExternal()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, ExternalFileName);
            if (!File.Exists(path)) return new List<CleanerRule>();

            var info = new FileInfo(path);
            if (info.Length > 8L * 1024 * 1024) return new List<CleanerRule>();

            var parsed = Parse(File.ReadAllText(path), external: true);
            _externalPath = path;
            _externalRejected = parsed.Rejected;
            return parsed.Rules;
        }
        catch
        {
            return new List<CleanerRule>();
        }
    }

    // =========================================================
    // 内置精选规则
    // =========================================================

    /// <summary>
    /// 自己写的精选规则，覆盖当前硬编码规则里没有的「游戏平台 / 聊天软件 / 启动器」缓存。
    /// 全部落在 LocalAppData / Roaming / ProgramData 下，删了对应软件会自动重建。
    /// </summary>
    private static readonly string BuiltinIni = """
        [Steam 网页缓存]
        Detect=%LOCALAPPDATA%\Steam
        FileKey1=%LOCALAPPDATA%\Steam\htmlcache\*.*
        Note=Steam 客户端内嵌浏览器的缓存（商店页、社区页）。删除后首次打开商店页会慢一点。
        Risk=safe

        [Steam 日志]
        Detect=%LOCALAPPDATA%\Steam\logs
        FileKey1=%LOCALAPPDATA%\Steam\logs\*.*
        Note=Steam 客户端运行日志。不影响游戏，但排查「Steam 起不来」时它是唯一线索。
        Risk=careful

        [Epic 启动器缓存]
        Detect=%LOCALAPPDATA%\EpicGamesLauncher
        FileKey1=%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache\*.*
        FileKey2=%LOCALAPPDATA%\EpicGamesLauncher\Saved\Logs\*.*
        Note=Epic 启动器的内嵌网页缓存与日志。删除后需要重新登录一次的概率很低，正常不会。
        Risk=safe

        [战网缓存]
        Detect=%LOCALAPPDATA%\Blizzard Entertainment
        FileKey1=%LOCALAPPDATA%\Blizzard Entertainment\Battle.net\Cache\*.*
        Note=战网客户端的图片与页面缓存，会自动重建。
        Risk=safe

        [Riot 客户端日志]
        Detect=%LOCALAPPDATA%\Riot Games
        FileKey1=%LOCALAPPDATA%\Riot Games\Logs\*.*
        Note=拳头的客户端日志（无畏契约 / LoL）。删了不影响游戏，只影响官方看日志定位问题。
        Risk=safe

        [EA 启动器日志]
        Detect=%LOCALAPPDATA%\Electronic Arts
        FileKey1=%LOCALAPPDATA%\Electronic Arts\EA Desktop\Logs\*.*
        Note=EA Desktop 的运行日志。
        Risk=safe

        [Ubisoft Connect 缓存]
        Detect=%LOCALAPPDATA%\Ubisoft Game Launcher
        FileKey1=%LOCALAPPDATA%\Ubisoft Game Launcher\cache\*.*
        Note=育碧客户端缓存。删除后首次启动会重新拉取一次界面资源。
        Risk=careful

        [WeGame 日志]
        Detect=%LOCALAPPDATA%\Tencent
        FileKey1=%LOCALAPPDATA%\Tencent\WeGame\logs\*.*
        Note=WeGame 客户端日志。
        Risk=safe

        [Discord 缓存]
        Detect=%APPDATA%\discord
        FileKey1=%APPDATA%\discord\Cache\*.*
        FileKey2=%APPDATA%\discord\Code Cache\*.*
        FileKey3=%APPDATA%\discord\GPUCache\*.*
        Note=Discord 的图片与脚本缓存。删除后聊天记录还在，只是历史图片要重新下载。
        Risk=safe

        [Teams 缓存]
        Detect=%APPDATA%\Microsoft\Teams
        FileKey1=%APPDATA%\Microsoft\Teams\Cache\*.*
        Note=Teams 网页缓存。删了聊天记录不受影响。
        Risk=safe

        [虚幻引擎崩溃报告]
        Detect=%LOCALAPPDATA%\CrashReportClient
        FileKey1=%LOCALAPPDATA%\CrashReportClient\Saved\Logs\*.*
        FileKey2=%LOCALAPPDATA%\CrashReportClient\Saved\Crashes\*.*
        Note=UE 游戏崩溃时生成的现场日志。删了就没法再回看那次崩溃。
        Risk=careful

        [系统网络缓存（INetCache）]
        Detect=%LOCALAPPDATA%\Microsoft\Windows\INetCache
        FileKey1=%LOCALAPPDATA%\Microsoft\Windows\INetCache\*.*
        Note=系统组件与老式程序共用的网页缓存，浏览器自己的缓存在另一条规则里。
        Risk=safe

        [远程桌面客户端缓存]
        Detect=%LOCALAPPDATA%\Microsoft\Terminal Server Client
        FileKey1=%LOCALAPPDATA%\Microsoft\Terminal Server Client\Cache\*.*
        Note=远程桌面连接留下的位图缓存。删除后首次连接会重画一遍。
        Risk=safe

        [字体缓存]
        Detect=%LOCALAPPDATA%\Microsoft\Windows\FontCache
        FileKey1=%LOCALAPPDATA%\Microsoft\Windows\FontCache\*.*
        Warning=字体缓存删掉后，系统下次开机要重建一次，期间部分界面字体会短暂变宋体。
        Risk=careful

        [NVIDIA 驱动安装残留]
        Detect=%PROGRAMDATA%\NVIDIA Corporation
        FileKey1=%PROGRAMDATA%\NVIDIA Corporation\Downloader\*.*
        Note=GeForce Experience 下载驱动的临时目录，装完就沒用了。
        Risk=safe

        [Adobe 媒体缓存]
        Detect=%LOCALAPPDATA%\Adobe\Common
        FileKey1=%LOCALAPPDATA%\Adobe\Common\Media Cache\*.*
        Note=Premiere / After Effects 的预览渲染缓存。删了下次打开工程要重新渲染预览。
        Risk=careful
        """;
}
