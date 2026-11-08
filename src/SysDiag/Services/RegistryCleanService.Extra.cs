using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 注册表清理的第二批扫描：字体 / 帮助文档 / 服务映像 / 声音事件 / 防火墙规则。
///
/// 设计原则和主文件一致，而且更保守：**只报「登记的东西确实不在磁盘上」**，
/// 判断依据必须是文件是否存在这种硬证据，不做启发式猜测。
///
/// 刻意不做的几类（Wise 有，这里明确放弃）：
/// <list type="bullet">
/// <item><b>COM 组件（CLSID\InprocServer32）</b>——大量 CLSID 是延迟注册/按需创建的，
///       DLL 不在不代表无效，误删会让某些程序装不上。</item>
/// <item><b>文件扩展名关联</b>——UWP 与 WinRT 的 .xxx → ProgID 映射大量是动态生成的，
///       静态判断误报率太高。</item>
/// <item><b>映像劫持（IFEO）</b>——那是**安全信号**不是垃圾数据。删除会掩盖线索，
///       交给取证模块报，不放进清理列表。</item>
/// </list>
/// </summary>
public static partial class RegistryCleanService
{
    private static readonly Regex ExeTail = new(
        @"^(.+?\.(?:exe|sys|dll))", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ------------------------------------------------------------------ 字体

    private const string FontsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts";

    /// <summary>
    /// 字体登记表里指向的文件不在。
    /// 数据形如 <c>Arial (TrueType)</c> = <c>arial.ttf</c>（相对 Windows\Fonts）
    /// 或绝对路径（用户装的字体有时写全路径）。
    /// </summary>
    private static void ScanFonts(List<RegistryFinding> list, ref int skipped)
    {
        try
        {
            var fontDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

            using var key = Registry.LocalMachine.OpenSubKey(FontsKey);
            if (key == null) return;

            foreach (var name in key.GetValueNames())
            {
                var raw = key.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(raw)) { skipped++; continue; }

                var file = ExpandPath(raw);

                // 只处理字体文件；有些值写的是 "@font.dll,-123" 这类资源串，跳过
                var ext = Path.GetExtension(file);
                if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase)
                    && !ext.Equals(".fon", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var full = Path.IsPathRooted(file) ? file : Path.Combine(fontDir, file);
                if (File.Exists(full)) continue;

                list.Add(new RegistryFinding
                {
                    KeyPath = @"HKLM\" + FontsKey,
                    ValueName = name,
                    DisplayName = name,
                    Category = "字体",
                    Evidence = $"{Path.GetFileName(full)} 已不在 Fonts 目录",
                    Raw = raw,
                    Risk = RegRisk.Safe
                });
            }
        }
        catch { /* 跳过 */ }
    }

    // ------------------------------------------------------------------ 帮助文档

    private const string HelpKey = @"SOFTWARE\Microsoft\Windows\Help";

    /// <summary>旧的 .hlp/.chm 帮助文档登记，文件被删了但键还在。</summary>
    private static void ScanHelpFiles(List<RegistryFinding> list, ref int skipped)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(HelpKey);
            if (key == null) return;

            foreach (var name in key.GetValueNames())
            {
                var raw = key.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(raw)) { skipped++; continue; }

                var path = ExpandPath(raw);
                if (!Path.IsPathRooted(path)) { skipped++; continue; }
                if (File.Exists(path)) continue;

