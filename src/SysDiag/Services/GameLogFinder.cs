using SysDiag.Models;
using System.IO;
using System.Threading;

namespace SysDiag.Services;

public sealed class GameLogDir
{
    public string Game { get; set; } = "";
    public string Path { get; set; } = "";
    public int FileCount { get; set; }
    public long Size { get; set; }
    public DateTime LastWrite { get; set; }
    public string Engine { get; set; } = "Unreal";

    public string SizeText => LogFileItem.FormatSize(Size);
    public string CountText => $"{FileCount} 个文件";
    public string TimeText => LastWrite == default ? "-" : LastWrite.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>
/// 全盘查找 UE 游戏的日志目录（*\Saved\Logs）。游戏可能装在任何盘符、任何路径，
/// 因此按目录名 + 父目录名匹配，而不是猜安装位置。
/// </summary>
public static class GameLogFinder
{
    /// <summary>这些目录里不会有游戏日志，跳过以节省时间。</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "$Recycle.Bin", "System Volume Information", "Recovery", "PerfLogs",
        "WinSxS", "DriverStore", "Installer", "assembly", "Package Cache",
        "node_modules", ".git", ".svn", ".hg", "__pycache__", ".vs", "obj", "bin",
        "WindowsApps", "$WinREAgent", "Temp", "tmp"
    };

    /// <summary>这些顶层目录不含游戏安装，直接跳过。</summary>
    private static readonly HashSet<string> SkipRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "ProgramData", "AppData", "Config.Msi", "MSOCache", "Intel", "AMD", "NVIDIA"
    };

    public static List<GameLogDir> FindAll(
        IProgress<string> progress = null,
        CancellationToken token = default,
        int maxDepth = 8,
        int maxDirs = 400_000)
    {
        var results = new List<GameLogDir>();
        var visited = 0;
        var truncated = false;

        foreach (var drive in SafeDrives())
        {
            if (token.IsCancellationRequested) break;

            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((drive, 0));

            while (queue.Count > 0)
            {
                if (token.IsCancellationRequested) return results;

                var (current, depth) = queue.Dequeue();
                visited++;

                if (visited > maxDirs)
                {
                    truncated = true;
                    break;
                }

                if (visited % 2000 == 0)
                    progress?.Report($"已扫描 {visited:N0} 个目录…（{current}）");

                foreach (var sub in SafeSubDirs(current))
                {
                    var name = System.IO.Path.GetFileName(sub);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (SkipNames.Contains(name)) continue;
                    if (depth == 0 && SkipRoots.Contains(name)) continue;

                    var parentName = System.IO.Path.GetFileName(current);

                    // UE 结构：<Game>\Saved\Logs
                    if (depth >= 2 &&
                        (name.Equals("Logs", StringComparison.OrdinalIgnoreCase) ||
                         name.Equals("log", StringComparison.OrdinalIgnoreCase)) &&
                        parentName.Equals("Saved", StringComparison.OrdinalIgnoreCase))
                    {
                        var gameDir = System.IO.Path.GetDirectoryName(current);
                        var game = string.IsNullOrEmpty(gameDir)
                            ? System.IO.Path.GetFileName(current)
                            : System.IO.Path.GetFileName(gameDir);

                        var entry = Build(game, sub);
                        if (entry != null) results.Add(entry);
                        continue; // 日志目录内部不必再深入
                    }

                    if (depth + 1 < maxDepth)
                        queue.Enqueue((sub, depth + 1));
                }
            }

            if (truncated) break;
        }

        progress?.Report(truncated
            ? $"扫描目录数已达上限 {maxDirs:N0}，结果可能不完整"
            : $"扫描完成，共检查 {visited:N0} 个目录");

        return results
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderByDescending(r => r.LastWrite)
            .ToList();
    }

    private static GameLogDir Build(string game, string logsPath)
    {
        try
        {
            var files = Directory.EnumerateFiles(logsPath, "*.log", SearchOption.TopDirectoryOnly).ToList();
            if (files.Count == 0) return null;

            long size = 0;
            var last = DateTime.MinValue;
            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    size += fi.Length;
                    if (fi.LastWriteTime > last) last = fi.LastWriteTime;
                }
                catch { }
            }
            if (size == 0) return null;

            return new GameLogDir
            {
                Game = string.IsNullOrEmpty(game) ? "未知游戏" : game,
                Path = logsPath,
                FileCount = files.Count,
                Size = size,
                LastWrite = last
            };
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        var list = new List<string>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    list.Add(d.RootDirectory.FullName);
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    private static IEnumerable<string> SafeSubDirs(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
