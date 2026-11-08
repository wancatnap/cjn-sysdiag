using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SysDiag.Services;

/// <summary>
/// Windows 里大量「名字」存的不是文本，而是资源引用：
/// <c>@%SystemRoot%\system32\FirewallAPI.dll,-23304</c>、<c>@oem36.inf,%logi_lamparray_service.SvcDesc%</c>。
/// 直接显示就是这串乱码，必须先解引用。
///
/// 两种形式要分开处理：
/// 1. DLL 字符串表 → SHLoadIndirectString 能直接取；
/// 2. 厂商 INF 的 [Strings] 段 → SHLoadIndirectString 找不到 INF，得自己按 %SystemRoot%\INF 找文件读段。
/// </summary>
internal static class MuiString
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string source, StringBuilder outBuffer, int outBufferSize,
        IntPtr reserved);

    /// <summary>解析资源引用。不是资源引用时原样返回；解析不出来时返回空串（调用方自己决定退回什么）。</summary>
    public static string Resolve(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        if (!raw.StartsWith('@')) return raw.Trim();

        try
        {
            var sb = new StringBuilder(2048);

            if (SHLoadIndirectString(raw, sb, sb.Capacity, IntPtr.Zero) == 0 && sb.Length > 0)
            {
                var text = sb.ToString().Trim();
                if (text.Length > 0 && !text.StartsWith('@')) return text;
            }
        }
        catch
        {
            // 继续走 INF 解析
        }

        return ResolveFromInf(raw);
    }

    /// <summary>解析失败时的兜底：给一个不含 @ / % 的可读名字，避免界面上出现资源串。</summary>
    public static string ResolveOr(string raw, string fallback)
    {
        var text = Resolve(raw);
        if (text.Length > 0) return text;

        // @xxx.dll,-123 这类取不到时，用 DLL / INF 的名字当提示，比整串好看
        var guess = Path.GetFileNameWithoutExtension(
            raw.TrimStart('@').Split(',')[0].Replace("%SystemRoot%", "").Replace("\\", "/"));

        return guess.Length > 0 && !guess.StartsWith('%') ? guess + "（名称未解析）" : fallback;
    }

    private static string ResolveFromInf(string raw)
    {
        var body = raw[1..];
        var comma = body.LastIndexOf(',');
        if (comma <= 0) return "";

        var file = body[..comma].Trim().Trim('"');
        var key = body[(comma + 1)..].Trim().Trim('%').Trim();

        if (file.Length == 0 || key.Length == 0) return "";

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        var candidates = Path.IsPathRooted(file)
            ? new[] { file }
            : new[] { Path.Combine(win, "INF", file), Path.Combine(win, "System32", file) };

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;

                var inStrings = false;

                foreach (var line in File.ReadAllLines(path))
                {
                    var text = line.Trim();

                    if (text.StartsWith('['))
                    {
                        inStrings = text.StartsWith("[Strings]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inStrings) continue;

                    var eq = text.IndexOf('=');
                    if (eq <= 0) continue;

                    if (!text[..eq].Trim().Trim('%').Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

                    var value = text[(eq + 1)..].Trim().Trim('"');
                    if (value.Length > 0) return value;
                }
            }
            catch
            {
                // 这个文件读不了就试下一个
            }
        }

        return "";
    }
}
