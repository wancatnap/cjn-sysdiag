using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>已安装软件（「程序和功能」的软件内版本）。</summary>
public static class InstalledAppsService
{
    public sealed class AppEntry
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string InstallDate { get; set; } = "";
        public long SizeKb { get; set; }
        public string Location { get; set; } = "";
        public string Uninstall { get; set; } = "";
        public bool PerUser { get; set; }
        public bool IsStore { get; set; }

        public string SizeText => SizeKb <= 0 ? "" : SizeKb >= 1024 * 1024
            ? $"{SizeKb / 1024.0 / 1024:0.##} GB"
            : $"{SizeKb / 1024.0:0.#} MB";
    }

    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>三处 Uninstall 键：机器级 64 位、机器级 32 位、当前用户。</summary>
    private static IEnumerable<(RegistryKey Hive, string Path, bool PerUser)> Roots()
    {
        yield return (Registry.LocalMachine, UninstallKey, false);
        yield return (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", false);
        yield return (Registry.CurrentUser, UninstallKey, true);
    }

    public static List<AppEntry> Scan()
    {
        var list = new List<AppEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, path, perUser) in Roots())
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root == null) continue;

                foreach (var sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var key = root.OpenSubKey(sub);
                        if (key == null) continue;

                        var name = (key.GetValue("DisplayName") as string ?? "").Trim();
                        if (name.Length == 0) continue;

                        // 系统组件（补丁、运行库条目）不是用户装的软件，剔掉
                        if (key.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (key.GetValue("ParentKeyName") is string parent && parent.Length > 0) continue;
                        if (key.GetValue("ReleaseType") is string rt && rt.Contains("Update", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var version = (key.GetValue("DisplayVersion") as string ?? "").Trim();
                        var dedupe = name + "|" + version;
                        if (!seen.Add(dedupe)) continue;

                        var uninstall = (key.GetValue("UninstallString") as string ?? "").Trim();
                        var quiet = (key.GetValue("QuietUninstallString") as string ?? "").Trim();

                        list.Add(new AppEntry
                        {
                            Name = name,
                            Version = version,
                            Publisher = (key.GetValue("Publisher") as string ?? "").Trim(),
                            InstallDate = FormatDate(key.GetValue("InstallDate") as string),
                            SizeKb = key.GetValue("EstimatedSize") is int kb ? kb : 0,
                            Location = (key.GetValue("InstallLocation") as string ?? "").Trim(),
                            Uninstall = quiet.Length > 0 ? quiet : uninstall,
                            PerUser = perUser,
                            IsStore = sub.Contains("!", StringComparison.Ordinal) ||
                                      name.Contains("Microsoft Store", StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    catch { /* 单个条目读不到跳过 */ }
                }
            }
            catch { /* 某一处键读不到跳过 */ }
        }

        return list.OrderByDescending(a => a.SizeKb).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string FormatDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        return DateTime.TryParseExact(raw.Trim(), "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt)
            ? dt.ToString("yyyy-MM-dd")
            : raw.Trim();
    }

    /// <summary>调软件自己的卸载程序。系统会按需要弹 UAC，属于预期行为。</summary>
    public static (bool Ok, string Message) Uninstall(AppEntry app)
    {
        if (app.Uninstall.Length == 0)
            return (false, $"「{app.Name}」没有登记卸载命令，只能从它的安装目录或应用商店里卸载。");

        try
        {
            var info = ParseCommand(app.Uninstall);

            Process.Start(new ProcessStartInfo
            {
                FileName = info.File,
                Arguments = info.Args,
                UseShellExecute = true
            });

            return (true, $"已启动「{app.Name}」的卸载程序，按它自己的提示完成即可。");
        }
        catch (Exception ex)
        {
            return (false, $"启动卸载失败：{ex.Message}");
        }
    }

    /// <summary>把 "C:\Program Files\App\uninst.exe /S" 拆成文件与参数（路径可能带引号）。</summary>
    public static (string File, string Args) ParseCommand(string command)
    {
        var text = command.Trim();

        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 0)
                return (text[1..end], text[(end + 1)..].Trim());
        }

        var space = text.IndexOf(' ');
        if (space < 0) return (text, "");

        var head = text[..space];

        // "C:\Program Files\..." 这类没加引号的路径：到 .exe 为止都算文件名
        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0 && exeIndex < text.Length - 4)
            return (text[..(exeIndex + 4)], text[(exeIndex + 4)..].Trim());

        return (head, text[(space + 1)..].Trim());
    }
}
