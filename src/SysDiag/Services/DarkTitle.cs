using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SysDiag.Services;

/// <summary>
/// 把窗口标题栏切成深色（Win10 1809+ 的沉浸式暗色模式）。
/// 不改窗口结构、不用自定义标题栏，一个 DWM 属性即可，失败时静默跳过。
/// </summary>
public static class DarkTitle
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE（Win10 20H1+）
    // 19 = 同属性在更早版本上的编号
    private const int DarkModeNew = 20;
    private const int DarkModeOld = 19;

    // Win11 专有：34 边框色、35 标题栏底色、36 标题文字色。旧系统上调用会失败，忽略即可。
    private const int BorderColor = 34;
    private const int CaptionColor = 35;
    private const int TextColor = 36;

    // COLORREF 是 0x00BBGGRR，与 XAML 的 #RRGGBB 相反
    private const int CaptionRef = 0x0020110A;   // #0A1120
    private const int TextRef = 0x00FAF0E8;      // #E8F0FA
    private const int BorderRef = 0x0040271B;    // #1B2740

    /// <summary>对指定窗口应用深色标题栏。</summary>
    public static void Apply(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;

            // 子窗口在父窗口的 Loaded 里被创建时，此时可能还没有 HWND，等句柄就绪再设一次
            if (handle == IntPtr.Zero)
            {
                window.SourceInitialized += (_, _) => Apply(window);
                return;
            }

            var on = 1;
            var hr = DwmSetWindowAttribute(handle, DarkModeNew, ref on, sizeof(int));
            if (hr != 0)
                DwmSetWindowAttribute(handle, DarkModeOld, ref on, sizeof(int));

            // 固定成主题色，避免跟随系统强调色后和别的窗口不一致
            var caption = CaptionRef;
            var text = TextRef;
            var border = BorderRef;
            DwmSetWindowAttribute(handle, CaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(handle, TextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(handle, BorderColor, ref border, sizeof(int));
        }
        catch
        {
            // 老系统不支持就保持默认标题栏，不影响功能
        }
    }
}
