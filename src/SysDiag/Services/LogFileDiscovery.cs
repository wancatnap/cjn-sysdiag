using System.IO;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 列出一个目录下的日志文件，交给用户自己勾选。
/// 只做枚举与分类，不读内容、不改任何东西。
/// </summary>
public static class LogFileDiscovery
{
    private const int MaxFiles = 6000;
    private const int MaxDepth = 6;

    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "node_modules", ".git", ".svn",
        "bin", "obj", "WinSxS", "Installer", "Prefetch"
    };

    public static List<LogPickItem> Scan(string root, bool recurse = true,
        IProgress<string> progress = null, int maxFiles = MaxFiles)
    {
        var list = new List<LogPickItem>();
        if (string.IsNullOrWhiteSpace(root)) return list;

        string rootFull;
        try { rootFull = Path.GetFullPath(root); }
        catch { return list; }
        if (!Directory.Exists(rootFull)) return list;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((rootFull, 0));
        var scannedDirs = 0;

        while (queue.Count > 0 && list.Count < maxFiles)
        {
            var (dir, depth) = queue.Dequeue();
            scannedDirs++;

            if (scannedDirs % 30 == 0)
                progress?.Report($"已扫描 {scannedDirs} 个目录，找到 {list.Count} 个文件…");

            string[] files;
            string[] dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = Directory.GetDirectories(dir);
            }
            catch
            {
                continue; // 无权限的目录直接跳过
            }

            foreach (var f in files)
            {
                if (list.Count >= maxFiles) break;
                try
                {
                    var info = new FileInfo(f);
                    var isEvtx = info.Extension.Equals(".evtx", StringComparison.OrdinalIgnoreCase);
                    list.Add(new LogPickItem
                    {
                        Path = f,
                        RelPath = Path.GetRelativePath(rootFull, f),
                        Size = info.Length,
                        Modified = info.LastWriteTime,
                        IsEventLog = isEvtx,
                        IsAnalyzable = LogAnalyzer.IsAnalyzableFile(f)
                    });
                }
                catch
                {
                    // 单个文件读不到属性就跳过
                }
            }

            if (!recurse || depth >= MaxDepth) continue;

            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (SkipDirs.Contains(name)) continue;
                queue.Enqueue((d, depth + 1));
            }
        }

        progress?.Report($"扫描完成：{scannedDirs} 个目录，{list.Count} 个文件");

        // 可分析的、最近修改的排前面
        return list
            .OrderByDescending(i => i.IsAnalyzable)
            .ThenByDescending(i => i.Modified)
            .ThenBy(i => i.RelPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
