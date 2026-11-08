using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 以管理员身份重启「整个程序」。USN 日志要打开磁盘卷句柄，只能整程序提权，
/// 不是提权跑一条命令就退出——重启后主窗口照常可用，并自动回到「文件删除记录」。
/// </summary>
public static class ElevationService
{
    /// <summary>用户在 UAC 对话框点了「否」。</summary>
    public const int ErrorCancelled = 1223;
    public const string CancelledMessage = "已取消管理员授权。";

    public static bool IsAdmin => LoggingService.IsAdmin();

    /// <summary>重启后的参数：主窗口据此自动打开「文件删除记录」并开始读取。</summary>
    public static string BuildResumeArgs(string drive, int hours)
        => $"--admin --open-usn {NormalizeDrive(drive)} {hours}";

    /// <summary>只留盘符字母，兼容 "C:" / "c:\" / "C" 三种写法。</summary>
    public static string NormalizeDrive(string drive)
    {
        var d = (drive ?? "C").Trim().TrimEnd(':', '\\');
        if (d.Length == 0) return "C";
        return char.ToUpperInvariant(d[0]).ToString();
    }

    /// <summary>
    /// 以管理员身份拉起新的程序实例。返回 true 表示新实例已启动，
    /// 调用方应立刻退出当前实例，避免同时存在两个窗口。
    /// </summary>
    public static bool RelaunchAsAdmin(string arguments, out string error)
    {
        error = "";

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            error = "拿不到程序路径，无法以管理员身份重启。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppPaths.Root
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            error = CancelledMessage;
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>自检用：参数拼出来还能原样解析回去，且不真的弹 UAC。</summary>
    public static LaunchOptions RoundTrip(string drive, int hours)
        => LaunchOptions.Parse(
            BuildResumeArgs(drive, hours).Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
