using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 调优条目的读取 / 应用 / 备份 / 还原。
///
/// 三条原则（都是被这类工具坑过之后定下来的）：
///
/// 1. **写之前先记下原值**。备份存在 %LocalAppData%\SysDiag\tweaks-backup.json，
///    记「原本有没有这个值、什么类型、什么内容」。还原时按记录写回，没有的就删掉，
///    不猜默认值。
/// 2. **默认值是「删掉」，不是「写某个数」**。很多策略键的默认状态是「不存在」，
///    写一个 0 和删掉它，系统行为可能不同。
/// 3. **读不到就说读不到**。权限不足、服务不存在都归到「读不到」，
///    不把「读不到」显示成「已关闭」——否则用户会以为没生效而反复点。
/// </summary>
public static class TweakService
{
    private const string BackupName = "tweaks-backup.json";

    public static bool IsAdmin => ElevationService.IsAdmin;

    private static string BackupFile => Path.Combine(AppPaths.DataDir, BackupName);

    /// <summary>
    /// 命令类条目的探针缓存（30 秒）。
    ///
    /// 这层缓存不是「优化」，是必须的：DISM 与 netsh 每次要 1~2 秒，
    /// 而界面一次刷新会读 4 个分组的合计（50 次 Read），
    /// 不缓存的话点一下分类要卡十几秒，看起来像没响应。
    /// </summary>
    private static readonly Dictionary<string, (DateTime At, TweakReading Reading)> ProbeCache = new();

    private static List<ServiceEntry> _serviceCache;
    private static DateTime _serviceCacheAt;

    /// <summary>
    /// 服务列表缓存（2 秒）。一次界面刷新会读 4 个服务条目 + 4 个分组合计，
    /// 每次都走 EnumServicesStatusEx 会明显拖慢刷新；2 秒足够覆盖一次刷新又不至于显示旧状态。
    /// </summary>
    private static List<ServiceEntry> Services()
    {
        if (_serviceCache != null && (DateTime.Now - _serviceCacheAt).TotalSeconds < 2)
            return _serviceCache;

        _serviceCache = ServiceInventoryService.Scan();
        _serviceCacheAt = DateTime.Now;

        return _serviceCache;
    }

    // ---------------------------------------------------------------- 读取状态

    public static TweakReading Read(TweakItem item)
    {
        return item.Kind switch
        {
            TweakKind.Service => ReadService(item),
            TweakKind.Command => ReadCommand(item),
            _ => ReadRegistry(item)
        };
    }

    public static List<TweakReading> ReadAll(IEnumerable<TweakItem> items)
        => items.Select(Read).ToList();

    private static TweakReading ReadRegistry(TweakItem item)
    {
        var on = 0;
        var off = 0;
        var unknown = 0;
        var parts = new List<string>();

        foreach (var write in item.Writes)
        {
            var actual = ReadValue(write, out var error);

            if (error != null)
            {
                unknown++;
                parts.Add($"{write.ValueName}：{error}");
                continue;
            }

            var text = actual.Existed ? Format(actual) : "（未设置）";
            parts.Add($"{write.ValueName} = {text}");

            if (actual.Existed && Same(actual, write, on: true)) on++;
            else if (!actual.Existed && write.DeleteWhenOff) off++;
            else if (actual.Existed && Same(actual, write, on: false)) off++;
        }

        var state = unknown == item.Writes.Count
            ? TweakState.Unknown
            : on == item.Writes.Count
                ? TweakState.On
                : off == item.Writes.Count
                    ? TweakState.Off
                    : TweakState.Partial;

        return new TweakReading
        {
            Item = item,
            State = state,
            Detail = string.Join("　·　", parts)
        };
    }

