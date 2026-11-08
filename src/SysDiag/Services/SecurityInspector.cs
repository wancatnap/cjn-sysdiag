using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 在日志里找外部软件（外挂、注入器、调试器）的痕迹。
/// 原则：宁可漏报，不要误报——游戏日志里 inject/hook/openprocess 这类词大量出现在正常内容中
/// （如 Medic_Injection 医疗注射、Skill_Hook 钩索、UInteractorOperatorProcessComponent），
/// 因此只使用带词边界的整词或完整短语匹配。
/// </summary>
public static class SecurityInspector
{
    private const int MaxHits = 200;

    /// <summary>外挂 / 调试 / 注入工具的整词特征。</summary>
    private static readonly Regex ToolPattern = new(
        @"(?<![a-z0-9])(cheatengine|x64dbg|x32dbg|ollydbg|ida64|ida32|frida|processhacker|procexp|" +
        @"speedhack|aimbot|wallhack|autohotkey|easyhook|kdmapper|winio|extremeinjector|" +
        @"外挂|作弊|自瞄|透视|按键精灵|鼠大侠)(?![a-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>反作弊侧的判定 / 拦截措辞。</summary>
    private static readonly Regex AcPattern = new(
        @"(detected\s+(cheat|hack|untrusted)|blocked\s+module|untrusted\s+module|" +
        @"integrity\s+(check\s+)?failed|signature\s+mismatch|" +
        @"检测到.{0,8}(作弊|外挂|异常模块|非法模块)|已拦截.{0,8}(模块|进程)|" +
        @"sandbox\s+violation|memory\s+tamper)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从临时目录 / AppData 加载 DLL 的可疑模块加载。</summary>
    private static readonly Regex ModulePathPattern = new(
        @"(LoadLibrary|LoadModule|inject(ed)?)[^\r\n]{0,80}(\\Temp\\|\\AppData\\Local\\Temp\\)[^\s""]{1,80}\.dll",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>廉价前置过滤：这行不可能命中时才跳过正则。</summary>
    private static readonly string[] FastTokens =
    {
        "cheat", "dbg", "frida", "procexp", "processhacker", "speedhack", "aimbot", "wallhack",
        "autohotkey", "easyhook", "kdmapper", "winio", "inject", "外挂", "作弊", "自瞄", "透视",
        "按键", "鼠大侠", "blocked", "untrusted", "integrity", "violation", "tamper",
        "检测到", "已拦截"
    };

    public static bool MayContain(string line)
    {
        if (string.IsNullOrEmpty(line)) return false;
        foreach (var t in FastTokens)
            if (line.Contains(t, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>检查一行日志，命中则返回线索。</summary>
    public static SecurityHit Inspect(string file, int lineNo, string line)
    {
        if (!MayContain(line)) return null;

        var text = line.Trim();
        if (text.Length == 0) return null;

        if (AcPattern.IsMatch(text))
        {
            return new SecurityHit
            {
                Category = "反作弊判定",
                Indicator = MatchOf(AcPattern, text),
                File = file,
                Line = lineNo,
                Content = Clip(text),
                Strength = "强"
            };
        }

        if (ModulePathPattern.IsMatch(text))
        {
            return new SecurityHit
            {
                Category = "可疑模块加载",
                Indicator = "从 Temp/AppData 加载 DLL",
                File = file,
                Line = lineNo,
                Content = Clip(text),
                Strength = "中"
            };
        }

        if (ToolPattern.IsMatch(text))
        {
            return new SecurityHit
            {
                Category = "已知工具名",
                Indicator = MatchOf(ToolPattern, text),
                File = file,
                Line = lineNo,
                Content = Clip(text),
                Strength = "弱"
            };
        }

        return null;
    }

    /// <summary>本机 Prefetch 里是否有已知工具的运行痕迹（需要管理员权限，取不到会给出说明）。</summary>
    public static List<string> ScanPrefetch(out string note)
    {
        note = null;
        var result = new List<string>();

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");

            if (!Directory.Exists(dir))
            {
                note = "未找到 Prefetch 目录（可能已禁用）";
                return result;
            }

            var files = Directory.GetFiles(dir, "*.pf");
            if (files.Length == 0)
            {
                note = "Prefetch 目录不可读（需要管理员权限）";
                return result;
            }

            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f) ?? "";
                // Prefetch 文件名形如 CHEATENGINE.EXE-1A2B3C4D
                if (ToolPattern.IsMatch(name)) result.Add(name);
            }

            note = $"已检查 {files.Length:N0} 条 Prefetch 记录";
        }
        catch (Exception ex)
        {
            note = "Prefetch 检查失败：" + ex.Message;
        }

        return result;
    }

    /// <summary>当前进程里是否有已知工具在运行（只能反映此刻）。</summary>
    public static List<string> ScanRunningProcesses()
    {
        var result = new List<string>();
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (ToolPattern.IsMatch(p.ProcessName)) result.Add(p.ProcessName);
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static string MatchOf(Regex re, string text)
    {
        var m = re.Match(text);
        return m.Success ? m.Value : "";
    }

    private static string Clip(string s) => s.Length > 200 ? s[..200] + "…" : s;
}
