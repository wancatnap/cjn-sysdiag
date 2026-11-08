using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// CPU 停核（core parking）与 C-State 调参。
///
/// 全部通过电源方案（power scheme）里**隐藏的处理器设置**实现，不写 bcdedit、不碰驱动：
/// - <c>CPMINCORES</c> / <c>CPMAXCORES</c>：核心停放的最小 / 最大核心数（百分比）。
///   两者都设为 100 等于「所有核心常开」，即关闭停核。
/// - <c>IDLEDISABLE</c>：处理器闲置禁用。设为 1 会让核心不再进入深度空闲（C-State），
///   是这套设置里最接近「关 C-State」的官方开关；<c>IDLESTATEMAX</c> 可限制最深空闲级别。
///
/// GUID 不是凭记忆写的：本机 <c>powercfg /query SCHEME_CURRENT 54533251-...</c> 实查过。
/// （CPMAXCORES 常见误写是 ea062031-0e34-4ff1-966b-1e8c1f0a0aa1，实为 …-9b6d-eb1059334028。）
///
/// 三条硬约束：
/// 1. **只改当前生效方案**，不动「平衡/高性能」模板本身，切方案即恢复。
/// 2. **改前把 AC/DC 原值一起记下来**，还原时按记录写回；恢复失败也不猜默认值。
/// 3. **需要管理员**（写电源方案要 HKLM 权限）。普通权限下只读展示，不假装能改。
/// </summary>
public static class CoreParkingService
{
    private const string BackupName = "core-parking-backup.json";

    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

    private static readonly Guid CpMinCores = new("0cc5b647-c1df-4637-891a-dec35c318583");
    private static readonly Guid CpMaxCores = new("ea062031-0e34-4ff1-9b6d-eb1059334028");
    private static readonly Guid IdleDisable = new("5d76a2ca-e8c0-402f-a133-2158492d58ad");
    private static readonly Guid IdleStateMax = new("9943e905-9a30-4ec1-9b99-44dd3b76f7a2");
    private static readonly Guid PerfBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>自检用：暴露实查过的 GUID，防止有人凭记忆改回错误值。</summary>
    public static Guid MaxCoresGuid => CpMaxCores;

