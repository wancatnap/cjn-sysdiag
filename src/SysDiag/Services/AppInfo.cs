using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace SysDiag.Services;

/// <summary>版本与构建标识：界面上显示，便于确认跑的是不是最新构建。</summary>
public static class AppInfo
{
    /// <summary>发版号，与 README 标题保持一致。</summary>
    public const string Version = "4.9";

    /// <summary>构建时间（取 exe 的写入时间；单文件发布下 Assembly.Location 为空）。</summary>
    public static DateTime BuildTime
    {
        get
        {
            try
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return File.GetLastWriteTime(path);
            }
            catch { /* 忽略 */ }

            return DateTime.MinValue;
        }
    }

    public static string BuildText =>
        BuildTime == DateTime.MinValue ? $"v{Version}" : $"v{Version} · 构建 {BuildTime:MM-dd HH:mm}";

    public static string FullText => $"{Version} ({BuildText})";
}
