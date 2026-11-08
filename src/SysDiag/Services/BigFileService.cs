using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace SysDiag.Services;

/// <summary>一个大文件的条目。</summary>
public sealed class BigFileItem
{
    public string Name { get; init; } = "";
    public string FullPath { get; init; } = "";
    public long Size { get; init; }
    public DateTime LastAccess { get; init; }
    public string Kind { get; init; } = "其他";

    public string SizeText => BigFileService.FormatSize(Size);

    public string LastAccessText => LastAccess.Year < 2000 ? "—" : LastAccess.ToString("yyyy-MM-dd HH:mm");

    public string Folder => Path.GetDirectoryName(FullPath) ?? "";
}

/// <summary>
/// 大文件扫描。
///
/// 只做一件事：按大小阈值把磁盘上的大文件列出来，让用户自己判断该不该删。
/// **不做「推荐清理」**——一个 20 GB 的游戏安装包可能是他明天要玩的，
/// 一个 5 GB 的工程文件可能是三年的活。所以这里只提供排序和定位，删除永远由用户点。
///
/// 扫描策略：广度优先遍历，跳过系统目录与回收站（那些地方的东西删了要么危险要么没意义），
/// 忽略无权限目录与重解析点（避免符号链接绕圈）。结果按大小降序取前 N 条。
/// </summary>
public static class BigFileService
{
    /// <summary>这些目录不进去：系统本体、程序安装位、回收站、卷影。</summary>
    private static readonly string[] SkipNames =
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "$WinREAgent", "System Volume Information",
        "Recovery", "Config.Msi", "Windows.old", "PerfLogs"
    };

    /// <summary>常见的大文件类型，用来给列表打标签。判断不了就算「其他」。</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "视频", [".mkv"] = "视频", [".avi"] = "视频", [".mov"] = "视频",
        [".wmv"] = "视频", [".flv"] = "视频", [".webm"] = "视频", [".ts"] = "视频",
        [".m2ts"] = "视频", [".mpg"] = "视频", [".rmvb"] = "视频",

        [".iso"] = "镜像", [".img"] = "镜像", [".vhd"] = "镜像", [".vhdx"] = "镜像",
        [".wim"] = "镜像", [".esd"] = "镜像", [".dmg"] = "镜像",

        [".zip"] = "压缩包", [".7z"] = "压缩包", [".rar"] = "压缩包",
        [".tar"] = "压缩包", [".gz"] = "压缩包", [".xz"] = "压缩包", [".bz2"] = "压缩包",

        [".exe"] = "安装包", [".msi"] = "安装包", [".msix"] = "安装包", [".appx"] = "安装包",

        [".flac"] = "音频", [".wav"] = "音频", [".mp3"] = "音频", [".ape"] = "音频",

        [".pak"] = "游戏资源", [".assets"] = "游戏资源", [".bundle"] = "游戏资源",
        [".vpk"] = "游戏资源", [".pck"] = "游戏资源",

        [".log"] = "日志", [".dmp"] = "转储", [".etl"] = "转储"
    };

    /// <summary>固定磁盘的根目录（U 盘、光驱不算）。</summary>
    public static List<(string Root, string Label)> FixedDrives()
    {
        var list = new List<(string, string)>();

        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;

                    var free = FormatSize(d.AvailableFreeSpace);
                    list.Add((d.RootDirectory.FullName, $"{d.Name.TrimEnd('\\')} 可用 {free}"));
                }
                catch { /* 单个盘读不到继续 */ }
            }
        }
        catch { /* 跳过 */ }

        return list;
    }

    /// <summary>
    /// 扫描大文件。
    /// <paramref name="minBytes"/> 是阈值；<paramref name="roots"/> 为空时扫所有固定盘。
    /// 结果按大小降序，最多 <paramref name="limit"/> 条。
    /// </summary>
    public static List<BigFileItem> Scan(
        long minBytes,
        IReadOnlyList<string> roots,
        IProgress<string> progress,
        CancellationToken token,
        int limit = 400)
    {
        var targets = roots is { Count: > 0 }
            ? roots.ToList()
            : FixedDrives().Select(d => d.Root).ToList();

        var found = new List<BigFileItem>();
        var visited = 0;

        var stack = new Stack<string>(targets);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();

            var dir = stack.Pop();

            visited++;

            if (visited % 300 == 0)
                progress?.Report($"{dir}　·　已找到 {found.Count} 个大文件");

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);

                    if (SkipNames.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;

                    // 重解析点（符号链接、挂载点）不跟，否则可能绕圈或跑到别的卷
                    try
                    {
                        var attr = File.GetAttributes(sub);
                        if ((attr & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }

                    stack.Push(sub);
                }
            }
            catch { /* 无权限或读不到，跳过这一层的子目录 */ }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        var info = new FileInfo(file);

                        if (info.Length < minBytes) continue;

                        found.Add(new BigFileItem
                        {
                            Name = info.Name,
                            FullPath = info.FullName,
                            Size = info.Length,
                            LastAccess = info.LastAccessTime,
                            Kind = KindOf(info.Extension)
                        });
                    }
                    catch { /* 单个文件读不到继续 */ }
                }
            }
            catch { /* 跳过 */ }
        }

        progress?.Report($"扫完 {visited} 个目录，找到 {found.Count} 个大文件");

        return found
            .OrderByDescending(x => x.Size)
            .Take(limit)
            .ToList();
    }

    public static string KindOf(string extension)
        => Kinds.TryGetValue(extension ?? "", out var kind) ? kind : "其他";

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";

        return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
    }
}
