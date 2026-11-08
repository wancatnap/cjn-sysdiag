using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 注册表清理：**只删「指向的东西已经不存在了」的孤儿项**，不做「优化」「压缩」这类玄学。
///
/// <para>思路来自 CCleaner / BleachBit 这一类工具的注册表模块，以及它们踩过的坑：
/// 真正安全可证的只有「这个值指向的 exe/dll 已经不在磁盘上」，其余（无效扩展名、
/// 过期 COM 注册、MUI 缓存清理）都可能误伤，所以这里一条都不碰。</para>
///
/// <para><b>三道保险</b>：
/// 1. 扫描阶段逐条给出「它指向什么、那个东西已经不存在」的证据，不接受凭空的「可疑」；
/// 2. 删除阶段先用 <c>reg export</c> 把涉及的键整段导出到 <c>outputs\reg-backup-时间\</c>，
///    任何一个键导出失败就跳过该键下的所有条目——没有备份就不删；
/// 3. 默认全部不勾选，必须逐条或点「全选安全项」。</para>
///
/// 全部只读扫描；写操作只有删除，且可回滚（双击 .reg 导入）。
/// </summary>
public static partial class RegistryCleanService
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string SharedDllKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs";
    private const string MuiCacheKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";

    /// <summary>命令行里找绝对路径：<c>C:\...\foo.exe</c> 或带引号的同一形态。</summary>
    private static readonly Regex PathInCommand = new(
        @"""([A-Za-z]:\\[^""]+?\.(?:exe|dll|com|bat|cmd))""|([A-Za-z]:\\[^\s""]+?\.(?:exe|dll|com|bat|cmd))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // =========================================================
    // 扫描
    // =========================================================

    public static RegistryScanResult Scan(IProgress<string> progress = null)
    {
        var sw = Stopwatch.StartNew();
        var list = new List<RegistryFinding>();
        var skipped = 0;

        void Step(string text) => progress?.Report(text);

        Step("检查启动项…");
        ScanRunEntries(list, ref skipped);

        Step("检查已装软件登记…");
        ScanUninstallEntries(list, ref skipped);

        Step("检查 App Paths…");
        ScanAppPaths(list, ref skipped);

        Step("检查共享 DLL 计数…");
        ScanSharedDlls(list);

        Step("检查 MUI 缓存…");
        ScanMuiCache(list);

        // 第二批：字体 / 帮助文档 / 服务映像 / 声音事件 / 防火墙规则
        Step("检查字体登记…");
        ScanFonts(list, ref skipped);

        Step("检查帮助文档…");
        ScanHelpFiles(list, ref skipped);

        Step("检查服务映像路径…");
        ScanServiceImages(list, ref skipped);

        Step("检查声音事件…");
        ScanSoundEvents(list, ref skipped);

        Step("检查防火墙规则…");
        ScanFirewallRules(list, ref skipped);

        // 同一路径重复出现时只留一条（例如 Run 与 RunOnce 都登记了同一个失效程序）
        var dedup = new List<RegistryFinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in list)
        {
            if (seen.Add(f.PathText)) dedup.Add(f);
        }

        return new RegistryScanResult
        {
            Findings = dedup,
            Skipped = skipped,
            ElapsedText = $"{sw.ElapsedMilliseconds / 1000.0:0.0} 秒"
        };
    }

    /// <summary>把 SOFTWARE\… 变成 32 位视图 SOFTWARE\WOW6432Node\…。</summary>
    private static string Wow(string sub)
        => sub.Replace(@"SOFTWARE\", @"SOFTWARE\WOW6432Node\", StringComparison.OrdinalIgnoreCase);

    /// <summary>32 位程序把启动项写在 WOW6432Node 下，两处都要看。</summary>
    private static IEnumerable<(RegistryKey Hive, string KeyPath, string Sub)> RunLocations()
    {
        yield return (Registry.CurrentUser, @"HKCU\" + RunKey, RunKey);
        yield return (Registry.CurrentUser, @"HKCU\" + RunOnceKey, RunOnceKey);
        yield return (Registry.LocalMachine, @"HKLM\" + RunKey, RunKey);
        yield return (Registry.LocalMachine, @"HKLM\" + Wow(RunKey), Wow(RunKey));
        yield return (Registry.LocalMachine, @"HKLM\" + RunOnceKey, RunOnceKey);
        yield return (Registry.LocalMachine, @"HKLM\" + Wow(RunOnceKey), Wow(RunOnceKey));
    }

    private static void ScanRunEntries(List<RegistryFinding> list, ref int skipped)
    {
        foreach (var (hive, keyPath, sub) in RunLocations())
        {
            try
            {
                using var key = hive.OpenSubKey(sub);
                if (key == null) continue;

                foreach (var name in key.GetValueNames())
                {
                    var value = key.GetValue(name) as string;
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var target = FirstExistingOrMissing(value);
                    if (target == null) { skipped++; continue; }        // 命令里没有绝对路径，不判断
                    if (target.Exists) continue;

                    list.Add(new RegistryFinding
                    {
                        KeyPath = keyPath,
                        ValueName = name,
                        DisplayName = name,
                        Category = "启动项",
                        Evidence = $"指向的 {target.Path} 已不存在",
                        Raw = value,
                        Risk = RegRisk.Safe
                    });
                }
            }
            catch { /* 单个键读不到不影响其它 */ }
        }
    }

    private static void ScanUninstallEntries(List<RegistryFinding> list, ref int skipped)
    {
        foreach (var (hive, keyPath, sub) in new[]
                 {
                     (Registry.LocalMachine, @"HKLM\" + UninstallKey, UninstallKey),
                     (Registry.LocalMachine, @"HKLM\" + Wow(UninstallKey), Wow(UninstallKey)),
                     (Registry.CurrentUser, @"HKCU\" + UninstallKey, UninstallKey)
                 })
        {
            try
            {
                using var root = hive.OpenSubKey(sub);
                if (root == null) continue;

                foreach (var subName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = root.OpenSubKey(subName);
                        if (entry == null) continue;

                        var display = entry.GetValue("DisplayName") as string;

                        // 没有显示名的多半是 MSI 的组件登记，不是用户能认出来的软件，不碰
                        if (string.IsNullOrWhiteSpace(display)) { skipped++; continue; }

                        var uninstall = (entry.GetValue("UninstallString") as string) ?? "";
                        var quiet = (entry.GetValue("QuietUninstallString") as string) ?? "";
                        var location = entry.GetValue("InstallLocation") as string;

                        // MSI 卸载项要查 product code 数据库才能判断，本地判断不了，直接跳过
                        if (uninstall.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ||
                            quiet.Contains("msiexec", StringComparison.OrdinalIgnoreCase))
                        {
                            skipped++;
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(location))
                        {
                            var dir = Environment.ExpandEnvironmentVariables(location.Trim().Trim('"'));
                            if (Directory.Exists(dir) || File.Exists(dir)) continue;
                        }

                        var target = FirstExistingOrMissing(uninstall.Length > 0 ? uninstall : quiet);
                        if (target == null) { skipped++; continue; }
                        if (target.Exists) continue;

                        list.Add(new RegistryFinding
                        {
                            KeyPath = $"{keyPath}\\{subName}",
                            ValueName = "",
                            DisplayName = display.Trim(),
                            Category = "卸载项",
                            Evidence = $"卸载程序 {target.Path} 已不存在，且没有可用的安装目录",
                            Raw = uninstall.Length > 0 ? uninstall : quiet,
                            Risk = RegRisk.Safe
                        });
                    }
                    catch { /* 跳过这一项 */ }
                }
            }
            catch { /* 跳过这一支 */ }
        }
    }

    private static void ScanAppPaths(List<RegistryFinding> list, ref int skipped)
    {
        foreach (var (hive, keyPath, sub) in new[]
                 {
                     (Registry.LocalMachine, @"HKLM\" + AppPathsKey, AppPathsKey),
                     (Registry.LocalMachine, @"HKLM\" + Wow(AppPathsKey), Wow(AppPathsKey))
                 })
        {
            try
            {
                using var root = hive.OpenSubKey(sub);
                if (root == null) continue;

                foreach (var exeName in root.GetSubKeyNames())
                {
                    try
                    {
                        using var entry = root.OpenSubKey(exeName);
                        if (entry == null) continue;

                        var def = entry.GetValue("") as string;
                        var dir = entry.GetValue("Path") as string;

                        var candidate = "";
                        if (!string.IsNullOrWhiteSpace(def)) candidate = def;
                        else if (!string.IsNullOrWhiteSpace(dir)) candidate = Path.Combine(dir, exeName);

                        if (candidate.Length == 0) { skipped++; continue; }

                        var full = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
                        if (!IsAbsoluteFile(full)) { skipped++; continue; }
                        if (File.Exists(full)) continue;

                        list.Add(new RegistryFinding
                        {
                            KeyPath = $"{keyPath}\\{exeName}",
                            ValueName = "",
                            DisplayName = exeName,
                            Category = "App Paths",
                            Evidence = $"登记的 {full} 已不存在",
                            Raw = candidate,
                            Risk = RegRisk.Safe
                        });
                    }
                    catch { /* 跳过 */ }
                }
            }
            catch { /* 跳过 */ }
        }
    }

    private static void ScanSharedDlls(List<RegistryFinding> list)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SharedDllKey);
            if (key == null) return;

            var names = key.GetValueNames();

            foreach (var name in names)
            {
                if (!IsAbsoluteFile(name)) continue;
                if (File.Exists(name)) continue;

                list.Add(new RegistryFinding
                {
                    KeyPath = @"HKLM\" + SharedDllKey,
                    ValueName = name,
                    DisplayName = Path.GetFileName(name),
                    Category = "共享 DLL",
                    Evidence = $"{name} 已不存在",
                    Raw = name,
                    Risk = RegRisk.Careful
                });
            }
        }
        catch { /* 跳过 */ }
    }

    private static void ScanMuiCache(List<RegistryFinding> list)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MuiCacheKey);
            if (key == null) return;

            var added = 0;

            foreach (var name in key.GetValueNames())
            {
                if (added >= 120) break;                       // 这个键动辄上千条，限量

                // 值名形如 C:\Program Files\Foo\foo.exe.FriendlyAppName
                var lower = name.ToLowerInvariant();
                var cut = lower.LastIndexOf(".exe.", StringComparison.Ordinal);
                if (cut <= 0) continue;

                var exe = name[..(cut + 4)];
                if (!IsAbsoluteFile(exe) || File.Exists(exe)) continue;

                list.Add(new RegistryFinding
                {
                    KeyPath = @"HKCU\" + MuiCacheKey,
                    ValueName = name,
                    DisplayName = Path.GetFileName(exe),
                    Category = "MUI 缓存",
                    Evidence = $"{exe} 已不存在",
                    Raw = name,
                    Risk = RegRisk.Careful
                });

                added++;
            }
        }
        catch { /* 跳过 */ }
    }

    // =========================================================
    // 清理（先备份，再删）
    // =========================================================

    /// <summary>
    /// 删除选中的孤儿项。**先备份再删**：任何一个键导出失败，该键下的条目全部跳过。
    /// </summary>
    public static RegistryCleanOutcome Clean(IEnumerable<RegistryFinding> selected)
    {
        var outcome = new RegistryCleanOutcome();
        var items = selected.Where(f => f.Selected).ToList();
        if (items.Count == 0) return outcome;

        AppPaths.Ensure();

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(AppPaths.OutputDir, $"reg-backup-{stamp}");
        Directory.CreateDirectory(backupDir);
        outcome.BackupDir = backupDir;

        // 按「键」分组：整键删除只需导出一次
        var byKey = items
            .GroupBy(f => f.KeyPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in byKey)
        {
            var keyPath = group.Key;
            var file = Path.Combine(backupDir, $"key-{backedUp.Count + 1:00}.reg");

            if (!ExportKey(keyPath, file, out var err))
            {
                // 备份失败 → 整键跳过，绝不冒险
                outcome.Failed += group.Count();
                if (outcome.Errors.Count < 10) outcome.Errors.Add($"{keyPath} 备份失败：{err}");
                continue;
            }

            backedUp.Add(keyPath);

            foreach (var finding in group)
            {
                try
                {
                    if (DeleteOne(finding)) outcome.Removed++;
                    else
                    {
                        outcome.Failed++;
                        if (outcome.Errors.Count < 10) outcome.Errors.Add($"{finding.PathText} 删除失败");
                    }
                }
                catch (Exception ex)
                {
                    outcome.Failed++;
                    if (outcome.Errors.Count < 10) outcome.Errors.Add($"{finding.PathText} → {ex.Message}");
                }
            }
        }

        // 写一份清单，方便回滚时对照
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"注册表清理备份　{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"共 {items.Count} 条，涉及 {byKey.Count} 个键");
            sb.AppendLine();
            sb.AppendLine("回滚方法：双击本目录下的 .reg 文件，确认导入即可恢复。");
            sb.AppendLine();
            foreach (var f in items) sb.AppendLine($"[{f.Category}] {f.ActionText}　（{f.Evidence}）");

            File.WriteAllText(Path.Combine(backupDir, "清单.txt"), sb.ToString(), new UTF8Encoding(true));
        }
        catch { /* 清单写不出来不影响删除结果 */ }

        return outcome;
    }

    private static bool DeleteOne(RegistryFinding finding)
    {
        var (hive, sub) = SplitHive(finding.KeyPath);
        if (hive == null) return false;

        if (finding.IsKey)
        {
            var parent = sub.Contains('\\') ? sub[..sub.LastIndexOf('\\')] : "";
            var leaf = sub.Contains('\\') ? sub[(sub.LastIndexOf('\\') + 1)..] : sub;
            if (leaf.Length == 0) return false;

            using var parentKey = hive.OpenSubKey(parent, writable: true);
            if (parentKey == null) return false;

            parentKey.DeleteSubKeyTree(leaf, throwOnMissingSubKey: false);
            return true;
        }

        using var key = hive.OpenSubKey(sub, writable: true);
        if (key == null) return false;

        key.DeleteValue(finding.ValueName, throwOnMissingValue: false);
        return true;
    }

    private static bool ExportKey(string keyPath, string file, out string error)
    {
        error = "";

        try
        {
            var full = ToRegExePath(keyPath);

            var psi = new ProcessStartInfo("reg.exe", $"export \"{full}\" \"{file}\" /y")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) { error = "无法启动 reg.exe"; return false; }

            var text = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(8000))
            {
                try { proc.Kill(); } catch { }
                error = "导出超时";
                return false;
            }

            if (proc.ExitCode != 0 || !File.Exists(file))
            {
                error = text.Trim().Length > 0 ? text.Trim() : $"退出码 {proc.ExitCode}";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary><c>HKCU\Software\...</c> → <c>HKEY_CURRENT_USER\Software\...</c>（reg.exe 只认全名）。</summary>
    private static string ToRegExePath(string keyPath)
    {
        var (hive, sub) = SplitHive(keyPath);
        var name = hive == Registry.CurrentUser ? "HKEY_CURRENT_USER"
            : hive == Registry.LocalMachine ? "HKEY_LOCAL_MACHINE"
            : hive == Registry.ClassesRoot ? "HKEY_CLASSES_ROOT"
            : hive == Registry.Users ? "HKEY_USERS"
            : "HKEY_CURRENT_USER";

        return sub.Length > 0 ? $"{name}\\{sub}" : name;
    }

    private static (RegistryKey Hive, string Sub) SplitHive(string keyPath)
    {
        if (string.IsNullOrWhiteSpace(keyPath)) return (null, "");

        var slash = keyPath.IndexOf('\\');
        var hive = slash < 0 ? keyPath : keyPath[..slash];
        var sub = slash < 0 ? "" : keyPath[(slash + 1)..];

        // 我们的扫描器会把 WOW6432 重定向写成 "HKLM\WOW6432Node\..."，
        // 真实路径是 "SOFTWARE\WOW6432Node\..."，这里已被拼进 sub，无需特殊处理。
        return hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => (Registry.CurrentUser, sub),
            "HKLM" or "HKEY_LOCAL_MACHINE" => (Registry.LocalMachine, sub),
            "HKCR" or "HKEY_CLASSES_ROOT" => (Registry.ClassesRoot, sub),
            "HKU" or "HKEY_USERS" => (Registry.Users, sub),
            _ => (null, "")
        };
    }

    // =========================================================
    // 小工具
    // =========================================================

    private static bool IsAbsoluteFile(string path)
    {
        try
        {
            return path.Length > 3 && Path.IsPathRooted(path)
                   && (path.Contains(":\\", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从命令行里取出第一个绝对路径，判断它是否还在。
    /// 返回 null 表示「命令里没有可判断的绝对路径」，调用方应计入 skipped 而不是当成孤儿。
    /// </summary>
    private sealed record PathProbe(string Path, bool Exists);

    private static PathProbe FirstExistingOrMissing(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var expanded = Environment.ExpandEnvironmentVariables(command);
        var matches = PathInCommand.Matches(expanded);
        if (matches.Count == 0) return null;

        string first = null;

        foreach (Match m in matches)
        {
            var raw = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            var path = raw.Trim();

            if (first == null) first = path;

            try
            {
                if (File.Exists(path)) return new PathProbe(path, true);
            }
            catch { /* 路径非法就跳过 */ }
        }

        return new PathProbe(first, false);
    }
}
