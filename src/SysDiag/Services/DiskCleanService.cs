using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// C 盘空间扫描与清理。
/// 扫描阶段完全只读；清理阶段逐项执行，默认「安全项直接删、谨慎项进回收站」。
/// 不做「静默删除」——调用方必须先拿到用户勾选结果。
/// </summary>
public static class DiskCleanService
{
    private const string DriveRoot = @"C:\";

    // ---------------- 扫描 ----------------

    public static DiskScanResult Scan(IProgress<string> progress = null)
    {
        var sw = Stopwatch.StartNew();

        var result = new DiskScanResult { DriveLetter = "C" };

        try
        {
            var di = new DriveInfo("C");
            if (di.IsReady)
            {
                result.TotalBytes = di.TotalSize;
                result.FreeBytes = di.TotalFreeSpace;
            }
        }
        catch { }

        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var temp = Path.GetTempPath().TrimEnd('\\');

        var list = result.Targets;

        // —— 安全级：删了系统会自动重建，不影响功能 ——

        AddDirSet(list, progress, "temp-user", "临时文件", "用户临时文件",
            new[] { temp }, DiskRisk.Safe,
            "软件安装、解压、浏览器下载留下的临时文件。正在运行的程序占用的文件会跳过。");

        AddDirSet(list, progress, "temp-system", "临时文件", "系统临时文件",
            new[] { Path.Combine(win, "Temp") }, DiskRisk.Safe,
            "系统组件写入的临时文件。普通权限只能删掉一部分，管理员可以清得更干净。");

        AddDirSet(list, progress, "thumb", "缓存", "缩略图与图标缓存",
            ThumbCacheFiles(local), DiskRisk.Safe,
            "资源管理器为图片/视频生成的预览图。删除后打开文件夹会重新生成，第一次略慢。",
            false);

        AddDirSet(list, progress, "dxcache", "缓存", "DirectX 着色器缓存",
            new[] { Path.Combine(local, "D3DSCache") }, DiskRisk.Safe,
            "游戏首次编译的着色器缓存。删掉后头几次进游戏会重新编译，之后恢复正常。");

        AddDirSet(list, progress, "nvcache", "缓存", "NVIDIA 着色器缓存",
            new[]
            {
                Path.Combine(local, "NVIDIA", "DXCache"),
                Path.Combine(local, "NVIDIA", "GLCache"),
                Path.Combine(local, "NVIDIA", "ComputeCache"),
                Path.Combine(local, "NVIDIA Corporation", "NV_Cache")
            }, DiskRisk.Safe,
            "NVIDIA 驱动的编译缓存。删除后游戏首次加载稍慢，不影响画质与帧数。");

        AddDirSet(list, progress, "do", "缓存", "传递优化缓存",
            new[]
            {
                Path.Combine(win, "ServiceProfiles", "NetworkService", "AppData", "Local",
                    "Microsoft", "Windows", "DeliveryOptimization"),
                Path.Combine(win, "SoftwareDistribution", "DeliveryOptimization")
            }, DiskRisk.Safe,
            "Windows 更新的 P2P 分发缓存。删除后不影响已装更新，只是下次更新要重新下载。");

        AddDirSet(list, progress, "wer", "诊断", "错误报告存档（WER）",
            new[]
            {
                Path.Combine(local, "Microsoft", "Windows", "WER"),
                Path.Combine(common, "Microsoft", "Windows", "WER"),
                Path.Combine(local, "CrashDumps")
            }, DiskRisk.Safe,
            "程序崩溃时生成的报告与转储。删除后不影响使用，但之后就没法回看这次崩溃现场。");

        // —— 谨慎级：删了有代价，默认不勾 ——

        AddDirSet(list, progress, "wudownload", "系统", "Windows 更新缓存",
            new[] { Path.Combine(win, "SoftwareDistribution", "Download") }, DiskRisk.Careful,
            "已下载的更新安装包。删除后已安装的更新不受影响；如有更新正在下载会中断，需要重新下载。");

        AddDirSet(list, progress, "minidump", "系统", "内存转储（蓝屏现场）",
            new[] { Path.Combine(win, "Minidump") }, DiskRisk.Careful,
            "蓝屏时写出的小型转储，是排查蓝屏原因的主要证据。没有蓝屏问题可以清。");

        var memoryDmp = Path.Combine(win, "MEMORY.DMP");
        if (File.Exists(memoryDmp))
        {
            var size = SafeLength(memoryDmp);
            if (size > 0)
            {
                list.Add(new DiskCleanTarget
                {
                    Key = "memorydmp",
                    Category = "系统",
                    Name = "完整内存转储（MEMORY.DMP）",
                    Paths = new List<string> { memoryDmp },
                    IsDirectory = false,
                    Size = size,
                    FileCount = 1,
                    Risk = DiskRisk.Careful,
                    Note = "上次蓝屏时写出的完整内存镜像，通常与物理内存同量级。确认不需要排查蓝屏再删。"
                });
            }
        }

        AddDirSet(list, progress, "browsercache", "缓存", "浏览器缓存（Edge / Chrome）",
            BrowserCacheDirs(local), DiskRisk.Careful,
            "只清 Cache / Code Cache / GPUCache，不动 Cookie、密码、书签、历史记录。清理后网页首次加载稍慢。");

        var recycle = QueryRecycleBin(DriveRoot);
        if (recycle.size > 0)
        {
            list.Add(new DiskCleanTarget
            {
                Key = "recycle",
                Category = "回收站",
                Name = "回收站",
                IsRecycleBin = true,
                Size = recycle.size,
                FileCount = (int)Math.Min(recycle.items, int.MaxValue),
                Risk = DiskRisk.Careful,
                Note = "清空后无法还原。想先确认里面有没有要留的东西，可以打开桌面回收站逐个看。"
            });
        }

        // —— 仅展示：只统计体积，不提供删除 ——

        AddKeep(list, progress, "windowsold", "系统", "旧版 Windows 安装（Windows.old）",
            Path.Combine(DriveRoot, "Windows.old"),
            "升级前的旧系统备份。请用「设置 → 系统 → 存储 → 临时文件」或磁盘清理删除，直接删会留下权限残留。");

        AddKeep(list, progress, "winsxs", "系统", "组件存储（WinSxS）",
            Path.Combine(win, "WinSxS"),
            "系统组件仓库，体积大但大部分是硬链接，实际占用远小于显示值。请勿手动删除；要压缩请用 DISM /StartComponentCleanup。");

        AddKeep(list, progress, "installer", "系统", "Windows Installer 缓存",
            Path.Combine(win, "Installer"),
            "已安装软件的卸载/修复数据。删了会导致部分软件无法卸载或修复，请勿手动清理。");

        AddKeep(list, progress, "hiberfil", "系统", "休眠文件（hiberfil.sys）",
            Path.Combine(DriveRoot, "hiberfil.sys"),
            "休眠与快速启动用的内存镜像。要关掉请在管理员命令行执行 powercfg /h off（会同时关闭快速启动）。");

        // —— 规则库：内置精选 + 外部 winapp2.ini（见 CleanerRules） ——
        // 这里只做「把规则变成待清项」，安全过滤已经在解析阶段做完（路径必须在缓存根下、不含通配符）。
        foreach (var rule in CleanerRules.All())
        {
            var dirs = rule.Paths.Where(SafeExists).ToList();
            if (dirs.Count == 0) continue;

            AddDirSet(list, progress, rule.Key, rule.Category, rule.Name, dirs, rule.Risk,
                rule.Note + (rule.HasRegistry ? "（该规则含注册表项，本工具只清文件，注册表请用「注册表清理」）" : ""),
                rule.IsDirectory);
        }

        result.ElapsedMs = sw.ElapsedMilliseconds;

        // 大的排前面，方便一眼看到占用最多的
        result.Targets = list
            .Where(t => t.Size > 0)
            .OrderByDescending(t => t.Risk == DiskRisk.Keep ? 0 : 1)
            .ThenByDescending(t => t.Size)
            .ToList();

        return result;
    }

