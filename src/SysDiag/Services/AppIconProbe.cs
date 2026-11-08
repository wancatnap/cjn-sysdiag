using System.Runtime.InteropServices;

namespace SysDiag.Services;

/// <summary>
/// 检查 exe 里是否真的写进了图标资源。
///
/// 为什么需要它：漏配 `&lt;ApplicationIcon&gt;` 时构建不会报错，只是任务栏和任务管理器
/// 显示 .NET 的默认图标——不把两个版本并排看很难发现，所以做成断言。
///
/// 判定依据是 PE 里的 RT_GROUP_ICON 资源，不是「能不能取到图标句柄」：
/// ExtractIcon 在 exe 没有图标时会**返回系统默认图标**，句柄照样非 0，据此判断会永远通过。
///
/// 探测方式用 EnumResourceNames 而不是「找 id=1」：资源 id 由资源编译器分配，
/// 不保证从 1 连续编号；写死 id 会在一部分构建上假失败（本机就踩过）。
/// </summary>
public static class AppIconProbe
{
    private const int RtGroupIcon = 14;
    private const uint LoadLibraryAsDataFile = 0x00000002;

    private delegate bool EnumResNameProc(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

    /// <summary>数一数 exe 里有几组图标资源。返回 0 表示没有自定义图标。</summary>
    public static int GroupIconCount(string exePath)
    {
        var (count, isOwn) = Probe(exePath);
        _ = isOwn;
        return count;
    }

    /// <summary>自检用：返回「几组图标」以及失败原因，便于在断言里看到到底卡在哪一步。</summary>
    public static string Diagnose(string exePath)
    {
        var (count, isOwn) = Probe(exePath);

        if (count > 0)
            return $"{count} 组图标资源（{(isOwn ? "当前进程自身" : "从文件加载")}）";

        return "0 组图标资源" + (_lastError == 0 ? "" : $"，Win32 错误 {_lastError}");
    }

    private static int _lastError;

    private static (int Count, bool IsOwn) Probe(string exePath)
    {
        _lastError = 0;

        if (string.IsNullOrEmpty(exePath))
        {
            _lastError = 87; // ERROR_INVALID_PARAMETER
            return (0, false);
        }

        var isOwn = string.Equals(exePath, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        var module = IntPtr.Zero;
        var loaded = false;

        try
        {
            if (isOwn)
            {
                // 自身已经映射在内存里：直接拿模块句柄，比 LoadLibrary 再加载一次稳
                module = GetModuleHandle(null);
            }

            if (module == IntPtr.Zero)
            {
                module = LoadLibraryEx(exePath, IntPtr.Zero, LoadLibraryAsDataFile);
                loaded = module != IntPtr.Zero;
            }

            if (module == IntPtr.Zero)
            {
                _lastError = Marshal.GetLastWin32Error();
                return (0, isOwn);
            }

            var count = 0;
            var callback = new EnumResNameProc((_, _, _, _) =>
            {
                count++;
                return true; // 继续枚举
            });

            if (!EnumResourceNames(module, (IntPtr)RtGroupIcon, callback, IntPtr.Zero))
            {
                _lastError = Marshal.GetLastWin32Error();
                // ERROR_RESOURCE_TYPE_NOT_FOUND(1813) 就是「没有图标资源」，不算异常
                if (_lastError == 1813) _lastError = 0;
            }

            GC.KeepAlive(callback);
            return (count, isOwn);
        }
        catch
        {
            return (0, isOwn);
        }
        finally
        {
            if (loaded) FreeLibrary(module);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string fileName, IntPtr file, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, EnumResNameProc callback, IntPtr param);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr module);
}
