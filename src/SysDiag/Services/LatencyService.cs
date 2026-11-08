using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 延迟优化：把「系统时钟取向 / 中断模式 / 外设节能 / 计时器」这四类能实测的东西列出来。
///
/// 参照的是 码枪堂「延迟优化」那一页的四个子项，但实现全部自己写，取值只认能验证的：
///
/// - **系统时钟取向**：`bcdedit useplatformclock / disabledynamictick`。读也需要管理员，
///   普通权限下如实标「读不到」，不猜。
/// - **中断模式**：设备的 MSI（消息信号中断）状态，从
///   `Enum\...\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties` 读。
///   MSI 关掉时设备走传统线中断，DPC 延迟通常更高——这是「驱动级延迟」里唯一能直接读写的部分。
/// - **外设节能**：电源方案里的「USB 选择性暂停」+ HID 设备自己的选择性挂起。
/// - **计时器**：`NtQueryTimerResolution` 读系统当前的分辨率（100ns 单位），只读不改。
///
/// 刻意没做的：NVIDIA「低延迟模式 / Reflex」写的是驱动 DRS 数据库，需要 NVAPI 才能安全写，
/// 本程序只在游戏内/NVIDIA 控制面板里改，不在这里瞎写 ID。
/// </summary>
public static class LatencyService
{
    private const string EnumRoot = @"SYSTEM\CurrentControlSet\Enum\";

    private const string DisplayClass = "{4d36e968-e325-11ce-bfc1-08002be10318}";
    private const string NetClass = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private static readonly Guid SubUsb = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid SetUsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");

    // ---------------------------------------------------------------- 数据结构

    public sealed class Check
    {
        public string Group { get; init; } = "";
        public string Title { get; init; } = "";

        /// <summary>当前状态的一句话说明（含具体数值/路径）。</summary>
        public string Detail { get; init; } = "";

        /// <summary>ok / off / warn / unknown。</summary>
        public string State { get; init; } = "unknown";

        public string StateText => State switch
        {
            "ok" => "已优化",
            "off" => "未优化",
            "warn" => "需注意",
            _ => "读不到"
        };

        public bool NeedsAdmin { get; init; }
        public bool NeedsRestart { get; init; }

        /// <summary>可以执行的动作名；空表示只读展示。</summary>
        public string ActionLabel { get; init; } = "";

        public Func<(bool Ok, string Message)> Apply { get; init; }

        /// <summary>
        /// 多个可选项（例如 HPET 的「强制」/「恢复平台默认」）。
        /// 非空时不显示单按钮，改为每个选项一个按钮。
        /// </summary>
        public IReadOnlyList<Choice> Options { get; init; } = Array.Empty<Choice>();

        /// <summary>有任何可执行动作。</summary>
        public bool HasActions => Apply != null || Options.Count > 0;

        /// <summary>动作带来的代价。</summary>
        public string Effect { get; init; } = "";
    }

    /// <summary>一个可选动作。</summary>
    public sealed class Choice
    {
        public string Label { get; init; } = "";
        public string Note { get; init; } = "";
        public Func<(bool Ok, string Message)> Run { get; init; }
    }

    public static List<Check> Scan()
    {
        var list = new List<Check>();

        list.AddRange(ClockChecks());
        list.AddRange(InterruptChecks());
        list.AddRange(UsbChecks());
        list.Add(TimerCheck());

        // 码枪堂那几项：虚拟化 / 卓越性能 / 游戏优先级 / N 卡 Boost
        list.AddRange(GameBoostService.Checks());

        return list;
    }

    // ---------------------------------------------------------------- 系统时钟

    [DllImport("ntdll.dll")]
    private static extern int NtQueryTimerResolution(out uint minimum, out uint maximum, out uint current);