    /// <summary>自检用：暴露核心停放最小核心数 GUID。</summary>
    public static Guid MinCoresGuid => CpMinCores;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting,
        out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting,
        out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting,
        uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting,
        uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid scheme);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    // ---------------------------------------------------------------- 读取

    /// <summary>一项处理器电源设置的可读状态。</summary>
    public sealed class Reading
    {
        public string Id { get; init; } = "";
        public string Title { get; init; } = "";
        public string Desc { get; init; } = "";
        public string Effect { get; init; } = "";

        public uint? Ac { get; init; }
        public uint? Dc { get; init; }
        public bool Readable => Ac.HasValue;

        /// <summary>已处于「关闭停核 / 禁用空闲」的目标状态。</summary>
        public bool Applied { get; init; }

        public string ValueText => !Readable
            ? "读不到（需要管理员权限读取当前电源方案）"
            : $"交流 {Ac}{(Dc.HasValue ? $" / 电池 {Dc}" : "")}";
    }

    /// <summary>读当前生效方案的停核与 C-State 状态。只读，普通权限也能调到一部分。</summary>
    public static List<Reading> Read()
    {
        var list = new List<Reading>();

        var scheme = PowerPlanService.GetActiveGuid();
        if (scheme == null)
        {
            list.Add(new Reading { Id = "cp-unknown", Title = "当前电源方案", Desc = "读不到当前生效的电源方案。" });
            return list;
        }

        var g = scheme.Value;

        var minCores = ReadBoth(g, CpMinCores);
        var maxCores = ReadBoth(g, CpMaxCores);
        var idleDisable = ReadBoth(g, IdleDisable);
        var idleMax = ReadBoth(g, IdleStateMax);
        var boost = ReadBoth(g, PerfBoostMode);

        list.Add(new Reading
        {
            Id = "cp-parking",
            Title = "核心停放（Core Parking）",
            Desc = $"核心停放最小核心数：{Fmt(minCores.Ac)}%　最大核心数：{Fmt(maxCores.Ac)}%" +
                   (minCores.Dc.HasValue ? $"\n电池下：最小 {Fmt(minCores.Dc)}% / 最大 {Fmt(maxCores.Dc)}%" : ""),
            Effect = "两个值都是 100 时，所有核心保持唤醒（停核关闭）。\n" +
                     "代价：空闲功耗与温度上升，笔记本续航变短。\n" +
                     "注意：Windows 的「核心停放」是按核心组动态停用空闲核心；现代 Intel 的硬件自主调频（HWP）" +
                     "与 AMD 的 CPPC 会在更底层接管，注册表/电源项改了不一定改变实际行为——改完请用「延迟实测」复测。",
            Ac = minCores.Ac,
            Dc = minCores.Dc,
            // 交流与电池都要到 100 才算「已关闭停核」。只看交流会把「插电不停核、电池仍停核」误报成已应用。
            Applied = minCores.Ac == 100 && minCores.Dc == 100 && maxCores.Ac == 100 && maxCores.Dc == 100
        });

        list.Add(new Reading
        {
            Id = "cp-idle",
            Title = "处理器闲置（C-State）",
            Desc = $"处理器闲置禁用：{Fmt(idleDisable.Ac)}（0=允许进入空闲，1=禁止）" +
                   (idleMax.Ac.HasValue ? $"\n最大空闲状态：{idleMax.Ac}" : ""),
            Effect = "置 1 后核心不再进入深度空闲（C1 以下），唤醒延迟理论上更低。\n" +
                     "代价很直接：空闲功耗、发热、风扇转速都会上升，笔记本尤其明显。\n" +
                     "这是「关 C-State」在电源方案里唯一稳定的官方开关；再深的 C-State 由 BIOS/固件控制，软件改不了。",
            Ac = idleDisable.Ac,
            Dc = idleDisable.Dc,
            // 同理：只禁用交流而电池仍允许空闲，不算「已应用」，否则用户会以为插电和电池一致。
            Applied = idleDisable.Ac == 1 && idleDisable.Dc == 1
        });

        list.Add(new Reading
        {
            Id = "cp-boost",
            Title = "性能提升模式（Boost）",
            Desc = $"当前：{BoostText(boost.Ac)}",
            Effect = "Enabled/Aggressive 让单核更容易冲高频；对帧率稳定性有帮助，但功耗与温度更高。\n" +
                     "这一项只读展示：改它需要按 CPU 代际判断，静态规则容易给错值，本软件不代劳。",
            Ac = boost.Ac,
            Dc = boost.Dc
        });

        return list;
    }

    private static (uint? Ac, uint? Dc) ReadBoth(Guid scheme, Guid setting)
    {
        var sub = SubProcessor;
        var set = setting;

        uint? ac = PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out var a) == 0 ? a : null;
        uint? dc = PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out var d) == 0 ? d : null;

        return (ac, dc);
    }

    private static string Fmt(uint? v) => v.HasValue ? v.Value.ToString() : "读不到";

    private static string BoostText(uint? v) => v switch
    {
        0 => "Disabled（关闭）",
        1 => "Enabled（开启）",
        2 => "Aggressive（激进）",
        3 => "Efficient Enabled",
        4 => "Efficient Aggressive",
        null => "读不到",
        _ => $"未知值 {v}"
    };

    /// <summary>
    /// 双 CCD 的 X3D 处理器（7900X3D / 7950X3D / 9900X3D / 9950X3D）。
    ///
    /// 这类 CPU 上「关闭停核」是**反效果**：AMD 的 3D V-Cache Performance Optimizer
    /// 靠核心停放把游戏压在带 3D 缓存的那个 CCD 上；把全部核心唤醒后，游戏线程可能被调度到
    /// 无缓存 CCD，帧率反而更差。单 CCD 的 7800X3D / 9800X3D 不受影响。
    /// 结论来自 RigPolice 的 CPU Parking Disabler（MIT）实测与 AMD 官方说明。
    /// </summary>
    public static bool IsDualCcdX3D() => MatchesDualCcdX3D(ProcessorName());

    /// <summary>自检用：把型号匹配单独抽出来，避免为了测试去读真机。</summary>
    public static bool MatchesDualCcdX3D(string processorName)
    {
        if (string.IsNullOrWhiteSpace(processorName)) return false;

        var name = processorName.ToUpperInvariant();

        // 只有 12/16 核的 X3D 才是双 CCD；7800X3D / 9800X3D 是单 CCD，不能用这条拦
        foreach (var model in new[] { "7900X3D", "7950X3D", "9900X3D", "9950X3D" })
        {
            if (name.Contains(model, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>读处理器型号。读不到返回空串（不猜）。</summary>
    public static string ProcessorName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("ProcessorNameString") as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>当前是否在用电池。用来在界面上提醒「这些改动会明显影响续航」。</summary>
    public static bool OnBattery()
    {
        try
        {
            return GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------- 写入

    private sealed class BackupItem
    {
        public string SettingId { get; set; } = "";
        public uint Ac { get; set; }
        public uint Dc { get; set; }
        public bool HadAc { get; set; }
        public bool HadDc { get; set; }
    }

    private sealed class BackupFile
    {
        public string Scheme { get; set; } = "";
        public DateTime SavedAt { get; set; }
        public List<BackupItem> Items { get; set; } = new();
    }

    private static string BackupPath => Path.Combine(AppPaths.DataDir, BackupName);

    /// <summary>关闭停核：CPMINCORES / CPMAXCORES 都设为 100。</summary>
    public static (bool Ok, string Message) DisableParking()
    {
        if (!ElevationService.IsAdmin)
            return (false, "改电源方案需要管理员权限。点「以管理员身份重启」后再试。");

        // 双 CCD X3D 直接拒绝：不是「建议别做」，是做了会让游戏更差
        if (IsDualCcdX3D())
            return (false, "检测到双 CCD 的 X3D 处理器（" + ProcessorName() + "）。\n\n" +
                           "这类 CPU 的 3D V-Cache 优化器靠核心停放把游戏固定在带缓存的 CCD 上；" +
                           "关闭停核会让游戏线程可能跑到无缓存 CCD，帧率反而更差。\n" +
                           "本软件不允许在这类处理器上关闭停核。");

        var scheme = PowerPlanService.GetActiveGuid();
        if (scheme == null) return (false, "读不到当前生效的电源方案。");

        SaveBackup(scheme.Value, new[] { CpMinCores, CpMaxCores });

        var g = scheme.Value;
        var sub = SubProcessor;

        var a = WriteBoth(g, sub, CpMinCores, 100);
        var b = WriteBoth(g, sub, CpMaxCores, 100);

        if (!a || !b) return (false, "写入被拒绝。可能需要在管理员权限下重试。");

        Apply(g);

        return (true, "已关闭核心停放（最小/最大核心数均为 100%）。\n" +
                      "交流与电池都已写入；切回其他电源方案即恢复模板默认，用「还原」也能回退。");
    }

    /// <summary>禁用处理器闲置（进入 C-State）。</summary>
    public static (bool Ok, string Message) DisableIdle()
    {
        if (!ElevationService.IsAdmin)
            return (false, "改电源方案需要管理员权限。点「以管理员身份重启」后再试。");

        var scheme = PowerPlanService.GetActiveGuid();
        if (scheme == null) return (false, "读不到当前生效的电源方案。");

        SaveBackup(scheme.Value, new[] { IdleDisable });

        if (!WriteBoth(scheme.Value, SubProcessor, IdleDisable, 1))
            return (false, "写入被拒绝。可能需要在管理员权限下重试。");

        Apply(scheme.Value);

        return (true, "已禁用处理器闲置（IDLEDISABLE=1）。\n" +
                      "⚠ 功耗与发热会上升；笔记本请插电时再用，并注意温度。");
    }

    /// <summary>把备份里的原值写回当前方案。</summary>
    public static (bool Ok, string Message) Restore()
    {
        if (!ElevationService.IsAdmin)
            return (false, "还原电源方案需要管理员权限。");

        var backup = LoadBackup();
        if (backup == null || backup.Items.Count == 0)
            return (false, "没有找到改动记录，无法还原。");

        var scheme = PowerPlanService.GetActiveGuid();
        if (scheme == null) return (false, "读不到当前生效的电源方案。");

        var g = scheme.Value;
        var sub = SubProcessor;
        var failed = new List<string>();
        var done = 0;

        foreach (var item in backup.Items)
        {
            var setting = Guid.Parse(item.SettingId);

            if (item.HadAc && PowerWriteACValueIndex(IntPtr.Zero, ref g, ref sub, ref setting, item.Ac) != 0)
            {
                failed.Add(item.SettingId + "（交流）");
                continue;
            }

            if (item.HadDc && PowerWriteDCValueIndex(IntPtr.Zero, ref g, ref sub, ref setting, item.Dc) != 0)
            {
                failed.Add(item.SettingId + "（电池）");
                continue;
            }

            done++;
        }

        Apply(g);

        if (failed.Count > 0)
            return (false, $"已还原 {done} 项，失败 {failed.Count} 项：{string.Join("、", failed)}");

        return (true, $"已按改动前的原值还原 {done} 项。");
    }

    /// <summary>是否有可还原的记录。</summary>
    public static bool HasBackup()
    {
        try
        {
            return File.Exists(BackupPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool WriteBoth(Guid scheme, Guid sub, Guid setting, uint value)
    {
        var g = scheme;
        var s = sub;
        var t = setting;

        var ac = PowerWriteACValueIndex(IntPtr.Zero, ref g, ref s, ref t, value) == 0;
        var dc = PowerWriteDCValueIndex(IntPtr.Zero, ref g, ref s, ref t, value) == 0;

        return ac && dc;
    }

    private static void Apply(Guid scheme)
    {
        var g = scheme;
        PowerSetActiveScheme(IntPtr.Zero, ref g);
    }

    /// <summary>只记录第一次改动前的原值；重复改动不覆盖已有备份，避免把「已改过的值」当原值。</summary>
    private static void SaveBackup(Guid scheme, IEnumerable<Guid> settings)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            var existing = LoadBackup();
            var file = existing ?? new BackupFile { Scheme = scheme.ToString(), SavedAt = DateTime.Now };

            foreach (var setting in settings)
            {
                var id = setting.ToString();
                if (file.Items.Any(x => x.SettingId == id)) continue;

                var (ac, dc) = ReadBoth(scheme, setting);
                if (!ac.HasValue) continue;

                file.Items.Add(new BackupItem
                {
                    SettingId = id,
                    Ac = ac.Value,
                    Dc = dc ?? 0,
                    HadAc = true,
                    HadDc = dc.HasValue
                });
            }

            File.WriteAllText(BackupPath,
                JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* 备份失败不阻断，但还原时会如实报「没有记录」 */ }
    }

    private static BackupFile LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath)) return null;
            return JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(BackupPath));
        }
        catch
        {
            return null;
        }
    }
}
