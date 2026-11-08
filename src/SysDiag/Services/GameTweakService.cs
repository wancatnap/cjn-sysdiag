using System.IO;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 游戏优化里的系统级操作：着色器缓存、VC++ 运行库、GPU 首选项、全屏优化。
/// 注册表只改 HKCU 下与「当前用户 + 当前程序」绑定的项，不碰全局显卡设置。
/// </summary>
public static class GameTweakService
{
    private const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string CompatKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string FullscreenFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public static bool IsAdmin => LoggingService.IsAdmin();

    // ------------------------------------------------------------ 着色器缓存

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>常见着色器缓存位置，存在且有文件才返回。</summary>
    public static List<ShaderCacheItem> ScanShaderCaches()
    {
        var candidates = new (string Name, string Vendor, string Path)[]
        {
            ("DXCache",           "NVIDIA", Path.Combine(LocalAppData, "NVIDIA", "DXCache")),
            ("GLCache",           "NVIDIA", Path.Combine(LocalAppData, "NVIDIA", "GLCache")),
            ("PerDriverVersion",  "NVIDIA", Path.Combine(LocalAppData, "Low", "NVIDIA", "PerDriverVersion", "DXCache")),
            ("D3DSCache",         "Microsoft", Path.Combine(LocalAppData, "D3DSCache")),
            ("DxCache",           "AMD", Path.Combine(LocalAppData, "AMD", "DxCache")),
            ("DxcCache",          "AMD", Path.Combine(LocalAppData, "AMD", "DxcCache")),
            ("ShaderCache",       "Intel", Path.Combine(LocalAppData, "Intel", "ShaderCache"))
        };

        var list = new List<ShaderCacheItem>();

        foreach (var (name, vendor, path) in candidates)
        {
            if (!Directory.Exists(path)) continue;

            var (count, size) = Measure(path);
            if (count == 0) continue;

            list.Add(new ShaderCacheItem
            {
                Name = name,
                Vendor = vendor,
                Path = path,
                FileCount = count,
                Size = size
            });
        }

        return list.OrderByDescending(i => i.Size).ToList();
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
        catch { }

        return (count, size);
    }