    private static IEnumerable<Check> ClockChecks()
    {
        var (ok, output) = RunBcd();
        var value = ok ? ReadBcdValue(output, "useplatformclock") : "";

        // 三态：true = 强制 HPET；false = 明确禁用；未设置 = 平台默认（Win10+ 一般走 TSC）
        var state = !ok ? "unknown"
            : value.Length == 0 ? "ok"
            : value is "Yes" or "yes" or "true" or "True" ? "warn"
            : "ok";

        yield return new Check
        {
            Group = "系统时钟取向",
            Title = "平台时钟（HPET / useplatformclock）",
            Detail = !ok
                ? "读不到启动配置（bcdedit 需要管理员权限）。这一项改的是内核计时源，没读到就不猜。"
                : value.Length == 0
                    ? "useplatformclock 未设置 → 平台默认（Win10 起通常走 TSC + 动态计时，多数平台这样最稳）"
                    : value is "Yes" or "yes" or "true" or "True"
                        ? "useplatformclock = true → 已强制 HPET（高精度事件计时器）"
                        : $"useplatformclock = {value} → 已明确禁用 HPET",
            State = state,
            NeedsAdmin = true,
            NeedsRestart = true,
            Effect = "两种模式二选一，改完必须重启：\n" +
                     "· 强制 HPET：计时源独立、抖动小，部分主板（尤其老 Intel）帧时间更稳；但延迟比 TSC 高，某些平台反而更抖。\n" +
                     "· 平台默认：让内核自己选（现代平台走 TSC），延迟最低，兼容性最好。\n" +
                     "没有实测对比就别动——这一项不是「越开越好」。",
            // 非管理员时也给出按钮：点下去会提示提权重启，比「没有按钮」清楚
            Options = new[]
            {
                new Choice
                {
                    Label = "强制 HPET",
                    Note = "bcdedit /set {current} useplatformclock true，重启后生效",
                    Run = () => SetBcd("useplatformclock", "true")
                },
                new Choice
                {
                    Label = "恢复平台默认",
                    Note = "bcdedit /deletevalue {current} useplatformclock，重启后生效",
                    Run = () => DeleteBcd("useplatformclock")
                }
            }
        };

        yield return new Check
        {
            Group = "系统时钟取向",
            Title = "动态时钟滴答（disabledynamictick）",
            Detail = !ok
                ? "读不到启动配置（bcdedit 需要管理员权限）。"
                : Describe(output, "disabledynamictick", "已关闭动态滴答", "动态滴答开启（系统默认）"),
            State = !ok ? "unknown" : StateOf(output, "disabledynamictick", true),
            NeedsAdmin = true,
            NeedsRestart = true,
            ActionLabel = !ok ? "" : "关闭动态滴答",
            Effect = "关掉后内核定时器不再合并，理论上响应更稳，代价是待机功耗略升。",
            Apply = !ok ? null : () => SetBcd("disabledynamictick", "yes")
        };
    }

    private static string Describe(string bcd, string key, string on, string off)
    {
        var value = ReadBcdValue(bcd, key);

        return value.Length == 0
            ? $"{key} 未设置 → {off}"
            : $"{key} = {value} → {(value is "Yes" or "yes" or "true" or "True" ? on : off)}";
    }

    private static string StateOf(string bcd, string key, bool optimizedWhenTrue)
    {
        var value = ReadBcdValue(bcd, key);
        if (value.Length == 0) return "off";

        var on = value is "Yes" or "yes" or "true" or "True";
        return on == optimizedWhenTrue ? "ok" : "off";
    }

    internal static string ReadBcdValue(string bcd, string key)
    {
        // bcdedit 的输出形如：useplatformclock        Yes
        foreach (var line in bcd.Split('\n'))
        {
            var text = line.Trim();
            if (!text.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;

            var parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2) return parts[^1];
        }

        return "";
    }

