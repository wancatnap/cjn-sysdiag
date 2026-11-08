using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// Windows 日志记录开关：事件日志通道、Prefetch（程序运行痕迹）、最近使用记录。
/// 读取不需要权限；写入需要管理员（「最近使用」除外，它在 HKCU）。
/// </summary>
public static class LoggingService
{
    public const string KeySystem = "system";
    public const string KeyApp = "application";
    public const string KeyPrefetch = "prefetch";
    public const string KeyRecent = "recent";

    // ---- 下面 5 项参照 Yamato-Security/EnableWindowsLogSettings 的取证清单补的 ----
    // 原来那 4 项只解决「日志在不在」，这 5 项解决「关键行为有没有被记下来」。
    public const string KeyProcessAudit = "procaudit";
    public const string KeyPowerShell = "pslog";
    public const string KeyTaskSched = "tasksched";
    public const string KeyRdp = "rdp";
    public const string KeyUsb = "usb";

    /// <summary>界面顺序 + 提权参数顺序。前 4 项保持原顺序，「--apply-logging 1,1,1,1」仍然向后兼容。</summary>
    public static readonly string[] AllKeys =
    {
        KeySystem, KeyApp, KeyPrefetch, KeyRecent,
        KeyProcessAudit, KeyPowerShell, KeyTaskSched, KeyRdp, KeyUsb
    };

    /// <summary>「进程创建」审计子类别 GUID：用 GUID 避免中英文系统子类别名不同。</summary>
    private const string AuditProcessCreation = "{0CCE922B-69AE-11D9-BED3-505054503030}";

    private const string AuditPolicyCmdLine = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System\Audit";
    private const string PsScriptBlock = @"SOFTWARE\Policies\Microsoft\Windows\PowerShell\ScriptBlockLogging";

    private const string TaskSchedChannel = "Microsoft-Windows-TaskScheduler/Operational";
    private const string RdpChannel = "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational";
    private const string UsbChannel = "Microsoft-Windows-DriverFrameworks-UserMode/Operational";

    private const string PrefetchPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";

    private const string ChannelsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\Channels";
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string ExplorerPolicy = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";

    public static string ResultFile => Path.Combine(AppPaths.OutputDir, "logging-apply.txt");

    public static bool IsAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    // ==================== 读取状态 ====================

    /// <summary>
    /// 取证日志建议清单（只读）。
    ///
    /// 上半部分的 9 个开关解决「这项日志开没开」，这一份解决「开了但留不住 / 通道本身没开」：
    /// 默认 20 MB 的安全日志，遇到反复试错的外挂进程，几小时就能把最早的记录挤掉。
    /// 容量类的检查来自 Yamato Security 的 EnableWindowsLogSettings 里被点名的几项。
    /// </summary>
    public static List<LoggingAdvice> ReadAdvice()
    {
        var list = new List<LoggingAdvice>();

        foreach (var (service, label, recommend) in new[]
                 {
                     ("Security", "安全日志容量", 192),
                     ("System", "系统日志容量", 64),
                     ("Application", "应用日志容量", 64)
                 })
        {
            var mb = ReadMaxSizeMb(service);
            var ok = mb >= recommend;

            list.Add(new LoggingAdvice
            {
                Name = label,
                Category = "容量",
                Current = mb > 0 ? $"{mb} MB" : "读不到",
                Advice = $"建议 ≥ {recommend} MB。默认 20 MB，密集报错时几小时就会把最早的记录挤出去，而那些记录往往最关键。",
                HowTo = ok ? "已满足" : $"wevtutil sl {service} /ms:{recommend * 1024 * 1024}（需管理员）",
                Level = ok ? "ok" : "hint"
            });
        }

        // Sysmon：比系统自带日志细得多的进程/网络/驱动事件源
        var sysmon = ServiceExists("Sysmon64") || ServiceExists("Sysmon");
        list.Add(new LoggingAdvice
        {
            Name = "Sysmon",
            Category = "组件",
            Current = sysmon ? "已安装" : "未安装",
            Advice = "可选的进阶项：能记录进程创建的命令行、网络连接、驱动加载。不装也能用本工具的全部功能。",
            HowTo = sysmon ? "已满足" : "从微软 Sysinternals 下载 Sysmon 并按官方配置安装（需管理员）",
            Level = sysmon ? "ok" : "ok"      // 缺它不是缺陷，不标黄
        });

        foreach (var (channel, label) in new[]
                 {
                     ("Microsoft-Windows-WMI-Activity/Operational", "WMI 活动日志"),
                     ("Microsoft-Windows-Kernel-PnP/Configuration", "设备安装日志")
                 })
        {
            // Operational 通道没有 Enabled 值就是没开过，这里不能用「缺省视为已开」的宽松判断
            var enabled = ChannelEnabled(channel, ifMissing: false);

            list.Add(new LoggingAdvice
            {
                Name = label,
                Category = "通道",
                Current = enabled ? "已启用" : "未启用",
                Advice = label == "设备安装日志"
                    ? "插拔 USB、装驱动都会记在这里。关掉之后「设备安装记录」和「PnP 事件分析」会没数据。"
                    : "脚本通过 WMI 查硬件、起进程都会留记录，是查「改机脚本」的旁证。",
                HowTo = enabled ? "已满足" : $"wevtutil sl \"{channel}\" /e:true（需管理员）",
                Level = enabled ? "ok" : "hint"
            });
        }

        return list;
    }

