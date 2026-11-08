using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SysDiag.Models;

namespace SysDiag.Services;

/// <summary>
/// 服务清单与启停控制：直接调 advapi32 的服务控制接口。
///
/// 没用 <c>System.ServiceProcess.ServiceController</c>：那个包在 .NET 8 的
/// WPF 共享框架里不随附，为一个"读状态"再引一个 NuGet 不划算；
/// 也没用 <c>sc.exe</c>：精简镜像里 System32 的 exe 经常缺，而且解析文本输出更脆。
/// 直接 P/Invoke 还能一次问出所有服务的状态（EnumServicesStatusEx），比逐个查快得多。
/// </summary>
public static class ServiceInventoryService
{
    // ---------------- 原生声明 ----------------

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SC_MANAGER_ENUMERATE = 0x0004;

    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_START = 0x0010;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;

    private const uint SERVICE_WIN32 = 0x00000030;
    private const uint SERVICE_STATE_ALL = 0x00000003;

    private const uint SERVICE_PAUSE_CONTINUE = 0x0040;

    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_CONTROL_PAUSE = 0x00000002;
    private const uint SERVICE_CONTROL_CONTINUE = 0x00000003;

    private const uint SERVICE_CONFIG_FAILURE_ACTIONS = 2;
    private const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

    /// <summary>SC_ACTION_TYPE：0 无动作 / 1 重启服务 / 2 重启计算机 / 3 运行命令。</summary>
    private const uint SC_ACTION_RESTART = 1;

    private const int SC_ENUM_PROCESS_INFO = 0;
    private const uint ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EnumServiceStatusProcess
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    /// <summary>QueryServiceConfig2(SERVICE_CONFIG_FAILURE_ACTIONS) 的返回结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public IntPtr RebootMsg;
        public IntPtr Command;
        public uint ActionsCount;

