using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 电源计划：直接读 powrprof.dll，不调 <c>powercfg.exe</c>。
///
/// 为什么不用 powercfg：精简镜像里 System32 的 exe 被删得很随意，powercfg 也常中招；
/// 而且解析它的文本输出要处理多语言。powrprof 是电源管理本体，
/// 「设置 → 系统 → 电源」用的也是这套 API，读出的是同一份数据。
/// </summary>
public static class PowerPlanService
{
    // 系统内置方案的固定 GUID（不随语言变化）
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const uint AccessScheme = 16;

    private static readonly Guid SubVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoIdle = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid StandbyIdle = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcThrottleMin = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    private static readonly Guid ProcThrottleMax = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupGuid,
        uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid,
        IntPtr subGroupGuid, IntPtr powerSettingGuid, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerDuplicateScheme(IntPtr rootPowerKey, ref Guid sourceSchemeGuid,
        out IntPtr destinationSchemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    /// <summary>列出所有电源计划，并标出当前生效的那个。</summary>
    public static List<PowerPlanEntry> List()
    {
        var result = new List<PowerPlanEntry>();
        var active = GetActiveGuid();

        var buffer = new byte[16];
        var size = (uint)buffer.Length;

        for (uint index = 0; ; index++)
        {
            size = (uint)buffer.Length;

            var status = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);

            if (status != 0) break;     // ERROR_NO_MORE_ITEMS = 259，其余错误同样结束

            var guid = new Guid(buffer);

            var name = ReadName(guid);
            if (name.Length == 0) continue;

            // 同名的重复方案很少见，但复制过方案的系统里会有，用 GUID 去重
            if (result.Any(x => x.Guid == guid)) continue;

            result.Add(new PowerPlanEntry
            {
                Guid = guid,
                Name = name,
                IsActive = active.HasValue && active.Value == guid,
                IsBuiltIn = guid == Balanced || guid == HighPerformance || guid == PowerSaver
                            || guid == UltimatePerformance,
                Kind = KindOf(guid, name),
                MonitorOffMinutes = MinutesOf(guid, SubVideo, VideoIdle),
                SleepMinutes = MinutesOf(guid, SubSleep, StandbyIdle),
                ProcessorMin = PercentOf(guid, ProcThrottleMin),
                ProcessorMax = PercentOf(guid, ProcThrottleMax)
            });
        }

        return result
            .OrderByDescending(x => x.IsActive)
            .ThenByDescending(x => x.Kind == "卓越性能")
            .ThenByDescending(x => x.Kind == "高性能")
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>当前生效方案。</summary>
    public static Guid? GetActiveGuid()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;

        try
        {
            return Marshal.PtrToStructure<Guid>(ptr);
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    /// <summary>切换方案。</summary>
    public static (bool Ok, string Message) SetActive(Guid guid)
    {
        var status = PowerSetActiveScheme(IntPtr.Zero, ref guid);

        return status == 0
            ? (true, "已切换电源计划。")
            : (false, $"切换失败（错误 {status}）。部分设备由厂商电源管理接管，需要在厂商软件里改。");
    }

    /// <summary>
    /// 高性能方案不存在时现场复制一份（现代笔记本默认只留「平衡」，高性能被藏起来了）。
    /// 复制出来的方案名由系统给，通常是「高性能」。
    ///
    /// apply = false 时只查询、不创建不切换：自检会走这条，
    /// 因为它跑在用户自己的机器上，不能为了让断言通过就改电源方案。
    /// </summary>
    public static (bool Ok, string Message, Guid Created) EnsureHighPerformance(bool apply = true)
    {
        var existing = List().FirstOrDefault(x => x.Kind == "高性能");
        if (existing != null)
            return (true, $"已存在高性能方案：{existing.Name}", existing.Guid);

        if (!apply)
            return (false, "本机没有高性能方案；点「创建并切换高性能」会现场复制一份并切过去。", Guid.Empty);

        var source = HighPerformance;
        var status = PowerDuplicateScheme(IntPtr.Zero, ref source, out var ptr);

        if (status != 0 || ptr == IntPtr.Zero)
            return (false, $"创建高性能方案失败（错误 {status}）。这台机器的固件可能不支持。" , Guid.Empty);

        try
        {
            var created = Marshal.PtrToStructure<Guid>(ptr);
            SetActive(created);
            return (true, "已创建并切换到高性能方案。", created);
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    /// <summary>
    /// 卓越性能方案不存在时现场复制一份。
    /// 微软把它藏起来了（默认不显示），复制出来的名字是「卓越性能」。
    ///
    /// 与「高性能」的区别：卓越性能不做任何核心降频，代价是待机功耗和温度明显上升。
    ///
    /// 两个实测坑（都在本机 HKC2441 那台精简系统上撞到）：
    /// 1. 复制/切换方案**不需要管理员**，普通用户就能做。
    /// 2. 复制出来的方案在 `PowerEnumerate` 与 `powercfg /list` 里**看不到**，
    ///    必须先 `SetActive` 一次才出现；一旦切走，又从列表里消失（`powercfg /query` 仍能查到）。
    ///    所以这里必须把 GUID 记下来，之后靠 GUID 直接切换，不能依赖枚举。
    ///
    /// apply = false 只查询不创建（自检走这条，不能为了断言通过就改用户的电源方案）。
    /// </summary>
    public static (bool Ok, string Message, Guid Created) EnsureUltimatePerformance(bool apply = true)
    {
        var state = Ultimate();

        if (state.Exists)
        {
            if (!apply) return (true, $"已存在卓越性能方案：{state.Name}", state.Guid);

            if (state.Active) return (true, "当前已经在用「卓越性能」方案。", state.Guid);

            var previous = GetActiveGuid();
            var (ok, message) = SetActive(state.Guid);

            if (ok) RememberUltimate(state.Guid, previous);

            return (ok, ok ? $"已切换到卓越性能方案：{state.Name}" : message, state.Guid);
        }

        if (!apply)
            return (false, "本机没有卓越性能方案；点「创建卓越性能」会现场复制一份。", Guid.Empty);

        var source = UltimatePerformance;
        var status = PowerDuplicateScheme(IntPtr.Zero, ref source, out var ptr);

        if (status != 0 || ptr == IntPtr.Zero)
            return (false, $"创建卓越性能方案失败（错误 {status}）。部分精简系统把这份模板删掉了。", Guid.Empty);

        try
        {
            var created = Marshal.PtrToStructure<Guid>(ptr);
            var previous = GetActiveGuid();
            var (ok, message) = SetActive(created);

            if (ok) RememberUltimate(created, previous);

            return (ok,
                ok ? "已创建并切换到「卓越性能」方案。\n\n代价：CPU 不再主动降频，待机功耗和温度会明显上升（笔记本不建议长期用）。"
                   : "方案已创建，但切换失败：" + message + "\n（新方案在列表里要激活过一次才可见）",
                created);
        }
        finally
        {
            LocalFree(ptr);
        }
    }

    /// <summary>卓越性能方案的状态。枚举不到时靠记住的 GUID 认。</summary>
    public sealed record UltimateState(bool Exists, bool Active, string Name, Guid Guid);

    public static UltimateState Ultimate()
    {
        var inList = List().FirstOrDefault(p => p.Kind == "卓越性能");
        if (inList != null) return new UltimateState(true, inList.IsActive, inList.Name, inList.Guid);

        // 枚举不到时直接扫注册表——精简系统会把「非激活」的方案从 PowerEnumerate 里过滤掉，
        // 但方案本身还在 HKLM\...\Power\User\PowerSchemes 下。
        var fromRegistry = FindUltimateInRegistry();
        if (fromRegistry != null)
        {
            var name = ReadName(fromRegistry.Value);
            if (name.Length == 0) name = "卓越性能";

            return new UltimateState(true, GetActiveGuid() == fromRegistry, name, fromRegistry.Value);
        }

        var record = LoadUltimateRecord();
        if (record == null || !Guid.TryParse(record.Guid, out var remembered) || remembered == Guid.Empty)
            return new UltimateState(false, false, "", Guid.Empty);

        var rememberedName = ReadName(remembered);
        if (rememberedName.Length == 0) return new UltimateState(false, false, "", Guid.Empty);

        return new UltimateState(true, GetActiveGuid() == remembered, rememberedName, remembered);
    }

    /// <summary>在注册表里找「卓越性能」方案（绕过 PowerEnumerate 的过滤）。</summary>
    private static Guid? FindUltimateInRegistry()
    {
        try
        {
            using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes");

            if (root == null) return null;

            foreach (var name in root.GetSubKeyNames())
            {
                if (!Guid.TryParse(name, out var guid)) continue;
                if (guid == UltimatePerformance) continue;   // 模板本身不算一个可用的方案

                using var key = root.OpenSubKey(name);
                var raw = key?.GetValue("FriendlyName") as string ?? "";
                if (raw.Length == 0) continue;

                var text = MuiString.Resolve(raw);

                if (text.Contains("卓越") || raw.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
                    return guid;
            }
        }
        catch { /* 读不到就当没有 */ }

        return null;
    }

    /// <summary>切回上一次切走前的方案（点「卓越性能」之前用的那个）。</summary>
    public static (bool Ok, string Message) RestorePreviousPlan()
    {
        var record = LoadUltimateRecord();

        if (record == null || !Guid.TryParse(record.Previous, out var previous) || previous == Guid.Empty)
            return (false, "没有记录到切换前的方案。可以手动在列表里选一个。");

        var (ok, message) = SetActive(previous);

        return ok ? (true, "已切回切换前的电源方案。") : (false, message);
    }

    private sealed class UltimateRecord
    {
        public string Guid { get; set; } = "";
        public string Previous { get; set; } = "";
        public string Time { get; set; } = "";
    }

    private static string UltimateFile => Path.Combine(AppPaths.DataDir, "power-ultimate.json");

    private static UltimateRecord LoadUltimateRecord()
    {
        try
        {
            if (!File.Exists(UltimateFile)) return null;

            return System.Text.Json.JsonSerializer.Deserialize<UltimateRecord>(File.ReadAllText(UltimateFile));
        }
        catch
        {
            return null;
        }
    }

    private static void RememberUltimate(Guid guid, Guid? previous)
    {
        try
        {
            var old = LoadUltimateRecord();

            // 已经切过一次时保留最早的「上一个方案」，避免来回切把记录覆盖成卓越自己
            var keepPrevious = old != null
                               && Guid.TryParse(old.Previous, out var p)
                               && p != Guid.Empty
                               && p != guid;

            var record = new UltimateRecord
            {
                Guid = guid.ToString(),
                Previous = keepPrevious ? old!.Previous : previous?.ToString() ?? "",
                Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
            };

            if (!Directory.Exists(AppPaths.DataDir)) Directory.CreateDirectory(AppPaths.DataDir);

            File.WriteAllText(UltimateFile, System.Text.Json.JsonSerializer.Serialize(record,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 记不住不影响切换本身 */ }
    }

    private static string ReadName(Guid scheme)
    {
        var buffer = new byte[1024];
        var size = (uint)buffer.Length;

        if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
            return "";

        var text = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length));
        return text.TrimEnd('\0').Trim();
    }

    private static uint? ReadAc(Guid scheme, Guid sub, Guid setting)
    {
        var s = sub;
        var t = setting;

        return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref s, ref t, out var value) == 0 ? value : null;
    }

    private static int MinutesOf(Guid scheme, Guid sub, Guid setting)
    {
        var seconds = ReadAc(scheme, sub, setting);
        if (seconds == null) return -1;

        return (int)Math.Round(seconds.Value / 60.0);
    }

    private static int PercentOf(Guid scheme, Guid setting)
    {
        var value = ReadAc(scheme, SubProcessor, setting);
        return value == null ? -1 : (int)value.Value;
    }

    private static string KindOf(Guid guid, string name)
    {
        if (guid == UltimatePerformance) return "卓越性能";
        if (guid == HighPerformance) return "高性能";
        if (guid == Balanced) return "平衡";
        if (guid == PowerSaver) return "节能";

        // 复制出来的方案 GUID 会变，只能靠名字认
        if (name.Contains("卓越") || name.Contains("Ultimate", StringComparison.OrdinalIgnoreCase))
            return "卓越性能";
        if (name.Contains("高性能") || name.Contains("High performance", StringComparison.OrdinalIgnoreCase))
            return "高性能";
        if (name.Contains("节能") || name.Contains("Power saver", StringComparison.OrdinalIgnoreCase))
            return "节能";
        if (name.Contains("平衡") || name.Contains("Balanced", StringComparison.OrdinalIgnoreCase))
            return "平衡";

        return "自定义";
    }
}
