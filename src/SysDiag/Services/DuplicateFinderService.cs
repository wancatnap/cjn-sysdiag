using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace SysDiag.Services;

/// <summary>重复组里的一个文件。</summary>
public sealed class DuplicateFile
{
    public string Path { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
    public long Size { get; init; }
    public DateTime LastWrite { get; init; }

    public string LastWriteText => LastWrite.Year < 2000 ? "—" : LastWrite.ToString("yyyy-MM-dd HH:mm");
}

/// <summary>一组内容完全相同的文件。</summary>
public sealed class DuplicateGroup
{
    public string Hash { get; init; } = "";
    public long Size { get; init; }
    public List<DuplicateFile> Files { get; init; } = new();

    /// <summary>这组里删到只剩一个能省多少。</summary>
    public long WastedBytes => Size * Math.Max(0, Files.Count - 1);

    public string SizeText => DuplicateFinderService.FormatSize(Size);
    public string WastedText => DuplicateFinderService.FormatSize(WastedBytes);
    public string Header => $"{SizeText} × {Files.Count} 个　·　可省 {WastedText}";
}

/// <summary>一次扫描的统计。全部是可核对的事实，不是估算。</summary>
public sealed class DuplicateScanResult
{
    public List<DuplicateGroup> Groups { get; init; } = new();
    public int FilesEnumerated { get; init; }
    public int SizeCandidates { get; init; }
    public int PrehashCandidates { get; init; }
    public int FullyHashed { get; init; }
    public long BytesRead { get; init; }
    public bool Canceled { get; init; }
    public string Error { get; init; } = "";

    public long WastedBytes => Groups.Sum(g => g.WastedBytes);

    public string Summary =>
        $"扫到 {FilesEnumerated} 个文件，其中 {SizeCandidates} 个存在同尺寸文件，完整比对 {FullyHashed} 个；" +
        $"找到重复 {Groups.Count} 组，可省 {DuplicateFinderService.FormatSize(WastedBytes)}";
}

/// <summary>
/// 重复文件查找。算法照 Czkawka 的三级流水线，把最贵的全量哈希留到最后：
///
///   1. 按文件大小分组 —— 大小不同的绝不重复，这一步只在内存里做，砍掉绝大多数；
///   2. 预哈希 —— 同尺寸的文件只读「头 4KB + 尾 4KB」，指纹不同就不再往下走；
///   3. 全量 SHA256 —— 只有前两关都活下来的文件才完整读一遍。
///
/// 为什么不用 BLAKE3：Czkawka 用它是为了更快的吞吐，而本工具在 C# 里用内置 SHA256
/// 不必引第三方包，且真正的耗时在磁盘 IO 不在这几十 MB 的哈希运算上。
///
/// 明确不做：相似图片（感知哈希）、相似视频、相似音频。它们要么需要图像/视频解码依赖，
/// 要么误判代价高（把不同照片判成同一张，用户删了就是照片没了）。重复文件是**字节完全相同**，
/// 判错概率在工程上等于零，这是敢让用户删的前提。
/// </summary>
public static class DuplicateFinderService
{
    /// <summary>这些目录不进去：系统本体、程序安装位、回收站、卷影。</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "$WinREAgent", "System Volume Information",
        "Recovery", "Config.Msi", "Windows.old", "PerfLogs",
        "node_modules", ".git", "AppData"
    };

    private const int PrehashSlice = 4096;
    private const int MaxGroups = 300;

    public static string FormatSize(long bytes)
    {
        const double KB = 1024, MB = KB * 1024, GB = MB * 1024;
        if (bytes >= GB) return (bytes / GB).ToString(bytes >= 10 * GB ? "0.0" : "0.00") + " GB";
        if (bytes >= MB) return (bytes / MB).ToString(bytes >= 100 * MB ? "0" : "0.0") + " MB";
        if (bytes >= KB) return (bytes / KB).ToString("0") + " KB";
        return bytes + " B";
    }

    /// <summary>
    /// 扫描一个目录。minSize 以下的小文件不参与——它们数量最多、省不了多少空间，
    /// 而且重复的小文件往往是程序自己的配置，删了会坏。
    /// </summary>
    public static DuplicateScanResult Scan(string root, long minSize, CancellationToken token,
        Action<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return new DuplicateScanResult { Error = "目录不存在。" };

        try
        {
            progress?.Invoke("正在列举文件…");
            var files = new List<(string Path, long Size)>();
            foreach (var file in EnumerateFiles(root, minSize, token))
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length < minSize) continue;
                    files.Add((file, info.Length));
                }
                catch { }