    private static int ReadMaxSizeMb(string service)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\EventLog\{service}");
            if (key?.GetValue("MaxSize") is int bytes && bytes > 0)
                return Math.Max(1, bytes / 1024 / 1024);
        }
        catch { /* 读不到就返回 0 */ }

        return 0;
    }

    private static bool ServiceExists(string name)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static List<LoggingSwitch> ReadState()
    {
        var list = new List<LoggingSwitch>();

        var sysChannel = ChannelEnabled("System");
        var eventLogRunning = ServiceRunning("EventLog");
        list.Add(new LoggingSwitch
        {
            Key = KeySystem,
            Name = "系统日志",
            Detail = $"System 通道{(sysChannel ? "启用" : "停用")} · 事件日志服务{(eventLogRunning ? "运行中" : "已停止")}",
            Enabled = sysChannel && eventLogRunning,
            NeedsAdmin = true
        });

        var appChannel = ChannelEnabled("Application");
        list.Add(new LoggingSwitch
        {
            Key = KeyApp,
            Name = "应用程序日志",
            Detail = $"Application 通道{(appChannel ? "启用" : "停用")}",
            Enabled = appChannel,
            NeedsAdmin = true
        });

        var prefetch = ReadHklmDword(PrefetchPath, "EnablePrefetcher");
        var sysMain = ReadHklmDword(@"SYSTEM\CurrentControlSet\Services\SysMain", "Start");
        var prefetchOn = prefetch is >= 1 and <= 3;
        list.Add(new LoggingSwitch
        {
            Key = KeyPrefetch,
            Name = "程序运行痕迹（Prefetch）",
            Detail = $"EnablePrefetcher={prefetch?.ToString() ?? "(未设置)"} · SysMain 启动类型={StartTypeText(sysMain)}",
            Enabled = prefetchOn,
            NeedsAdmin = true
        });

        var trackDocs = ReadHkcuDword(ExplorerAdvanced, "Start_TrackDocs");
        var trackProgs = ReadHkcuDword(ExplorerAdvanced, "Start_TrackProgs");
        var noRecent = ReadHkcuDword(ExplorerPolicy, "NoRecentDocsHistory");
        var recentOn = (trackDocs ?? 1) == 1 && (trackProgs ?? 1) == 1 && (noRecent ?? 0) != 1;

        list.Add(new LoggingSwitch
        {
            Key = KeyRecent,
            Name = "最近使用记录",
            Detail = $"Start_TrackDocs={trackDocs?.ToString() ?? "(默认)"} · Start_TrackProgs={trackProgs?.ToString() ?? "(默认)"} · " +
                     $"NoRecentDocsHistory={noRecent?.ToString() ?? "(未设置)"}",
            Enabled = recentOn,
            NeedsAdmin = false,
            PolicyLocked = (noRecent ?? 0) == 1
        });

        // ---- 关键行为日志（参照 EnableWindowsLogSettings 的取证清单） ----

        var cmdLine = ReadHklmDword(AuditPolicyCmdLine, "ProcessCreationIncludeCmdLine_Enabled");
        list.Add(new LoggingSwitch
        {
            Key = KeyProcessAudit,
            Name = "进程创建审计（4688 + 命令行）",
            Detail = (cmdLine == 1
                    ? "已记录新进程及其完整命令行"
                    : $"ProcessCreationIncludeCmdLine_Enabled={cmdLine?.ToString() ?? "(未设置)"}") +
                     " · 审计子类别要管理员才能核对，开启时会一并设置",
            Enabled = cmdLine == 1,
            NeedsAdmin = true
        });

        var psBlock = ReadHklmDword(PsScriptBlock, "EnableScriptBlockLogging");
        list.Add(new LoggingSwitch
        {
            Key = KeyPowerShell,
            Name = "PowerShell 脚本块日志",
            Detail = psBlock == 1
                ? "已记录每个执行过的脚本块（4104）"
                : $"EnableScriptBlockLogging={psBlock?.ToString() ?? "(未设置)"} · 关闭时脚本里的实际内容不会留下痕迹",
            Enabled = psBlock == 1,
            NeedsAdmin = true
        });

        AddChannelSwitch(list, KeyTaskSched, "计划任务操作日志", TaskSchedChannel,
            "任务被创建 / 修改 / 删除 / 运行都会留事件，查「谁动过启动项」靠它");

        AddChannelSwitch(list, KeyRdp, "远程登录会话日志", RdpChannel,
            "远程桌面登录、断开、重连的会话事件，配合安全日志 4624 能还原远端来源");

        AddChannelSwitch(list, KeyUsb, "USB 设备接入日志", UsbChannel,
            "U 盘 / 移动硬盘插拔与驱动加载事件，取证时用来对「什么时候插过什么」");

        return list;
    }

    /// <summary>追加一个「事件通道」型开关。这类通道默认关闭，读不到 Enabled 值按「关闭」算。</summary>
    private static void AddChannelSwitch(List<LoggingSwitch> list, string key, string name,
        string channel, string why)
    {
        var on = ChannelEnabled(channel, ifMissing: false);
        list.Add(new LoggingSwitch
        {
            Key = key,
            Name = name,
            Detail = $"{(on ? "通道已启用" : "通道已关闭")} · {why}",
            Enabled = on,
            NeedsAdmin = true
        });
    }

    /// <summary>哪些开关需要管理员权限（供界面决定是否提权）。</summary>
    public static bool NeedsElevation(IEnumerable<LoggingSwitch> changed)
        => changed.Any(s => s.RequiresAdmin(s.Wanted));

    // ==================== 应用 ====================

    public static List<string> Apply(IReadOnlyDictionary<string, bool> desired, bool elevated)
    {
        var log = new List<string>();

        if (desired.TryGetValue(KeyRecent, out var recent))
            log.Add(ApplyRecent(recent));

        foreach (var key in new[]
                 {
                     KeySystem, KeyApp, KeyPrefetch,
                     KeyProcessAudit, KeyPowerShell, KeyTaskSched, KeyRdp, KeyUsb
                 })
        {
            if (!desired.TryGetValue(key, out var on)) continue;

            if (!elevated)
            {
                log.Add($"[跳过] {NameOf(key)}：需要管理员权限");
                continue;
            }

            try
            {
                switch (key)
                {
                    case KeySystem:
                        SetChannel("System", on, log);
                        if (on) EnsureServiceRunning("EventLog", log);
                        else log.Add("[提示] 事件日志服务本身未停用，只关闭了 System 通道");
                        break;

                    case KeyApp:
                        SetChannel("Application", on, log);
                        break;

                    case KeyPrefetch:
                        SetHklmDword(PrefetchPath, "EnablePrefetcher", on ? 3 : 0);
                        SetHklmDword(@"SYSTEM\CurrentControlSet\Services\SysMain", "Start", on ? 2 : 4);
                        if (on) EnsureServiceRunning("SysMain", log);
                        else StopService("SysMain", log);
                        log.Add($"[成功] 程序运行痕迹：{(on ? "已开启（EnablePrefetcher=3，SysMain 自动）" : "已关闭（EnablePrefetcher=0，SysMain 禁用）")}");
                        break;

                    case KeyProcessAudit:
                        SetHklmDword(AuditPolicyCmdLine, "ProcessCreationIncludeCmdLine_Enabled", on ? 1 : 0);
                        var audit = Run("auditpol.exe",
                            $"/set /subcategory:\"{AuditProcessCreation}\" /{(on ? "success:enable" : "success:disable")}");
                        log.Add($"[成功] 进程创建审计：{(on ? "已开启（含命令行）" : "已关闭")}" +
                                (string.IsNullOrWhiteSpace(audit) ? "" : $"　auditpol：{OneLine(audit)}"));
                        break;

                    case KeyPowerShell:
                        SetHklmDword(PsScriptBlock, "EnableScriptBlockLogging", on ? 1 : 0);
                        log.Add($"[成功] PowerShell 脚本块日志：{(on ? "已开启（4104）" : "已关闭")}（新开的进程生效）");
                        break;

                    case KeyTaskSched:
                        SetChannel(TaskSchedChannel, on, log);
                        break;

                    case KeyRdp:
                        SetChannel(RdpChannel, on, log);
                        break;

                    case KeyUsb:
                        SetChannel(UsbChannel, on, log);
                        break;
                }
            }
            catch (Exception ex)
            {
                log.Add($"[失败] {NameOf(key)}：{ex.Message}");
            }
        }

        return log;
    }

    /// <summary>命令行入口：--apply-logging 1,1,1,1（顺序：系统 / 应用 / Prefetch / 最近使用）。</summary>
    public static string ApplyFromFlags(string flags)
    {
        var desired = ParseFlags(flags);
        var log = Apply(desired, IsAdmin());

        var text = new StringBuilder();
        text.AppendLine($"应用日志开关   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"管理员权限：{(IsAdmin() ? "是" : "否")}");
        text.AppendLine(new string('-', 46));
        foreach (var line in log) text.AppendLine(line);
        if (log.Count == 0) text.AppendLine("没有需要更改的项目。");

        try
        {
            AppPaths.Ensure();
            File.WriteAllText(ResultFile, text.ToString(), new UTF8Encoding(true));
        }
        catch { }

        return text.ToString();
    }

    /// <summary>把 "1,0,1,1" 解析成字典，缺省的项不出现（表示不动）。</summary>
    public static Dictionary<string, bool> ParseFlags(string flags)
    {
        var parts = (flags ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var desired = new Dictionary<string, bool>();
        var keys = AllKeys;

        for (var i = 0; i < keys.Length && i < parts.Length; i++)
            desired[keys[i]] = parts[i] is "1" or "true" or "on";

        return desired;
    }

    // ==================== 具体动作 ====================

    /// <summary>「最近使用」分两步写：HKCU Advanced 总能写；Policies 键受保护，写不进去只提示。</summary>
    private static string ApplyRecent(bool on)
    {
        try
        {
            SetHkcuDword(ExplorerAdvanced, "Start_TrackDocs", on ? 1 : 0);
            SetHkcuDword(ExplorerAdvanced, "Start_TrackProgs", on ? 1 : 0);
        }
        catch (Exception ex)
        {
            return $"[失败] 最近使用记录：{ex.Message}";
        }

        var note = on ? "已开启（Start_TrackDocs / Start_TrackProgs = 1）"
                      : "已关闭（Start_TrackDocs / Start_TrackProgs = 0）";

        try
        {
            SetPolicyNoRecent(!on);
            note += on ? "，策略已清除" : "，策略 NoRecentDocsHistory=1";
        }
        catch
        {
            if (on)
                note += "；策略键受保护，未能清除（需要管理员）。若仍看不到最近文件，请用管理员身份再应用一次";
        }

        return $"[成功] 最近使用记录：{note}（重新登录后对资源管理器生效）";
    }

    private static void SetChannel(string channel, bool enable, List<string> log)
    {
        var err = SetChannelNative(channel, enable);
        if (string.IsNullOrWhiteSpace(err))
        {
            log.Add($"[成功] {channel} 通道：{(enable ? "已启用" : "已停用")}");
            return;
        }

        // 兜底：native 调用不通时再用 wevtutil
        var fallback = Run("wevtutil.exe", $"sl {channel} /e:{(enable ? "true" : "false")}");
        if (string.IsNullOrWhiteSpace(fallback))
            log.Add($"[成功] {channel} 通道：{(enable ? "已启用" : "已停用")}（wevtutil）");
        else
            log.Add($"[失败] {channel} 通道：{err} / {fallback.Trim()}");
    }

    private static void EnsureServiceRunning(string name, List<string> log)
    {
        if (ServiceRunning(name)) return;

        var code = ServiceAction(name, "StartService");
        log.Add(ServiceRunning(name)
            ? $"[成功] 服务 {name}：已启动"
            : $"[警告] 服务 {name}：启动未成功（返回 {code}），可能需要重启");
    }

    private static void StopService(string name, List<string> log)
    {
        if (!ServiceRunning(name)) return;
        var code = ServiceAction(name, "StopService");
        if (code != 0) log.Add($"[警告] 服务 {name}：停止返回 {code}");
    }

    /// <summary>通过 WMI 控制服务，避免依赖 sc.exe（部分环境会拦截外部命令）。</summary>
    private static uint ServiceAction(string name, string method)
    {
        try
        {
            using var svc = new ManagementObject(new ManagementPath($"Win32_Service.Name='{name}'"));
            using var result = svc.InvokeMethod(method, null, null);
            return result == null ? 1u : Convert.ToUInt32(result["ReturnValue"] ?? 1u);
        }
        catch
        {
            return 1u;
        }
    }

    private static bool ChannelEnabled(string channel) => ChannelEnabled(channel, ifMissing: true);

    /// <summary>通道是否启用。默认开启的通道（System / Application）读不到值时按 true 算，默认关闭的按 false 算。</summary>
    private static bool ChannelEnabled(string channel, bool ifMissing)
    {
        var v = ReadHklmDword($@"{ChannelsPath}\{channel}", "Enabled");
        return v == null ? ifMissing : v != 0;
    }

    private static bool ServiceRunning(string name)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT State FROM Win32_Service WHERE Name='{name}'");
            foreach (var o in searcher.Get())
            {
                using var mo = (ManagementObject)o;
                return string.Equals(mo["State"]?.ToString(), "Running", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch { }
        return false;
    }

    // ---- 通道开关：直接调 wevtapi，不依赖 wevtutil.exe ----

    private const int EvtChannelConfigEnabled = 1;
    private const int VtBool = 11;

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct EvtVariant
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public short BoolVal;
    }

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtOpenChannelConfig(IntPtr session, string channelPath, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtSetChannelConfigProperty(IntPtr channelConfig, int propertyId, int flags, ref EvtVariant value);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtSaveChannelConfig(IntPtr channelConfig, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtClose(IntPtr objectHandle);

    private static string SetChannelNative(string channel, bool enable)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = EvtOpenChannelConfig(IntPtr.Zero, channel, 0);
            if (handle == IntPtr.Zero) return "打开通道配置失败：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

            var value = new EvtVariant { Type = VtBool, BoolVal = (short)(enable ? -1 : 0) };
            if (!EvtSetChannelConfigProperty(handle, EvtChannelConfigEnabled, 0, ref value))
                return "设置通道属性失败：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

            if (!EvtSaveChannelConfig(handle, 0))
                return "保存通道配置失败：" + new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

            return "";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            if (handle != IntPtr.Zero) EvtClose(handle);
        }
    }

    private static string StartTypeText(int? start) => start switch
    {
        0 or 1 => "驱动",
        2 => "自动",
        3 => "手动",
        4 => "已禁用",
        _ => "(未设置)"
    };

    private static string NameOf(string key) => key switch
    {
        KeySystem => "系统日志",
        KeyApp => "应用程序日志",
        KeyPrefetch => "程序运行痕迹",
        KeyRecent => "最近使用记录",
        KeyProcessAudit => "进程创建审计",
        KeyPowerShell => "PowerShell 脚本块日志",
        KeyTaskSched => "计划任务操作日志",
        KeyRdp => "远程登录会话日志",
        KeyUsb => "USB 设备接入日志",
        _ => key
    };

    /// <summary>把命令输出压成一行，避免把 auditpol 的多行结果塞进对话框。</summary>
    private static string OneLine(string text)
        => string.Join(" ", (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()));

    private static string Run(string exe, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return (stdout + "\n" + stderr).Trim();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static int? ReadHklmDword(string path, string name)
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(path);
            var v = k?.GetValue(name);
            return v == null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    private static int? ReadHkcuDword(string path, string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(path);
            var v = k?.GetValue(name);
            return v == null ? null : Convert.ToInt32(v);
        }
        catch { return null; }
    }

    private static void SetHklmDword(string path, string name, int value)
    {
        using var k = Registry.LocalMachine.OpenSubKey(path, writable: true)
                      ?? throw new UnauthorizedAccessException($"无法打开注册表 {path}");
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void SetHkcuDword(string path, string name, int value)
    {
        using var k = Registry.CurrentUser.CreateSubKey(path, writable: true)
                      ?? throw new UnauthorizedAccessException($"无法写入注册表 {path}");
        k.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void SetPolicyNoRecent(bool noRecent)
    {
        using var k = Registry.CurrentUser.CreateSubKey(ExplorerPolicy, writable: true);
        if (k == null) return;
        if (noRecent) k.SetValue("NoRecentDocsHistory", 1, RegistryValueKind.DWord);
        else k.DeleteValue("NoRecentDocsHistory", false);
    }
}
