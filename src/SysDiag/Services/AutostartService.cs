using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace SysDiag.Services;

/// <summary>
/// 开机自启动。
///
/// 提供两种方式，不是「多给个选项」而已，而是能力不同：
///
/// - **当前用户（Run 键）**：不需要管理员，登录后以普通权限启动。
///   缺的是——本软件的一半功能（服务、驱动、HKLM 写入）会要求提权，
///   用户每次都要多点一次 UAC。
/// - **计划任务（最高权限）**：登录后直接以管理员身份启动，不会再弹 UAC。
///   代价是创建时必须已经具备管理员权限，且会在「任务计划程序」里留下一条任务。
///
/// 两者都写清楚「装在哪儿、怎么删」，不搞注册完找不到入口那一套。
/// </summary>
public static class AutostartService
{
    /// <summary>Run 键里的值名。带程序名，用户一眼能认出来。</summary>
    public const string RunValueName = "破烂优化";

    /// <summary>计划任务名。</summary>
    public const string TaskName = @"SysDiag-Autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启动时附加的参数，用来和手动打开区分（不改变界面，只是可追踪）。</summary>
    public const string AutostartArg = "--autostart";

    public enum Mode
    {
        None,
        User,
        Admin,
        Both
    }

    public sealed class Status
    {
        public Mode Mode { get; init; }
        public string ExePath { get; init; } = "";
        public string RunValue { get; init; } = "";
        public bool AdminTaskExists { get; init; }
        public bool PathMatches { get; init; }
        public string Detail { get; init; } = "";

        public bool Enabled => Mode != Mode.None;

        public string ModeText => Mode switch
        {
            Mode.User => "已开启（当前用户）",
            Mode.Admin => "已开启（管理员权限）",
            Mode.Both => "已开启（两种方式同时存在）",
            _ => "未开启"
        };
    }

    public static string ExePath
    {
        get
        {
            var path = Environment.ProcessPath ?? "";

            // 单文件发布时 ProcessPath 就是 exe 本身；用 dotnet run 调试时它是 dotnet.exe，
            // 那种情况下自启动没有意义，界面会提示。
            return path;
        }
    }

    /// <summary>当前 exe 是不是一个可以直接拿去自启动的文件（不是 dotnet.exe / 测试宿主）。</summary>
    public static bool IsPortableExe
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(ExePath);
            return name.Length > 0
                   && !name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                   && !name.Equals("testhost", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static Status Read()
    {
        var runValue = "";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            runValue = key?.GetValue(RunValueName) as string ?? "";
        }
        catch { /* 读不到当作没设置 */ }

        var taskExists = TaskExists(out var taskCommand);

        var mode = (runValue.Length > 0, taskExists) switch
        {
            (true, true) => Mode.Both,
            (true, false) => Mode.User,
            (false, true) => Mode.Admin,
            _ => Mode.None
        };

        var exe = ExePath;
        var matches = runValue.Length == 0
            ? !taskExists || taskCommand.Contains(exe, StringComparison.OrdinalIgnoreCase)
            : runValue.Contains(exe, StringComparison.OrdinalIgnoreCase);

        var detail = mode switch
        {
            Mode.User => $@"HKCU\{RunKey}\{RunValueName} = {runValue}",
            Mode.Admin => $@"计划任务「{TaskName}」：{OneLine(taskCommand)}",
            Mode.Both => $@"HKCU Run：{runValue}　·　计划任务「{TaskName}」同时存在，登录后会启动两次，建议只留一种",
            _ => $@"未写入 HKCU\{RunKey}，也没有计划任务「{TaskName}」"
        };

        return new Status
        {
            Mode = mode,
            ExePath = exe,
            RunValue = runValue,
            AdminTaskExists = taskExists,
            PathMatches = matches,
            Detail = detail
        };
    }

    // ---------------------------------------------------------------- 开启