                if (files.Count % 500 == 0) progress?.Invoke($"正在列举文件… 已找到 {files.Count} 个");
            }

            if (token.IsCancellationRequested)
                return new DuplicateScanResult { FilesEnumerated = files.Count, Canceled = true };

            // 第 1 级：同尺寸才有可能是重复
            var bySize = files.GroupBy(f => f.Size)
                .Where(g => g.Count() > 1)
                .OrderByDescending(g => g.Key)
                .ToList();

            var sizeCandidates = bySize.Sum(g => g.Count());

            // 第 2 级：头尾各 4KB 的指纹
            var byPrehash = new Dictionary<string, List<(string Path, long Size)>>(StringComparer.Ordinal);
            var hashed = 0;
            long bytesRead = 0;

            foreach (var group in bySize)
            {
                foreach (var (path, size) in group)
                {
                    if (token.IsCancellationRequested) break;

                    var key = Prehash(path, size, ref bytesRead);
                    if (key == null) continue;

                    if (!byPrehash.TryGetValue(key, out var bucket))
                        byPrehash[key] = bucket = new List<(string, long)>();

                    bucket.Add((path, size));
                    hashed++;

                    if (hashed % 20 == 0) progress?.Invoke($"正在比对 {hashed}/{sizeCandidates} 个同尺寸文件…");
                }

                if (token.IsCancellationRequested) break;
            }

            if (token.IsCancellationRequested)
                return new DuplicateScanResult
                {
                    FilesEnumerated = files.Count,
                    SizeCandidates = sizeCandidates,
                    FullyHashed = hashed,
                    BytesRead = bytesRead,
                    Canceled = true
                };

            var prehashCandidates = byPrehash.Values.Where(v => v.Count > 1).ToList();

            // 第 3 级：完整哈希
            var byHash = new Dictionary<string, List<(string Path, long Size)>>(StringComparer.Ordinal);
            var fullyHashed = 0;

            foreach (var bucket in prehashCandidates)
            {
                foreach (var (path, size) in bucket)
                {
                    if (token.IsCancellationRequested) break;

                    var hash = FullHash(path, ref bytesRead);
                    if (hash == null) continue;

                    if (!byHash.TryGetValue(hash, out var target))
                        byHash[hash] = target = new List<(string, long)>();

                    target.Add((path, size));
                    fullyHashed++;

                    progress?.Invoke($"正在完整比对… {fullyHashed}");
                }

                if (token.IsCancellationRequested) break;
            }

            var groups = byHash
                .Where(kv => kv.Value.Count > 1)
                .Select(kv => new DuplicateGroup
                {
                    Hash = kv.Key,
                    Size = kv.Value[0].Size,
                    Files = kv.Value
                        .Select(f =>
                        {
                            DateTime stamp = DateTime.MinValue;
                            try { stamp = File.GetLastWriteTime(f.Path); } catch { }
                            return new DuplicateFile { Path = f.Path, Size = f.Size, LastWrite = stamp };
                        })
                        .OrderBy(f => f.LastWrite)
                        .ThenBy(f => f.Path.Length)
                        .ToList()
                })
                .OrderByDescending(g => g.WastedBytes)
                .Take(MaxGroups)
                .ToList();

            return new DuplicateScanResult
            {
                Groups = groups,
                FilesEnumerated = files.Count,
                SizeCandidates = sizeCandidates,
                PrehashCandidates = prehashCandidates.Sum(b => b.Count),
                FullyHashed = fullyHashed,
                BytesRead = bytesRead,
                Canceled = token.IsCancellationRequested
            };
        }
        catch (Exception ex)
        {
            return new DuplicateScanResult { Error = ex.Message };
        }
    }

    /// <summary>
    /// 广度优先枚举。跳过系统目录、重解析点（junction 会绕圈）、以及无权限目录。
    /// 不用 Directory.EnumerateFiles(AllDirectories)：它在遇到第一个无权限目录时就整体抛异常，
    /// 而用户目录里几乎必然有这种目录。
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root, long minSize, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested) yield break;

            var dir = stack.Pop();

            string[] subDirs;
            try { subDirs = Directory.GetDirectories(dir); }
            catch { subDirs = Array.Empty<string>(); }

            foreach (var sub in subDirs)
            {
                var name = Path.GetFileName(sub);
                if (SkipNames.Contains(name)) continue;

                try
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }

                stack.Push(sub);
            }

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }

            foreach (var file in files) yield return file;
        }
    }

    /// <summary>
    /// 头 4KB + 尾 4KB + 文件长度的 SHA256。
    /// 读不满时（小文件）就整份读一遍，仍然比全量便宜。
    /// </summary>
    private static string? Prehash(string path, long size, ref long bytesRead)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.SequentialScan);

            var buffer = new byte[PrehashSlice * 2];
            var read = 0;

            if (size <= PrehashSlice * 2)
            {
                read = ReadFully(stream, buffer, 0, buffer.Length);
            }
            else
            {
                var head = ReadFully(stream, buffer, 0, PrehashSlice);
                stream.Seek(-PrehashSlice, SeekOrigin.End);
                var tail = ReadFully(stream, buffer, PrehashSlice, PrehashSlice);
                read = head + tail;
            }

            bytesRead += read;

            using var sha = SHA256.Create();
            var lengthBytes = BitConverter.GetBytes(size);
            sha.TransformBlock(lengthBytes, 0, lengthBytes.Length, null, 0);
            sha.TransformFinalBlock(buffer, 0, read);
            return Convert.ToHexString(sha.Hash!);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>完整 SHA256。读到一半文件被删/被锁就返回 null，不让整轮扫描失败。</summary>
    private static string? FullHash(string path, ref long bytesRead)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                1024 * 1024, FileOptions.SequentialScan);
            using var sha = SHA256.Create();

            var buffer = new byte[512 * 1024];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                bytesRead += read;
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch
        {
            return null;
        }
    }

    private static int ReadFully(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = stream.Read(buffer, offset + total, count - total);
            if (n <= 0) break;
            total += n;
        }
        return total;
    }

    /// <summary>
    /// 默认勾选策略：每组保留「最早修改的那个」，其余全部选中。
    /// 保留最早的理由是它通常是原件，后来的多半是复制/下载的副本。
    /// 这只是**预选**，用户仍然要手动确认才会删。
    /// </summary>
    public static List<string> PreselectDuplicates(List<DuplicateGroup> groups)
    {
        var result = new List<string>();
        foreach (var group in groups)
        {
            if (group.Files.Count <= 1) continue;
            foreach (var file in group.Files.Skip(1))
                result.Add(file.Path);
        }
        return result;
    }
}
