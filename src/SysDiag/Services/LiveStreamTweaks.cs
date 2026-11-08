using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace SysDiag.Services;

/// <summary>
/// 直播软件的系统层配套设置。
///
/// 这些条目来自 OBS 官方论坛的编码指南与社区排查清单里反复出现的那几条，
/// 但**都能在本机静态表达、都能还原**，所以适合放进工具里：
/// <list type="number">
/// <item><b>GPU 图形首选项</b>：Windows 会把"后台"程序的 GPU 调度降级，直播软件的画布合成
/// 可能被丢到核显上，编码器吃不到独显。给它的 exe 显式指定"高性能"能避开这一层。</item>
/// <item><b>MMCSS 捕获任务</b>：Windows 的多媒体类调度器里有一组 <c>Capture</c> 任务配置，
/// 直播软件注册捕获线程时会用到。默认是 Medium + Normal IO，改成 High 能让采集线程
/// 在游戏满载时更稳地拿到时间片（代价是更用力地抢 CPU）。</item>
/// <item><b>HAGS 状态</b>：硬件加速 GPU 调度与部分采集/编码组合存在冲突（表现为预览卡、
/// 编码延迟抖动）。这一项只**读状态并提示**，不替用户改——它同时影响游戏帧率，
/// 是不是该关取决于具体的卡和驱动版本，只能实测。</item>
/// <item><b>叠加层冲突</b>：Discord / GeForce / RTSS / Steam 的覆盖层会与捕获钩子打架，
/// 是"游戏里明明流畅、直播画面却卡"的常见原因。这一项也是只读提示。</item>
/// </list>
///
/// 明确不做：改 OBS 的配置文件（那不是本软件的东西）、改编码器参数（码率/预设取决于
/// 上行带宽和平台，猜错了更糟）、强制关闭 HAGS（有争议，见上）。
/// </summary>
public static class LiveStreamTweaks
{
    private const string GpuPrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string MmcssCapture = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Capture";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string GameBar = @"Software\Microsoft\GameBar";

    /// <summary>会与捕获钩子冲突的覆盖层进程（进程名，不含 .exe）。</summary>
    private static readonly (string Process, string Name)[] Overlays =
    {
        ("Discord", "Discord 覆盖层"),
        ("NVIDIA Share", "GeForce 覆盖层"),
        ("NVIDIA Overlay", "GeForce 覆盖层"),
        ("RTSS", "RTSS 帧率显示"),
        ("MSIAfterburner", "MSI Afterburner"),
        ("GameBar", "Xbox Game Bar"),
        ("obs64", "OBS 自身（若同时开两个捕获会抢钩子）")
    };

    // ---------------------------------------------------------------- 读状态