    private static TweakReading ReadService(TweakItem item)
    {
        var entry = Services()
            .FirstOrDefault(s => s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
        {
            return new TweakReading
            {
                Item = item,
                State = TweakState.Unknown,
                Detail = $"服务 {item.ServiceName} 不存在（可能被系统移除）"
            };
        }

        // 只认「优化已生效」这一种明确状态：服务被禁用。
        // 其它启动类型（自动 / 手动 / 按需）一律算「未生效」——以前把「自动」判成「部分开启」，
        // 界面上看着像坏了一半，实际只是没被改过。
        var state = entry.StartRaw == item.OnStart
            ? TweakState.On
            : TweakState.Off;

        var detail = $"启动类型 {entry.StartTypeText}　·　当前 {entry.LevelText}";

        if (state == TweakState.On && entry.Running)
            detail += "　·　启动类型已禁用但进程还在跑（重启后彻底停）";

        return new TweakReading
        {
            Item = item,
            State = state,
            Detail = detail
        };
    }

    private static TweakReading ReadCommand(TweakItem item)
    {
        if (ProbeCache.TryGetValue(item.Id, out var hit)
            && (DateTime.Now - hit.At).TotalSeconds < 30)
        {
            return hit.Reading;
        }

        var reading = ReadCommandCore(item);
        ProbeCache[item.Id] = (DateTime.Now, reading);

        return reading;
    }

    private static TweakReading ReadCommandCore(TweakItem item)
    {
        if (item.Probe == null || item.ProbeOn == null)
        {
            return new TweakReading
            {
                Item = item,
                State = TweakState.Unknown,
                Detail = "这一项没有状态探针，只能执行后按提示判断"
            };
        }

        try
        {
            var text = item.Probe();
            var on = item.ProbeOn();

            return new TweakReading
            {
                Item = item,
                State = on ? TweakState.On : TweakState.Off,
                Detail = text
            };
        }
        catch (Exception ex)
        {
            return new TweakReading
            {
                Item = item,
                State = TweakState.Unknown,
                Detail = "读取失败：" + ex.Message
            };
        }
    }

    // ---------------------------------------------------------------- 应用

    public static (bool Ok, string Message) Apply(TweakItem item, bool on)
    {
        if (item.NeedsAdmin && !IsAdmin)
        {
            return (false, $"「{item.Title}」写在 HKLM / 系统层，需要管理员权限。" +
                           "请点右上角「以管理员身份重启」后再试。");
        }

        ProbeCache.Remove(item.Id);

        try
        {
            return item.Kind switch
            {
                TweakKind.Service => ApplyService(item, on),
                TweakKind.Command => ApplyCommand(item, on),
                _ => ApplyRegistry(item, on)
            };
        }
        catch (Exception ex)
        {
            return (false, $"写入失败：{ex.Message}");
        }
    }

    private static (bool Ok, string Message) ApplyRegistry(TweakItem item, bool on)
    {
        var backup = LoadBackup();
        var record = backup.TryGetValue(item.Id, out var existing) ? existing : new List<BackupWrite>();
        var isNew = record.Count == 0;

        foreach (var write in item.Writes)
        {
            // 只在「第一次改这个条目」时记录原值，反复开关不会把原值覆盖成上一次的状态
            if (isNew)
            {
                var actual = ReadValue(write, out _);
                record.Add(new BackupWrite
                {
                    Hive = write.Hive.ToString(),
                    KeyPath = write.KeyPath,
                    ValueName = write.ValueName,
                    Existed = actual.Existed,
                    Kind = actual.Existed ? actual.ValueKind.ToString() : write.Kind.ToString(),
                    Value = actual.Existed ? Format(actual) : ""
                });
            }

            WriteValue(write, on);
        }

        backup[item.Id] = record;
        SaveBackup(backup);

        return (true, on ? $"已开启「{item.Title}」。" : $"已关闭「{item.Title}」。");
    }

    private static (bool Ok, string Message) ApplyService(TweakItem item, bool on)
    {
        _serviceCache = null;   // 改完要立刻看到新状态，别用缓存

        var entry = Services()
            .FirstOrDefault(s => s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));

        if (entry == null) return (false, $"服务 {item.ServiceName} 不存在。");

        var backup = LoadBackup();
        if (!backup.ContainsKey(item.Id))
        {
            backup[item.Id] = new List<BackupWrite>
            {
                new()
                {
                    Hive = "SERVICE",
                    KeyPath = item.ServiceName,
                    ValueName = "Start",
                    Existed = true,
                    Kind = "DWord",
                    Value = entry.StartRaw.ToString(CultureInfo.InvariantCulture)
                }
            };

            SaveBackup(backup);
        }

        var target = on ? item.OnStart : item.OffStart;

        // 「开启这项优化」= 设为禁用并停掉；「取消」= 恢复启动类型并重新启动。
        // 顺序很关键：先把启动类型改掉，再动进程，否则会被「自动启动」的策略拉回来。
        var set = ServiceInventoryService.SetStartType(item.ServiceName, target);
        if (!set.Ok) return set;

        if (on && entry.State == "Running")
            ServiceInventoryService.Stop(item.ServiceName);

        if (!on && entry.State != "Running")
            ServiceInventoryService.Start(item.ServiceName);

        var typeText = ServiceInventoryService.StartTypeTextOf(target, false);

        return (true, on
            ? $"已把 {item.ServiceName} 设为「{typeText}」并停止。"
            : $"已把 {item.ServiceName} 恢复为「{typeText}」并启动。");
    }