    internal static (bool Ok, string Output) RunBcd()
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("bcdedit.exe", "/enum {current}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(info);
            if (process == null) return (false, "");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(6000);

            // 没权限时 bcdedit 会写「拒绝访问 / Access is denied」
            var denied = output.Contains("拒绝访问", StringComparison.Ordinal)
                         || output.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                         || output.Contains("无法打开", StringComparison.Ordinal);

            return (process.ExitCode == 0 && !denied, output);
        }
        catch
        {
            return (false, "");
        }
    }

    internal static (bool Ok, string Message) SetBcd(string key, string value)
    {
        return RunBcdCommand($"/set {{current}} {key} {value}", $"{key} 已设为 {value}，重启系统后生效。");
    }

    /// <summary>删除一项启动参数（恢复「系统默认」）。本来就没有时视为成功。</summary>
    internal static (bool Ok, string Message) DeleteBcd(string key)
    {
        var result = RunBcdCommand($"/deletevalue {{current}} {key}", $"{key} 已删除，重启后回到平台默认。");

        // 不存在时 bcdedit 返回「找不到元素 / The boot configuration data store could not be opened」
        if (!result.Ok && result.Message.Contains("找不到", StringComparison.Ordinal))
            return (true, $"{key} 本来就没有设置，已经是平台默认了。");

        return result;
    }

    private static (bool Ok, string Message) RunBcdCommand(string arguments, string success)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("bcdedit.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(info);
            if (process == null) return (false, "无法启动 bcdedit。");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(8000);

            if (process.ExitCode != 0 || output.Contains("拒绝访问"))
            {
                // 「找不到」和「没权限」是两回事，混在一起会误导用户去提权
                var missing = output.Contains("找不到", StringComparison.Ordinal)
                              || output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                              || output.Contains("not found", StringComparison.OrdinalIgnoreCase);

                return (false, (missing ? "找不到这一项（可能本来就没有设置）。\n" : "写入被拒绝（需要管理员权限）。\n")
                               + OneLine(output));
            }

            return (true, success);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 中断模式

    private static IEnumerable<Check> InterruptChecks()
    {
        yield return BuildMsiCheck("显示适配器（GPU）", DisplayClass);
        yield return BuildMsiCheck("网络适配器", NetClass);
    }

    /// <summary>MSI 状态：1 = 已启用消息信号中断；键不存在 = 该设备不支持/未启用。</summary>
    private static Check BuildMsiCheck(string label, string classGuid)
    {
        var devices = PnpDevices(classGuid);
        var on = 0;
        var off = 0;
        var missing = 0;
        var details = new List<string>();

        foreach (var (name, instance) in devices)
        {
            var value = ReadMsi(instance);

            switch (value)
            {
                case 1:
                    on++;
                    break;
                case 0:
                    off++;
                    details.Add($"{name}：未启用");
                    break;
                default:
                    missing++;
                    break;
            }
        }

        var total = devices.Count;
        var state = total == 0 ? "unknown" : off > 0 ? "off" : on > 0 ? "ok" : "unknown";

        return new Check
        {
            Group = "中断模式（驱动级）",
            Title = label + " MSI 中断",
            Detail = total == 0
                ? "没有找到这类设备。"
                : $"{total} 个设备：已启用 {on}、未启用 {off}、无 MSI 能力 {missing}" +
                  (details.Count > 0 ? "\n" + string.Join("；", details.Take(4)) : ""),
            State = state,
            NeedsAdmin = true,
            NeedsRestart = true,
            ActionLabel = off > 0 ? "全部改为 MSI" : "",
            Effect = "传统线中断是共享的，容易被别的设备抢占，DPC 延迟更高；改成 MSI 后中断独立。" +
                     "极少数老驱动会因此蓝屏——所以改完要重启验证，出问题进安全模式改回 0。",
            Apply = off > 0 ? () => EnableMsi(devices) : null
        };
    }

    private static int ReadMsi(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"{EnumRoot}{instanceId}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties");

            return key?.GetValue("MSISupported") is int value ? value : -1;
        }
        catch
        {
            return -1;
        }
    }

    private static List<(string Name, string Instance)> PnpDevices(string classGuid)
    {
        var list = new List<(string, string)>();

        try
        {
            foreach (var item in EvidenceService.Wmi(
                         "SELECT Name, PNPDeviceID, ClassGuid FROM Win32_PnPEntity " +
                         $"WHERE ClassGuid = '{classGuid}'"))
            {
                var name = Str(item, "Name");
                var id = Str(item, "PNPDeviceID");

                if (name.Length > 0 && id.Length > 0) list.Add((name, id));
            }
        }
        catch { /* 拿不到就当没有 */ }

        return list;
    }

    private static (bool Ok, string Message) EnableMsi(List<(string Name, string Instance)> devices)
    {
        if (!ElevationService.IsAdmin)
            return (false, "改 MSI 要写 HKLM 的设备键，需要管理员权限。\n点「以管理员身份重启」后再试。");

        var done = new List<string>();
        var failed = new List<string>();

        foreach (var (name, instance) in devices)
        {
            if (ReadMsi(instance) != 0) continue;

            try
            {
                using var key = Registry.LocalMachine.CreateSubKey(
                    $@"{EnumRoot}{instance}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties",
                    writable: true);

                if (key == null)
                {
                    failed.Add(name + "：打不开键");
                    continue;
                }

                key.SetValue("MSISupported", 1, RegistryValueKind.DWord);
                done.Add(name);
            }
            catch (Exception ex)
            {
                failed.Add(name + "：" + ex.Message);
            }
        }

        if (done.Count == 0 && failed.Count == 0)
            return (true, "没有需要改的设备（都已经启用 MSI）。");

        var message = done.Count > 0
            ? $"已为 {done.Count} 个设备启用 MSI：{string.Join("、", done)}。\n重启后生效。"
            : "没有设备被改动。";

        if (failed.Count > 0) message += "\n失败：" + string.Join("；", failed);

        return (failed.Count == 0, message);
    }

    // ---------------------------------------------------------------- 外设节能

    private static IEnumerable<Check> UsbChecks()
    {
        var (ok, value, note) = ReadUsbSelectiveSuspend();

        yield return new Check
        {
            Group = "外设节能",
            Title = "USB 选择性暂停",
            Detail = ok
                ? $"当前电源方案：{(value == 0 ? "已禁用（外设不会被挂起）" : "已启用（空闲时挂起外设，可能增加唤醒延迟）")}"
                : note,
            State = !ok ? "unknown" : value == 0 ? "ok" : "off",
            NeedsAdmin = true,
            ActionLabel = ok && value != 0 ? "关闭选择性暂停" : "",
            Effect = "关掉后键鼠不会被系统挂起，唤醒延迟更稳定；代价是待机功耗略升（笔记本更明显）。",
            Apply = ok && value != 0 ? DisableUsbSelectiveSuspend : null
        };

        var (hids, sleeping) = HidPowerSaving();
        var limit = hids;

        yield return new Check
        {
            Group = "外设节能",
            Title = "HID 设备「允许关闭以省电」",
            Detail = limit == 0
                ? "没有找到 HID 设备。"
                : $"{limit} 个键鼠/输入设备中，{sleeping} 个允许系统关闭以省电" +
                  (sleeping > 0 ? "（空闲后需要唤醒，会带来输入延迟）" : ""),
            State = limit == 0 ? "unknown" : sleeping == 0 ? "ok" : "off",
            NeedsAdmin = true,
            ActionLabel = sleeping > 0 ? "关闭这些设备的省电" : "",
            Effect = "关掉后设备常驻唤醒状态，输入更跟手；代价是待机耗电略增。",
            Apply = sleeping > 0 ? DisableHidPowerSaving : null
        };
    }

    private static (bool Ok, int Value, string Note) ReadUsbSelectiveSuspend()
    {
        try
        {
            var scheme = PowerPlanService.GetActiveGuid();
            if (scheme == null) return (false, -1, "读不到当前电源方案。");

            var guid = scheme.Value;
            var sub = SubUsb;
            var setting = SetUsbSelectiveSuspend;

            var result = PowerReadACValueIndex(IntPtr.Zero, ref guid, ref sub, ref setting, out var value);
            if (result != 0) return (false, -1, $"读不到 USB 选择性暂停（错误 {result}）。");

            return (true, value, "");
        }
        catch (Exception ex)
        {
            return (false, -1, "读不到：" + ex.Message);
        }
    }

    private static (bool Ok, string Message) DisableUsbSelectiveSuspend()
    {
        try
        {
            var scheme = PowerPlanService.GetActiveGuid();
            if (scheme == null) return (false, "读不到当前电源方案。");

            var guid = scheme.Value;
            var sub = SubUsb;
            var setting = SetUsbSelectiveSuspend;

            var ac = PowerWriteACValueIndex(IntPtr.Zero, ref guid, ref sub, ref setting, 0);
            var dc = PowerWriteDCValueIndex(IntPtr.Zero, ref guid, ref sub, ref setting, 0);

            if (ac != 0 && dc != 0)
                return (false, $"写入被拒绝（错误 {ac}）。改电源方案需要管理员权限。");

            var applied = PowerSetActiveScheme(IntPtr.Zero, ref guid);

            return (true, applied == 0
                ? "已关闭 USB 选择性暂停（交流与电池都关）。"
                : "已写入，但重新激活电源方案失败，注销后生效。");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, out int valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, int valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid,
        ref Guid subGroupGuid, ref Guid settingGuid, int valueIndex);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    /// <summary>数一下 HID 里被设成「允许省电」的设备（SelectiveSuspendEnabled = 1）。</summary>
    private static (int Total, int Sleeping) HidPowerSaving()
    {
        var total = 0;
        var sleeping = 0;

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(EnumRoot + @"HID");
            if (root == null) return (0, 0);

            foreach (var vendor in root.GetSubKeyNames().Take(80))
            {
                using var vendorKey = root.OpenSubKey(vendor);
                if (vendorKey == null) continue;

                foreach (var instance in vendorKey.GetSubKeyNames().Take(40))
                {
                    using var dev = vendorKey.OpenSubKey(instance + @"\Device Parameters");
                    if (dev == null) continue;

                    total++;

                    if (dev.GetValue("SelectiveSuspendEnabled") is int v && v != 0) sleeping++;
                }
            }
        }
        catch { /* 读不全就按读到的算 */ }

        return (total, sleeping);
    }

    private static (bool Ok, string Message) DisableHidPowerSaving()
    {
        if (!ElevationService.IsAdmin)
            return (false, "改 HID 设备参数要写 HKLM，需要管理员权限。");

        var changed = 0;

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(EnumRoot + @"HID");
            if (root == null) return (false, "读不到 HID 设备。");

            foreach (var vendor in root.GetSubKeyNames().Take(80))
            {
                using var vendorKey = root.OpenSubKey(vendor);
                if (vendorKey == null) continue;

                foreach (var instance in vendorKey.GetSubKeyNames().Take(40))
                {
                    var path = $@"{EnumRoot}HID\{vendor}\{instance}\Device Parameters";

                    try
                    {
                        using var dev = Registry.LocalMachine.OpenSubKey(path, writable: true);
                        if (dev?.GetValue("SelectiveSuspendEnabled") is not int v || v == 0) continue;

                        dev.SetValue("SelectiveSuspendEnabled", 0, RegistryValueKind.DWord);
                        changed++;
                    }
                    catch { /* 单个设备改不了就跳过 */ }
                }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        return (changed > 0,
            changed > 0 ? $"已关闭 {changed} 个 HID 设备的省电选项，重新插拔或重启后生效。" : "没有可改的设备。");
    }

    // ---------------------------------------------------------------- 计时器

    private static Check TimerCheck()
    {
        try
        {
            NtQueryTimerResolution(out var minimum, out var maximum, out var current);

            double Ms(uint v) => v / 10000.0;

            return new Check
            {
                Group = "计时器",
                Title = "系统计时器分辨率",
                Detail = $"当前 {Ms(current):0.000} ms　·　系统最小 {Ms(maximum):0.000} ms　·　最大 {Ms(minimum):0.000} ms" +
                         "\n值越小，Sleep/等待的粒度越细。很多「超低延迟」工具就是让一个常驻进程把分辨率锁在 0.5 ms。",
                State = Ms(current) <= 1.0 ? "ok" : "off",
                Effect = "本程序不常驻，所以只报告不锁定；要长期锁定需要一个开机自启的常驻进程。",
                NeedsAdmin = false
            };
        }
        catch (Exception ex)
        {
            return new Check
            {
                Group = "计时器",
                Title = "系统计时器分辨率",
                Detail = "读不到：" + ex.Message,
                State = "unknown"
            };
        }
    }

    // ---------------------------------------------------------------- 工具

    private static string Str(Dictionary<string, object> dict, string key)
        => dict != null && dict.TryGetValue(key, out var v) && v != null ? v.ToString()?.Trim() ?? "" : "";

    private static string OneLine(string text)
        => string.Join(" ", (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Length > 0).Take(2));
}
