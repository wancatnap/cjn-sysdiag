using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 系统调优：关闭 Game Bar、GPU 硬件加速调度、系统服务优化、CPU 前后台调度。
/// 每一项都先备份原值到 tuning-backup.json，随时可以还原；不改驱动、不碰游戏进程。
/// </summary>
public static class SystemTuningService
{
    private const string GameBarStore = @"System\GameConfigStore";
    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameDvrPolicy = @"SOFTWARE\Microsoft\PolicyManager\default\ApplicationManagement\AllowGameDVR";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string PriorityControl = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>要禁用的服务。SysMain（超级预读）和 EventLog 刻意保留，事件日志一停系统会出问题。</summary>
    /// <summary>
    /// 要禁用的服务。开启「优化系统服务」时这些全部置为禁用，
    /// 其中 SysMain 是跟本项联动的：开启时一并关闭，关闭时按备份重新开启。
    /// </summary>
    private static readonly (string Name, string Label)[] TargetServices =
    {
        ("WSearch",   "Windows Search 搜索索引"),
        ("DiagTrack", "连接用户体验和遥测"),
        ("DoSvc",     "交付优化（后台下载）"),
        ("Spooler",   "Print Spooler 打印后台"),
        ("SysMain",   "SysMain 超级预读")
    };

    /// <summary>唯一保留不动的服务：事件日志一停，本工具自己就读不到日志了。</summary>
    public static IReadOnlyList<string> KeptServices { get; } =
        new[] { "EventLog（Windows 事件日志）" };

    public static IReadOnlyList<CpuTuneProfile> CpuProfiles { get; } = new[]
    {
        new CpuTuneProfile("默认平衡", 2,  "Windows 客户端默认：短时间片、固定、前台无额外加成"),
        new CpuTuneProfile("前台调度", 26, "短时间片 + 可变 + 前台 1 倍加成（温和偏向前台）"),
        new CpuTuneProfile("激进一",   38, "短时间片 + 可变 + 前台 2 倍加成（Windows「程序」预设）"),
        new CpuTuneProfile("激进二",   24, "长时间片 + 可变 + 前台 1 倍加成（吞吐优先）")
    };

    public static bool IsAdmin => LoggingService.IsAdmin();

    private static string BackupFile => Path.Combine(AppPaths.DataDir, "tuning-backup.json");

    // ---------------------------------------------------------------- 状态读取

    public static List<TuneEntry> GetAll() => new()
    {
        ReadGameBar(),
        ReadHwScheduling(),
        ReadServices(),
        ReadCpuScheduling(),
        ReadMouseAcceleration()
    };

    private static TuneEntry ReadMouseAcceleration()
    {
        var (disabled, speed, t1, t2) = MouseTweakService.Read();

        return new TuneEntry
        {
            Kind = TuneKind.MouseAcceleration,
            Title = "鼠标精准输入优化",
            Desc = "移除系统对鼠标轨迹的额外干预（关闭「提高指针精确度」），让移动更贴合鼠标原始手感。",
            Detail = $"MouseSpeed = {speed}　MouseThreshold1 = {t1}　MouseThreshold2 = {t2}",
            Warning = "关掉后鼠标在桌面上会感觉「变快变直」，桌面日常使用可能不习惯；FPS 游戏通常反而更准。想回桌面手感就关掉本项。",
            Applied = disabled,
            NeedsRestart = false,
            NeedsAdmin = false
        };
    }

    public static (bool Ok, string Message) SetMouseAcceleration(bool disable)
    {
        var backup = LoadBackup();

        if (backup.Mouse == null)
        {
            var (_, speed, t1, t2) = MouseTweakService.Read();
            backup.Mouse = new Dictionary<string, string>
            {
                ["MouseSpeed"] = speed,
                ["MouseThreshold1"] = t1,
                ["MouseThreshold2"] = t2
            };
        }

        SaveBackup(backup);

        return disable ? MouseTweakService.DisableAcceleration() : MouseTweakService.Restore();
    }