    private static (bool Ok, string Message) ApplyCommand(TweakItem item, bool on)
    {
        var command = on ? item.OnCommand : item.OffCommand;
        if (command.Length == 0) return (false, "这个条目没有配置命令。");

        var (code, output) = RunCommand(command);

        // 有些命令（netsh / dism）失败时退出码仍是 0，只在输出里写错误
        var failed = code != 0
                     || output.Contains("失败", StringComparison.Ordinal)
                     || output.Contains("error", StringComparison.OrdinalIgnoreCase)
                     || output.Contains("not supported", StringComparison.OrdinalIgnoreCase)
                     || output.Contains("不支持", StringComparison.Ordinal);

        if (failed)
        {
            return (false, $"命令执行失败（退出码 {code}）：\n{Trim(output)}");
        }

        var note = output.Length > 0 ? "\n" + Trim(output) : "";

        return (true, (on ? $"已执行「{item.Title}」。" : $"已撤销「{item.Title}」。") + note);
    }

    // ---------------------------------------------------------------- 备份 / 还原

    public sealed class BackupWrite
    {
        public string Hive { get; set; } = "";
        public string KeyPath { get; set; } = "";
        public string ValueName { get; set; } = "";
        public bool Existed { get; set; }
        public string Kind { get; set; } = "DWord";
        public string Value { get; set; } = "";
    }

