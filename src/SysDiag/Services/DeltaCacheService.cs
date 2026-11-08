using System.IO;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 游戏缓存清理：定位 Saved 目录下的日志、崩溃转储、着色器缓存等，
/// 只清缓存类目录，不碰 Config / SaveGames 这类用户配置。
/// </summary>
public static class DeltaCacheService
{
    /// <summary>可安全清理的子目录（相对 Saved）。</summary>
    private static readonly (string Name, string Relative)[] Targets =
    {
        ("日志", "Logs"),
        ("崩溃转储", "Crashes"),
        ("着色器缓存 PSOCache", "PSOCache"),
        ("管线缓存", "PipelineCaches"),
        ("网页缓存", "WebCaches"),
        ("临时缓存", "Cache")
    };

    public static List<DeltaCacheItem> Scan(IProgress<string> progress = null)
    {
        var list = new List<DeltaCacheItem>();

        var dirs = GameLogFinder.FindAll(progress);
        var savedDirs = dirs
            .Where(d => d.Path.Contains("Delta", StringComparison.OrdinalIgnoreCase)
                        || d.Game.Contains("Delta", StringComparison.OrdinalIgnoreCase))
            .Select(d => Path.GetDirectoryName(d.Path.TrimEnd('\\')))
            .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var saved in savedDirs)
        {
            foreach (var (name, rel) in Targets)
            {
                var path = Path.Combine(saved, rel);
                if (!Directory.Exists(path)) continue;

                var (count, size) = Measure(path);
                if (count == 0) continue;

                list.Add(new DeltaCacheItem
                {
                    Name = name,
                    Path = path,
                    SavedRoot = saved,
                    FileCount = count,
                    Size = size
                });
            }
        }

        return list;
    }

    private static (int Count, long Size) Measure(string dir)
    {
        var count = 0;
        long size = 0;

        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                count++;
                try { size += new FileInfo(f).Length; } catch { }
            }
        }
        catch
        {
            // 个别文件被占用时忽略
        }

        return (count, size);
    }

    public static (bool Ok, string Message) Delete(DeltaCacheItem item)
    {
        if (!Directory.Exists(item.Path))
            return (false, "目录已不存在：" + item.Path);

        try
        {
            // 安全检查：必须位于 Delta Force 的 Saved 目录下
            if (!item.Path.StartsWith(item.SavedRoot, StringComparison.OrdinalIgnoreCase))
                return (false, "路径校验失败，已中止。");

            var files = Directory.GetFiles(item.Path, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0) return (true, item.Name + " 已经是空的。");

            var errors = new List<string>();
            RecycleBin.MoveToRecycleBin(files, errors);

            return errors.Count == 0
                ? (true, $"已把 {files.Count} 个文件移入回收站：{item.Name}")
                : (false, $"{item.Name} 有 {errors.Count} 个文件未能删除（可能正被游戏占用）。");
        }
        catch (Exception ex)
        {
            return (false, item.Name + " 清理失败：" + ex.Message);
        }
    }
}
