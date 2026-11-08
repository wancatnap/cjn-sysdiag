using System.IO;
namespace SysDiag.Services;

public static class AppPaths
{
    private static bool _ready;

    /// <summary>exe 所在目录；输出目录优先落在这里，不可写时回退到“文档”。</summary>
    public static string Root { get; private set; } = AppContext.BaseDirectory;

    public static string OutputDir => Path.Combine(Root, "outputs");

    /// <summary>配置与浏览器缓存放在 LocalAppData，保证程序放在 Program Files 等只读位置也能运行。</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysDiag");

    public static string WebViewDir => Path.Combine(DataDir, "WebView2");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static void Ensure()
    {
        if (_ready) return;

        try
        {
            Directory.CreateDirectory(OutputDir);
        }
        catch
        {
            Root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "破烂优化");
            Directory.CreateDirectory(OutputDir);
        }

        try { Directory.CreateDirectory(DataDir); } catch { /* 极端情况下忽略 */ }
        try { Directory.CreateDirectory(WebViewDir); } catch { }

        _ready = true;
    }
}