    /// <summary>当前用户方式：只写 HKCU，不需要管理员。</summary>
    public static (bool Ok, string Message) EnableUser()
    {
        if (!IsPortableExe)
            return (false, "当前是调试宿主进程，自启动没有意义。请用发布出来的 exe 运行本功能。");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);

            if (key == null) return (false, "无法写入 HKCU Run 键。");

            var command = $"\"{ExePath}\" {AutostartArg}";
            key.SetValue(RunValueName, command, RegistryValueKind.String);

            return (true, $"已设置开机自启动（当前用户）。\n\n写入位置：HKCU\\{RunKey}\n值名：{RunValueName}\n命令：{command}\n\n" +
                          "下次登录后会自动启动，以普通权限运行。");
        }
        catch (Exception ex)
        {
            return (false, "写入失败：" + ex.Message);
        }
    }

    /// <summary>计划任务方式：登录后以最高权限启动，不再弹 UAC。</summary>
    public static (bool Ok, string Message) EnableAdmin()
    {
        if (!ElevationService.IsAdmin)
            return (false, "创建「以管理员身份开机启动」的计划任务需要管理员权限。\n" +
                           "请点右上角「以管理员身份重启」后再试，或先改用「当前用户」方式。");

        if (!IsPortableExe)
            return (false, "当前是调试宿主进程，自启动没有意义。请用发布出来的 exe 运行本功能。");

        // 两种方式并存会导致登录后被启动两次，先把 Run 键清掉
        RemoveRunValue();

        var (code, output) = RunTool("schtasks.exe", new[]
        {
            "/create",
            "/tn", TaskName,
            "/tr", $"\"{ExePath}\" {AutostartArg}",
            "/sc", "onlogon",
            "/rl", "highest",
            "/f"
        });

        if (code != 0)
        {
            return (false, $"创建计划任务失败（退出码 {code}）：\n{OneLine(output)}\n\n" +
                           "如果系统策略禁止创建计划任务，请改用「当前用户」方式。");
        }

        return (true, $"已设置开机自启动（管理员权限）。\n\n计划任务名：{TaskName}\n触发：登录时\n" +
                      $"运行：\"{ExePath}\" {AutostartArg}\n权限：最高\n\n" +
                      "登录后会直接以管理员身份启动，不再弹出 UAC。\n" +
                      $"想手动删除：schtasks /delete /tn \"{TaskName}\" /f");
    }

    // ---------------------------------------------------------------- 关闭

    public static (bool Ok, string Message) Disable()
    {
        var removed = new List<string>();

        if (RemoveRunValue()) removed.Add("HKCU Run 键");

        if (TaskExists(out _))
        {
            if (!ElevationService.IsAdmin)
            {
                return (false, "计划任务需要管理员权限才能删除。\n请点右上角「以管理员身份重启」后再试。");
            }

            var (code, output) = RunTool("schtasks.exe", new[] { "/delete", "/tn", TaskName, "/f" });

            if (code == 0) removed.Add($"计划任务「{TaskName}」");
            else return (false, $"删除计划任务失败（退出码 {code}）：\n{OneLine(output)}");
        }

        return removed.Count == 0
            ? (true, "本来就没有设置开机自启动。")
            : (true, "已关闭开机自启动，清除了：" + string.Join("、", removed) + "。");
    }

    private static bool RemoveRunValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return false;

            if (!key.GetValueNames().Any(n => n.Equals(RunValueName, StringComparison.OrdinalIgnoreCase)))
                return false;

            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TaskExists(out string command)
    {
        command = "";

        var (code, output) = RunTool("schtasks.exe", new[] { "/query", "/tn", TaskName, "/fo", "list", "/v" });
        if (code != 0) return false;

        command = output;
        return true;
    }

    private static (int Code, string Output) RunTool(string file, string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args) info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process == null) return (-1, "");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(20_000);

            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string OneLine(string text)
        => string.Join(" ", (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Take(3));
}