    /// <summary>深度分析：统计 C 盘各个顶层目录的占用。较慢（几十秒到几分钟）。</summary>
    public static List<RootUsageItem> ScanRootUsage(IProgress<string> progress = null)
    {
        var items = new List<RootUsageItem>();

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "Config.Msi", "$WinREAgent", "Recovery", "$SysReset"
        };

        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(DriveRoot).ToList(); }
        catch { return items; }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir.TrimEnd('\\'));
            if (skip.Contains(name)) continue;

            progress?.Report("统计 " + name + " …");

            var m = MeasureDirectory(dir);
            if (m.Size <= 0) continue;

            items.Add(new RootUsageItem { Name = name, Path = dir, Size = m.Size });
        }

        var sum = items.Sum(i => i.Size);
        foreach (var i in items)
            i.Percent = sum <= 0 ? 0 : i.Size * 100.0 / sum;

        return items.OrderByDescending(i => i.Size).ToList();
    }

    // ---------------- 清理 ----------------

    /// <summary>
    /// 清理勾选项。
    /// <paramref name="preferDirect"/> 为 true：安全级直接删除（立即释放空间），谨慎级仍进回收站。
    /// 为 false：全部进回收站（可还原，但要点清空回收站才真正释放）。
    /// </summary>
    public static DiskCleanOutcome Clean(IReadOnlyList<DiskCleanTarget> targets, bool preferDirect,
        IProgress<string> progress = null)
    {
        var sw = Stopwatch.StartNew();
        var outcome = new DiskCleanOutcome();

        var errors = new List<string>();

        foreach (var t in targets.Where(x => x.CanClean))
        {
            progress?.Report("正在清理：" + t.Name);

            if (t.IsRecycleBin)
            {
                // 清理回收站只有一种做法：清空。属于真正删除。
                if (EmptyRecycleBin(DriveRoot, out var err))
                {
                    outcome.DeletedBytes += t.Size;
                    outcome.RemovedTargets++;
                }
                else if (errors.Count < 20)
                {
                    errors.Add($"回收站：{err}");
                }

                continue;
            }

            // 只有「安全级」才会被直接删除；谨慎级永远走回收站
            var useDirect = preferDirect && t.Risk == DiskRisk.Safe;

            if (useDirect)
            {
                long freed = 0;
                int removed = 0;

                if (t.IsDirectory)
                {
                    foreach (var dir in t.Paths)
                    {
                        if (!Directory.Exists(dir)) continue;
                        var r = PurgeDirectory(dir, errors);
                        freed += r.Bytes;
                        removed += r.Files;
                    }
                }
                else
                {
                    foreach (var file in t.Paths)
                    {
                        try
                        {
                            if (!File.Exists(file)) continue;
                            var len = SafeLength(file);
                            ClearReadOnly(file);
                            File.Delete(file);
                            freed += len;
                            removed++;
                        }
                        catch (Exception ex)
                        {
                            AddError(errors, $"{file} → {ex.Message}");
                        }
                    }
                }

                outcome.DeletedBytes += freed;
                outcome.RemovedFiles += removed;
                if (removed > 0) outcome.RemovedTargets++;
            }
            else
            {
                var moved = MoveToRecycle(t.Paths, t.IsDirectory, errors);
                outcome.RecycledBytes += t.Size;
                outcome.RemovedFiles += moved;
                if (moved > 0) outcome.RemovedTargets++;
            }
        }

        outcome.Errors = errors;        outcome.ElapsedMs = sw.ElapsedMilliseconds;
        return outcome;
    }

    /// <summary>清空 C 盘回收站（真正删除）。</summary>
    public static bool EmptyRecycleBin(string root, out string error)
    {
        error = "";

        try
        {
            const uint NOCONFIRMATION = 0x1, NOPROGRESSUI = 0x2, NOSOUND = 0x4;
            var code = SHEmptyRecycleBin(IntPtr.Zero, root,
                NOCONFIRMATION | NOPROGRESSUI | NOSOUND);

            if (code == 0 || code == unchecked((int)0x8000FFFF)) return true; // 后者=回收站本来就是空的

            error = "Shell 错误码 0x" + code.ToString("X8");
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    // ---------------- 内部实现 ----------------

    private static void AddDirSet(List<DiskCleanTarget> list, IProgress<string> progress, string key,
        string category, string name, IEnumerable<string> dirs, DiskRisk risk, string note,
        bool isDirectory = true)
    {
        var existing = dirs
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Where(SafeExists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existing.Count == 0) return;

        long size = 0;
        var count = 0;

        foreach (var dir in existing)
        {
            progress?.Report("统计 " + name + "…");

            if (isDirectory && Directory.Exists(dir))
            {
                var m = MeasureDirectory(dir);
                size += m.Size;
                count += m.Count;
            }
            else if (File.Exists(dir))
            {
                size += SafeLength(dir);
                count++;
            }
        }

        if (size <= 0) return;

        list.Add(new DiskCleanTarget
        {
            Key = key,
            Category = category,
            Name = name,
            Paths = existing,
            IsDirectory = isDirectory,
            Size = size,
            FileCount = count,
            Risk = risk,
            Note = note
        });
    }

    private static void AddKeep(List<DiskCleanTarget> list, IProgress<string> progress, string key,
        string category, string name, string path, string note)
    {
        if (!SafeExists(path)) return;

        progress?.Report("统计 " + name + "…");

        long size;
        var count = 0;

        if (Directory.Exists(path))
        {
            var m = MeasureDirectory(path);
            size = m.Size;
            count = m.Count;
        }
        else
        {
            size = SafeLength(path);
            count = 1;
        }

        if (size <= 0) return;

        list.Add(new DiskCleanTarget
        {
            Key = key,
            Category = category,
            Name = name,
            Paths = new List<string> { path },
            IsDirectory = Directory.Exists(path),
            Size = size,
            FileCount = count,
            Risk = DiskRisk.Keep,
            Note = note
        });
    }

    /// <summary>递归统计目录体积与文件数；不跟随重解析点（避免符号链接绕圈）。</summary>
    private static (long Size, int Count) MeasureDirectory(string root)
    {
        long size = 0;
        var count = 0;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();

            try
            {
                foreach (var f in Directory.EnumerateFiles(cur))
                {
                    try
                    {
                        size += new FileInfo(f).Length;
                        count++;
                    }
                    catch { }
                }
            }
            catch { }

            try
            {
                foreach (var d in Directory.EnumerateDirectories(cur))
                {
                    try
                    {
                        if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch { continue; }

                    stack.Push(d);
                }
            }
            catch { }
        }

        return (size, count);
    }

    private static (int Files, long Bytes) PurgeDirectory(string root, List<string> errors)
    {
        var files = 0;
        long bytes = 0;

        var processDir = ProcessDirectory();
        var stack = new Stack<string>();
        var subDirs = new List<string>();

        stack.Push(root);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();

            // 自己的解压/运行目录不能删，删了会把本进程掀掉
            if (processDir.Length > 0 && cur.StartsWith(processDir, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                foreach (var f in Directory.EnumerateFiles(cur))
                {
                    try
                    {
                        var fi = new FileInfo(f);
                        var len = fi.Length;

                        if ((fi.Attributes & FileAttributes.ReadOnly) != 0)
                            fi.Attributes &= ~FileAttributes.ReadOnly;

                        File.Delete(f);
                        files++;
                        bytes += len;
                    }
                    catch (Exception ex)
                    {
                        AddError(errors, $"{f} → {Short(ex.Message)}");
                    }
                }
            }
            catch (Exception ex)
            {
                AddError(errors, $"{cur} → {Short(ex.Message)}");
            }

            try
            {
                foreach (var d in Directory.EnumerateDirectories(cur))
                {
                    subDirs.Add(d);
                    stack.Push(d);
                }
            }
            catch { }
        }

        // 子目录深的先删，父目录才能变空
        foreach (var d in subDirs.OrderByDescending(x => x.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(d).Any())
                    Directory.Delete(d);
            }
            catch { }
        }

        return (files, bytes);
    }

    private static int MoveToRecycle(IEnumerable<string> paths, bool isDirectory, List<string> errors)
    {
        var moved = RecycleBin.MoveToRecycleBin(paths, errors);

        // 目录被整体移走后，重建空目录，避免依赖该目录的程序报错
        if (isDirectory)
        {
            foreach (var dir in paths)
            {
                try
                {
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch { }
            }
        }

        return moved.Count;
    }

    private static IEnumerable<string> ThumbCacheFiles(string local)
    {
        var dir = Path.Combine(local, "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(dir)) return Array.Empty<string>();

        var list = new List<string>();

        foreach (var pattern in new[] { "thumbcache_*.db", "iconcache_*.db" })
        {
            try { list.AddRange(Directory.EnumerateFiles(dir, pattern)); }
            catch { }
        }

        return list;
    }

    private static IEnumerable<string> BrowserCacheDirs(string local)
    {
        var result = new List<string>();

        var roots = new[]
        {
            Path.Combine(local, "Microsoft", "Edge", "User Data"),
            Path.Combine(local, "Google", "Chrome", "User Data"),
            Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data")
        };

        var subs = new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "ShaderCache" };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            List<string> profiles;
            try { profiles = Directory.EnumerateDirectories(root).ToList(); }
            catch { continue; }

            foreach (var profile in profiles)
            {
                foreach (var sub in subs)
                {
                    var dir = Path.Combine(profile, sub);
                    if (Directory.Exists(dir)) result.Add(dir);
                }
            }
        }

        return result;
    }

    private static string ProcessDirectory()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return "";

            var dir = Path.GetDirectoryName(exe);
            return string.IsNullOrEmpty(dir) ? "" : dir.TrimEnd('\\');
        }
        catch { return ""; }
    }

    private static void AddError(List<string> errors, string message)
    {
        if (errors.Count < 20) errors.Add(message);
    }

    private static string Short(string message)
    {
        var m = message.Replace("\r", " ").Replace("\n", " ").Trim();
        return m.Length > 90 ? m[..90] : m;
    }

    private static void ClearReadOnly(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attrs & ~FileAttributes.ReadOnly);
        }
        catch { }
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path) || Directory.Exists(path); }
        catch { return false; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    // ---------------- Shell 回收站 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

    private static (long size, long items) QueryRecycleBin(string root)
    {
        try
        {
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            var hr = SHQueryRecycleBin(root, ref info);
            if (hr != 0) return (0, 0);
            return (info.i64Size, info.i64NumItems);
        }
        catch { return (0, 0); }
    }
}
