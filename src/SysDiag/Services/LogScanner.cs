using System.IO;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>扫描本机常见日志位置，生成可清理清单。只读扫描，不做任何删除。</summary>
public static class LogScanner
{
    private static readonly string[] LogExt = { "*.log", "*.txt", "*.etl", "*.dmp", "*.old" };

    public static List<LogCandidate> Scan()
    {
        var list = new List<LogCandidate>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var temp = Path.GetTempPath().TrimEnd('\\');
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        // 1. 本工具自己的解压缓存
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(AppPaths.OutputDir, "_work_*"))
                list.Add(Make("分析缓存", "解压工作目录", dir, new[] { dir }, CandidateKind.Safe));
        }
        catch { }

        // 2. 游戏引擎日志：%LOCALAPPDATA%\*\Saved\Logs
        try
        {
            foreach (var game in SafeDirs(local))
            {
                var logs = Path.Combine(game, "Saved", "Logs");
                if (!Directory.Exists(logs)) continue;

                var files = SafeFiles(logs, "*.log", false);
                if (files.Count == 0) continue;

                list.Add(Make("游戏日志", $"{Path.GetFileName(game)} 引擎日志", logs, files, CandidateKind.Safe));
            }
        }
        catch { }

        // 3. 游戏/软件自带 Logs 目录
        try
        {
            foreach (var game in SafeDirs(local))
            {
                var logs = Path.Combine(game, "Logs");
                if (!Directory.Exists(logs)) continue;

                var files = SafeFiles(logs, "*.log", false);
                if (files.Count == 0) continue;

                list.Add(Make("应用日志", $"{Path.GetFileName(game)} 日志", logs, files, CandidateKind.Safe));
            }
        }
        catch { }

        // 4. Steam 客户端日志
        try
        {
            var steam = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;

            if (!string.IsNullOrEmpty(steam))
            {
                var logs = Path.Combine(steam.Replace('/', '\\'), "logs");
                if (Directory.Exists(logs))
                {
                    var files = SafeFiles(logs, "*log*.txt", false)
                        .Concat(SafeFiles(logs, "*.log", false))
                        .Distinct()
                        .ToList();

                    if (files.Count > 0)
                        list.Add(Make("启动器日志", "Steam 客户端日志", logs, files, CandidateKind.Safe));
                }
            }
        }
        catch { }

        // 5. 临时目录中的日志（排除 WorkBuddy 更新缓存相关）
        try
        {
            var files = SafeFiles(temp, "*.log", false)
                .Where(f => !f.Contains("workbuddy", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (files.Count > 0)
                list.Add(Make("临时文件", "用户临时目录日志", temp, files, CandidateKind.Safe));
        }
        catch { }

        // 6. 最近分析过的目录（覆盖 D 盘等非标准路径的游戏日志）
        foreach (var folder in AppSettings.LoadRecentFolders())
        {
            try
            {
                var files = new List<string>();
                foreach (var pattern in LogExt) files.AddRange(SafeFiles(folder, pattern, true));
                files = files.Distinct().ToList();
                if (files.Count == 0) continue;

                list.Add(Make("最近分析", Path.GetFileName(folder.TrimEnd('\\')) + " 目录",
                    folder, files, CandidateKind.Safe));
            }
            catch { }
        }

        // 7. Windows 组件日志（通常需要管理员，且部分文件被系统占用）
        var winLogs = Path.Combine(win, "Logs");
        foreach (var sub in new[] { "CBS", "DISM", "WindowsUpdate", "NetSetup", "MeasuredBoot" })
        {
            var dir = Path.Combine(winLogs, sub);
            if (!Directory.Exists(dir)) continue;

            var files = new List<string>();
            foreach (var pattern in LogExt) files.AddRange(SafeFiles(dir, pattern, true));
            if (files.Count == 0) continue;

            list.Add(Make("Windows 日志", $"Windows\\Logs\\{sub}", dir,
                files.Distinct().ToList(), CandidateKind.NeedsAdmin));
        }

        // 8. 事件日志文件：仅展示体积，不提供删除
        var evtxDir = Path.Combine(win, "System32", "winevt", "Logs");
        if (Directory.Exists(evtxDir))
        {
            var evtx = SafeFiles(evtxDir, "*.evtx", false);
            if (evtx.Count > 0)
                list.Add(Make("事件日志", "系统事件日志（.evtx）", evtxDir, evtx, CandidateKind.ReadOnly));
        }

        return list
            .Where(c => c.Files.Count > 0)
            .GroupBy(c => c.DisplayPath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(c => c.Size)
            .ToList();
    }

    /// <summary>把用户自选的目录加入清单（覆盖任意游戏/软件的非标准路径）。</summary>
    public static LogCandidate ScanCustomFolder(string folder)
    {
        if (!Directory.Exists(folder)) return null;

        var files = new List<string>();
        foreach (var pattern in LogExt)
        {
            try { files.AddRange(Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories)); }
            catch { }
        }

        files = files.Distinct().ToList();
        if (files.Count == 0) return null;

        return Make("自定义目录", Path.GetFileName(folder.TrimEnd('\\')), folder, files, CandidateKind.Safe);
    }

    // ---------------- 辅助 ----------------

    private static LogCandidate Make(string category, string name, string displayPath,
        IEnumerable<string> targets, CandidateKind kind)
    {
        var items = new List<LogFileItem>();

        foreach (var target in targets)
        {
            try
            {
                if (Directory.Exists(target))
                {
                    long size = 0;
                    foreach (var f in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                    {
                        try { size += new FileInfo(f).Length; } catch { }
                    }

                    items.Add(new LogFileItem
                    {
                        Path = target,
                        Name = Path.GetFileName(target.TrimEnd('\\')),
                        Size = size,
                        IsDirectory = true
                    });
                }
                else if (File.Exists(target))
                {
                    var fi = new FileInfo(target);
                    items.Add(new LogFileItem { Path = target, Name = fi.Name, Size = fi.Length });
                }
            }
            catch { }
        }

        var candidate = new LogCandidate
        {
            Category = category,
            Name = name,
            DisplayPath = displayPath,
            Kind = kind
        };
        candidate.SetFiles(items);
        return candidate;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static List<string> SafeFiles(string dir, string pattern, bool recursive)
    {
        try
        {
            return Directory.EnumerateFiles(
                    dir, pattern,
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .ToList();
        }
        catch { return new List<string>(); }
    }
}