        /// <summary>指向紧随结构之后的 SC_ACTION 数组（在查询返回的缓冲区内）。</summary>
        public IntPtr Actions;
    }

    /// <summary>一条失败动作：类型 + 延迟毫秒。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public uint Type;
        public uint Delay;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool EnumServicesStatusEx(IntPtr scManager, int infoLevel, uint serviceType,
        uint serviceState, IntPtr buffer, int bufferSize, out int bytesNeeded, out int servicesReturned,
        ref int resumeHandle, string groupName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartService(IntPtr service, int argCount, string[] arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool ControlService(IntPtr service, uint control, ref ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string loadOrderGroup, IntPtr tagId, string dependencies,
        string accountName, string password, string displayName);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer,
        int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryServiceConfig2(IntPtr service, uint infoLevel, IntPtr buffer,
        int bufferSize, out int bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, IntPtr info);

    // ---------------- 采集 ----------------

    /// <summary>采集全部 Win32 服务（不含内核驱动，驱动在「设备管理器」页看）。</summary>
    public static List<ServiceEntry> Scan()
    {
        var states = ReadStates();

        var list = new List<ServiceEntry>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (root == null) return list;

        foreach (var name in root.GetSubKeyNames())
        {
            try
            {
                using var key = root.OpenSubKey(name);
                if (key == null) continue;

                // 只要 Win32 服务（0x10 独立进程 / 0x20 共享进程）；内核驱动另有页面
                if (key.GetValue("Type") is not int type || (type & SERVICE_WIN32) == 0) continue;

                var start = key.GetValue("Start") is int s ? s : 3;
                var delayed = key.GetValue("DelayedAutostart") is int d && d == 1;

                // 注册表里有、SCM 枚举里没有的，基本都是「按用户」服务的模板
                // （cbdhsvc / OneSyncSvc 这类，登录后才按账户实例化成 cbdhsvc_xxxxx）。
                // 标成「按用户启动」而不是「状态未知」，否则一眼看过去像有 20 多个服务坏了。
                var found = states.TryGetValue(name, out var state);

                list.Add(new ServiceEntry
                {
                    Name = name,
                    DisplayName = NonEmpty(Resolve(Value(key, "DisplayName")), name),
                    Description = Resolve(Value(key, "Description")),
                    State = found ? state.State : "Template",
                    StartRaw = start,
                    DelayedAutoStart = delayed,
                    StartTypeText = StartTypeTextOf(start, delayed),
                    Account = Value(key, "ObjectName") ?? "",
                    ImagePath = Value(key, "ImagePath") ?? "",
                    ProcessId = found ? state.Pid : 0,
                    Dependencies = Value(key, "DependOnService") ?? "",
                    CanStop = found && state.CanStop
                });
            }
            catch
            {
                // 单个键读不到不影响整张表
            }
        }

        return list
            .OrderByDescending(x => x.State == "Running")
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string NonEmpty(string value, string fallback)
        => value.Length > 0 ? value : fallback;

    private static string Value(RegistryKey key, string name)
    {
        try
        {
            return key.GetValue(name) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// 显示名与描述常写成资源引用（@xxx.dll,-123 / @oemXX.inf,%key%），
    /// 直接显示就是一串 @oem36.inf。统一交给 MuiString 解引用，
    /// 解析不出来时退回服务名——总比把资源串摆在界面上强。
    /// </summary>
    private static string Resolve(string raw) => MuiString.Resolve(raw);

    public static string StartTypeTextOf(int start, bool delayed) => start switch
    {
        0 => "引导启动",
        1 => "系统启动",
        2 => delayed ? "自动（延迟）" : "自动",
        3 => "手动",
        4 => "禁用",
        _ => $"未知（{start}）"
    };

    /// <summary>一次枚举拿到所有服务的状态与进程号。</summary>
    private static Dictionary<string, (string State, int Pid, bool CanStop)> ReadStates()
    {
        var map = new Dictionary<string, (string, int, bool)>(StringComparer.OrdinalIgnoreCase);

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT | SC_MANAGER_ENUMERATE);
        if (scm == IntPtr.Zero) return map;

        try
        {
            var size = 64 * 1024;
            var buffer = Marshal.AllocHGlobal(size);
            var resume = 0;

            try
            {
                while (true)
                {
                    var ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                        buffer, size, out var needed, out var count, ref resume, null);

                    if (!ok)
                    {
                        var error = Marshal.GetLastWin32Error();

                        if (error == ERROR_MORE_DATA && needed > size)
                        {
                            size = needed + 8192;
                            buffer = Marshal.ReAllocHGlobal(buffer, (IntPtr)size);
                            continue;
                        }

                        break;
                    }

                    var itemSize = Marshal.SizeOf<EnumServiceStatusProcess>();
                    for (var i = 0; i < count; i++)
                    {
                        var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + i * itemSize);
                        var name = Marshal.PtrToStringUni(item.ServiceName) ?? "";
                        if (name.Length == 0) continue;

                        map[name] = (
                            StateText(item.Status.CurrentState),
                            (int)item.Status.ProcessId,
                            (item.Status.ControlsAccepted & SERVICE_CONTROL_STOP) != 0);
                    }

                    if (resume == 0) break;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }

        return map;
    }

    private static string StateText(uint state) => state switch
    {
        1 => "Stopped",
        2 => "StartPending",
        3 => "StopPending",
        4 => "Running",
        5 => "ContinuePending",
        6 => "PausePending",
        7 => "Paused",
        _ => "Unknown"
    };

    // ---------------- 控制 ----------------

    /// <summary>启动服务并等到 Running（最多等 10 秒）。</summary>
    public static (bool Ok, string Message) Start(string name)
    {
        if (!ElevationService.IsAdmin)
            return (false, "启动服务需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "无法连接服务控制管理器（错误 " + Marshal.GetLastWin32Error() + "）");

        var service = IntPtr.Zero;
        try
        {
            service = OpenService(scm, name, SERVICE_START | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return (false, $"打开服务 {name} 失败（错误 {Marshal.GetLastWin32Error()}）");

            // 已经在跑就别再发启动命令
            if (QueryState(service) == 4)
                return (true, $"{name} 已经在运行。");

            if (!StartService(service, 0, null))
            {
                var error = Marshal.GetLastWin32Error();
                return (false, error == 1056
                    ? $"{name} 已在运行。"
                    : $"启动 {name} 失败：{ErrorMessage(error)}");
            }

            return WaitForState(service, 4, name);
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    /// <summary>停止服务并等到 Stopped。</summary>
    public static (bool Ok, string Message) Stop(string name)
    {
        if (!ElevationService.IsAdmin)
            return (false, "停止服务需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "无法连接服务控制管理器（错误 " + Marshal.GetLastWin32Error() + "）");

        var service = IntPtr.Zero;
        try
        {
            service = OpenService(scm, name, SERVICE_STOP | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return (false, $"打开服务 {name} 失败（错误 {Marshal.GetLastWin32Error()}）");

            if (QueryState(service) == 1)
                return (true, $"{name} 已经是停止状态。");

            var status = new ServiceStatus { ServiceType = SERVICE_WIN32 };

            if (!ControlService(service, SERVICE_CONTROL_STOP, ref status))
            {
                var error = Marshal.GetLastWin32Error();
                return (false, error == 1061
                    ? $"{name} 当前无法停止（可能正在启动或不允许停止）。"
                    : $"停止 {name} 失败：{ErrorMessage(error)}");
            }

            return WaitForState(service, 1, name);
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    /// <summary>改启动类型。startRaw：2 自动 / 3 手动 / 4 禁用。</summary>
    public static (bool Ok, string Message) SetStartType(string name, int startRaw)
    {
        if (!ElevationService.IsAdmin)
            return (false, "修改启动类型需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "无法连接服务控制管理器（错误 " + Marshal.GetLastWin32Error() + "）");

        var service = IntPtr.Zero;
        try
        {
            service = OpenService(scm, name, SERVICE_CHANGE_CONFIG);
            if (service == IntPtr.Zero)
                return (false, $"打开服务 {name} 失败（错误 {Marshal.GetLastWin32Error()}）");

            const uint noChange = 0xFFFFFFFF;
            const uint noErrorControl = 0xFFFFFFFF;

            if (!ChangeServiceConfig(service, noChange, (uint)startRaw, noErrorControl,
                    null, null, IntPtr.Zero, null, null, null, null))
            {
                return (false, $"修改 {name} 的启动类型失败：{ErrorMessage(Marshal.GetLastWin32Error())}");
            }

            return (true, $"{name} 的启动类型已改为「{StartTypeTextOf(startRaw, false)}」。");
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    /// <summary>暂停服务（只有支持暂停的服务才能暂停，例如打印机队列）。</summary>
    public static (bool Ok, string Message) Pause(string name)
        => Control(name, SERVICE_CONTROL_PAUSE, 7, "暂停", "暂停");

    /// <summary>继续被暂停的服务。</summary>
    public static (bool Ok, string Message) Continue(string name)
        => Control(name, SERVICE_CONTROL_CONTINUE, 4, "继续", "继续");

    private static (bool Ok, string Message) Control(string name, uint control, uint targetState,
        string verb, string targetLabel)
    {
        if (!ElevationService.IsAdmin)
            return (false, $"{verb}服务需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "无法连接服务控制管理器（错误 " + Marshal.GetLastWin32Error() + "）");

        var service = IntPtr.Zero;
        try
        {
            service = OpenService(scm, name, SERVICE_PAUSE_CONTINUE | SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return (false, $"打开服务 {name} 失败（错误 {Marshal.GetLastWin32Error()}）");

            var status = new ServiceStatus { ServiceType = SERVICE_WIN32 };

            if (!ControlService(service, control, ref status))
            {
                var error = Marshal.GetLastWin32Error();
                return (false, error == 1052
                    ? $"{name} 不支持{verb}（服务没有注册暂停/继续控制）。"
                    : $"{verb} {name} 失败：{ErrorMessage(error)}");
            }

            return WaitForState(service, targetState, name);
        }
        finally
        {
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    /// <summary>设置「延迟启动」：只对自动启动的服务有意义。</summary>
    public static (bool Ok, string Message) SetDelayedAutoStart(string name, bool delayed)
    {
        if (!ElevationService.IsAdmin)
            return (false, "修改启动方式需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "无法连接服务控制管理器（错误 " + Marshal.GetLastWin32Error() + "）");

        var service = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            service = OpenService(scm, name, SERVICE_CHANGE_CONFIG);
            if (service == IntPtr.Zero)
                return (false, $"打开服务 {name} 失败（错误 {Marshal.GetLastWin32Error()}）");

            // SERVICE_DELAYED_AUTO_START_INFO 就是一个 BOOL
            buffer = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(buffer, delayed ? 1 : 0);

            if (!ChangeServiceConfig2(service, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, buffer))
                return (false, $"设置 {name} 的延迟启动失败：{ErrorMessage(Marshal.GetLastWin32Error())}");

            return (true, delayed ? $"{name} 已设为「自动（延迟）启动」。" : $"{name} 已取消延迟启动。");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    /// <summary>读恢复策略：失败几次后是否自动重启、间隔多少。</summary>
    public static (bool Configured, string Summary) ReadFailureActions(string name)
    {
        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) return (false, "读取失败（无法连接服务控制管理器）");

        var service = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            service = OpenService(scm, name, 0x0001);   // SERVICE_QUERY_CONFIG
            if (service == IntPtr.Zero) return (false, "读取失败（权限不足）");

            const int size = 8192;
            buffer = Marshal.AllocHGlobal(size);

            if (!QueryServiceConfig2(service, SERVICE_CONFIG_FAILURE_ACTIONS, buffer, size, out _))
                return (false, "读取失败（" + ErrorMessage(Marshal.GetLastWin32Error()) + "）");

            var actions = Marshal.PtrToStructure<ServiceFailureActions>(buffer);

            if (actions.ActionsCount == 0 || actions.Actions == IntPtr.Zero)
                return (false, "未设置：服务失败后不会自动重启");

            var delays = new List<string>();
            var itemSize = Marshal.SizeOf<ScAction>();

            for (var i = 0; i < actions.ActionsCount; i++)
            {
                var item = Marshal.PtrToStructure<ScAction>(actions.Actions + i * itemSize);

                delays.Add(item.Type switch
                {
                    SC_ACTION_RESTART => DelayText(item.Delay),
                    2 => "重启计算机",
                    3 => "运行命令",
                    _ => "无动作"
                });
            }

            return (true, $"已设置 {delays.Count} 段：{string.Join(" / ", delays)}" +
                          (actions.ResetPeriod > 0
                              ? $"（失败计数 {actions.ResetPeriod / 3600} 小时重置）"
                              : ""));
        }
        catch (Exception ex)
        {
            return (false, "读取失败：" + ex.Message);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    private static string DelayText(uint milliseconds)
        => milliseconds == 0 ? "立即重启" : $"{milliseconds / 1000} 秒后重启";

    /// <summary>
    /// 写恢复策略。三种预设覆盖绝大多数用法：
    /// 0 = 清除（失败不重启）；1 = 1/2/5 分钟后重启；2 = 立即重启，随后 1/5 分钟。
    /// </summary>
    public static (bool Ok, string Message) SetFailureActions(string name, int preset)
    {
        if (!ElevationService.IsAdmin)
            return (false, "修改恢复策略需要管理员权限。请点右上角「以管理员身份重启」后再试。");

        uint[] delays = preset switch
        {
            1 => new uint[] { 60_000, 120_000, 300_000 },
            2 => new uint[] { 0, 60_000, 300_000 },
            _ => Array.Empty<uint>()
        };

        var scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero)
            return (false, "无法连接服务控制管理器（错误 " + Marshal.GetLastWin32Error() + "）");

        var service = IntPtr.Zero;
        var buffer = IntPtr.Zero;

        try
        {
            service = OpenService(scm, name, SERVICE_CHANGE_CONFIG);
            if (service == IntPtr.Zero)
                return (false, $"打开服务 {name} 失败（错误 {Marshal.GetLastWin32Error()}）");

            var structSize = Marshal.SizeOf<ServiceFailureActions>();
            var actionSize = Marshal.SizeOf<ScAction>();
            buffer = Marshal.AllocHGlobal(structSize + delays.Length * actionSize);

            var info = new ServiceFailureActions
            {
                ResetPeriod = 86400,          // 24 小时后失败计数归零
                RebootMsg = IntPtr.Zero,
                Command = IntPtr.Zero,
                ActionsCount = (uint)delays.Length,
                Actions = delays.Length > 0 ? buffer + structSize : IntPtr.Zero
            };

            Marshal.StructureToPtr(info, buffer, false);

            for (var i = 0; i < delays.Length; i++)
            {
                Marshal.StructureToPtr(
                    new ScAction { Type = SC_ACTION_RESTART, Delay = delays[i] },
                    buffer + structSize + i * actionSize, false);
            }

            if (!ChangeServiceConfig2(service, SERVICE_CONFIG_FAILURE_ACTIONS, buffer))
                return (false, $"设置 {name} 的恢复策略失败：{ErrorMessage(Marshal.GetLastWin32Error())}");

            return (true, delays.Length == 0
                ? $"{name} 的失败恢复已清除（失败后不自动重启）。"
                : $"{name} 已设为失败后自动重启（{string.Join(" / ", delays.Select(DelayText))}）。");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(scm);
        }
    }

    private static uint QueryState(IntPtr service)
    {
        var size = Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            if (QueryServiceStatusEx(service, 0, buffer, size, out _))
                return Marshal.PtrToStructure<ServiceStatusProcess>(buffer).CurrentState;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return 0;
    }

    private static (bool Ok, string Message) WaitForState(IntPtr service, uint target, string name)
    {
        for (var i = 0; i < 40; i++)
        {
            var state = QueryState(service);
            if (state == target)
                return (true, $"{(target == 4 ? "启动" : "停止")} {name} 成功。");

            if (state == 0)
                break;

            Thread.Sleep(250);
        }

        return (false, $"命令已发出，但 {name} 在 10 秒内没有变成目标状态——" +
                       "可能还在处理，或用服务自己的方式重启后会恢复。");
    }

    private static string ErrorMessage(int code) => code switch
    {
        5 => "拒绝访问（权限不足）",
        1051 => "依赖的服务没有运行",
        1053 => "服务没有及时响应启动请求",
        1060 => "服务不存在",
        1062 => "服务没有启动",
        1072 => "服务已被标记为删除",
        _ => "错误 " + code
    };
}