    private static TuneEntry ReadGameBar()
    {
        var dvr = ReadInt(Registry.CurrentUser, GameBarStore, "GameDVR_Enabled");
        var capture = ReadInt(Registry.CurrentUser, GameDvrKey, "AppCaptureEnabled");
        var autoMode = ReadInt(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled");

        // 三项都没被改过时视为「未优化」；都为 0 才算关掉
        var applied = dvr == 0 && capture == 0;

        var parts = new List<string>();
        parts.Add(dvr.HasValue ? $"GameDVR_Enabled = {dvr}" : "GameDVR_Enabled 未设置");
        parts.Add(capture.HasValue ? $"AppCaptureEnabled = {capture}" : "AppCaptureEnabled 未设置");
        parts.Add(autoMode.HasValue ? $"AutoGameModeEnabled = {autoMode}" : "AutoGameModeEnabled 未设置");

        return new TuneEntry
        {
            Kind = TuneKind.GameBar,
            Title = "关闭 Game Bar",
            Desc = "关闭系统后台的游戏辅助组件（录屏、截图、好友浮层），减少后台常驻与资源占用。",
            Detail = string.Join(" · ", parts),
            Warning = "关掉后 Win+G 快捷键失效，Xbox 录屏与后台录制不可用；想录屏要在关闭前先录。",
            Applied = applied,
            NeedsRestart = false,
            NeedsAdmin = false
        };
    }

    private static TuneEntry ReadHwScheduling()
    {
        var mode = ReadInt(Registry.LocalMachine, GraphicsDrivers, "HwSchMode");
        var applied = mode == 2;

        return new TuneEntry
        {
            Kind = TuneKind.HwScheduling,
            Title = "GPU 硬件加速调度",
            Desc = "打开 HwSchMode（硬件 GPU 调度），把显存管理与任务调度交给显卡驱动，理论上可降低渲染队列等待。",
            Detail = mode.HasValue ? $"HwSchMode = {mode}（1 = 关闭，2 = 开启）" : "HwSchMode 未设置（系统默认关闭）",
            Warning = "实际效果因配置而异。中低端配置可能增加帧时间波动导致抽帧卡顿，个别安全软件可能与此功能冲突。开启后建议进游戏实测。",
            Applied = applied,
            NeedsRestart = true,
            NeedsAdmin = true
        };
    }

    private static TuneEntry ReadServices()
    {
        var rows = new List<string>();
        var allDisabled = true;
        var anyFound = false;

        foreach (var (name, label) in TargetServices)
        {
            var start = ReadInt(Registry.LocalMachine, ServicesKey + "\\" + name, "Start");
            if (start == null)
            {
                rows.Add($"{label}：未安装");
                continue;
            }

            anyFound = true;
            if (start != 4) allDisabled = false;
            rows.Add($"{label}：{StartTypeText(start.Value)}");
        }

        return new TuneEntry
        {
            Kind = TuneKind.Services,
            Title = "优化系统服务",
            Desc = "禁用 Windows 搜索索引、诊断遥测、后台下载、打印服务与 SysMain 超级预读，减少后台常驻任务。",
            Detail = string.Join("　", rows),
            Warning = "可能影响系统搜索、更新下载与打印。会一并关闭 SysMain（超级预读），常用程序启动可能略慢；关闭本项时会按原值重新开启 SysMain，并恢复其余服务。Windows 事件日志保留不动。",
            Applied = anyFound && allDisabled,
            NeedsRestart = false,
            NeedsAdmin = true
        };
    }

    private static TuneEntry ReadCpuScheduling()
    {
        var value = ReadInt(Registry.LocalMachine, PriorityControl, "Win32PrioritySeparation");
        var current = value ?? 2;
        var profile = CpuProfiles.FirstOrDefault(p => p.Value == current);

        return new TuneEntry
        {
            Kind = TuneKind.CpuScheduling,
            Title = "CPU 前后台调度",
            Desc = "调整 Windows 前台/后台 CPU 时间片倾向。它和「优化鼠标悬停时间」不是一类功能，不保证提升 FPS。",
            Detail = profile != null
                ? $"当前档位：{profile.Name}（{current}） · {profile.Summary}"
                : $"当前值 {current}（非预设档位，可能是系统或第三方工具设置）",
            Warning = "档位越激进，前台程序分到的时间片越多，后台（含录制、下载）越容易卡。建议一次只改一项，改完重启再进游戏实测。",
            Applied = current != 2,
            NeedsRestart = true,
            NeedsAdmin = true
        };
    }

    // ---------------------------------------------------------------- Game Bar

    public static (bool Ok, string Message) SetGameBar(bool disable)
    {
        var backup = LoadBackup();
        var target = disable ? 0 : 1;

        try
        {
            backup.GameBar ??= SnapshotGameBar();

            WriteInt(Registry.CurrentUser, GameBarStore, "GameDVR_Enabled", target);
            WriteInt(Registry.CurrentUser, GameDvrKey, "AppCaptureEnabled", target);
            WriteInt(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled", target);

            // 策略键需要管理员；没有权限时只影响「设置」页面显示，HKCU 的开关已生效
            var policyNote = "";
            try
            {
                WriteInt(Registry.LocalMachine, GameDvrPolicy, "value", target);
                SaveBackup(backup);
            }
            catch (UnauthorizedAccessException)
            {
                policyNote = "（策略键需要管理员，已跳过；用户级开关已生效）";
            }
            catch (Exception)
            {
                policyNote = "（策略键写入失败，已跳过）";
            }

            return (true, disable
                ? "已关闭 Game Bar 与后台录制。重新登录或重启后彻底生效。" + policyNote
                : "已重新开启 Game Bar 与后台录制。重新登录或重启后生效。" + policyNote);
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "写注册表被拒绝，请以管理员身份重启后再试。");
        }
        catch (Exception ex)
        {
            return (false, "操作失败：" + ex.Message);
        }
    }

    private static Dictionary<string, int?> SnapshotGameBar() => new()
    {
        ["GameDVR_Enabled"] = ReadInt(Registry.CurrentUser, GameBarStore, "GameDVR_Enabled"),
        ["AppCaptureEnabled"] = ReadInt(Registry.CurrentUser, GameDvrKey, "AppCaptureEnabled"),
        ["AutoGameModeEnabled"] = ReadInt(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled")
    };

    // ------------------------------------------------------------ GPU 调度

    public static (bool Ok, string Message) SetHwScheduling(bool enable)
    {
        var backup = LoadBackup();

        try
        {
            if (backup.HwSchMode == null && !backup.HwSchModeMissing)
            {
                var current = ReadInt(Registry.LocalMachine, GraphicsDrivers, "HwSchMode");
                if (current.HasValue) backup.HwSchMode = current;
                else backup.HwSchModeMissing = true;
            }

            WriteInt(Registry.LocalMachine, GraphicsDrivers, "HwSchMode", enable ? 2 : 1);
            SaveBackup(backup);

            return (true, enable
                ? "已开启 GPU 硬件加速调度（HwSchMode = 2）。必须重启电脑才生效。"
                : "已关闭 GPU 硬件加速调度（HwSchMode = 1）。必须重启电脑才生效。");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限：请点右下角「以管理员身份重启」后再试。");
        }
        catch (Exception ex)
        {
            return (false, "操作失败：" + ex.Message);
        }
    }

    // -------------------------------------------------------------- 系统服务

    /// <summary>本项会动到的服务名，供界面与自检核对。</summary>
    public static IReadOnlyList<string> TargetedServices { get; } =
        TargetServices.Select(t => t.Name).ToList();

    /// <summary>
    /// 还原时该服务应回到的启动类型：
    /// 原本就是禁用（4）或引导型（0）的回到「自动」（2），其余按原值。
    /// 这样「开启优化 = 关掉 SysMain，关闭优化 = SysMain 重新开起来」两向都成立。
    /// </summary>
    public static int ResolveRestoreStart(int original) => original is 4 or 0 ? 2 : original;

    public static (bool Ok, string Message) SetServices(bool disable)
    {
        var backup = LoadBackup();
        var done = new List<string>();
        var failed = new List<string>();

        foreach (var (name, label) in TargetServices)
        {
            var current = ReadInt(Registry.LocalMachine, ServicesKey + "\\" + name, "Start");
            if (current == null)
            {
                failed.Add($"{label}（未安装）");
                continue;
            }

            backup.Services ??= new Dictionary<string, int>();
            if (!backup.Services.ContainsKey(name))
                backup.Services[name] = current.Value;

            var target = disable ? 4 : ResolveRestoreStart(backup.Services[name]);

            try
            {
                WriteInt(Registry.LocalMachine, ServicesKey + "\\" + name, "Start", target);
                done.Add(label);

                if (disable) TryStopService(name);
                else TryStartService(name);
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "需要管理员权限：请点右下角「以管理员身份重启」后再试。");
            }
            catch (Exception ex)
            {
                failed.Add($"{label}（{ex.Message}）");
            }
        }

        SaveBackup(backup);

        var msg = disable
            ? $"已禁用 {done.Count} 个服务：{string.Join("、", done)}（含 SysMain 超级预读）。正在运行的部分需要重启电脑才彻底停止。"
            : $"已恢复 {done.Count} 个服务的启动类型：{string.Join("、", done)}。SysMain 已重新开启，重启后生效。";

        if (failed.Count > 0)
            msg += "\n未处理：" + string.Join("、", failed);

        return (done.Count > 0, msg);
    }

    // ------------------------------------------------------------ CPU 调度

    public static (bool Ok, string Message) SetCpuProfile(int value)
    {
        var profile = CpuProfiles.FirstOrDefault(p => p.Value == value);
        if (profile == null && value != 2)
            return (false, $"未知档位 {value}。");

        var backup = LoadBackup();

        try
        {
            if (backup.Win32PrioritySeparation == null && !backup.Win32PriorityMissing)
            {
                var current = ReadInt(Registry.LocalMachine, PriorityControl, "Win32PrioritySeparation");
                if (current.HasValue) backup.Win32PrioritySeparation = current;
                else backup.Win32PriorityMissing = true;
            }

            WriteInt(Registry.LocalMachine, PriorityControl, "Win32PrioritySeparation", value);
            SaveBackup(backup);

            var name = profile?.Name ?? "自定义";
            return (true, $"已把 CPU 前后台调度设为「{name}」（{value}）。重启后生效。");
        }
        catch (UnauthorizedAccessException)
        {
            return (false, "需要管理员权限：请点右下角「以管理员身份重启」后再试。");
        }
        catch (Exception ex)
        {
            return (false, "操作失败：" + ex.Message);
        }
    }

    /// <summary>把本页所有改动还原成第一次修改前的状态。</summary>
    public static (bool Ok, string Message) RestoreAll()
    {
        var backup = LoadBackup();
        if (backup.IsEmpty)
            return (false, "没有找到备份记录，说明这台机器还没有通过本工具改过设置。");

        var notes = new List<string>();

        if (backup.GameBar != null)
        {
            try
            {
                RestoreValue(Registry.CurrentUser, GameBarStore, "GameDVR_Enabled", backup.GameBar.GetValueOrDefault("GameDVR_Enabled"));
                RestoreValue(Registry.CurrentUser, GameDvrKey, "AppCaptureEnabled", backup.GameBar.GetValueOrDefault("AppCaptureEnabled"));
                RestoreValue(Registry.CurrentUser, GameBarKey, "AutoGameModeEnabled", backup.GameBar.GetValueOrDefault("AutoGameModeEnabled"));
                notes.Add("Game Bar");
            }
            catch (Exception ex) { notes.Add("Game Bar 失败：" + ex.Message); }
        }

        if (backup.HwSchMode != null || backup.HwSchModeMissing)
        {
            try
            {
                if (backup.HwSchModeMissing)
                    DeleteValue(Registry.LocalMachine, GraphicsDrivers, "HwSchMode");
                else
                    WriteInt(Registry.LocalMachine, GraphicsDrivers, "HwSchMode", backup.HwSchMode!.Value);
                notes.Add("GPU 调度");
            }
            catch (Exception ex) { notes.Add("GPU 调度失败：" + ex.Message); }
        }

        if (backup.Services != null)
        {
            var ok = 0;
            foreach (var kv in backup.Services)
            {
                try
                {
                    WriteInt(Registry.LocalMachine, ServicesKey + "\\" + kv.Key, "Start", kv.Value);
                    ok++;
                }
                catch { }
            }
            notes.Add($"服务 {ok}/{backup.Services.Count}");
        }

        if (backup.Win32PrioritySeparation != null || backup.Win32PriorityMissing)
        {
            try
            {
                if (backup.Win32PriorityMissing)
                    DeleteValue(Registry.LocalMachine, PriorityControl, "Win32PrioritySeparation");
                else
                    WriteInt(Registry.LocalMachine, PriorityControl, "Win32PrioritySeparation",
                        backup.Win32PrioritySeparation!.Value);
                notes.Add("CPU 调度");
            }
            catch (Exception ex) { notes.Add("CPU 调度失败：" + ex.Message); }
        }

        if (backup.Mouse != null)
        {
            var (ok, _) = MouseTweakService.RestoreExact(
                backup.Mouse.GetValueOrDefault("MouseSpeed") ?? "",
                backup.Mouse.GetValueOrDefault("MouseThreshold1") ?? "",
                backup.Mouse.GetValueOrDefault("MouseThreshold2") ?? "");
            notes.Add(ok ? "鼠标设置" : "鼠标设置失败");
        }

        try { File.Delete(BackupFile); } catch { }

        return (true, "已还原：" + string.Join("、", notes) + "。需要重启的项重启后生效。");
    }

    // ------------------------------------------------------------ 注册表工具

    private static int? ReadInt(RegistryKey root, string path, string name)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            var raw = key?.GetValue(name);
            if (raw == null) return null;
            return raw switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var v) => v,
                _ => null
            };
        }
        catch { return null; }
    }

    private static void WriteInt(RegistryKey root, string path, string name, int value)
    {
        using var key = root.CreateSubKey(path, writable: true)
                        ?? throw new IOException("打不开注册表项：" + path);
        key.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void DeleteValue(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void RestoreValue(RegistryKey root, string path, string name, int? value)
    {
        if (value.HasValue) WriteInt(root, path, name, value.Value);
        else DeleteValue(root, path, name);
    }

    private static string StartTypeText(int start) => start switch
    {
        0 => "引导启动",
        1 => "系统启动",
        2 => "自动",
        3 => "手动",
        4 => "已禁用",
        _ => start.ToString()
    };

    // ------------------------------------------------------------ 停服务（P/Invoke）

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scm, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool StartService(IntPtr service, uint numArgs, IntPtr args);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    private static void TryStopService(string name)
    {
        const uint ScManagerConnect = 0x0001;
        const uint ServiceStop = 0x0020;
        const uint ServiceQueryStatus = 0x0004;
        const uint ServiceControlStop = 1;
        var scm = IntPtr.Zero;
        var svc = IntPtr.Zero;

        try
        {
            scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) return;

            svc = OpenService(scm, name, ServiceStop | ServiceQueryStatus);
            if (svc == IntPtr.Zero) return;

            var status = new ServiceStatus();
            ControlService(svc, ServiceControlStop, ref status);
        }
        catch
        {
            // 停不掉不影响禁用：重启后自然不再启动
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    /// <summary>
    /// 还原时把服务拉起来。必须在注册表 Start 改回可用值之后再调用，
    /// 否则服务仍处于「已禁用」状态，StartService 会直接失败。
    /// </summary>
    private static void TryStartService(string name)
    {
        const uint ScManagerConnect = 0x0001;
        const uint ServiceStart = 0x0010;
        const uint ServiceQueryStatus = 0x0004;
        var scm = IntPtr.Zero;
        var svc = IntPtr.Zero;

        try
        {
            scm = OpenSCManager(null, null, ScManagerConnect);
            if (scm == IntPtr.Zero) return;

            svc = OpenService(scm, name, ServiceStart | ServiceQueryStatus);
            if (svc == IntPtr.Zero) return;

            StartService(svc, 0, IntPtr.Zero);
        }
        catch
        {
            // 起不来也不影响注册表恢复：下次开机按新的启动类型走
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    // ------------------------------------------------------------ 备份

    private sealed class TuningBackup
    {
        public Dictionary<string, int?>? GameBar { get; set; }
        public int? HwSchMode { get; set; }
        public bool HwSchModeMissing { get; set; }
        public Dictionary<string, int>? Services { get; set; }
        public int? Win32PrioritySeparation { get; set; }
        public bool Win32PriorityMissing { get; set; }
        public Dictionary<string, string>? Mouse { get; set; }

        public bool IsEmpty => GameBar == null && HwSchMode == null && !HwSchModeMissing
                               && Services == null && Win32PrioritySeparation == null
                               && !Win32PriorityMissing && Mouse == null;
    }

    private static TuningBackup LoadBackup()
    {
        try
        {
            if (File.Exists(BackupFile))
                return JsonSerializer.Deserialize<TuningBackup>(File.ReadAllText(BackupFile)) ?? new();
        }
        catch { }
        return new TuningBackup();
    }

    private static void SaveBackup(TuningBackup backup)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(BackupFile,
                JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