    public static (bool Ok, string Message) ClearShaderCache(ShaderCacheItem item)
    {
        if (!Directory.Exists(item.Path))
            return (false, "目录已不存在。");

        try
        {
            var files = Directory.GetFiles(item.Path, "*", SearchOption.AllDirectories).ToList();
            if (files.Count == 0) return (true, "已经是空的。");

            var errors = new List<string>();
            RecycleBin.MoveToRecycleBin(files, errors);

            return errors.Count == 0
                ? (true, $"已把 {files.Count} 个文件移入回收站")
                : (false, $"{errors.Count} 个文件被占用，未能删除");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ------------------------------------------------------------ 运行库检查

    /// <summary>只读检查 VC++ 2015-2022 运行库，不下载不安装。</summary>
    public static List<VcRuntimeInfo> CheckVcRuntimes()
    {
        var list = new List<VcRuntimeInfo>();

        list.Add(ReadRuntime(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64", "x64"));
        list.Add(ReadRuntime(@"SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86", "x86"));

        return list;
    }

    private static VcRuntimeInfo ReadRuntime(string path, string arch)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path);
            if (key == null)
                return new VcRuntimeInfo { Arch = arch, Installed = false };

            var installed = Convert.ToInt32(key.GetValue("Installed") ?? 0) == 1;
            var version = (key.GetValue("Version") as string ?? "").Trim().TrimStart('v');

            return new VcRuntimeInfo { Arch = arch, Installed = installed, Version = version };
        }
        catch
        {
            return new VcRuntimeInfo { Arch = arch, Installed = false };
        }
    }

    // ------------------------------------------------------------ 定位游戏 exe

    /// <summary>
    /// 在游戏目录里找主程序。三级兜底：
    /// <list type="number">
    /// <item>已知名字（三角洲，老用户的行为不变）</item>
    /// <item>虚幻引擎的 <c>*-Shipping.exe</c>——绝大多数 UE 游戏主程序都这么命名</item>
    /// <item>体积最大的 exe（限深度扫描，跳过 Engine / ThirdParty / 运行库）</item>
    /// </list>
    /// 三级都找不到就返回空，让用户自己挑 exe，**不猜**。
    /// </summary>
    public static string FindGameExe(string gameRoot)
    {
        if (string.IsNullOrEmpty(gameRoot) || !Directory.Exists(gameRoot)) return "";

        foreach (var name in new[] { "DeltaForceClient-Win64-Shipping.exe", "DeltaForceClient.exe" })
        {
            try
            {
                var hit = Directory.EnumerateFiles(gameRoot, name, SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
            catch { }
        }

        try
        {
            var shipping = Directory
                .EnumerateFiles(gameRoot, "*-Shipping.exe", SearchOption.AllDirectories)
                .FirstOrDefault(p => !p.Contains(@"\Engine\", StringComparison.OrdinalIgnoreCase));

            if (shipping != null) return shipping;
        }
        catch { }

        return LargestExe(gameRoot);
    }

    /// <summary>
    /// 限深度找体积最大的 exe。游戏主程序几乎总是目录里最大的那个，
    /// 但要排掉卸载器、安装器、崩溃上报这些同样很大的噪音。
    /// </summary>
    private static string LargestExe(string root, int maxDepth = 4)
    {
        var best = "";
        var bestSize = 0L;

        var stack = new Stack<(string Dir, int Depth)>();
        stack.Push((root, 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*.exe"))
                {
                    try
                    {
                        var name = Path.GetFileName(file);

                        if (name.Contains("unins", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("crash", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("report", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("vcredist", StringComparison.OrdinalIgnoreCase)
                            || name.Contains("dxsetup", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var size = new FileInfo(file).Length;

                        if (size > bestSize)
                        {
                            bestSize = size;
                            best = file;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (depth >= maxDepth) continue;

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);

                    if (name.Equals("Engine", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("_CommonRedist", StringComparison.OrdinalIgnoreCase))
                        continue;

                    stack.Push((sub, depth + 1));
                }
            }
            catch { }
        }

        return best;
    }

    // ------------------------------------------------------- GPU 高性能首选项

    /// <summary>读 HKCU\...\UserGpuPreferences 里这条程序的首选项。2 = 高性能。</summary>
    public static (bool HighPerformance, string Raw) GetGpuPreference(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return (false, "");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPrefKey);
            var raw = key?.GetValue(exePath) as string ?? "";
            return (raw.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase), raw);
        }
        catch
        {
            return (false, "");
        }
    }

    public static (bool Ok, string Message) SetGpuPreference(string exePath, bool highPerformance)
    {
        if (string.IsNullOrEmpty(exePath))
            return (false, "没找到游戏主程序，无法设置。");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPrefKey, writable: true);
            if (key == null) return (false, "打不开注册表项。");

            if (highPerformance)
            {
                key.SetValue(exePath, "GpuPreference=2;", RegistryValueKind.String);
                return (true, "已设为「高性能」。游戏下次启动生效；如果没变，在「设置 → 显示 → 图形」里确认一下。");
            }

            key.DeleteValue(exePath, throwOnMissingValue: false);
            return (true, "已取消高性能设置，恢复为系统默认。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // --------------------------------------------------------- 禁用全屏优化

    /// <summary>读兼容性标记。注意要保留 RUNASADMIN / HIGHDDPI 等其他标记。</summary>
    public static (bool Disabled, string Raw) GetFullscreenOptimization(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return (false, "");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(CompatKey);
            var raw = key?.GetValue(exePath) as string ?? "";
            return (raw.Contains(FullscreenFlag, StringComparison.OrdinalIgnoreCase), raw);
        }
        catch
        {
            return (false, "");
        }
    }

    /// <summary>
    /// 加/删 DISABLEDXMAXIMIZEDWINDOWEDMODE，其余标记原样保留。
    /// 一条值里可能同时有 RUNASADMIN、HIGHDDPI 等，不能整体覆盖。
    /// </summary>
    public static (bool Ok, string Message) SetFullscreenOptimization(string exePath, bool disable)
    {
        if (string.IsNullOrEmpty(exePath))
            return (false, "没找到游戏主程序，无法设置。");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(CompatKey, writable: true);
            if (key == null) return (false, "打不开注册表项。");

            var raw = key.GetValue(exePath) as string ?? "";
            var flags = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            // 第一个 "~" 是固定前缀，表示用户级兼容性设置
            flags.RemoveAll(f => f.Equals("~", StringComparison.Ordinal));
            flags.RemoveAll(f => f.Equals(FullscreenFlag, StringComparison.OrdinalIgnoreCase));

            if (disable) flags.Add(FullscreenFlag);

            if (flags.Count == 0)
            {
                key.DeleteValue(exePath, throwOnMissingValue: false);
                return (true, "已清除这条程序的兼容性设置。");
            }

            // 这条值必须以 "~ " 开头才被系统认作用户级兼容性设置
            var rebuilt = "~ " + string.Join(" ", flags);
            key.SetValue(exePath, rebuilt, RegistryValueKind.String);

            return (true, disable
                ? "已勾选「禁用全屏优化」，其余兼容性标记已保留。重新启动游戏生效。"
                : "已取消「禁用全屏优化」，其余兼容性标记已保留。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
