using System.IO;
using System.Text;

namespace SysDiag.Services;

/// <summary>未处理异常落盘：只写文件，不弹系统对话框。</summary>
public static class CrashLog
{
    public static string FilePath
    {
        get
        {
            try
            {
                AppPaths.Ensure();
                return Path.Combine(AppPaths.OutputDir, "crash.log");
            }
            catch
            {
                return Path.Combine(Path.GetTempPath(), "sysdiag-crash.log");
            }
        }
    }

    public static void Write(Exception ex, string? file = null)
    {
        try
        {
            var target = file ?? FilePath;
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex.GetType().Name}: {ex.Message}");
            sb.AppendLine(ex.StackTrace ?? "");
            sb.AppendLine();

            File.AppendAllText(target, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* 写日志失败也不能再抛 */ }
    }
}