                list.Add(new RegistryFinding
                {
                    KeyPath = @"HKLM\" + HelpKey,
                    ValueName = name,
                    DisplayName = name,
                    Category = "帮助文档",
                    Evidence = $"{path} 已不存在",
                    Raw = raw,
                    Risk = RegRisk.Safe
                });
            }
        }
        catch { /* 跳过 */ }
    }

    // ------------------------------------------------------------------ 服务映像

    /// <summary>
    /// 服务的 ImagePath 指向的文件不存在。这种服务本来就起不来，只会让「服务」列表变脏。
    /// 只报文件确实缺失的；解析不出路径的一律算跳过，不猜。
    /// </summary>
    private static void ScanServiceImages(List<RegistryFinding> list, ref int skipped)
    {
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services == null) return;

            var added = 0;

            foreach (var name in services.GetSubKeyNames())
            {
                if (added >= 60) break;

                try
                {
                    using var svc = services.OpenSubKey(name);
                    var image = svc?.GetValue("ImagePath") as string;

                    if (string.IsNullOrWhiteSpace(image)) continue;

                    var exe = ServiceImageFile(image);
                    if (exe.Length == 0) { skipped++; continue; }

                    var path = ExpandPath(exe);
                    if (!Path.IsPathRooted(path)) { skipped++; continue; }
                    if (File.Exists(path)) continue;

                    list.Add(new RegistryFinding
                    {
                        KeyPath = @"HKLM\SYSTEM\CurrentControlSet\Services\" + name,
                        ValueName = "",
                        DisplayName = name,
                        Category = "服务映像",
                        Evidence = $"ImagePath 指向的 {path} 已不存在",
                        Raw = image,
                        Risk = RegRisk.Careful
                    });

                    added++;
                }
                catch { /* 单个服务读不到继续 */ }
            }
        }
        catch { /* 跳过 */ }
    }

    /// <summary>从 ImagePath 里取出可执行文件部分（去掉引号与启动参数）。</summary>
    private static string ServiceImageFile(string imagePath)
    {
        var p = imagePath.Trim();

        if (p.StartsWith('"'))
        {
            var end = p.IndexOf('"', 1);
            return end > 1 ? p[1..end] : "";
        }

        var m = ExeTail.Match(p);
        return m.Success ? m.Groups[1].Value : "";
    }

    // ------------------------------------------------------------------ 声音事件

    /// <summary>声音方案里登记的事件声音文件被删了。</summary>
    private static void ScanSoundEvents(List<RegistryFinding> list, ref int skipped)
    {
        const string root = @"AppEvents\Schemes\Apps";

        try
        {
            using var apps = Registry.CurrentUser.OpenSubKey(root);
            if (apps == null) return;

            foreach (var app in apps.GetSubKeyNames())
            {
                using var appKey = apps.OpenSubKey(app);
                if (appKey == null) continue;

                foreach (var evt in appKey.GetSubKeyNames())
                {
                    using var evtKey = appKey.OpenSubKey(evt + @"\.Current");
                    var raw = evtKey?.GetValue("") as string;

                    if (string.IsNullOrWhiteSpace(raw)) { skipped++; continue; }

                    var path = ExpandPath(raw);
                    if (!Path.IsPathRooted(path)) { skipped++; continue; }
                    if (File.Exists(path)) continue;

                    list.Add(new RegistryFinding
                    {
                        KeyPath = $@"HKCU\{root}\{app}\{evt}\.Current",
                        ValueName = "",
                        DisplayName = $"{app}\\{evt}",
                        Category = "声音事件",
                        Evidence = $"{path} 已不存在",
                        Raw = raw,
                        Risk = RegRisk.Safe
                    });
                }
            }
        }
        catch { /* 跳过 */ }
    }

    // ------------------------------------------------------------------ 防火墙规则

    private const string FirewallKey =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    /// <summary>
    /// 防火墙规则里绑定程序的路径已不存在。规则本身是 <c>v2.10|Action=...|App=C:\...|...</c> 格式。
    /// 只处理含 App= 且是文件路径的规则；端口规则不动。
    /// </summary>
    private static void ScanFirewallRules(List<RegistryFinding> list, ref int skipped)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(FirewallKey);
            if (key == null) return;

            var added = 0;

            foreach (var name in key.GetValueNames())
            {
                if (added >= 60) break;

                var raw = key.GetValue(name) as string;
                if (string.IsNullOrWhiteSpace(raw)) continue;

                var app = FieldOf(raw, "App");
                if (app.Length == 0 || app.Equals("System", StringComparison.OrdinalIgnoreCase))
                {
                    skipped++;
                    continue;
                }

                var path = ExpandPath(app);
                if (!Path.IsPathRooted(path)) { skipped++; continue; }
                if (File.Exists(path)) continue;

                list.Add(new RegistryFinding
                {
                    KeyPath = @"HKLM\" + FirewallKey,
                    ValueName = name,
                    DisplayName = string.IsNullOrEmpty(name) ? "(无规则名)" : name,
                    Category = "防火墙规则",
                    Evidence = $"规则引用的 {path} 已不存在",
                    Raw = raw,
                    Risk = RegRisk.Safe
                });

                added++;
            }
        }
        catch { /* 跳过 */ }
    }

    /// <summary>从 <c>v2.10|Action=Allow|App=C:\x.exe|...</c> 里取某个字段。</summary>
    private static string FieldOf(string rule, string field)
    {
        foreach (var part in rule.Split('|'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;

            if (part[..eq].Equals(field, StringComparison.OrdinalIgnoreCase))
                return part[(eq + 1)..].Trim();
        }

        return "";
    }

    // ------------------------------------------------------------------ 路径

    /// <summary>
    /// 展开注册表里常见的路径写法：环境变量、<c>\SystemRoot\</c>、<c>\??\</c>、裸 <c>System32\</c>。
    /// </summary>
    private static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";

        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(win, p[12..]);

        if (p.StartsWith(@"\??\", StringComparison.Ordinal))
            return p[4..];

        if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(win, p);

        return p;
    }
}