    private static Dictionary<string, List<BackupWrite>> LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupFile)) return new Dictionary<string, List<BackupWrite>>(StringComparer.Ordinal);

            var json = File.ReadAllText(BackupFile);
            var data = JsonSerializer.Deserialize<Dictionary<string, List<BackupWrite>>>(json);

            return data == null
                ? new Dictionary<string, List<BackupWrite>>(StringComparer.Ordinal)
                : new Dictionary<string, List<BackupWrite>>(data, StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, List<BackupWrite>>(StringComparer.Ordinal);
        }
    }

    private static void SaveBackup(Dictionary<string, List<BackupWrite>> data)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(BackupFile,
                JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 备份失败不影响本次写入，只是少一层保险 */ }
    }

    /// <summary>还有多少条改动可以还原。</summary>
    public static int BackupCount => LoadBackup().Count;

    public static IReadOnlyList<string> BackupIds => LoadBackup().Keys.ToList();

    /// <summary>把备份过原值的条目全部写回，然后清空备份。</summary>
    public static (bool Ok, string Message) RestoreAll()
    {
        var backup = LoadBackup();
        if (backup.Count == 0) return (false, "没有可还原的改动（备份记录为空）。");

        var restored = 0;
        var failed = new List<string>();

        foreach (var (id, writes) in backup)
        {
            try
            {
                foreach (var write in writes)
                {
                    if (write.Hive == "SERVICE")
                    {
                        if (int.TryParse(write.Value, out var start))
                            ServiceInventoryService.SetStartType(write.KeyPath, start);
                        continue;
                    }

                    RestoreRegistryValue(write);
                }

                restored++;
            }
            catch (Exception ex)
            {
                failed.Add($"{id}：{ex.Message}");
            }
        }

        if (failed.Count == 0)
        {
            SaveBackup(new Dictionary<string, List<BackupWrite>>(StringComparer.Ordinal));
            return (true, $"已还原 {restored} 项改动，备份记录已清空。部分改动需要重启资源管理器或重启系统才生效。");
        }

        return (false, $"还原了 {restored} 项，{failed.Count} 项失败：\n" +
                       string.Join("\n", failed.Take(6)));
    }

    /// <summary>只还原某一条。</summary>
    public static (bool Ok, string Message) RestoreOne(string id)
    {
        var backup = LoadBackup();
        if (!backup.TryGetValue(id, out var writes)) return (false, "这一项没有备份记录（可能是外部改的）。");

        try
        {
            foreach (var write in writes)
            {
                if (write.Hive == "SERVICE")
                {
                    if (int.TryParse(write.Value, out var start))
                        ServiceInventoryService.SetStartType(write.KeyPath, start);
                    continue;
                }

                RestoreRegistryValue(write);
            }

            backup.Remove(id);
            SaveBackup(backup);

            return (true, "已还原这一项。");
        }
        catch (Exception ex)
        {
            return (false, "还原失败：" + ex.Message);
        }
    }

    private static void RestoreRegistryValue(BackupWrite write)
    {
        var hive = Enum.TryParse<RegistryHive>(write.Hive, out var parsed) ? parsed : RegistryHive.CurrentUser;
        var kind = Enum.TryParse<RegistryValueKind>(write.Kind, out var k) ? k : RegistryValueKind.DWord;

        using var key = OpenBase(hive).CreateSubKey(write.KeyPath, writable: true);
        if (key == null) return;

        if (!write.Existed)
        {
            key.DeleteValue(write.ValueName, throwOnMissingValue: false);
            return;
        }

        var value = ParseStored(write.Value, kind);
        if (value != null) key.SetValue(write.ValueName, value, kind);
    }

    // ---------------------------------------------------------------- 导出还原脚本

    /// <summary>
    /// 导出一份「还原到现在这个状态」的 .reg + .cmd。
    ///
    /// 为什么还要这个：备份文件在 %LocalAppData%，重装系统或换用户账户就丢了。
    /// .reg 可以拷走，双击导入即可，不依赖本程序。
    /// </summary>
    public static (string RegPath, string CmdPath) ExportRestoreScript()
    {
        Directory.CreateDirectory(AppPaths.OutputDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        var regPath = Path.Combine(AppPaths.OutputDir, $"调优-还原脚本-{stamp}.reg");
        var cmdPath = Path.Combine(AppPaths.OutputDir, $"调优-还原脚本-{stamp}.cmd");

        var reg = new StringBuilder();
        reg.AppendLine("Windows Registry Editor Version 5.00");
        reg.AppendLine();

        var cmd = new StringBuilder();
        cmd.AppendLine("@echo off");
        cmd.AppendLine("chcp 65001 >nul");
        cmd.AppendLine("rem 破烂优化 —— 把调优项改回当前状态的脚本（本机导出）");
        cmd.AppendLine();

        foreach (var item in TweakCatalog.All)
        {
            foreach (var write in item.Writes)
            {
                var actual = ReadValue(write, out var error);
                if (error != null) continue;

                reg.AppendLine($"; {item.Title} → {write.Display}");
                reg.AppendLine(RegLine(write, actual));
                reg.AppendLine();
            }

            if (item.Kind == TweakKind.Service)
            {
                var entry = Services()
                    .FirstOrDefault(s => s.Name.Equals(item.ServiceName, StringComparison.OrdinalIgnoreCase));

                if (entry != null)
                    cmd.AppendLine($"sc config {item.ServiceName} start= {StartWord(entry.StartRaw)}");
            }
        }

        cmd.AppendLine();
        cmd.AppendLine("echo 还原命令已执行。部分改动需要重启资源管理器或重启系统才生效。");
        cmd.AppendLine("pause");

        File.WriteAllText(regPath, reg.ToString(), new UTF8Encoding(true));
        File.WriteAllText(cmdPath, cmd.ToString(), new UTF8Encoding(false));

        return (regPath, cmdPath);
    }

    private static string StartWord(int start) => start switch
    {
        0 or 1 => "boot",
        2 => "auto",
        3 => "demand",
        4 => "disabled",
        _ => "demand"
    };

    /// <summary>.reg 文件里的根键必须是全名（HKEY_CURRENT_USER），写 HKCU 导入会报错。</summary>
    private static string FullHive(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        RegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        RegistryHive.Users => "HKEY_USERS",
        RegistryHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        _ => "HKEY_CURRENT_USER"
    };

    private static string RegLine(TweakWrite write, ActualValue actual)
    {
        var path = $@"{FullHive(write.Hive)}\{write.KeyPath}";
        var name = write.ValueName.Length > 0 ? $"\"{write.ValueName}\"" : "@";

        if (!actual.Existed)
            return $"[{path}]\n{name}=-";

        return actual.ValueKind switch
        {
            RegistryValueKind.DWord => $"[{path}]\n{name}=dword:{unchecked((uint)Convert.ToInt32(actual.Value, CultureInfo.InvariantCulture)):x8}",
            RegistryValueKind.Binary => $"[{path}]\n{name}=hex:{BinaryHex(actual.Value as byte[] ?? Array.Empty<byte>())}",
            _ => $"[{path}]\n{name}=\"{EscapeReg(actual.Value as string ?? "")}\""
        };
    }

    private static string BinaryHex(byte[] bytes)
        => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    private static string EscapeReg(string text)
        => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ---------------------------------------------------------------- 注册表底层

    private static RegistryKey OpenBase(RegistryHive hive) => hive switch
    {
        RegistryHive.LocalMachine => Registry.LocalMachine,
        RegistryHive.Users => Registry.Users,
        RegistryHive.ClassesRoot => Registry.ClassesRoot,
        _ => Registry.CurrentUser
    };

    private sealed class ActualValue
    {
        public bool Existed { get; init; }
        public object Value { get; init; }
        public RegistryValueKind ValueKind { get; init; } = RegistryValueKind.DWord;

        public static ActualValue Missing => new() { Existed = false };
    }

    private static ActualValue ReadValue(TweakWrite write, out string error)
    {
        error = null;

        try
        {
            using var key = OpenBase(write.Hive).OpenSubKey(write.KeyPath);
            if (key == null) return ActualValue.Missing;

            var names = key.GetValueNames();
            if (!names.Any(n => string.Equals(n, write.ValueName, StringComparison.OrdinalIgnoreCase)))
                return ActualValue.Missing;

            var value = key.GetValue(write.ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value == null) return ActualValue.Missing;

            return new ActualValue
            {
                Existed = true,
                Value = value,
                ValueKind = key.GetValueKind(write.ValueName)
            };
        }
        catch (Exception ex)
        {
            error = ex switch
            {
                UnauthorizedAccessException or System.Security.SecurityException
                    => "读不到（需要管理员：这个键在别的账户下）",
                _ => "读不到（" + ex.Message + "）"
            };
            return ActualValue.Missing;
        }
    }

    private static void WriteValue(TweakWrite write, bool on)
    {
        using var key = OpenBase(write.Hive).CreateSubKey(write.KeyPath, writable: true);
        if (key == null) throw new InvalidOperationException("无法打开注册表键：" + write.KeyPath);

        if (!on && write.DeleteWhenOff)
        {
            key.DeleteValue(write.ValueName, throwOnMissingValue: false);
            return;
        }

        var raw = on ? write.OnValue : write.OffValue;

        // @CurrentVersion 是延迟求值的占位符（锁定大版本要用到）
        if (raw == "@CurrentVersion")
        {
            var version = TweakCatalog.CurrentReleaseVersion();
            if (version.Length == 0) throw new InvalidOperationException("读不到当前系统版本号，无法写入目标版本。");
            raw = version;
        }

        var parsed = ParseStored(raw, write.Kind);

        if (parsed == null)
        {
            key.DeleteValue(write.ValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(write.ValueName, parsed, write.Kind);
    }

    private static object ParseStored(string text, RegistryValueKind kind)
    {
        switch (kind)
        {
            case RegistryValueKind.DWord:
            {
                if (!int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
                    return null;

                return number;
            }

            case RegistryValueKind.QWord:
            {
                if (!long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var big))
                    return null;

                return big;
            }

            case RegistryValueKind.Binary:
            {
                var cleaned = text.Replace(" ", "").Replace("-", "").Replace(",", "");
                if (cleaned.Length % 2 != 0) return null;

                var bytes = new byte[cleaned.Length / 2];
                for (var i = 0; i < bytes.Length; i++)
                {
                    if (!byte.TryParse(cleaned.Substring(i * 2, 2), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out var b))
                        return null;

                    bytes[i] = b;
                }

                return bytes;
            }

            default:
                return text;
        }
    }

    /// <summary>把实际值转成「可以比较、也可以写进备份」的字符串。</summary>
    private static string Format(ActualValue actual)
    {
        if (!actual.Existed) return "";

        return actual.Value switch
        {
            int number => number.ToString(CultureInfo.InvariantCulture),
            long big => big.ToString(CultureInfo.InvariantCulture),
            byte[] bytes => string.Join(" ", bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture))),
            string[] many => string.Join("|", many),
            var other => other?.ToString() ?? ""
        };
    }

    private static bool Same(ActualValue actual, TweakWrite write, bool on)
    {
        var expectedText = on ? write.OnValue : write.OffValue;

        if (expectedText == "@CurrentVersion")
            expectedText = TweakCatalog.CurrentReleaseVersion();

        var expected = ParseStored(expectedText, write.Kind);
        if (expected == null) return false;

        return write.Kind switch
        {
            RegistryValueKind.DWord => actual.Value is int a && expected is int b && a == b,
            RegistryValueKind.QWord => actual.Value is long la && expected is long lb && la == lb,
            RegistryValueKind.Binary => actual.Value is byte[] ba && expected is byte[] bb && ba.SequenceEqual(bb),
            _ => string.Equals(actual.Value as string ?? "", expected as string ?? "", StringComparison.OrdinalIgnoreCase)
        };
    }

    // ---------------------------------------------------------------- 命令执行

    private static (int Code, string Output) RunCommand(string command)
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = System.Diagnostics.Process.Start(info);
            if (process == null) return (-1, "无法启动命令进程。");

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(120_000);

            return (process.HasExited ? process.ExitCode : -1, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string Trim(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .ToList();

        if (lines.Count > 6) lines = lines.Take(6).ToList();

        var joined = string.Join("\n", lines);
        return joined.Length > 600 ? joined[..600] + "…" : joined;
    }
}