    /// <summary>HAGS：2 = 开，1 = 关。读不到返回 null。</summary>
    public static bool? HagsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(GraphicsDrivers);
            return key?.GetValue("HwSchMode") is int v ? v == 2 : null;
        }
        catch { return null; }
    }

    /// <summary>Windows 游戏模式是否开启。读不到返回 null。</summary>
    public static bool? GameModeEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GameBar);
            return key?.GetValue("AutoGameModeEnabled") is int v ? v != 0 : null;
        }
        catch { return null; }
    }

    /// <summary>当前在跑的覆盖层。只读，用来提示。</summary>
    public static List<string> RunningOverlays()
    {
        var hits = new List<string>();

        foreach (var (proc, name) in Overlays)
        {
            try
            {
                if (Process.GetProcessesByName(proc).Length > 0 && !hits.Contains(name))
                    hits.Add(name);
            }
            catch { }
        }

        return hits;
    }

    /// <summary>这个 exe 是否已经设了「高性能」GPU 首选项。</summary>
    public static bool HasHighPerformanceGpu(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPrefKey);
            return key?.GetValue(exePath) is string v
                   && v.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------------------------------------------------------- 写入

    /// <summary>
    /// 给指定 exe 设「高性能」GPU 首选项。
    /// 这是 HKCU 项，不需要管理员，也不会影响其他程序——只对这一个 exe 生效。
    /// </summary>
    public static (bool Ok, string Message) ApplyGpuPreference(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return (false, "找不到直播软件的 exe 路径。");

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(GpuPrefKey, true);
            if (key == null) return (false, "打不开 UserGpuPreferences 键。");

            var existing = key.GetValue(exePath) as string ?? "";
            if (existing.Contains("GpuPreference=2", StringComparison.OrdinalIgnoreCase))
                return (true, "已经是「高性能」GPU 首选项。");

            // 保留该值里原有的其他字段（SwapEffectUpgradeEnable 之类），只补 GPU 首选项
            var merged = existing.Length > 0
                ? (existing.TrimEnd(';') + ";GpuPreference=2;")
                : "GpuPreference=2;";

            key.SetValue(exePath, merged, RegistryValueKind.String);
            return (true, "已设为「高性能」GPU（只对这个 exe 生效）。");
        }
        catch (Exception ex)
        {
            return (false, "写入失败：" + ex.Message);
        }
    }

    /// <summary>撤掉 GPU 首选项里的高性能标记，保留该值的其他字段。</summary>
    public static (bool Ok, string Message) RestoreGpuPreference(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(GpuPrefKey, true);
            if (key?.GetValue(exePath) is not string existing) return (true, "本来就没有设置。");

            var parts = existing
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !p.StartsWith("GpuPreference=", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (parts.Count == 0)
                key.DeleteValue(exePath, false);
            else
                key.SetValue(exePath, string.Join(";", parts) + ";", RegistryValueKind.String);

            return (true, "已撤掉 GPU 首选项。");
        }
        catch (Exception ex)
        {
            return (false, "还原失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 把 MMCSS 的捕获任务调到 High。需要管理员。
    ///
    /// 只改这三个值，不动 SystemProfile 里的其他任务：
    /// Priority 6（默认 8，数字越小优先级越高）、Scheduling Category = High、
    /// SFIO Priority = High。改前把原值记进 backup，Restore 用它还原。
    /// </summary>
    public static (bool Ok, string Message) ApplyCaptureProfile()
    {
        var backup = new Dictionary<string, string>();

        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(MmcssCapture, true);
            if (key == null) return (false, "打不开捕获任务键（需要管理员）。");

            foreach (var name in new[] { "Priority", "Scheduling Category", "SFIO Priority" })
            {
                if (key.GetValue(name) != null)
                    backup[name] = key.GetValue(name)?.ToString() ?? "";
            }

            SaveBackup(backup);

            key.SetValue("Priority", 6, RegistryValueKind.DWord);
            key.SetValue("Scheduling Category", "High", RegistryValueKind.String);
            key.SetValue("SFIO Priority", "High", RegistryValueKind.String);

            return (true, "捕获任务已设为 High（采集线程更稳，代价是更用力抢 CPU）。");
        }
        catch (Exception ex)
        {
            return (false, "写入失败（需要管理员）：" + ex.Message);
        }
    }

    /// <summary>用备份还原捕获任务。没有备份就写回 Windows 默认值。</summary>
    public static (bool Ok, string Message) RestoreCaptureProfile()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(MmcssCapture, true);
            if (key == null) return (false, "打不开捕获任务键（需要管理员）。");

            var backup = LoadBackup();

            if (backup.Count == 0)
            {
                // 没有备份说明本软件没改过，写回 Windows 默认值
                key.SetValue("Priority", 8, RegistryValueKind.DWord);
                key.SetValue("Scheduling Category", "Medium", RegistryValueKind.String);
                key.SetValue("SFIO Priority", "Normal", RegistryValueKind.String);
                return (true, "已写回 Windows 默认（Priority 8 / Medium / Normal）。");
            }

            foreach (var (name, value) in backup)
            {
                if (name == "Priority" && int.TryParse(value, out var p))
                    key.SetValue(name, p, RegistryValueKind.DWord);
                else
                    key.SetValue(name, value, RegistryValueKind.String);
            }

            DeleteBackup();
            return (true, "已按改前的原值还原。");
        }
        catch (Exception ex)
        {
            return (false, "还原失败（需要管理员）：" + ex.Message);
        }
    }

    // ---------------------------------------------------------------- 备份

    private static string BackupPath =>
        Path.Combine(AppPaths.OutputDir, "live-capture-backup.json");

    private static void SaveBackup(Dictionary<string, string> data)
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(BackupPath,
                System.Text.Json.JsonSerializer.Serialize(data), new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    private static Dictionary<string, string> LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return new Dictionary<string, string>();
            return System.Text.Json.JsonSerializer
                       .Deserialize<Dictionary<string, string>>(File.ReadAllText(BackupPath))
                   ?? new Dictionary<string, string>();
        }
        catch { return new Dictionary<string, string>(); }
    }

    private static void DeleteBackup()
    {
        try { if (File.Exists(BackupPath)) File.Delete(BackupPath); } catch { }
    }

    // ---------------------------------------------------------------- 说明

    /// <summary>
    /// 给界面用的一句话自检结论。全部只读，不写任何设置。
    /// </summary>
    public static List<(string Name, string Value, string Hint)> Diagnostics()
    {
        var list = new List<(string, string, string)>();

        var hags = HagsEnabled();
        list.Add(("硬件加速 GPU 调度", hags switch
        {
            true => "已开启",
            false => "已关闭",
            _ => "读不到"
        }, "与部分采集/编码组合有冲突，表现为预览卡顿。是否该关取决于显卡与驱动版本，只能自己实测对比。"));

        var gameMode = GameModeEnabled();
        list.Add(("Windows 游戏模式", gameMode switch
        {
            true => "已开启",
            false => "已关闭",
            _ => "读不到"
        }, "开启后系统会把游戏与直播软件的调度优先级一起抬高，官方指南建议开启。"));

        var overlays = RunningOverlays();
        list.Add(("覆盖层进程", overlays.Count == 0 ? "没有" : string.Join("、", overlays),
            overlays.Count == 0
                ? "没有会与捕获钩子冲突的覆盖层。"
                : "多个覆盖层同时挂钩会造成「游戏流畅但直播卡」。直播时关掉它们。"));

        return list;
    }
}
